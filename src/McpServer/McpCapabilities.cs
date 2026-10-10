using DMCBK.Core;
using DMCBK.PluginSdk;
using Umpk.Client;

namespace DMCBK.OfficialPlugins.McpServer;

/// <summary>
/// Capability groups for the tool surface.
/// Each tool declares its group; a disabled group fails with <c>capability_disabled</c> instead of executing.
/// </summary>
public enum McpCapability
{
    /// <summary>Session/server/reference reads.</summary>
    SessionStatus,

    /// <summary>Chat sends, server commands, internal commands, respawn, disconnect.</summary>
    ChatAndCommands,

    /// <summary>Movement, look, dig, place, use, entity interaction.</summary>
    Movement,

    /// <summary>Inventory snapshots, search, containers, transfers.</summary>
    Inventory,

    /// <summary>Entity, block, item and sign reads.</summary>
    EntityWorld,
}

/// <summary>
/// The real tool implementations against the live MCC session.
/// Thin by design: every method checks the capability toggle, snapshots the session scope once (never the throwing <c>PluginContext.Session</c>), links the session-detached token with a bounded timeout, maps session exceptions to the envelope, and awaits the thread-safe <see cref="GameApi"/> facades from the ASP.NET thread pool (never the session loop, so no <c>Task.Run</c> hop is needed and none is used).
/// <para>
/// Split across files: this one holds session gating plus the read tools; <c>Movement</c> and <c>Inventory</c> partials hold the action tools.
/// </para>
/// </summary>
public sealed partial class McpCapabilities
{
    private readonly PluginContext _context;
    private readonly ObservedStateStore _store;
    private readonly McpGuidance _guidance;

    /// <summary>Maximum time any single tool waits for a verifying action.</summary>
    public static readonly TimeSpan MaxToolWait = TimeSpan.FromSeconds(15);

    /// <summary>Commands that <c>mcc_run_internal_command</c> refuses: remote code execution or session pivot.</summary>
    /// <summary>Commands refused by mcc_run_internal_command. Public so the blocklist is pinned by tests.</summary>
    public static readonly HashSet<string> BlockedInternalCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "plugins", "plugin", "scripts", "script", "connect", "reco",
        "reload", "set", "exit", "quit", "stop", "send",
    };

    public McpCapabilities(PluginContext context, ObservedStateStore store, McpGuidance guidance)
    {
        _context = context;
        _store = store;
        _guidance = guidance;
    }

    internal bool IsEnabled(McpCapability capability)
    {
        McpServerSettings settings = CurrentSettings();
        return capability switch
        {
            McpCapability.SessionStatus => settings.CapSessionStatus,
            McpCapability.ChatAndCommands => settings.CapChatAndCommands,
            McpCapability.Movement => settings.CapMovement,
            McpCapability.Inventory => settings.CapInventory,
            McpCapability.EntityWorld => settings.CapEntityWorld,
            _ => false,
        };
    }

    private McpServerSettings CurrentSettings()
    {
        try
        {
            return _context.Settings.Load<McpServerSettings>();
        }
        catch (Exception)
        {
            return new McpServerSettings();
        }
    }

    private McpResult RequireCapability(McpCapability capability)
        => IsEnabled(capability)
            ? McpResult.Ok()
            : McpResult.Fail(
                McpErrorCodes.CapabilityDisabled,
                $"The {capability} capability is disabled in this plugin's settings.");

    /// <summary>
    /// Snapshots liveness once per call.
    /// Returns null with <paramref name="failure"/> set when no usable session is attached (offline, configuring, or reconnecting): tools report the envelope, never throw.
    /// </summary>
    private bool TryLive(out string? failureKind, out string? failureMessage)
    {
        failureKind = null;
        failureMessage = null;
        if (!_context.InSession)
        {
            failureKind = McpErrorCodes.Disconnected;
            failureMessage = "MCC is not connected to a server.";
            return false;
        }

        ClientStatus status = _context.Client.Status;
        switch (status)
        {
            case ClientStatus.Playing:
                return true;
            case ClientStatus.Configuring:
            case ClientStatus.Connecting:
            case ClientStatus.Authenticating:
            case ClientStatus.Reconnecting:
                failureKind = McpErrorCodes.Busy;
                failureMessage = $"The session is {status}; retry shortly.";
                return false;
            default:
                failureKind = McpErrorCodes.Disconnected;
                failureMessage = "MCC is not connected to a server.";
                return false;
        }
    }

    private static McpResult SessionFailure(string kind, string message)
        => McpResult.Fail(kind, message);

    /// <summary>Maps the session-layer exceptions to the envelope. Never lets a raw exception reach the MCP transport.</summary>
    private static McpResult MapSessionException(Exception ex)
        => ex switch
        {
            OperationCanceledException => McpResult.Fail(McpErrorCodes.Busy, "The tool was cancelled (session ended or timed out)."),
            DmcbkNotInSessionException => McpResult.Fail(McpErrorCodes.Disconnected, "MCC is not connected to a server."),
            DmcbkFeatureDisabledException fex => McpResult.Fail(
                McpErrorCodes.FeatureDisabled, $"The {fex.Feature} gameplay feature is disabled."),
            InvalidOperationException ioex => McpResult.Fail(McpErrorCodes.Disconnected, ioex.Message),
            ArgumentOutOfRangeException aex => McpResult.Fail(McpErrorCodes.InvalidArgs, aex.Message),
            ArgumentException aex => McpResult.Fail(McpErrorCodes.InvalidArgs, aex.Message),
            _ => McpResult.Fail(McpErrorCodes.ActionFailed, ex.Message),
        };

    private bool GameplayEnabled(Func<DMCBK.Core.Configuration.GameplayConfig, bool> pick)
    {
        var gameplay = _context.Client.Configuration?.Gameplay;
        return gameplay is null || pick(gameplay);
    }

    private async Task<string> UsernameAsync(CancellationToken ct)
    {
        ISessionScope? scope = _context.CurrentSession;
        if (scope is not null)
        {
            try
            {
                string name = await scope.Client
                    .InvokeAsync(client => client.State.Self.Username, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
            catch (Exception)
            {
            }
        }

        return _context.Client.Configuration?.ResolvedAccount?.Login ?? "unknown";
    }

    private static object Loc(double x, double y, double z)
        => new { x = Math.Round(x, 2), y = Math.Round(y, 2), z = Math.Round(z, 2) };

    private McpResult Guard(McpCapability capability)
    {
        McpResult gate = RequireCapability(capability);
        if (!gate.Success)
            return gate;

        if (!TryLive(out string? kind, out string? message))
            return SessionFailure(kind!, message!);

        return McpResult.Ok();
    }

    /// <summary>mcc_session_status: connection state, feature flags, and player location.</summary>
    public async Task<McpResult> GetSessionStatusAsync(CancellationToken ct)
    {
        McpResult gate = RequireCapability(McpCapability.SessionStatus);
        if (!gate.Success)
            return gate;

        bool live = TryLive(out _, out _);
        try
        {
            SessionInfoSnapshot? session = live
                ? await _context.Game.Session.GetInfoAsync(ct).ConfigureAwait(false)
                : null;
            PlayerStatus? player = live
                ? await _context.Game.Player.GetStatusAsync(ct).ConfigureAwait(false)
                : null;
            var gameplay = _context.Client.Configuration?.Gameplay;

            return McpResult.Ok(new
            {
                connected = live,
                status = _context.Client.Status.ToString(),
                host = session?.Host,
                port = session?.Port,
                username = await UsernameAsync(ct),
                protocol = session?.Protocol,
                version = session?.VersionName,
                terrainEnabled = gameplay?.Terrain ?? true,
                inventoryEnabled = gameplay?.Inventory ?? true,
                entityEnabled = gameplay?.Entity ?? true,
                physicsEnabled = gameplay?.Physics ?? true,
                pathfindingEnabled = gameplay?.Pathfinding ?? true,
                location = player is null ? null : Loc(player.Position.X, player.Position.Y, player.Position.Z),
                hasSpawned = player?.HasSpawned ?? false,
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_server_info: endpoint plus the measured tick rate (null when unknown, never zero).</summary>
    public async Task<McpResult> GetServerInfoAsync(CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.SessionStatus);
        if (!gate.Success)
            return gate;

        try
        {
            SessionInfoSnapshot info = await _context.Game.Session.GetInfoAsync(ct).ConfigureAwait(false);
            return McpResult.Ok(new
            {
                host = info.Host,
                port = info.Port,
                version = info.VersionName,
                protocol = info.Protocol,
                brand = info.Brand,
                tps = info.TpsEstimate,
                latencyMs = info.ObservedLatencyMs,
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_player_state: vitals, mode, orientation, and location.</summary>
    public async Task<McpResult> GetPlayerStateAsync(CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.SessionStatus);
        if (!gate.Success)
            return gate;

        try
        {
            PlayerStatus status = await _context.Game.Player.GetStatusAsync(ct).ConfigureAwait(false);
            PlayerPose pose = await _context.Game.Movement.GetPoseAsync(ct).ConfigureAwait(false);
            int held = -1;
            try
            {
                held = await _context.Game.Inventory.GetHeldSlotAsync(ct).ConfigureAwait(false);
            }
            catch (DmcbkFeatureDisabledException)
            {
                // Inventory off: the slot is unknown, the rest of the state is still worth reporting.
            }

            return McpResult.Ok(new
            {
                nickname = await UsernameAsync(ct),
                health = status.Health,
                food = status.Food,
                saturation = status.Saturation,
                gamemode = status.GameMode,
                currentSlot = held >= 0 ? held + 1 : (int?)null,
                yaw = pose.Yaw,
                pitch = pose.Pitch,
                onGround = pose.OnGround,
                hasSpawned = status.HasSpawned,
                location = Loc(status.Position.X, status.Position.Y, status.Position.Z),
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_player_stats: extended vitals plus orientation and location.</summary>
    public async Task<McpResult> GetPlayerStatsAsync(CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.SessionStatus);
        if (!gate.Success)
            return gate;

        try
        {
            PlayerStatus status = await _context.Game.Player.GetStatusAsync(ct).ConfigureAwait(false);
            PlayerPose pose = await _context.Game.Movement.GetPoseAsync(ct).ConfigureAwait(false);
            SessionInfoSnapshot session = await _context.Game.Session.GetInfoAsync(ct).ConfigureAwait(false);
            int held = -1;
            try
            {
                held = await _context.Game.Inventory.GetHeldSlotAsync(ct).ConfigureAwait(false);
            }
            catch (DmcbkFeatureDisabledException)
            {
            }

            return McpResult.Ok(new
            {
                username = await UsernameAsync(ct),
                health = status.Health,
                food = status.Food,
                saturation = status.Saturation,
                level = status.ExperienceLevel,
                totalExperience = status.TotalExperience,
                gamemode = status.GameMode,
                currentSlot = held >= 0 ? held + 1 : (int?)null,
                yaw = pose.Yaw,
                pitch = pose.Pitch,
                hasSpawned = status.HasSpawned,
                location = Loc(status.Position.X, status.Position.Y, status.Position.Z),
                tps = session.TpsEstimate,
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_status_effects: active player effects only.</summary>
    public async Task<McpResult> GetStatusEffectsAsync(CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.SessionStatus);
        if (!gate.Success)
            return gate;

        try
        {
            IReadOnlyList<EffectSnapshot> effects =
                await _context.Game.Player.GetEffectsAsync(ct).ConfigureAwait(false);
            return McpResult.Ok(new
            {
                count = effects.Count,
                effects = effects.Select(e => new
                {
                    id = e.EffectId,
                    amplifier = e.Amplifier,
                    remainingTicks = e.Duration,
                    isInfinite = e.IsInfinite,
                }).ToList(),
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_world_state: dimension, time, weather, border, chunks, and latency.</summary>
    public async Task<McpResult> GetWorldStateAsync(CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.SessionStatus);
        if (!gate.Success)
            return gate;

        try
        {
            SessionInfoSnapshot session = await _context.Game.Session.GetInfoAsync(ct).ConfigureAwait(false);
            DimensionInfo dimension = await _context.Game.World.GetDimensionAsync(ct).ConfigureAwait(false);
            WorldTimeInfo time = await _context.Game.World.GetTimeAsync(ct).ConfigureAwait(false);
            WorldBorderInfo border = await _context.Game.World.GetWorldBorderAsync(ct).ConfigureAwait(false);
            PlayerStatus player = await _context.Game.Player.GetStatusAsync(ct).ConfigureAwait(false);
            var observed = _store.GetTime();
            WeatherInfo weather = await _context.Game.World.GetWeatherAsync(ct).ConfigureAwait(false);

            return McpResult.Ok(new
            {
                connected = true,
                host = session.Host,
                port = session.Port,
                username = await UsernameAsync(ct),
                protocol = session.Protocol,
                version = session.VersionName,
                tps = session.TpsEstimate,
                dimension = dimension.DimensionName,
                dimensionType = dimension.DimensionType,
                minY = dimension.MinY,
                height = dimension.Height,
                hasSkylight = dimension.HasSkylight,
                worldAge = time.WorldAge,
                timeOfDay = time.TimeOfDay,
                dayTime = time.DayTime,
                isDaytime = time.IsDaytime,
                observedWorldAge = observed?.WorldAge,
                observedTimeOfDay = observed?.TimeOfDay,
                rainLevel = weather.RainLevel,
                thunderLevel = weather.ThunderLevel,
                isRaining = weather.IsRaining,
                border = new
                {
                    centerX = border.CenterX,
                    centerZ = border.CenterZ,
                    size = border.Size,
                },
                location = Loc(player.Position.X, player.Position.Y, player.Position.Z),
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }
}
