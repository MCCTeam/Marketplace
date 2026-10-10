// DiscordRpc: publishes a Discord Rich Presence for the live session through the local Discord IPC pipe.
// Needs an application id from https://discord.com/developers/applications and a running Discord client; with no id configured the plugin self-disables and says why.
//
// The RPC client lives for as long as the plugin, not the session, and the presence is refreshed on the wall-clock cron so its cadence survives a reconnect.

using DiscordRPC;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Tomlet.Attributes;
using Umpk.Client;
using HostLogger = Microsoft.Extensions.Logging.ILogger;

namespace DMCBK.OfficialPlugins.DiscordRpc;

/// <summary>The settings for DiscordRpc, saved to settings.toml.</summary>
public sealed class DiscordRpcSettings : IValidatablePluginSettings
{
    /// <summary>Master switch for the plugin.</summary>
    [TomlInlineComment("$settings.enabled$")]
    public bool Enabled { get; set; } = true;

    /// <summary>The Discord application id the presence is published under. Required.</summary>
    [TomlInlineComment("$settings.application_id$")]
    public string ApplicationId { get; set; } = string.Empty;

    /// <summary>The first presence line template.</summary>
    [TomlInlineComment("$settings.presence_details$")]
    public string PresenceDetails { get; set; } = "Playing on {server_host}:{server_port}";

    /// <summary>The second presence line template.</summary>
    [TomlInlineComment("$settings.presence_state$")]
    public string PresenceState { get; set; } = "{dimension} - HP: {health}/{max_health}";

    /// <summary>The large image asset key configured on the Discord application.</summary>
    [TomlInlineComment("$settings.large_image_key$")]
    public string LargeImageKey { get; set; } = "mcc_icon";

    /// <summary>The large image hover text.</summary>
    [TomlInlineComment("$settings.large_image_text$")]
    public string LargeImageText { get; set; } = "Minecraft Console Client";

    /// <summary>The small image asset key configured on the Discord application.</summary>
    [TomlInlineComment("$settings.small_image_key$")]
    public string SmallImageKey { get; set; } = string.Empty;

    /// <summary>The small image hover text.</summary>
    [TomlInlineComment("$settings.small_image_text$")]
    public string SmallImageText { get; set; } = string.Empty;

    /// <summary>Whether the server address is published or masked.</summary>
    [TomlInlineComment("$settings.show_server_address$")]
    public bool ShowServerAddress { get; set; } = true;

    /// <summary>Whether the coordinates are published or masked.</summary>
    [TomlInlineComment("$settings.show_coordinates$")]
    public bool ShowCoordinates { get; set; } = true;

    /// <summary>Whether health and food are published or masked.</summary>
    [TomlInlineComment("$settings.show_health$")]
    public bool ShowHealth { get; set; } = true;

    /// <summary>Whether the dimension is published or masked.</summary>
    [TomlInlineComment("$settings.show_dimension$")]
    public bool ShowDimension { get; set; } = true;

    /// <summary>Whether the game mode is published or masked.</summary>
    [TomlInlineComment("$settings.show_gamemode$")]
    public bool ShowGamemode { get; set; } = true;

    /// <summary>Whether the session elapsed time is shown.</summary>
    [TomlInlineComment("$settings.show_elapsed_time$")]
    public bool ShowElapsedTime { get; set; } = true;

    /// <summary>Whether the online player count is published as a Discord party size.</summary>
    [TomlInlineComment("$settings.show_player_count$")]
    public bool ShowPlayerCount { get; set; } = true;

    /// <summary>How often, in seconds, the presence is refreshed.</summary>
    [TomlInlineComment("$settings.update_interval_seconds$")]
    public int UpdateIntervalSeconds { get; set; } = 10;

    /// <summary>Normalizes on load and reload, and self-disables when there is no application id.</summary>
    public void Validate()
    {
        ApplicationId ??= string.Empty;
        PresenceDetails ??= string.Empty;
        PresenceState ??= string.Empty;
        LargeImageKey ??= string.Empty;
        LargeImageText ??= string.Empty;
        SmallImageKey ??= string.Empty;
        SmallImageText ??= string.Empty;

        if (UpdateIntervalSeconds < 1)
            UpdateIntervalSeconds = 10;

        if (string.IsNullOrWhiteSpace(ApplicationId))
            Enabled = false;
    }
}

/// <summary>The DiscordRpc plugin entry point.</summary>
public sealed class DiscordRpcPlugin : IPlugin
{
    private readonly object _gate = new();

    private DiscordRpcSettings _settings = new();
    private PluginContext? _context;
    private HostLogger? _logger;
    private IPluginLocalization? _strings;
    private DiscordRpcClient? _rpcClient;
    private IDisposable? _refreshJob;
    private Timestamps? _sessionTimestamps;

    /// <inheritdoc/>
    public void Configure(PluginDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        descriptor.Id = "discord-rpc";
        descriptor.Version = "2.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
        descriptor.WithSettings<DiscordRpcSettings>();
    }

    /// <inheritdoc/>
    public Task ActivateAsync(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _settings = context.Settings.Load<DiscordRpcSettings>();
        _context = context;
        _logger = context.Logger;
        _strings = context.Strings;

        // Validate() self-disables when the application id is missing; say why once, here.
        if (!_settings.Enabled)
        {
            _logger?.LogInformation(
                "{Message}",
                string.IsNullOrWhiteSpace(_settings.ApplicationId)
                    ? _strings?.Get("missing_app_id")
                    : _strings?.Get("disabled"));
            return Task.CompletedTask;
        }

        if (!TryInitializeClient())
            return Task.CompletedTask;

        context.SessionStarted += (_, e) => OnSessionStarted(e.Session);
        context.SessionEnded += (_, _) => OnSessionEnded();

        _refreshJob = context.Cron.Every(TimeSpan.FromSeconds(_settings.UpdateIntervalSeconds), RefreshPresence);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Clears the presence and releases the RPC connection, including the named pipe and the worker threads the client owns.
    /// Without this, a stale presence and an open pipe survive unload.
    /// </summary>
    public Task DeactivateAsync(CancellationToken ct)
    {
        _refreshJob?.Dispose();
        _refreshJob = null;

        lock (_gate)
        {
            if (_rpcClient is { IsDisposed: false } client)
            {
                client.ClearPresence();
                client.Dispose();
            }

            _rpcClient = null;
        }

        return Task.CompletedTask;
    }

    private bool TryInitializeClient()
    {
        try
        {
            // The published package does not probe the Flatpak and snap runtime paths, so Linux gets the pipe client that does.
            var client = OperatingSystem.IsLinux()
                ? new DiscordRpcClient(_settings.ApplicationId.Trim(), client: new DiscordRpcPipeClient())
                : new DiscordRpcClient(_settings.ApplicationId.Trim());

            client.Logger = new PluginRpcLogger(_logger, _strings);
            client.OnReady += (_, e) =>
                _logger?.LogInformation("{Message}", _strings?.Format("connected", e.User.Username));
            client.OnConnectionFailed += (_, e) =>
                _logger?.LogWarning("{Message}", _strings?.Format("connection_failed", e.FailedPipe));

            client.Initialize();
            lock (_gate)
                _rpcClient = client;

            _logger?.LogInformation("{Message}", _strings?.Get("initialized"));
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogWarning(ex, "{Message}", _strings?.Format("init_error", ex.Message));
            return false;
        }
    }

    private void OnSessionStarted(ISessionScope session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _sessionTimestamps = _settings.ShowElapsedTime ? Timestamps.Now : null;
        RefreshPresence();
    }

    /// <summary>Clears the presence when the session ends, so a reconnect cannot leave a stale one up.</summary>
    private void OnSessionEnded()
    {
        _sessionTimestamps = null;
        lock (_gate)
        {
            if (_rpcClient is { IsDisposed: false } client)
                client.ClearPresence();
        }
    }

    private void RefreshPresence()
    {
        DiscordRpcClient? client;
        lock (_gate)
            client = _rpcClient;

        if (client is null or { IsDisposed: true } || _context is not { } context)
            return;

        if (context.CurrentSession is not { } session)
            return; // Nothing to publish between sessions; OnSessionEnded already cleared the presence.

        try
        {
            PresenceFacts facts = ReadFacts(session);
            var presence = new RichPresence
            {
                Details = PresenceFormatter.Truncate(
                    PresenceFormatter.Format(_settings.PresenceDetails, facts, _settings)),
                State = PresenceFormatter.Truncate(
                    PresenceFormatter.Format(_settings.PresenceState, facts, _settings)),
            };

            var assets = new Assets();
            bool hasAssets = false;
            if (!string.IsNullOrWhiteSpace(_settings.LargeImageKey))
            {
                assets.LargeImageKey = _settings.LargeImageKey.Trim();
                assets.LargeImageText = PresenceFormatter.Truncate(
                    PresenceFormatter.Format(_settings.LargeImageText, facts, _settings));
                hasAssets = true;
            }

            if (!string.IsNullOrWhiteSpace(_settings.SmallImageKey))
            {
                assets.SmallImageKey = _settings.SmallImageKey.Trim();
                assets.SmallImageText = PresenceFormatter.Truncate(
                    PresenceFormatter.Format(_settings.SmallImageText, facts, _settings));
                hasAssets = true;
            }

            if (hasAssets)
                presence.Assets = assets;

            if (_settings.ShowElapsedTime && _sessionTimestamps is not null)
                presence.Timestamps = _sessionTimestamps;

            if (_settings.ShowPlayerCount && facts.PlayerCount > 0)
            {
                presence.Party = new Party
                {
                    ID = $"mcc_{facts.ServerHost}_{facts.ServerPort}",
                    Size = facts.PlayerCount,
                    Max = facts.PlayerCount,
                };
            }

            client.SetPresence(presence);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogDebug(ex, "{Message}", _strings?.Format("update_error", ex.Message));
        }
    }

    /// <summary>Reads one snapshot of everything a presence line can mention.</summary>
    private static PresenceFacts ReadFacts(ISessionScope session)
    {
        SessionInfo? info = session.Client.Session;
        var self = session.State.Self;

        string dimension = "Unknown";
        if (session.State.HasWorld)
        {
            try
            {
                dimension = session.State.World.Dimension.DimensionName.ToString();
            }
            catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)
            {
                // The world is attached but the dimension is not resolved yet.
            }
        }

        return new PresenceFacts(
            info?.Endpoint.Host ?? string.Empty,
            info?.Endpoint.Port ?? 0,
            self.Username.Length > 0 ? self.Username : info?.Profile.Name ?? string.Empty,
            self.Health,
            self.Food,
            (int)self.Position.X,
            (int)self.Position.Y,
            (int)self.Position.Z,
            dimension,
            (int)self.GameMode,
            session.State.TabList.Entries.Count,
            info?.Version.Version.Protocol ?? 0);
    }
}

/// <summary>Routes the DiscordRPC library's own log output through the plugin logger.</summary>
internal sealed class PluginRpcLogger(HostLogger? logger, IPluginLocalization? strings) : DiscordRPC.Logging.ILogger
{
    private readonly HostLogger? _logger = logger;
    private readonly IPluginLocalization? _strings = strings;

    /// <inheritdoc/>
    public DiscordRPC.Logging.LogLevel Level { get; set; } = DiscordRPC.Logging.LogLevel.Warning;

    /// <inheritdoc/>
    public void Trace(string message, params object[] args) => Write(LogLevel.Trace, message, args);

    /// <inheritdoc/>
    public void Info(string message, params object[] args) => Write(LogLevel.Debug, message, args);

    /// <inheritdoc/>
    public void Warning(string message, params object[] args) => Write(LogLevel.Debug, message, args);

    /// <inheritdoc/>
    public void Error(string message, params object[] args) => Write(LogLevel.Debug, message, args);

    private void Write(LogLevel level, string message, object[] args)
    {
        // The library's messages are diagnostics for its own pipe handling, so they stay at debug or trace level and never reach the console.
        string text = string.Format(
            System.Globalization.CultureInfo.InvariantCulture, message.Replace("{", "{{").Replace("}", "}}"), args);
        _logger?.Log(level, "{Message}", _strings?.Format("library_diagnostic", text));
    }
}
