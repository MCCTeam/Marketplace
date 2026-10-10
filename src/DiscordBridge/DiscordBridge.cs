// DiscordBridge - relays Minecraft chat into Discord channels and messages from those channels (and from owner DMs) back into the game, runs internal MCC commands sent by a configured owner with a prefix, and posts teleport requests as an embed with accept/deny buttons.
// With no bot token configured it self-disables and says so.
//
// The gateway connection is plugin-lifetime: connected once in ActivateAsync, closed once in DeactivateAsync.
// Only the chat subscription is per session, so a reconnect never leaves a second socket behind.
// Channels are resolved lazily per route, so a route added at runtime needs no reconnect.

using System.Collections.Concurrent;
using System.Text;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.Exceptions;
using DMCBK.Core;
using DMCBK.Core.Commands;
using DMCBK.Core.Manual;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Umpk.Client.Events;
using Umpk.Commands;

namespace DMCBK.OfficialPlugins.DiscordBridge;

/// <summary>Where the last Discord-issued command came from, so a triggered render answers the asker.</summary>
/// <param name="At">When the command arrived.</param>
/// <param name="GuildId">The guild, or null for a DM.</param>
/// <param name="ChannelId">The channel the command came from.</param>
/// <param name="AuthorId">Who sent it.</param>
internal sealed record ImageSource(DateTime At, ulong? GuildId, ulong ChannelId, ulong? AuthorId);

/// <summary>One route plus its live state.</summary>
internal sealed class RouteState
{
    internal RouteState(EffectiveRoute route)
    {
        Route = route;
    }

    internal EffectiveRoute Route { get; set; }

    internal DiscordChannel? Channel { get; set; }

    internal DateTime LastResolveAttempt { get; set; } = DateTime.MinValue;

    internal DateTime? LastSendAt { get; set; }

    internal bool LastSendOk { get; set; }

    internal string LastError { get; set; } = string.Empty;
}

/// <summary>The DiscordBridge plugin entry point.</summary>
public sealed class DiscordBridgePlugin : IPlugin
{
    private const string AcceptTeleportId = "accept_teleport";
    private const string DenyTeleportId = "deny_teleport";

    private static readonly TimeSpan TeleportButtonLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ResolveRetryInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan TeleportPostRetention = TimeSpan.FromHours(1);
    private const int MaxAggregationBuffer = 200;

    private readonly ConcurrentQueue<string> _aggregationBuffer = new();
    private readonly ConcurrentQueue<(string Path, string Caption, string Destination)> _pendingImages = new();
    private readonly ConcurrentQueue<(string Text, string Destination)> _pendingTexts = new();
    private readonly ConcurrentDictionary<ulong, DateTime> _teleportPosts = new();
    private readonly ConcurrentDictionary<ulong, DiscordDmChannel> _dmChannels = new();
    private ImageSource? _lastCommandSource;
    private readonly Dictionary<string, int> _knownEffects = new(StringComparer.Ordinal);
    private DateTime _quietEffectsUntil = DateTime.MinValue;
    private readonly object _gate = new();

    private DiscordBridgeSettings _settings = new();
    private PluginContext? _context;
    private ILogger? _logger;
    private IPluginLocalization? _strings;
    private DiscordClient? _client;
    private string _selfName = "?";
    private IDisposable? _aggregationJob;
    private IDisposable? _imageSubscription;
    private readonly List<RouteState> _routes = [];
    private BridgeFormats _formats = new(string.Empty, string.Empty, string.Empty);
    private int _droppedLines;

    /// <summary>True once the gateway is connected.</summary>
    public bool IsConnected { get; private set; }

    /// <inheritdoc/>
    public void Configure(PluginDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        descriptor.Id = "discord-bridge";
        descriptor.Version = "2.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
        descriptor.WithSettings<DiscordBridgeSettings>();
    }

    /// <inheritdoc/>
    public Task ActivateAsync(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _settings = context.Settings.Load<DiscordBridgeSettings>();
        _context = context;
        _logger = context.Logger;
        _strings = context.Strings;
        _formats = new BridgeFormats(
            _settings.PrivateMessageFormat, _settings.PublicMessageFormat, _settings.TeleportRequestMessageFormat);

        // Registered once, on the plugin command scope: RegisterHostCommand has no unregister and would keep this instance, and the plugin's whole load context, rooted in the dispatcher after unload.
        // It goes in before the enabled check so a disabled bridge still answers with a reason.
        context.Commands.Register(new DiscordBridgeCommand(this, context.Strings));
        RebuildRoutes();

        if (!_settings.Enabled)
        {
            _logger?.LogInformation("{Message}", MissingTokenText());
            return Task.CompletedTask;
        }

        if (_settings.OwnersIds.Length == 0 && _routes.All(r => r.Route.Owners.Length == 0))
            _logger?.LogWarning("{Message}", _strings?.Get("missing_owners"));

        // Images handed over by the Map plugin.
        // Cross-plugin messages are BCL-typed because each plugin loads in its own collectible context.
        _imageSubscription = context.Messenger.Subscribe<string[]>(OnBridgeMessage);

        if (_settings.Message_Aggregation_Interval > 0)
        {
            _aggregationJob = context.Cron.Every(
                TimeSpan.FromSeconds(_settings.Message_Aggregation_Interval), FlushAggregationBuffer);
        }

        context.SessionStarted += (_, e) => OnSessionStarted(e.Session);

        // Connecting is long-running; it must not block activation of the remaining plugins.
        _ = Task.Run(ConnectAsync);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Host teardown: flush what is buffered, announce the disconnect, and close the gateway socket, which otherwise survives an unload along with its background loop.
    /// </summary>
    public async Task DeactivateAsync(CancellationToken ct)
    {
        _aggregationJob?.Dispose();
        _aggregationJob = null;
        _imageSubscription?.Dispose();
        _imageSubscription = null;

        FlushAggregationBuffer();
        await DisconnectAsync().WaitAsync(ct).ConfigureAwait(false);
    }

    private void OnSessionStarted(ISessionScope session)
    {
        // Per-session subscription: torn down automatically at session end, so a reconnect never stacks two.
        session.Events.Subscribe<ChatMessageReceived>(OnChatMessage);
        session.Events.Subscribe<Died>(_ => OnDied());
        session.Events.Subscribe<Respawned>(_ => OnRespawned());
        session.Events.Subscribe<EntityEffectApplied>(e => OnSelfEffect(e.EntityId));
        session.Events.Subscribe<EntityEffectRemoved>(e => OnSelfEffect(e.EntityId));

        // Seed the tracker silently: joining with beacon effects is not news.
        _ = SyncEffectsAsync(announce: false);
    }

    private void OnRespawned()
    {
        // Death and dimension hops resend the whole effect table; none of that is news.
        lock (_gate)
            _quietEffectsUntil = DateTime.UtcNow + TimeSpan.FromSeconds(15);

        _ = SyncEffectsAsync(announce: false);
    }

    private void OnSelfEffect(int entityId)
    {
        int? selfId = _context?.CurrentSession?.Client.State.Self.EntityId;
        if (selfId is null || entityId != selfId)
            return;

        _ = SyncEffectsAsync(announce: true);
    }

    /// <summary>Diffs the live effect table against the tracker and announces the delta.</summary>
    private async Task SyncEffectsAsync(bool announce)
    {
        PluginContext? context = _context;
        if (context is null || !context.InSession || !IsConnected)
            return;

        if (context.CurrentSession?.Client.State.Features.Entities != true)
            return;

        try
        {
            IReadOnlyList<EffectSnapshot> snapshots =
                await context.Game.Player.GetEffectsAsync().ConfigureAwait(false);
            var alive = snapshots
                .Where(e => e.IsInfinite || e.Duration > 0)
                .OrderBy(e => e.EffectId, StringComparer.Ordinal)
                .ToList();
            var current = alive
                .Select(e => new DiscordVitals.TrackedEffect(e.EffectId, e.Amplifier <= 0 ? 0 : e.Level))
                .ToList();

            List<DiscordVitals.TrackedEffect> gained;
            List<DiscordVitals.TrackedEffect> lost;
            bool quiet;
            lock (_gate)
            {
                (IReadOnlyList<DiscordVitals.TrackedEffect> g, IReadOnlyList<DiscordVitals.TrackedEffect> l) =
                    DiscordVitals.DiffEffects(_knownEffects, current);
                gained = [.. g];
                lost = [.. l];
                _knownEffects.Clear();
                foreach (DiscordVitals.TrackedEffect effect in current)
                    _knownEffects[effect.Id] = effect.Level;

                quiet = DateTime.UtcNow < _quietEffectsUntil;
            }

            if (!announce || quiet)
                return;

            var byId = alive.ToDictionary(e => e.EffectId, StringComparer.Ordinal);
            List<RouteState> targets = SnapshotRoutes().Where(r => r.Route.RelayOut).ToList();
            foreach (DiscordVitals.TrackedEffect effect in gained)
            {
                string line = GainedLine(context, effect, byId.GetValueOrDefault(effect.Id));
                foreach (RouteState route in targets)
                    _ = SendTextAsync(route, line);
            }

            foreach (DiscordVitals.TrackedEffect effect in lost)
            {
                string line = Format("effect_wore_off", EffectDisplayName(context, effect.Id));
                foreach (RouteState route in targets)
                    _ = SendTextAsync(route, line);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogDebug(ex, "{Message}", Text("effects_sync_failed"));
        }
    }

    private string GainedLine(PluginContext context, DiscordVitals.TrackedEffect effect, EffectSnapshot? snapshot)
    {
        string name = EffectDisplayName(context, effect.Id);
        if (effect.Level > 0)
            name += " " + DiscordVitals.ToRoman(effect.Level);

        int seconds = snapshot is null || snapshot.IsInfinite
            ? -1
            : (Math.Max(0, snapshot.Duration) + 19) / 20;
        return DiscordVitals.EffectEmoji(effect.Id) + " "
            + Format("effect_gained", name, DiscordVitals.FormatDuration(seconds));
    }

    private void OnDied()
    {
        // Death clears and resends the effect table; none of that is news either.
        lock (_gate)
        {
            _knownEffects.Clear();
            _quietEffectsUntil = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        }

        // Fire and forget: the bus handler must not block, and a missed death beat is just a missed post.
        _ = OnDiedAsync();
    }

    private async Task OnDiedAsync()
    {
        if (!_settings.DeathEmbed)
            return;

        PluginContext? context = _context;
        if (context is null || !context.InSession || !IsConnected)
            return;

        try
        {
            PlayerStatus status = await context.Game.Player.GetStatusAsync().ConfigureAwait(false);
            DiscordCard card = DiscordVitals.Death(
                Text("death_title"), status.Position.X, status.Position.Y, status.Position.Z);

            foreach (RouteState route in SnapshotRoutes().Where(r => r.Route.RelayOut))
                _ = SendCardAsync(route, card);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogDebug(ex, "{Message}", Text("death_read_failed"));
        }
    }

    private string MissingTokenText()
        => _settings.TokenEnvVar is { } name
            ? Format("token_env_missing", name)
            : Text("missing_token");

    private void OnChatMessage(ChatMessageReceived message)
    {
        if (_context is not { } context || !IsConnected || _client is null)
            return;

        ClassifiedChat classified = context.Chat.Classify(message);
        RelayedMessage? relayed = DiscordFormatting.Render(
            classified, _formats, _settings.Relay_All_Messages, DateTime.Now);
        if (relayed is null || relayed.Text.Length == 0)
            return;

        string text = DiscordFormatting.ToDiscordMarkdown(relayed.Text);
        List<RouteState> targets;
        lock (_gate)
            targets = _routes.Where(r => r.Route.RelayOut).ToList();

        // Watched events go to the owners' DMs instead of the channels: a whisper stays private, and a watched teleport request arrives as plain text.
        // Listing teleport here drops its channel buttons.
        if (BridgeRoutes.IsDmExclusive(classified.Kind, _settings.DmOwnersOn))
        {
            NotifyOwners(classified.Kind, text);
            return;
        }

        if (relayed.IsTeleportRequest)
        {
            // Teleport requests bypass aggregation: they carry the accept/deny buttons.
            foreach (RouteState route in targets)
                _ = SendTeleportRequestAsync(route, text);

            RelayToDmOwners(text);
            return;
        }

        if (_settings.Message_Aggregation_Interval > 0)
        {
            _aggregationBuffer.Enqueue(text);
            while (_aggregationBuffer.Count > MaxAggregationBuffer
                && _aggregationBuffer.TryDequeue(out _))
                Interlocked.Increment(ref _droppedLines);
        }
        else
        {
            foreach (RouteState route in targets)
                _ = SendTextAsync(route, text);

            RelayToDmOwners(text);
        }
    }

    /// <summary>Mirrors a line to every owner DM when the messages toggle is on. Text only.</summary>
    private void RelayToDmOwners(string text)
    {
        if (!_settings.DmRelayOut || _settings.OwnersIds.Length == 0 || string.IsNullOrEmpty(text))
            return;

        foreach (ulong owner in _settings.OwnersIds)
            _ = SendDmAsync(owner, text);
    }

    private void NotifyOwners(ChatKind kind, string text)
    {
        bool whisper = kind == ChatKind.PrivateMessage
            && _settings.DmOwnersOn.Contains(DiscordBridgeSettings.WatchWhisper, StringComparer.Ordinal);
        bool teleport = kind == ChatKind.TeleportRequest
            && _settings.DmOwnersOn.Contains(DiscordBridgeSettings.WatchTeleport, StringComparer.Ordinal);
        if (!whisper && !teleport)
            return;

        foreach (ulong owner in _settings.OwnersIds)
            _ = SendDmAsync(owner, text);
    }

    private void FlushAggregationBuffer()
    {
        if (_aggregationBuffer.IsEmpty || !IsConnected || _client is null)
            return;

        var lines = new List<string>();
        while (_aggregationBuffer.TryDequeue(out string? line))
            lines.Add(line);

        int dropped = Interlocked.Exchange(ref _droppedLines, 0);
        if (dropped > 0)
            _logger?.LogWarning("{Message}", Format("dropped_lines", dropped));

        List<RouteState> targets;
        lock (_gate)
            targets = _routes.Where(r => r.Route.RelayOut).ToList();

        foreach (string message in DiscordFormatting.PackAggregated(lines))
        {
            foreach (RouteState route in targets)
                _ = SendTextAsync(route, message);

            RelayToDmOwners(message);
        }
    }

    // Discord takes text, never null, and the table is only null before activation.
    internal string Text(string key) => _strings?.Get(key) ?? key;

    internal string Format(string key, params object?[] args) => _strings?.Format(key, args) ?? key;

    private async Task ConnectAsync()
    {
        try
        {
            var client = new DiscordClient(new DiscordConfiguration
            {
                Token = _settings.ResolvedToken.Trim(),
                TokenType = TokenType.Bot,
                AutoReconnect = true,
                // Guilds for the channels, messages for the relay, DMs for owner directs, and the message body itself.
                // That last one is privileged: it must also be flipped on in the portal, or inbound arrives with empty content and the bridge looks deaf.
                Intents = DiscordIntents.Guilds
                    | DiscordIntents.GuildMessages
                    | DiscordIntents.DirectMessages
                    | DiscordIntents.MessageContents,
                MinimumLogLevel = LogLevel.None,
            });

            client.Ready += (_, _) =>
            {
                _selfName = client.CurrentUser?.Username?.Trim() is { Length: > 0 } name ? name : "?";
                return Task.CompletedTask;
            };
            client.MessageCreated += OnDiscordMessageAsync;
            client.ComponentInteractionCreated += OnComponentInteractionAsync;

            await client.ConnectAsync().ConfigureAwait(false);

            lock (_gate)
                _client = client;

            IsConnected = true;
            _logger?.LogInformation("{Message}", _strings?.Get("connected"));

            foreach (RouteState route in SnapshotRoutes())
            {
                if (route.Route.RelayOut)
                    _ = SendEmbedAsync(route, Text("connected_notice"), 0x00FF00);
            }

            FlushPendingImages();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            IsConnected = false;
            _logger?.LogWarning(ex, "{Message}", _strings?.Format("unknown_error", ex.Message));
        }
    }

    private List<RouteState> SnapshotRoutes()
    {
        lock (_gate)
            return [.. _routes];
    }

    private void RebuildRoutes()
    {
        IReadOnlyList<EffectiveRoute> effective = BridgeRoutes.Resolve(
            _settings.Routes, _settings.GuildId, _settings.ChannelId,
            _settings.OwnersIds, _settings.CommandPrefix);

        lock (_gate)
        {
            var keep = _routes.ToDictionary(r => r.Route.Name, StringComparer.OrdinalIgnoreCase);
            _routes.Clear();
            foreach (EffectiveRoute route in effective)
            {
                if (keep.TryGetValue(route.Name, out RouteState? state))
                {
                    state.Route = route;
                    _routes.Add(state);
                }
                else
                    _routes.Add(new RouteState(route));
            }
        }
    }

    private void Persist()
    {
        _settings.Validate();
        RebuildRoutes();
        _context?.Settings.Save(_settings);
    }

    private async Task<DiscordChannel?> ResolveRouteChannelAsync(RouteState route)
    {
        DiscordClient? client;
        lock (_gate)
            client = _client;

        if (client is null)
            return null;

        lock (_gate)
        {
            if (route.Channel is not null)
                return route.Channel;

            if (DateTime.UtcNow - route.LastResolveAttempt < ResolveRetryInterval)
                return null;

            route.LastResolveAttempt = DateTime.UtcNow;
        }

        try
        {
            DiscordChannel channel = await client.GetChannelAsync(route.Route.ChannelId).ConfigureAwait(false);
            lock (_gate)
            {
                route.Channel = channel;
                route.LastError = string.Empty;
            }

            return channel;
        }
        catch (NotFoundException)
        {
            string error = Format("channel_not_found", route.Route.Name, route.Route.ChannelId);
            _logger?.LogWarning("{Message}", error);
            lock (_gate)
                route.LastError = error;

            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            string error = Format("channel_lookup_failed", route.Route.Name);
            _logger?.LogDebug(ex, "{Message}", error);
            lock (_gate)
                route.LastError = error;

            return null;
        }
    }

    private async Task DisconnectAsync()
    {
        DiscordClient? client;
        lock (_gate)
        {
            client = _client;
            _client = null;
        }

        if (client is null)
            return;

        try
        {
            foreach (RouteState route in SnapshotRoutes())
            {
                if (route.Route.RelayOut)
                    await SendEmbedAsync(route, Text("disconnected_notice"), 0xFF0000, client).ConfigureAwait(false);
            }

            await client.DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogDebug(ex, "{Message}", _strings?.Get("canceled_sending"));
        }
        finally
        {
            client.Dispose();
            IsConnected = false;
        }
    }

    private CancellationTokenSource NewSendTimeout()
        => new(TimeSpan.FromSeconds(_settings.Message_Send_Timeout));

    private void MarkSend(RouteState route, bool ok, string error)
    {
        lock (_gate)
        {
            route.LastSendAt = DateTime.UtcNow;
            route.LastSendOk = ok;
            route.LastError = error;
        }
    }

    private void NoteSendFailed(RouteState route, Exception ex)
    {
        MarkSend(route, false, ex.Message);
        _logger?.LogWarning(ex, "{Message}", Format("send_failed", route.Route.Name, ex.Message));
    }

    private async Task SendTextAsync(RouteState route, string message)
    {
        if (string.IsNullOrEmpty(message))
            return;

        DiscordClient? client;
        lock (_gate)
            client = _client;

        DiscordChannel? channel = await ResolveRouteChannelAsync(route).ConfigureAwait(false);
        if (client is null || channel is null)
            return;

        try
        {
            using CancellationTokenSource timeout = NewSendTimeout();
            await client.SendMessageAsync(channel, message).WaitAsync(timeout.Token).ConfigureAwait(false);
            MarkSend(route, true, string.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            NoteSendFailed(route, ex);
        }
    }

    /// <summary>Sends text and reports back, for the test command. Blocks up to the send timeout.</summary>
    internal bool TrySendText(EffectiveRoute route, string message, out string error)
    {
        error = string.Empty;
        RouteState? state = SnapshotRoutes().FirstOrDefault(r =>
            string.Equals(r.Route.Name, route.Name, StringComparison.OrdinalIgnoreCase));
        if (state is null)
        {
            error = Format("route_missing", route.Name);
            return false;
        }

        try
        {
            DiscordChannel? channel = ResolveRouteChannelAsync(state).GetAwaiter().GetResult();
            DiscordClient? client;
            lock (_gate)
                client = _client;

            if (client is null || channel is null)
            {
                error = Text("bridge_offline");
                return false;
            }

            using CancellationTokenSource timeout = NewSendTimeout();
            client.SendMessageAsync(channel, message).WaitAsync(timeout.Token).GetAwaiter().GetResult();
            MarkSend(state, true, string.Empty);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            NoteSendFailed(state, ex);
            error = ex.Message;
            return false;
        }
    }

    private async Task SendEmbedAsync(
        RouteState route, string description, int color, DiscordClient? explicitClient = null)
        => await SendCardAsync(route, new DiscordCard(string.Empty, description, color), explicitClient)
            .ConfigureAwait(false);

    private async Task SendCardAsync(
        RouteState route, DiscordCard card, DiscordClient? explicitClient = null)
    {
        DiscordClient? client = explicitClient;
        if (client is null)
        {
            lock (_gate)
                client = _client;
        }

        DiscordChannel? channel = await ResolveRouteChannelAsync(route).ConfigureAwait(false);
        if (client is null || channel is null)
            return;

        try
        {
            var builder = new DiscordEmbedBuilder
            {
                Description = card.Body,
                Color = new DiscordColor(card.Color),
            };
            if (!string.IsNullOrEmpty(card.Title))
                builder.Title = card.Title;

            using CancellationTokenSource timeout = NewSendTimeout();
            await client
                .SendMessageAsync(channel, builder)
                .WaitAsync(timeout.Token)
                .ConfigureAwait(false);
            MarkSend(route, true, string.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            NoteSendFailed(route, ex);
        }
    }

    private async Task SendTeleportRequestAsync(RouteState route, string description)
    {
        DiscordChannel? channel = await ResolveRouteChannelAsync(route).ConfigureAwait(false);
        DiscordClient? client;
        lock (_gate)
            client = _client;

        if (client is null || channel is null)
            return;

        try
        {
            var builder = new DiscordMessageBuilder()
                .WithEmbed(new DiscordEmbedBuilder
                {
                    Description = description,
                    Color = new DiscordColor(0x3399FF),
                })
                .AddComponents(
                    new DiscordButtonComponent(ButtonStyle.Success, AcceptTeleportId, Text("accept")),
                    new DiscordButtonComponent(ButtonStyle.Danger, DenyTeleportId, Text("deny")));

            using CancellationTokenSource timeout = NewSendTimeout();
            DiscordMessage posted = await client
                .SendMessageAsync(channel, builder)
                .WaitAsync(timeout.Token)
                .ConfigureAwait(false);
            MarkSend(route, true, string.Empty);

            _teleportPosts[posted.Id] = DateTime.UtcNow;
            DateTime cutoff = DateTime.UtcNow - TeleportPostRetention;
            foreach (var entry in _teleportPosts.Where(e => e.Value < cutoff).ToList())
                _teleportPosts.TryRemove(entry.Key, out _);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            NoteSendFailed(route, ex);
        }
    }

    private void OnBridgeMessage(string[] message)
    {
        if (!TryReadImageRequest(message, out string path, out string caption, out string destination))
        {
            if (TryReadTextRequest(message, out string text, out string textDestination))
            {
                if (!IsConnected || _client is null)
                {
                    // The gateway is not up yet; hold the request until it is.
                    _pendingTexts.Enqueue((text, textDestination));
                    return;
                }

                _ = SendTextToTargetsAsync(text, textDestination);
            }

            return;
        }

        if (!IsConnected || _client is null)
        {
            // The gateway is not up yet; hold the request until it is.
            _pendingImages.Enqueue((path, caption, destination));
            return;
        }

        _ = SendImageAsync(path, caption, destination);
    }

    /// <summary>Reads the Map plugin's tagged image-request shape.</summary>
    public static bool TryReadImageRequest(string[]? message, out string filePath, out string caption)
        => TryReadImageRequest(message, out filePath, out caption, out _);

    /// <summary>
    /// Reads the Map plugin's tagged image-request shape with its destination.
    /// Four-element messages predate destinations and read back empty, which means the default fan-out.
    /// </summary>
    public static bool TryReadImageRequest(
        string[]? message, out string filePath, out string caption, out string destination)
    {
        filePath = string.Empty;
        caption = string.Empty;
        destination = string.Empty;
        if (message is not { Length: 4 } and not { Length: 5 }
            || !string.Equals(message[0], "mcc.plugins.bridge.image.v1", StringComparison.Ordinal)
            || !string.Equals(message[1], "discord", StringComparison.Ordinal))
            return false;

        filePath = message[2];
        caption = message[3];
        destination = message.Length > 4 ? message[4] : string.Empty;
        return true;
    }

    private void FlushPendingImages()
    {
        while (IsConnected && _client is not null
            && _pendingImages.TryDequeue(out (string Path, string Caption, string Destination) pending))
            _ = SendImageAsync(pending.Path, pending.Caption, pending.Destination);

        while (IsConnected && _client is not null
            && _pendingTexts.TryDequeue(out (string Text, string Destination) pending))
            _ = SendTextToTargetsAsync(pending.Text, pending.Destination);
    }

    /// <summary>
    /// Reads the Alerts plugin's tagged text-request shape: [tag, target, text, destination].
    /// Alerts are never command answers, so an empty destination means the broadcast fan-out, never reply routing.
    /// </summary>
    public static bool TryReadTextRequest(string[]? message, out string text, out string destination)
    {
        text = string.Empty;
        destination = string.Empty;
        if (message is not { Length: 4 }
            || !string.Equals(message[0], "mcc.plugins.bridge.text.v1", StringComparison.Ordinal)
            || !string.Equals(message[1], "discord", StringComparison.Ordinal))
            return false;

        text = message[2];
        destination = message[3];
        return true;
    }

    private async Task SendTextToTargetsAsync(string text, string destination)
    {
        if (string.IsNullOrEmpty(text))
            return;

        BridgeRoutes.ImageTargets targets = BridgeRoutes.ParseImageTargets(destination);
        if (targets.Routes.Count == 0 && targets.Dms.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(destination))
            {
                // Explicit but broken: say so in the log instead of spraying every channel.
                _logger?.LogWarning("{Message}", Format("image_bad_target", destination.Trim()));
                return;
            }

            foreach (RouteState route in SnapshotRoutes().Where(r => r.Route.RelayOut))
                _ = SendTextAsync(route, text);

            return;
        }

        // Explicit targets bypass the RelayOut switches: naming a route is the opt-in.
        List<RouteState> routes = SnapshotRoutes();
        foreach (string name in targets.Routes)
        {
            RouteState? state = routes.FirstOrDefault(r =>
                string.Equals(r.Route.Name, name, StringComparison.OrdinalIgnoreCase));
            if (state is null)
            {
                _logger?.LogWarning("{Message}", Format("image_bad_target", name));
                continue;
            }

            _ = SendTextAsync(state, text);
        }

        foreach (ulong dm in targets.Dms)
            _ = SendDmAsync(dm, text);
    }

    private async Task SendImageAsync(string filePath, string? caption, string destination)
    {
        if (!File.Exists(filePath))
            return;

        BridgeRoutes.ImageTargets targets = BridgeRoutes.ParseImageTargets(destination);
        if (targets.Routes.Count == 0 && targets.Dms.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(destination))
            {
                // Explicit but broken: say so in the log instead of spraying every channel.
                _logger?.LogWarning("{Message}", Format("image_bad_target", destination.Trim()));
                return;
            }

            // No destination: a fresh Discord command wins over the broadcast, so a render asked from a DM lands back in that DM.
            // The last asker wins when two race.
            if (TryTakeReplySource(out ImageSource? reply) && reply is not null)
            {
                if (reply.GuildId is null && reply.AuthorId is { } dmAuthor)
                {
                    _ = SendDmFileAsync(dmAuthor, filePath, caption);
                    return;
                }

                RouteState? home = reply.GuildId is null
                    ? null
                    : FindRoute(reply.GuildId, reply.ChannelId);
                if (home is not null)
                {
                    _ = SendImageToRouteAsync(home, filePath, caption);
                    return;
                }
            }

            foreach (RouteState route in SnapshotRoutes().Where(r => r.Route.RelayOut))
                _ = SendImageToRouteAsync(route, filePath, caption);

            return;
        }

        // Explicit targets bypass the RelayOut switches: naming a route is the opt-in.
        List<RouteState> routes = SnapshotRoutes();
        foreach (string name in targets.Routes)
        {
            RouteState? state = routes.FirstOrDefault(r =>
                string.Equals(r.Route.Name, name, StringComparison.OrdinalIgnoreCase));
            if (state is null)
            {
                _logger?.LogWarning("{Message}", Format("image_bad_target", name));
                continue;
            }

            _ = SendImageToRouteAsync(state, filePath, caption);
        }

        foreach (ulong dm in targets.Dms)
            _ = SendDmFileAsync(dm, filePath, caption);
    }

    private async Task SendImageToRouteAsync(RouteState route, string filePath, string? caption)
    {
        if (!File.Exists(filePath))
            return;

        DiscordChannel? channel = await ResolveRouteChannelAsync(route).ConfigureAwait(false);
        DiscordClient? client;
        lock (_gate)
            client = _client;

        if (client is null || channel is null)
            return;

        try
        {
            await using FileStream stream = File.OpenRead(filePath);
            var builder = new DiscordMessageBuilder();
            if (!string.IsNullOrEmpty(caption))
                builder.WithContent(caption);

            builder.AddFile(Path.GetFileName(filePath), stream);

            using CancellationTokenSource timeout = NewSendTimeout();
            await client.SendMessageAsync(channel, builder).WaitAsync(timeout.Token).ConfigureAwait(false);
            MarkSend(route, true, string.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            NoteSendFailed(route, ex);
        }
    }

    /// <summary>Sends a DM, for the dm command and the owner watchers. True when Discord took it.</summary>
    internal async Task<(bool Ok, string Error)> SendDmAsync(ulong ownerId, string text)
    {
        DiscordClient? client;
        lock (_gate)
            client = _client;

        if (client is null || !IsConnected)
            return (false, Text("bridge_offline"));

        try
        {
            using CancellationTokenSource timeout = NewSendTimeout();
            DiscordDmChannel? dm = await GetDmChannelAsync(client, ownerId, timeout.Token).ConfigureAwait(false);
            if (dm is null)
                return (false, Format("dm_no_shared_server", ownerId));

            await client.SendMessageAsync(dm, text).WaitAsync(timeout.Token).ConfigureAwait(false);
            return (true, string.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _dmChannels.TryRemove(ownerId, out _);
            _logger?.LogWarning(ex, "{Message}", Format("send_failed", Text("dm_word"), ex.Message));
            return (false, ex.Message);
        }
    }

    /// <summary>Sends a file to an owner DM. Fire and forget; failures go to the log.</summary>
    private async Task SendDmFileAsync(ulong ownerId, string filePath, string? caption)
    {
        DiscordClient? client;
        lock (_gate)
            client = _client;

        if (client is null || !IsConnected || !File.Exists(filePath))
            return;

        try
        {
            using CancellationTokenSource timeout = NewSendTimeout();
            DiscordDmChannel? dm = await GetDmChannelAsync(client, ownerId, timeout.Token).ConfigureAwait(false);
            if (dm is null)
            {
                _logger?.LogWarning("{Message}", Format("dm_no_shared_server", ownerId));
                return;
            }

            await using FileStream stream = File.OpenRead(filePath);
            var builder = new DiscordMessageBuilder();
            if (!string.IsNullOrEmpty(caption))
                builder.WithContent(caption);

            builder.AddFile(Path.GetFileName(filePath), stream);
            await client.SendMessageAsync(dm, builder).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _dmChannels.TryRemove(ownerId, out _);
            _logger?.LogWarning(ex, "{Message}", Format("send_failed", Text("dm_word"), ex.Message));
        }
    }

    /// <summary>Opens a DM channel from the cache or through a shared guild. Null when impossible.</summary>
    private async Task<DiscordDmChannel?> GetDmChannelAsync(
        DiscordClient client, ulong ownerId, CancellationToken ct)
    {
        if (_dmChannels.TryGetValue(ownerId, out DiscordDmChannel? dm) && dm is not null)
            return dm;

        dm = await OpenDmChannelAsync(client, ownerId, ct).ConfigureAwait(false);
        if (dm is null)
            return null;

        _dmChannels[ownerId] = dm;
        return dm;
    }

    /// <summary>
    /// Opens a DM the way Discord allows a bot to: through a shared guild.
    /// Owners who messaged the bot first skip this entirely, because their channel is already cached from the inbound message.
    /// </summary>
    private async Task<DiscordDmChannel?> OpenDmChannelAsync(
        DiscordClient client, ulong ownerId, CancellationToken ct)
    {
        foreach (ulong guildId in SnapshotRoutes()
            .Select(r => r.Route.GuildId)
            .Where(id => id != 0)
            .Distinct())
        {
            try
            {
                DiscordGuild guild = await client.GetGuildAsync(guildId).WaitAsync(ct).ConfigureAwait(false);
                DiscordMember member = await guild.GetMemberAsync(ownerId).WaitAsync(ct).ConfigureAwait(false);
                return await member.CreateDmChannelAsync().WaitAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is NotFoundException
                or OperationCanceledException
                or TimeoutException)
            {
                // Not in this guild (or too slow); try the next one.
                _logger?.LogDebug(ex, "{Message}", Format("dm_no_shared_server", ownerId));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger?.LogDebug(ex, "{Message}", Format("send_failed", Text("dm_word"), ex.Message));
                return null;
            }
        }

        return null;
    }

    private async Task OnDiscordMessageAsync(DiscordClient sender, DSharpPlus.EventArgs.MessageCreateEventArgs e)
    {
        if (_context is not { } context || e.Author.Id == sender.CurrentUser.Id)
            return; // Always ignore our own messages, or this loops.

        string message = e.Message.Content.Trim();
        if (message.Length == 0)
            return;

        // Other bots may be relayed when configured, but never run commands.
        if (e.Author.IsBot)
        {
            if (_settings.Allow_Other_Bot_Messages
                && context.InSession
                && FindRoute(e.Guild?.Id, e.Channel.Id) is { Route.RelayIn: true })
                await context.Game.Chat.SendAsync(message).ConfigureAwait(false);

            return;
        }

        if (e.Guild is null)
        {
            await OnDirectMessageAsync(context, sender, e, message).ConfigureAwait(false);
            return;
        }

        EffectiveRoute? route = FindRoute(e.Guild.Id, e.Channel.Id)?.Route;
        if (route is null)
            return;

        if (!route.Owners.Contains(e.Author.Id))
            return;

        if (BridgeRoutes.TryStripPrefix(message, route.Prefix, out string command))
        {
            await RunCommandAsync(context, e.Message, command, route.AllowCommands).ConfigureAwait(false);
            return;
        }

        if (route.RelayIn && context.InSession)
            await context.Game.Chat.SendAsync(message).ConfigureAwait(false);
    }

    private async Task OnDirectMessageAsync(
        PluginContext context, DiscordClient sender, DSharpPlus.EventArgs.MessageCreateEventArgs e, string message)
    {
        if (!_settings.AllowDirectMessages || !_settings.OwnersIds.Contains(e.Author.Id))
            return;

        // Remember the channel: sending back needs no guild lookup afterwards.
        if (e.Channel is DiscordDmChannel dm)
            _dmChannels[e.Author.Id] = dm;

        if (BridgeRoutes.TryStripPrefix(message, _settings.CommandPrefix, out string command))
        {
            await RunCommandAsync(context, e.Message, command, allowCommands: true).ConfigureAwait(false);
            return;
        }

        if (context.InSession)
            await context.Game.Chat.SendAsync(message).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one Discord-issued command and answers where it came from.
    /// Pretty renders first (man, health, effects), then the captured dispatch: the command prints exactly what the console would show, and the capture is the reply.
    /// Anything the capture misses falls back to the one-line result.
    /// </summary>
    private async Task RunCommandAsync(
        PluginContext context, DiscordMessage source, string command, bool allowCommands)
    {
        if (!allowCommands)
        {
            await source.RespondAsync(Text("commands_off_here")).ConfigureAwait(false);
            return;
        }

        // Remember the asker: a map render triggered from here answers back here.
        lock (_gate)
        {
            _lastCommandSource = new ImageSource(
                DateTime.UtcNow, source.Channel?.GuildId, source.ChannelId, source.Author?.Id);
        }

        if (TryBuildManualReply(command, out string? page) && page is not null)
        {
            await SendMarkdownAsync(source, page).ConfigureAwait(false);
            return;
        }

        string[] parts = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            if (string.Equals(parts[0], "health", StringComparison.Ordinal)
                && await TryReplyVitalsAsync(context, source).ConfigureAwait(false))
                return;

            if (string.Equals(parts[0], "effects", StringComparison.Ordinal)
                && await TryReplyEffectsAsync(context, source).ConfigureAwait(false))
                return;
        }

        (CmdResult result, string captured) =
            await context.Client.Commands.DispatchCapturedAsync(command).ConfigureAwait(false);
        string body = DiscordFormatting.ReplyBody(captured, result.Message ?? result.Status.ToString());
        if (body.Length == 0)
            body = result.Status.ToString();

        await SendCodeBlocksAsync(source, body).ConfigureAwait(false);
    }

    private static async Task SendCodeBlocksAsync(DiscordMessage source, string text)
    {
        bool first = true;
        foreach (string chunk in DiscordFormatting.Chunk(text))
        {
            string fenced = "```\n" + chunk + "\n```";
            if (first)
            {
                await source.RespondAsync(fenced).ConfigureAwait(false);
                first = false;
            }
            else
                await source.Channel.SendMessageAsync(fenced).ConfigureAwait(false);
        }
    }

    private static async Task ReplyCardAsync(DiscordMessage source, DiscordCard card)
    {
        var embed = new DiscordEmbedBuilder
        {
            Title = card.Title,
            Description = card.Body,
            Color = new DiscordColor(card.Color),
        };
        await source.RespondAsync(new DiscordMessageBuilder().AddEmbed(embed)).ConfigureAwait(false);
    }

    /// <summary>Answers health with the pretty card. False when there is nothing to read.</summary>
    private async Task<bool> TryReplyVitalsAsync(PluginContext context, DiscordMessage source)
    {
        if (!context.InSession)
            return false;

        try
        {
            PlayerStatus status = await context.Game.Player.GetStatusAsync().ConfigureAwait(false);
            var words = new VitalsWords(
                Text("vitals_title"), Text("vitals_hp"), Text("vitals_food"),
                Text("vitals_sat"), Text("vitals_level"), Text("vitals_xp"));
            DiscordCard card = DiscordVitals.Vitals(
                status.Health, 20f, status.Food, 20, status.Saturation,
                status.ExperienceLevel, status.TotalExperience, status.ExperienceProgress, words);
            await ReplyCardAsync(source, card).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogDebug(ex, "{Message}", Text("vitals_read_failed"));
            return false;
        }
    }

    /// <summary>Answers effects with the pretty card. False when there is nothing to read.</summary>
    private async Task<bool> TryReplyEffectsAsync(PluginContext context, DiscordMessage source)
    {
        if (!context.InSession)
            return false;

        if (_context?.CurrentSession?.Client.State.Features.Entities != true)
            return false;

        try
        {
            IReadOnlyList<EffectSnapshot> snapshots =
                await context.Game.Player.GetEffectsAsync().ConfigureAwait(false);
            var listed = snapshots
                .Where(e => e.IsInfinite || e.Duration > 0)
                .OrderBy(e => e.EffectId, StringComparer.Ordinal)
                .Select(e => new ListedEffect(
                    e.EffectId,
                    EffectDisplayName(context, e),
                    e.Amplifier <= 0 ? 0 : e.Level,
                    e.IsInfinite ? -1 : (Math.Max(0, e.Duration) + 19) / 20))
                .ToList();
            var words = new EffectsWords(Text("effects_title"), Text("effects_none"));
            await ReplyCardAsync(source, DiscordVitals.Effects(listed, words)).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogDebug(ex, "{Message}", Text("effects_read_failed"));
            return false;
        }
    }

    private static string EffectDisplayName(PluginContext context, EffectSnapshot effect)
        => EffectDisplayName(context, effect.EffectId ?? string.Empty);

    private static string EffectDisplayName(PluginContext context, string effectId)
    {
        int separator = effectId.IndexOf(':');
        string path = separator >= 0 ? effectId[(separator + 1)..] : effectId;

        return context.Translations.TryResolve("effect.minecraft." + path, out string? translated)
            && !string.IsNullOrEmpty(translated)
                ? translated
                : path;
    }

    private RouteState? FindRoute(ulong? guildId, ulong channelId)
        => SnapshotRoutes().FirstOrDefault(r =>
            r.Route.ChannelId == channelId && (guildId is null || r.Route.GuildId == guildId));

    /// <summary>Takes the last asker when it is still fresh (a minute). Never consumes: one render per ask.</summary>
    private bool TryTakeReplySource(out ImageSource? reply)
    {
        lock (_gate)
        {
            reply = _lastCommandSource;
            return reply is not null && DateTime.UtcNow - reply.At < TimeSpan.FromMinutes(1);
        }
    }

    /// <summary>
    /// Answers man from the embedded manual instead of the dispatcher: man renders to the host UI, so a dispatch only ever answers Done.
    /// Pages are already Markdown, so Discord renders them as-is.
    /// Returns false for anything that is not a man command.
    /// </summary>
    private bool TryBuildManualReply(string command, out string markdown)
    {
        markdown = string.Empty;
        string[] parts = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !string.Equals(parts[0], "man", StringComparison.Ordinal))
            return false;

        if (parts.Length == 1)
        {
            markdown = BuildManualIndex();
            return true;
        }

        if (string.Equals(parts[1], "-k", StringComparison.Ordinal))
        {
            int at = command.IndexOf("-k", StringComparison.Ordinal) + 2;
            string term = command[at..].Trim();
            markdown = SearchManual(term);
            return true;
        }

        string topic = parts[1];
        markdown = _context!.Client.Manuals.Read(topic, _context.Client.UiCulture) ?? Format("man_unknown", topic);
        return true;
    }

    private string BuildManualIndex()
    {
        var sb = new StringBuilder("**MCC manual**");
        foreach (ManualGroup group in ManualTopics.Groups)
        {
            List<ManualTopic> inGroup = [.. _context!.Client.Manuals.Topics().Where(t => t.Group == group)];
            if (inGroup.Count == 0)
                continue;

            sb.Append("\n\n").Append(ManualTopics.GroupName(group));
            foreach (ManualTopic topic in inGroup)
            {
                sb.Append('\n').Append(topic.Id).Append(" - ")
                    .Append(DiscordFormatting.StripCodes(topic.Summary));
            }
        }

        return sb.ToString();
    }

    private string SearchManual(string term)
    {
        if (term.Length == 0)
            return Format("man_none", term);

        List<string> hits = [.. _context!.Client.Manuals.Topics()
            .Where(t => t.Id.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (_context!.Client.Manuals.Read(t.Id, _context.Client.UiCulture)?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
            .Take(20)
            .Select(t => t.Id)];
        return hits.Count == 0 ? Format("man_none", term) : string.Join('\n', hits);
    }

    private static async Task SendMarkdownAsync(DiscordMessage source, string markdown)
    {
        bool first = true;
        foreach (string chunk in DiscordFormatting.Chunk(markdown))
        {
            if (first)
            {
                await source.RespondAsync(chunk).ConfigureAwait(false);
                first = false;
            }
            else
                await source.Channel.SendMessageAsync(chunk).ConfigureAwait(false);
        }
    }

    private async Task OnComponentInteractionAsync(
        DiscordClient sender, DSharpPlus.EventArgs.ComponentInteractionCreateEventArgs e)
    {
        bool accept = string.Equals(e.Id, AcceptTeleportId, StringComparison.Ordinal);
        if (!accept && !string.Equals(e.Id, DenyTeleportId, StringComparison.Ordinal))
            return;

        // Buttons are owner-only, same as typed commands.
        // The channel behind the click decides whose owners count; a DM has no channel, so the global list applies there.
        ulong[] owners = e.Channel is null
            ? _settings.OwnersIds
            : FindRoute(e.Channel.GuildId, e.Channel.Id)?.Route.Owners ?? _settings.OwnersIds;
        if (!owners.Contains(e.User.Id))
        {
            await e.Interaction
                .CreateResponseAsync(
                    InteractionResponseType.ChannelMessageWithSource,
                    new DiscordInteractionResponseBuilder().WithContent(Text("button_denied")).AsEphemeral())
                .ConfigureAwait(false);
            return;
        }

        if (e.Message is not null
            && (!_teleportPosts.TryGetValue(e.Message.Id, out DateTime posted)
                || DateTime.UtcNow - posted > TeleportButtonLifetime))
        {
            // Unknown posts are old posts from before a restart, so they count as expired too.
            await e.Interaction
                .CreateResponseAsync(
                    InteractionResponseType.UpdateMessage,
                    new DiscordInteractionResponseBuilder().WithContent(Text("button_expired")))
                .ConfigureAwait(false);
            return;
        }

        if (_context is { InSession: true } context)
            await context.Game.Chat.SendAsync(accept ? "/tpaccept" : "/tpdeny").ConfigureAwait(false);

        await e.Interaction
            .CreateResponseAsync(
                InteractionResponseType.UpdateMessage,
                new DiscordInteractionResponseBuilder().WithContent(
                    accept ? Text("accepted") : Text("denied")))
            .ConfigureAwait(false);
    }

    internal string SelfName => _selfName;

    #region Runtime commands

    internal int ShowStatus(CommandContext ctx)
    {
        if (!_settings.Enabled)
            return ctx.Result.Fail(Text("disabled_short"));

        var lines = new List<string>
        {
            IsConnected ? Format("status_gateway_on", _selfName) : Text("status_gateway_off"),
            _context is { InSession: true } ? Text("status_session_up") : Text("status_session_down"),
            Format("status_dms",
                _settings.AllowDirectMessages ? Text("flag_on") : Text("flag_off"),
                _settings.DmRelayOut ? Text("flag_on") : Text("flag_off")),
        };

        lines.AddRange(SnapshotRoutes().Select(RouteStatusLine));
        return ctx.Result.Ok(string.Join('\n', lines));
    }

    internal string RouteStatusLine(RouteState state)
    {
        string last = state.LastSendAt is null
            ? Text("last_never")
            : state.LastSendOk
                ? Text("last_ok")
                : Format("last_failed", state.LastError);
        return Format("status_route", state.Route.Name, ModeLabel(state.Route), last);
    }

    internal string ModeLabel(EffectiveRoute route)
    {
        var parts = new List<string>();
        if (route.RelayOut)
            parts.Add(Text("mode_out"));

        if (route.RelayIn)
            parts.Add(Text("mode_in"));

        if (route.AllowCommands)
            parts.Add(Text("mode_cmd"));

        return parts.Count == 0 ? Text("mode_none") : string.Join('+', parts);
    }

    internal int ListChannels(CommandContext ctx)
    {
        if (!_settings.Enabled)
            return ctx.Result.Fail(Text("disabled_short"));

        List<RouteState> routes = SnapshotRoutes();
        if (routes.Count == 0)
            return ctx.Result.Ok(Text("channels_empty"));

        var lines = routes.Select(state =>
        {
            EffectiveRoute route = state.Route;
            string owners = route.Owners.Length == 0
                ? Text("owners_inherited_word")
                : Format("owners_n", route.Owners.Length);
            return Format("channels_row", route.Name, route.GuildId, route.ChannelId,
                ModeLabel(route), route.Prefix, owners);
        });
        return ctx.Result.Ok(string.Join('\n', lines));
    }

    internal int ChannelAdd(CommandContext ctx, string name, string guildText, string channelText)
    {
        if (!_settings.Enabled)
            return ctx.Result.Fail(Text("disabled_short"));

        name = (name ?? string.Empty).Trim();
        if (name.Length == 0)
            return ctx.Result.Fail(Text("bad_name"));

        if (!BridgeRoutes.TryParseId(guildText, out ulong guildId)
            || !BridgeRoutes.TryParseId(channelText, out ulong channelId))
            return ctx.Result.Fail(Format("bad_id", $"{guildText} {channelText}".Trim()));

        if (_settings.Routes.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
            return ctx.Result.Fail(Format("route_exists", name));

        // New channels start safe: chat flows, commands stay off until someone says so.
        _settings.Routes.Add(new RouteConfig
        {
            Name = name,
            GuildId = guildId,
            ChannelId = channelId,
            RelayOut = true,
            RelayIn = true,
            AllowCommands = false,
        });
        Persist();
        return ctx.Result.Ok(Format("route_added", name));
    }

    internal int ChannelRemove(CommandContext ctx, string name)
    {
        if (!_settings.Enabled)
            return ctx.Result.Fail(Text("disabled_short"));

        int removed = _settings.Routes.RemoveAll(r =>
            string.Equals(r.Name, (name ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
            return ctx.Result.Fail(Format("route_missing", (name ?? string.Empty).Trim()));

        Persist();
        return ctx.Result.Ok(Format("route_removed", (name ?? string.Empty).Trim()));
    }

    internal int ChannelSetFlag(CommandContext ctx, string name, string flag, string value)
    {
        if (!_settings.Enabled)
            return ctx.Result.Fail(Text("disabled_short"));

        RouteConfig? route = _settings.Routes.FirstOrDefault(r =>
            string.Equals(r.Name, (name ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
        if (route is null)
            return ctx.Result.Fail(Format("route_missing", (name ?? string.Empty).Trim()));

        if (!BridgeRoutes.TryParseSwitch(value, out bool on))
            return ctx.Result.Fail(Text("bad_value"));

        switch ((flag ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "out":
                route.RelayOut = on;
                break;
            case "in":
                route.RelayIn = on;
                break;
            case "cmd":
                route.AllowCommands = on;
                break;
            default:
                return ctx.Result.Fail(Text("bad_flag"));
        }

        Persist();
        string flagName = (flag ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "out" => Text("flag_out"),
            "in" => Text("flag_in"),
            _ => Text("flag_cmd"),
        };
        return ctx.Result.Ok(Format("flag_updated", route.Name, flagName,
            on ? Text("flag_on") : Text("flag_off")));
    }

    internal int ChannelSetOwners(CommandContext ctx, string name, string ids, bool inherit)
    {
        if (!_settings.Enabled)
            return ctx.Result.Fail(Text("disabled_short"));

        RouteConfig? route = _settings.Routes.FirstOrDefault(r =>
            string.Equals(r.Name, (name ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
        if (route is null)
            return ctx.Result.Fail(Format("route_missing", (name ?? string.Empty).Trim()));

        if (inherit)
        {
            route.OwnersIds = [];
            Persist();
            return ctx.Result.Ok(Format("owners_inherited", route.Name));
        }

        ulong[] parsed = BridgeRoutes.ParseIds(ids);
        if (parsed.Length == 0)
            return ctx.Result.Fail(Format("bad_id", (ids ?? string.Empty).Trim()));

        route.OwnersIds = parsed;
        Persist();
        return ctx.Result.Ok(Format("owners_set", route.Name));
    }

    internal int ChannelSetPrefix(CommandContext ctx, string name, string prefix, bool useDefault)
    {
        if (!_settings.Enabled)
            return ctx.Result.Fail(Text("disabled_short"));

        RouteConfig? route = _settings.Routes.FirstOrDefault(r =>
            string.Equals(r.Name, (name ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
        if (route is null)
            return ctx.Result.Fail(Format("route_missing", (name ?? string.Empty).Trim()));

        if (useDefault)
        {
            route.CommandPrefix = string.Empty;
            Persist();
            return ctx.Result.Ok(Format("prefix_defaulted", route.Name));
        }

        string normalized = BridgeRoutes.NormalizePrefix(prefix, "\0");
        if (normalized == "\0")
            return ctx.Result.Fail(Text("bad_prefix"));

        route.CommandPrefix = normalized;
        Persist();
        return ctx.Result.Ok(Format("prefix_set", route.Name, normalized));
    }

    internal int ChannelTest(CommandContext ctx, string name)
    {
        if (!_settings.Enabled)
            return ctx.Result.Fail(Text("disabled_short"));

        RouteState? state = SnapshotRoutes().FirstOrDefault(r =>
            string.Equals(r.Route.Name, (name ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
        if (state is null)
            return ctx.Result.Fail(Format("route_missing", (name ?? string.Empty).Trim()));

        if (TrySendText(state.Route, Format("test_probe", state.Route.Name), out string error))
            return ctx.Result.Ok(Format("test_sent", state.Route.Name));

        return ctx.Result.Fail(Format("test_failed", string.IsNullOrEmpty(error) ? Text("bridge_offline") : error));
    }

    internal int SetDirectMessages(CommandContext ctx, bool on)
    {
        if (!_settings.Enabled)
            return ctx.Result.Fail(Text("disabled_short"));

        _settings.AllowDirectMessages = on;
        Persist();
        return ctx.Result.Ok(on ? Text("dm_on") : Text("dm_off"));
    }

    internal int SetDmMessages(CommandContext ctx, bool on)
    {
        if (!_settings.Enabled)
            return ctx.Result.Fail(Text("disabled_short"));

        _settings.DmRelayOut = on;
        Persist();
        return ctx.Result.Ok(on ? Text("dm_messages_on") : Text("dm_messages_off"));
    }

    internal int SendDirectMessage(CommandContext ctx, string ownerText, string text)
    {
        if (!_settings.Enabled)
            return ctx.Result.Fail(Text("disabled_short"));

        if (!BridgeRoutes.TryParseId(ownerText, out ulong ownerId))
            return ctx.Result.Fail(Format("bad_id", (ownerText ?? string.Empty).Trim()));

        if (string.IsNullOrWhiteSpace(text))
            return ctx.Result.Fail(Text("dm_empty"));

        try
        {
            (bool ok, string error) = SendDmAsync(ownerId, text.Trim())
                .WaitAsync(TimeSpan.FromSeconds(Math.Max(_settings.Message_Send_Timeout, 3)))
                .GetAwaiter()
                .GetResult();
            return ok
                ? ctx.Result.Ok(Text("dm_sent"))
                : ctx.Result.Fail(Format("dm_failed", string.IsNullOrEmpty(error) ? Text("bridge_offline") : error));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ctx.Result.Fail(Format("dm_failed", ex.Message));
        }
    }

    #endregion
}
