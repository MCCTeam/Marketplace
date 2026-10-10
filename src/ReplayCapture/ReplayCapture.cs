// ReplayCapture records the session as a ReplayMod .mcpr file.
//
// The raw frame feed is taken at PluginContext.SessionCreated, while the session's client exists but has not dialled.
// That is the only moment a subscription can see the handshake, login and configuration frames: SessionStarted and the per-session ISessionScope.ObservePackets both fire once play begins, by which point that traffic is gone.
// The subscription lives on the raw UmpkClient, so it is not released with the session; it is disposed on SessionEnded and retaken on the next SessionCreated.
//
// Frames that arrive before SessionStarted has built a writer (ReplayWriter needs the negotiated protocol, version and server) are buffered and replayed into it in order, so the recording starts at the login exchange rather than the first Play packet.
//
// Frames come in both directions and the payload span is only valid inside the callback.
// AddPacket applies the clientbound-only rule of the .mcpr format and copies the payload during the call.

using System.Globalization;
using DMCBK.Core.Commands;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Tomlet.Attributes;
using Umpk.Commands;

namespace DMCBK.OfficialPlugins.ReplayCapture;

/// <summary>The user-editable, persisted settings for ReplayCapture (serialized to settings.toml).</summary>
public sealed class ReplayCaptureSettings : IValidatablePluginSettings
{
    /// <summary>Master switch for the plugin.</summary>
    [TomlInlineComment("$settings.enabled$")]
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds between automatic backup snapshots; -1 disables them.</summary>
    [TomlInlineComment("$settings.backup_interval$")]
    public double Backup_Interval { get; set; } = 300.0;

    /// <summary>Clamps the backup interval on load and reload.</summary>
    public void Validate()
    {
        if (Backup_Interval < -1)
            Backup_Interval = -1;
    }
}

/// <summary>Why the recorder is not running.</summary>
public enum ReplayUnavailableReason
{
    /// <summary>The recorder is running.</summary>
    None,

    /// <summary>The user turned the plugin off.</summary>
    Disabled,

    /// <summary>The host exposes no raw packet feed, so there is no frame source to record from.</summary>
    NoRawPacketFeed,

    /// <summary>No session is attached, so there is nothing to record.</summary>
    NoSession,
}

/// <summary>The availability rules, kept free of client state so they can be tested headlessly.</summary>
public static class ReplayAvailability
{
    /// <summary>
    /// True when the host exposes a raw frame stream to plugins.
    /// The stock host does, but <see cref="Evaluate"/> still honours a false value, so a host without the capability gets a clear refusal instead of an empty recording.
    /// </summary>
    public const bool RawPacketFeedAvailable = true;

    /// <summary>Decides whether a recording can run, and why not when it cannot.</summary>
    public static ReplayUnavailableReason Evaluate(bool enabled, bool inSession, bool rawFeedAvailable)
    {
        if (!enabled)
            return ReplayUnavailableReason.Disabled;

        if (!rawFeedAvailable)
            return ReplayUnavailableReason.NoRawPacketFeed;

        return inSession ? ReplayUnavailableReason.None : ReplayUnavailableReason.NoSession;
    }

    /// <summary>Whether backups should be scheduled for the given interval.</summary>
    public static bool BackupsEnabled(double backupIntervalSeconds) => backupIntervalSeconds > 0;
}

/// <summary>The ReplayCapture plugin entry point.</summary>
public sealed class ReplayCapturePlugin : IPlugin
{
    private const string WorkingFolder = "recording_cache";
    private const string OutputFolder = "replay_recordings";
    private const string BackupFileName = "REPLAY_BACKUP.mcpr";

    /// <summary>Bound on the pre-writer buffer; a real login and configuration exchange is a handful of frames.</summary>
    private const int MaxPendingFrames = 4096;

    private readonly object _gate = new();

    private ReplayCaptureSettings _settings = new();
    private PluginContext? _context;
    private ILogger? _logger;
    private IPluginLocalization? _strings;
    private ReplayWriter? _writer;
    private IDisposable? _backupJob;
    private IDisposable? _frameSubscription;
    private List<BufferedFrame>? _pending;

    /// <inheritdoc/>
    public void Configure(PluginDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        descriptor.Id = "replay-capture";
        descriptor.Version = "2.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
        descriptor.WithSettings<ReplayCaptureSettings>();
    }

    /// <inheritdoc/>
    public Task ActivateAsync(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _settings = context.Settings.Load<ReplayCaptureSettings>();
        _context = context;
        _logger = context.Logger;
        _strings = context.Strings;

        // Registered once, not per session, so the command still answers with a reason when the recorder is unavailable.
        // The plugin command scope is what takes it back off the dispatcher on unload.
        context.Commands.Register(new ReplayCommand(this, context.Strings));

        ReplayUnavailableReason reason = ReplayAvailability.Evaluate(
            _settings.Enabled, inSession: true, ReplayAvailability.RawPacketFeedAvailable);

        if (reason == ReplayUnavailableReason.NoRawPacketFeed)
        {
            _logger?.LogWarning("{Message}", _strings?.Get("no_raw_packet_feed"));
            return Task.CompletedTask;
        }

        if (reason == ReplayUnavailableReason.Disabled)
            return Task.CompletedTask;

        context.SessionCreated += (_, e) => OnSessionCreated(e.Client);
        context.SessionStarted += (_, e) => OnSessionStarted(e.Session);
        context.SessionEnded += (_, _) => OnSessionEnded();

        if (ReplayAvailability.BackupsEnabled(_settings.Backup_Interval))
            // Wall-clock cron rather than a tick counter, so the cadence survives a reconnect.
            _backupJob = context.Cron.Every(TimeSpan.FromSeconds(_settings.Backup_Interval), CreateBackup);

        return Task.CompletedTask;
    }

    /// <summary>Stops any running recording, which closes the replay file, and releases the backup job.</summary>
    public Task DeactivateAsync(CancellationToken ct)
    {
        Interlocked.Exchange(ref _frameSubscription, null)?.Dispose();
        _backupJob?.Dispose();
        _backupJob = null;
        StopRecording();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Takes the raw feed while the client exists but has not dialled: the only moment a subscription can see the handshake, login and configuration frames.
    /// Any buffer left by an attempt that never reached <see cref="OnSessionStarted"/> (a failed login) is dropped, since no writer owns it.
    /// </summary>
    private void OnSessionCreated(Umpk.Client.UmpkClient client)
    {
        lock (_gate)
            _pending = [];

        IDisposable subscription = client.ObservePackets(OnFrame);
        Interlocked.Exchange(ref _frameSubscription, subscription)?.Dispose();
    }

    private void OnSessionStarted(ISessionScope session)
    {
        if (_context is not { } context)
            return;

        Umpk.Client.SessionInfo? info = session.Client.Session;
        int protocol = info?.Version.Version.Protocol ?? 0;
        string versionName = info?.Version.Version.Name ?? "0.0";
        string server = info is null
            ? string.Empty
            : string.Format(CultureInfo.InvariantCulture, "{0}:{1}", info.Endpoint.Host, info.Endpoint.Port);

        lock (_gate)
        {
            _writer?.Dispose();
            var writer = new ReplayWriter(
                Path.Combine(context.Storage.DataDirectory, WorkingFolder, Guid.NewGuid().ToString("N")),
                Path.Combine(context.Storage.DataDirectory, OutputFolder),
                protocol,
                versionName,
                server);

            // Drain the buffered handshake, login and configuration frames in order before the writer takes new ones, so the recording starts at the login exchange and not at the first Play packet.
            List<BufferedFrame>? pending = _pending;
            _pending = null;
            if (pending is not null)
            {
                foreach (BufferedFrame frame in pending)
                    TryAddPacket(writer, frame.WireId, frame.Payload, frame.IsClientbound);
            }

            _writer = writer;
        }

        _logger?.LogInformation("{Message}", _strings?.Format("started", server));
    }

    private void OnSessionEnded()
    {
        // The subscription taken in OnSessionCreated lives on the raw UmpkClient, so it is not released with the session.
        // Dispose it here; the next SessionCreated takes a fresh one.
        Interlocked.Exchange(ref _frameSubscription, null)?.Dispose();
        lock (_gate)
            _pending = null;

        StopRecording();
    }

    /// <summary>
    /// One observed frame.
    /// This runs on the connection's read/write path, not the session loop, so it does the minimum: hand the frame to the writer, or buffer a copy (the payload span is only valid for this call) until <see cref="OnSessionStarted"/> builds one.
    /// </summary>
    private void OnFrame(in Umpk.Protocol.Java.PacketFrame frame)
    {
        lock (_gate)
        {
            if (_writer is { } writer)
            {
                TryAddPacket(writer, frame.WireId, frame.Payload, frame.IsClientbound);
                return;
            }

            if (_pending is { } pending && pending.Count < MaxPendingFrames)
                pending.Add(new BufferedFrame(frame.WireId, frame.CopyPayload(), frame.IsClientbound));
        }
    }

    private void TryAddPacket(ReplayWriter writer, int wireId, ReadOnlySpan<byte> payload, bool isClientbound)
    {
        if (!writer.RecordRunning)
            return;

        try
        {
            writer.AddPacket(wireId, payload, isClientbound);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // A recording whose file went away must not tear down the connection's read loop.
            _logger?.LogDebug(ex, "{Message}", _strings?.Get("save_failed"));
        }
    }

    /// <summary>One buffered frame. The payload is copied out of the frame's span, so it outlives the callback.</summary>
    private readonly record struct BufferedFrame(int WireId, byte[] Payload, bool IsClientbound);

    private void StopRecording()
    {
        ReplayWriter? writer;
        lock (_gate)
        {
            writer = _writer;
            _writer = null;
        }

        if (writer is null)
            return;

        try
        {
            string path = writer.Stop();
            if (path.Length > 0)
                _logger?.LogInformation("{Message}", _strings?.Format("created", path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _logger?.LogWarning(ex, "{Message}", _strings?.Get("save_failed"));
        }
        finally
        {
            writer.Dispose();
        }
    }

    private void CreateBackup()
    {
        ReplayWriter? writer;
        lock (_gate)
            writer = _writer;

        if (writer is not { RecordRunning: true })
            return;

        try
        {
            writer.CreateBackup(BackupFileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _logger?.LogDebug(ex, "{Message}", _strings?.Get("save_failed"));
        }
    }

    /// <summary>The <c>replay save</c> command: snapshot the running recording.</summary>
    internal int SaveCommand(CommandContext ctx, IPluginLocalization strings)
    {
        if (DescribeUnavailable(ctx, strings) is { } unavailable)
            return ctx.Result.Fail(unavailable);

        ReplayWriter? writer;
        lock (_gate)
            writer = _writer;

        if (writer is not { RecordRunning: true })
            return ctx.Result.Fail(strings.Get("restart"));

        try
        {
            string path = writer.CreateBackup(writer.GetDefaultFileName());
            return ctx.Result.Ok(strings.Format("created_at", path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return ctx.Result.Fail(ex.Message);
        }
    }

    /// <summary>The <c>replay stop</c> command: finalize the recording.</summary>
    internal int StopCommand(CommandContext ctx, IPluginLocalization strings)
    {
        if (DescribeUnavailable(ctx, strings) is { } unavailable)
            return ctx.Result.Fail(unavailable);

        ReplayWriter? writer;
        lock (_gate)
            writer = _writer;

        if (writer is not { RecordRunning: true })
            return ctx.Result.Fail(strings.Get("restart"));

        StopRecording();
        return ctx.Result.Ok(strings.Get("stopped"));
    }

    /// <summary>The user-facing reason the recorder is unavailable, or null when it is running.</summary>
    private string? DescribeUnavailable(CommandContext ctx, IPluginLocalization strings)
        => ReplayAvailability.Evaluate(
            _settings.Enabled, ctx.InSession, ReplayAvailability.RawPacketFeedAvailable) switch
        {
            ReplayUnavailableReason.Disabled => strings.Get("disabled"),
            ReplayUnavailableReason.NoRawPacketFeed => strings.Get("no_raw_packet_feed_short"),
            ReplayUnavailableReason.NoSession => strings.Get("no_session"),
            _ => null,
        };
}

/// <summary>The <c>replay</c> command: save and stop.</summary>
internal sealed class ReplayCommand(ReplayCapturePlugin plugin, IPluginLocalization strings) : CommandBase
{
    private readonly ReplayCapturePlugin _plugin = plugin;
    private readonly IPluginLocalization _strings = strings;

    /// <inheritdoc/>
    public override string CmdName => "replay";

    /// <inheritdoc/>
    public override string CmdDesc => _strings.Get("command_desc");

    /// <inheritdoc/>
    public override string CmdUsage => _strings.Get("command_usage");

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Plugins;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("save", _strings.Get("usage_save")),
        new("stop", _strings.Get("usage_stop")),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["replay save", "replay stop"];

    /// <inheritdoc/>
    public override string? ManTopic => "replay-capture";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);

        builder.Literal(CmdName, l => l
            .ThenLiteral("save", h => h
                .Executes(ctx => _plugin.SaveCommand(ctx.Source, _strings)))
            .ThenLiteral("stop", h => h
                .Executes(ctx => _plugin.StopCommand(ctx.Source, _strings)))
            .ThenLiteral("_help", h => h
                .Executes(ctx => ShowUsage(ctx.Source))
                .RedirectTo(help)));
    }
}
