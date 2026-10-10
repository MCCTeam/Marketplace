// AutoRelog: rejoins the server after an unexpected disconnect when the kick message matches one of the configured triggers (or on any disconnect with Ignore_Kick_Message), after a random delay and within a retry budget that resets once a session has stayed up.
//
// Client has its own reconnect supervisor, driven by connection.reconnect.* in client.toml and off by default.
// Its policy provider is fixed when the client is built, so a plugin cannot replace it.
// Rather than race it, this plugin only acts on the transition to Disconnected, which is where the core has decided NOT to reconnect (policy off, disconnect not retryable, or its own attempts exhausted).
// While the core is driving a reconnect the client sits in Reconnecting and this plugin stays out of the way.

using DMCBK.Core;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Tomlet.Attributes;
using Umpk.Client;
using Umpk.Protocol.Java;

namespace DMCBK.OfficialPlugins.AutoRelog;

/// <summary>A minimum and maximum delay, in seconds.</summary>
public sealed class DelayRange
{
    /// <summary>The minimum delay in seconds.</summary>
    [TomlProperty("min")]
    public double Min { get; set; } = 3.0;

    /// <summary>The maximum delay in seconds.</summary>
    [TomlProperty("max")]
    public double Max { get; set; } = 3.0;
}

/// <summary>The user-editable, persisted settings for AutoRelog (serialized to settings.toml).</summary>
public sealed class AutoRelogSettings : IValidatablePluginSettings
{
    /// <summary>Master switch for the plugin.</summary>
    [TomlInlineComment("$settings.enabled$")]
    public bool Enabled { get; set; } = true;

    /// <summary>The random delay range, in seconds, before rejoining.</summary>
    [TomlPrecedingComment("$settings.delay$")]
    public DelayRange Delay { get; set; } = new();

    /// <summary>How many rejoin attempts to make before giving up (-1 means unlimited).</summary>
    [TomlInlineComment("$settings.retries$")]
    public int Retries { get; set; } = 3;

    /// <summary>Whether to rejoin on ANY unexpected disconnect instead of only matching kick messages.</summary>
    [TomlInlineComment("$settings.ignore_kick_message$")]
    public bool Ignore_Kick_Message { get; set; }

    /// <summary>Case-insensitive substrings of the kick message that trigger a rejoin.</summary>
    [TomlPrecedingComment("$settings.kick_messages$")]
    public string[] Kick_Messages { get; set; } =
    [
        "Connection has been lost",
        "Server is restarting",
        "Server is full",
        "Too Many people",
    ];

    /// <summary>Clamps and normalizes on load and reload, and self-disables if nothing could ever match.</summary>
    public void Validate()
    {
        Delay ??= new DelayRange();
        Kick_Messages ??= [];

        Delay.Min = Math.Max(0.1, Delay.Min);
        Delay.Max = Math.Max(0.1, Delay.Max);
        Delay.Min = Math.Min(AutoRelogPolicy.MaxDelaySeconds, Delay.Min);
        Delay.Max = Math.Min(AutoRelogPolicy.MaxDelaySeconds, Delay.Max);

        if (Delay.Min > Delay.Max)
            (Delay.Min, Delay.Max) = (Delay.Max, Delay.Min);

        if (Enabled && !Ignore_Kick_Message && Kick_Messages.Length == 0)
            Enabled = false;
    }
}

/// <summary>The reconnect decision, free of client state so it can be unit tested headlessly.</summary>
public static class AutoRelogPolicy
{
    /// <summary>The largest delay the plugin accepts.</summary>
    public static double MaxDelaySeconds => int.MaxValue / 20.0;

    /// <summary>True when the retry budget is unlimited (<c>-1</c> or <c>int.MaxValue</c>).</summary>
    public static bool HasUnlimitedRetries(int retries) => retries < 0 || retries == int.MaxValue;

    /// <summary>True when another attempt is allowed given the attempts already spent.</summary>
    public static bool CanReconnect(int retries, int attemptsUsed)
        => HasUnlimitedRetries(retries) || attemptsUsed < retries;

    /// <summary>
    /// True when <paramref name="kickMessage"/> should trigger a rejoin.
    /// With <paramref name="ignoreKickMessage"/> any message matches, otherwise it is a case-insensitive substring test against <paramref name="triggers"/>.
    /// </summary>
    public static bool ShouldReconnect(
        string kickMessage, IReadOnlyList<string> triggers, bool ignoreKickMessage, out string? matched)
    {
        ArgumentNullException.ThrowIfNull(kickMessage);
        ArgumentNullException.ThrowIfNull(triggers);
        matched = null;
        if (ignoreKickMessage)
            return true;

        foreach (string trigger in triggers)
        {
            if (trigger.Length > 0 && kickMessage.Contains(trigger, StringComparison.OrdinalIgnoreCase))
            {
                matched = trigger;
                return true;
            }
        }

        return false;
    }

    /// <summary>Draws the next rejoin delay from the configured range.</summary>
    public static double NextDelaySeconds(double minSeconds, double maxSeconds, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (maxSeconds <= minSeconds)
            return minSeconds;

        return (random.NextDouble() * (maxSeconds - minSeconds)) + minSeconds;
    }

    /// <summary>
    /// The phrase synthesized for a dropped socket.
    /// It is compared against the user's Kick_Messages, whose shipped default contains it, so it is data and stays untranslated.
    /// </summary>
    public const string ConnectionLost = "Connection has been lost";

    /// <summary>
    /// Maps a transport close onto <see cref="ConnectionLost"/>.
    /// The core reports these with no server message, so without the mapping the shipped default trigger list would never match one.
    /// </summary>
    public static string DescribeTransportClose(CloseReason reason) => reason switch
    {
        CloseReason.SocketEof or CloseReason.IdleTimeout or CloseReason.ProtocolViolation
            => ConnectionLost,
        _ => reason.ToString(),
    };
}

/// <summary>The AutoRelog plugin entry point.</summary>
public sealed class AutoRelogPlugin : IPlugin
{
    /// <summary>How long a session must stay up before the retry budget is handed back.</summary>
    private static readonly TimeSpan StableJoinBeforeRetryReset = TimeSpan.FromSeconds(60);

    private readonly Random _random = new();
    private readonly object _gate = new();

    private AutoRelogSettings _settings = new();
    private PluginContext? _context;
    private ILogger? _logger;
    private IPluginLocalization? _strings;
    private IDisposable? _stableJoinJob;

    private int _attempts;
    private DateTime? _lastJoinUtc;
    private bool _reconnectPending;

    /// <inheritdoc/>
    public void Configure(PluginDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        descriptor.Id = "auto-relog";
        descriptor.Version = "2.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
        descriptor.WithSettings<AutoRelogSettings>();
    }

    /// <inheritdoc/>
    public Task ActivateAsync(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _settings = context.Settings.Load<AutoRelogSettings>();
        _context = context;
        _strings = context.Strings;
        _logger = context.Logger;

        // A fresh scope arrives on the first connect and on every reconnect, so the join bookkeeping needs no polling of its own.
        context.SessionStarted += (_, _) => OnSessionStarted();
        context.SessionEnded += (_, _) => OnSessionEnded();

        context.Client.StatusChanged += OnStatusChanged;

        // Wall-clock cron rather than a tick handler, because this has to keep running while no session is attached.
        _stableJoinJob = context.Cron.Every(TimeSpan.FromSeconds(10), ResetRetriesAfterStableJoin);

        _logger?.LogDebug("{Message}", _strings?.Format("launch", _settings.Retries));
        if (_settings.Ignore_Kick_Message)
            _logger?.LogDebug("{Message}", _strings?.Get("no_kick_message_filter"));

        return Task.CompletedTask;
    }

    private void OnSessionStarted()
    {
        lock (_gate)
        {
            _lastJoinUtc = DateTime.UtcNow;
            _reconnectPending = false;
        }

        _logger?.LogDebug("{Message}", _strings?.Get("joined"));
    }

    private void OnSessionEnded() => _logger?.LogDebug("{Message}", _strings?.Get("session_ended"));

    private void OnStatusChanged(object? sender, ClientStatusChangedEventArgs e)
    {
        if (!_settings.Enabled || e.Current != ClientStatus.Disconnected)
            // Reconnecting means the core supervisor is driving this one, so stay out of its way.
            return;

        if (e.Disconnect is not { } info)
            return; // A clean stop with no disconnect info (for example /quit).

        if (info.WasLocal || info.Reason == CloseReason.Cancelled)
        {
            _logger?.LogDebug("{Message}", _strings?.Get("ignore_user_logout"));
            return;
        }

        string kickMessage = DescribeDisconnect(info);
        _logger?.LogDebug("{Message}", _strings?.Format("disconnect_message", kickMessage));

        if (!AutoRelogPolicy.ShouldReconnect(
                kickMessage, _settings.Kick_Messages, _settings.Ignore_Kick_Message, out string? matched))
        {
            _logger?.LogDebug("{Message}", _strings?.Get("reconnect_ignored"));
            return;
        }

        if (!TryConsumeAttempt(out int retriesLeft))
        {
            _logger?.LogInformation("{Message}", _strings?.Format("retries_exhausted", _settings.Retries));
            return;
        }

        double delay = AutoRelogPolicy.NextDelaySeconds(_settings.Delay.Min, _settings.Delay.Max, _random);
        string? retriesDisplay = AutoRelogPolicy.HasUnlimitedRetries(_settings.Retries)
            ? _strings?.Get("retries_unlimited")
            : retriesLeft.ToString(System.Globalization.CultureInfo.InvariantCulture);

        _logger?.LogInformation(
            "{Message}",
            _strings?.Format(matched is null ? "reconnect_always" : "reconnect_matched", matched ?? kickMessage));
        _logger?.LogInformation("{Message}", _strings?.Format("wait_with_retries", delay, retriesDisplay));

        _ = Task.Run(() => ReconnectAfterDelayAsync(delay));
    }

    private async Task ReconnectAfterDelayAsync(double delaySeconds)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(false);
            if (_context is not { } context)
                return;

            await context.Client.ReconnectAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The client is shutting down.
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException)
        {
            // The failed attempt already moved the client back to Disconnected, which re-enters this decision with the next slice of the retry budget.
            // Nothing to roll back.
            _logger?.LogWarning("{Message}", _strings?.Format("reconnect_failed", ex.Message));
        }
        finally
        {
            lock (_gate)
                _reconnectPending = false;
        }
    }

    /// <summary>Renders the disconnect into the single plain string the trigger list is matched against.</summary>
    private string DescribeDisconnect(DisconnectInfo info)
    {
        if (info.Message is { } component && _context is { } context)
        {
            string rendered = component.ToPlainText(context.Translations);
            if (rendered.Length > 0)
                return rendered;
        }

        return info.Fault?.Message is { Length: > 0 } fault
            ? $"{AutoRelogPolicy.DescribeTransportClose(info.Reason)} ({fault})"
            : AutoRelogPolicy.DescribeTransportClose(info.Reason);
    }

    private bool TryConsumeAttempt(out int retriesLeft)
    {
        lock (_gate)
        {
            if (_reconnectPending)
            {
                retriesLeft = 0;
                return false; // An attempt is already in flight; never stack two rejoins.
            }

            if (!AutoRelogPolicy.CanReconnect(_settings.Retries, _attempts))
            {
                retriesLeft = 0;
                return false;
            }

            _attempts++;
            _lastJoinUtc = null;
            _reconnectPending = true;
            retriesLeft = AutoRelogPolicy.HasUnlimitedRetries(_settings.Retries)
                ? int.MaxValue
                : Math.Max(0, _settings.Retries - _attempts);
            return true;
        }
    }

    /// <summary>A session that stayed up long enough hands the retry budget back.</summary>
    private void ResetRetriesAfterStableJoin()
    {
        bool reset = false;
        lock (_gate)
        {
            if (_attempts > 0
                && _lastJoinUtc is { } lastJoinUtc
                && DateTime.UtcNow - lastJoinUtc >= StableJoinBeforeRetryReset)
            {
                _attempts = 0;
                _lastJoinUtc = null;
                reset = true;
            }
        }

        if (reset)
            _logger?.LogDebug("{Message}", _strings?.Get("retries_reset"));
    }

    /// <summary>
    /// Detaches from <see cref="Client.StatusChanged"/>, which is a client-lifetime event.
    /// An unloaded instance that stayed subscribed would go on rejoining the server, and would keep its own load context rooted through the delegate.
    /// </summary>
    public Task DeactivateAsync(CancellationToken ct)
    {
        _stableJoinJob?.Dispose();
        _stableJoinJob = null;
        if (_context is { } context)
            context.Client.StatusChanged -= OnStatusChanged;

        return Task.CompletedTask;
    }
}
