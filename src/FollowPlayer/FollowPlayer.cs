// FollowPlayer: `/follow start <player> [-f]` walks after a player, `/follow stop` stops.
// The bot re-paths every Update_Limit seconds while the target moves, and holds still once it is within Stop_At_Distance.
//
// The movement lease is taken when following starts and released when it stops or the session ends, so a reconnect never leaves a dangling owner.

using DMCBK.Core;
using DMCBK.Core.Commands;
using DMCBK.Core.Commands.ArgumentTypes;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Tomlet.Attributes;
using Umpk.Client.Events;
using Umpk.Client.Movement;
using Umpk.Client.Snapshots;
using Umpk.Commands;
using Umpk.Game.Entities;
using Umpk.Game.Players;
using Umpk.Geometry;

namespace DMCBK.OfficialPlugins.FollowPlayer;

/// <summary>The user-editable, persisted settings for FollowPlayer (serialized to settings.toml).</summary>
public sealed class FollowPlayerSettings : IValidatablePluginSettings
{
    /// <summary>Master switch for the plugin.</summary>
    [TomlInlineComment("$settings.enabled$")]
    public bool Enabled { get; set; } = true;

    /// <summary>The minimum number of seconds between two re-paths toward the target.</summary>
    [TomlInlineComment("$settings.update_limit$")]
    public double Update_Limit { get; set; } = 1.5;

    /// <summary>Stop moving once the target is closer than this many blocks.</summary>
    [TomlInlineComment("$settings.stop_at_distance$")]
    public double Stop_At_Distance { get; set; } = 3.0;

    /// <summary>Clamps the values into range on load and reload.</summary>
    public void Validate()
    {
        if (Update_Limit < 0)
            Update_Limit = 0;

        if (Stop_At_Distance < 0)
            Stop_At_Distance = 0;
    }
}

/// <summary>The follow decisions, free of session state so they can be unit tested headlessly.</summary>
public static class FollowPolicy
{
    /// <summary>The client tick rate the update throttle is counted in.</summary>
    public const int TicksPerSecond = 20;

    /// <summary>The largest reachability probe the plugin will ask for, in chunk columns either side.</summary>
    public const int MaxProbeChunks = 16;

    /// <summary>Converts seconds to whole client ticks.</summary>
    public static int ToTicks(double seconds)
    {
        double clamped = Math.Min(int.MaxValue / (double)TicksPerSecond, Math.Max(0, seconds));
        return (int)Math.Round(clamped * TicksPerSecond);
    }

    /// <summary>
    /// 1 to 16 characters of letters, digits or underscore.
    /// Anything else is refused before the entity lookup, so a typo cannot be mistaken for an out-of-range player.
    /// </summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 16)
            return false;

        foreach (char c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
                return false;
        }

        return true;
    }

    /// <summary>True when the bot should take another step toward a target at <paramref name="distance"/>.</summary>
    public static bool ShouldStep(double distance, double stopAtDistance) => distance >= stopAtDistance;

    /// <summary>The chunk coordinate containing a world block coordinate.</summary>
    public static int ToChunk(double blockCoordinate) => (int)Math.Floor(blockCoordinate) >> 4;

    /// <summary>
    /// The grid width (in chunk columns) needed to cover a target <paramref name="chunkDelta"/> columns away, always odd so the player's own column sits in the middle.
    /// Returns 0 when the target is further than <see cref="MaxProbeChunks"/>, which the caller treats as unreachable.
    /// </summary>
    public static int GridSizeFor(int chunkDelta)
    {
        int span = Math.Abs(chunkDelta);
        return span > MaxProbeChunks ? 0 : (span * 2) + 1;
    }

    /// <summary>
    /// Turns a player name into the tracked entity that is that player.
    /// The join is the profile uuid: the name is carried by the tab list, the entity is keyed by uuid, and a spawned player entity carries the same uuid its tab-list entry does.
    /// <para>
    /// A name in the tab list with no tracked entity means the player is on the server but out of tracking range, which is a refusal rather than an error.
    /// </para>
    /// </summary>
    /// <param name="name">The requested player name (matched case-insensitively).</param>
    /// <param name="tabList">The tab-list (name, uuid) pairs.</param>
    /// <param name="entityIdByUuid">Looks up the tracked entity id for a uuid, or null when untracked.</param>
    /// <param name="entityId">The resolved entity id.</param>
    /// <param name="uuid">The resolved profile uuid.</param>
    /// <param name="resolvedName">The name as the tab list spells it (fixing the caller's casing).</param>
    public static bool TryResolveTarget(
        string name,
        IReadOnlyList<(string Name, Guid Uuid)> tabList,
        Func<Guid, int?> entityIdByUuid,
        out int entityId,
        out Guid uuid,
        out string resolvedName)
    {
        ArgumentNullException.ThrowIfNull(tabList);
        ArgumentNullException.ThrowIfNull(entityIdByUuid);
        entityId = 0;
        uuid = Guid.Empty;
        resolvedName = name;

        foreach ((string Name, Guid Uuid) entry in tabList)
        {
            if (!string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;

            if (entityIdByUuid(entry.Uuid) is not { } tracked)
                return false;

            entityId = tracked;
            uuid = entry.Uuid;
            resolvedName = entry.Name;
            return true;
        }

        return false;
    }

    /// <summary>Reads the loaded flag for one chunk out of a <see cref="ChunkStatusGrid"/>.</summary>
    public static bool IsChunkLoaded(ChunkStatusGrid grid, int chunkX, int chunkZ)
    {
        ArgumentNullException.ThrowIfNull(grid);
        int column = chunkX - grid.CenterChunkX + ((grid.Columns - 1) / 2);
        int row = chunkZ - grid.CenterChunkZ + ((grid.Rows - 1) / 2);
        if (column < 0 || column >= grid.Columns || row < 0 || row >= grid.Rows)
            return false;

        return grid.Loaded[(row * grid.Columns) + column];
    }
}

/// <summary>The FollowPlayer plugin entry point.</summary>
public sealed class FollowPlayerPlugin : IPlugin
{
    private readonly object _gate = new();

    private FollowPlayerSettings _settings = new();
    private PluginContext? _context;
    private ILogger? _logger;
    private IPluginLocalization? _strings;

    private ISessionScope? _session;
    private IMovementLease? _lease;
    private string? _target;
    private int? _targetEntityId;

    // The target's profile uuid: the stable join between the tab-list name and the tracked entity.
    private Guid _targetUuid;
    private int _tickCounter;
    private bool _stepping;
    private bool _unsafeRequested;

    /// <inheritdoc/>
    public void Configure(PluginDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        descriptor.Id = "follow-player";
        descriptor.Version = "2.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
        descriptor.WithSettings<FollowPlayerSettings>();
    }

    /// <inheritdoc/>
    public Task ActivateAsync(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _settings = context.Settings.Load<FollowPlayerSettings>();
        _context = context;
        _logger = context.Logger;
        _strings = context.Strings;

        // Registered once here, never per session: the command dispatcher is client-lifetime, and the command body re-reads the live session each time.
        context.Commands.Register(new FollowCommand(this, context.Strings));

        context.SessionStarted += (_, e) => OnSessionStarted(e.Session);
        context.SessionEnded += (_, _) => OnSessionEnded();
        return Task.CompletedTask;
    }

    /// <summary>The plugin id, also used as the movement lease owner tag.</summary>
    internal static string Id => "follow-player";

    /// <summary>The name currently being followed, or null.</summary>
    internal string? Target
    {
        get
        {
            lock (_gate)
                return _target;
        }
    }

    private void OnSessionStarted(ISessionScope session)
    {
        lock (_gate)
        {
            _session = session;
            _tickCounter = 0;
            _stepping = false;

            // A reconnect starts fresh: the lease from the previous session died with its scope.
            _lease = null;
            _target = null;
            _targetEntityId = null;
            _targetUuid = Guid.Empty;
            _unsafeRequested = false;
        }

        session.Scheduler.OnTick(OnTick);
        session.Events.Subscribe<EntityMoved>(e => OnEntityMoved(session, e.EntityId));
        session.Events.Subscribe<EntitySpawned>(e => OnEntitySpawned(session, e.EntityId, e.Entity));
        session.Events.Subscribe<EntityRemoved>(e => OnEntityRemoved(session, e.EntityId));
    }

    private void OnSessionEnded()
    {
        // Release explicitly rather than waiting for scope teardown, so ownership goes back the moment the session ends.
        // This also runs on plugin unload; the host raises SessionEnded there too.
        StopFollowing();
        lock (_gate)
            _session = null;
    }

    private void OnTick()
    {
        lock (_gate)
            _tickCounter++;
    }

    /// <summary>
    /// Re-attaches to the target when it walks back into tracking range, matched on the profile uuid the spawn carries.
    /// </summary>
    private void OnEntitySpawned(ISessionScope session, int entityId, Entity entity)
    {
        string name;
        lock (_gate)
        {
            if (_target is null || _targetUuid == Guid.Empty || entity.Uuid != _targetUuid)
                return;

            _targetEntityId = entityId;
            name = _target;
        }

        _logger?.LogInformation("{Message}", _strings?.Format("player_came_into_range", name));
    }

    private void OnEntityRemoved(ISessionScope session, int entityId)
    {
        string? name;
        lock (_gate)
        {
            if (_targetEntityId != entityId || _target is null)
                return;

            name = _target;
            _targetEntityId = null;
        }

        // An entity that despawned is only out of tracking range, so pause.
        // A player gone from the tab list has actually left the server, so stop.
        if (IsInTabList(session, name))
        {
            _logger?.LogInformation("{Message}", _strings?.Format("player_left_range", name));
            return;
        }

        _logger?.LogInformation("{Message}", _strings?.Format("player_left_server", name));
        StopFollowing();
    }

    private static bool IsInTabList(ISessionScope session, string name)
    {
        foreach (TabListEntry entry in session.State.TabList.Entries)
        {
            if (string.Equals(entry.Profile.Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The follow step.
    /// Runs on the session loop, so the state reads are loop-affine.
    /// The navigation is dispatched off-loop so the loop is never blocked on pathfinding.
    /// </summary>
    private void OnEntityMoved(ISessionScope session, int entityId)
    {
        Vec3d goal;
        bool unsafeRequested;
        lock (_gate)
        {
            if (!_settings.Enabled || _target is null || _targetEntityId != entityId || _stepping)
                return;

            if (_tickCounter < FollowPolicy.ToTicks(_settings.Update_Limit))
                return;

            _tickCounter = 0;

            if (!session.State.Entities.TryGet(entityId, out Entity? entity))
                return;

            goal = entity.Position;
            double distance = goal.Subtract(session.State.Self.Position).Length();
            if (!FollowPolicy.ShouldStep(distance, _settings.Stop_At_Distance))
                return;

            unsafeRequested = _unsafeRequested;
            _stepping = true;
        }

        _ = session.Scheduler.RunOffLoop(() => StepAsync(session, goal, unsafeRequested), session.Detached);
    }

    private async Task StepAsync(ISessionScope session, Vec3d goal, bool unsafeRequested)
    {
        try
        {
            if (_context is not { } context)
                return;

            if (!await IsReachableAsync(context, session, goal).ConfigureAwait(false))
                return;

            // The movement API has no unsafe-path knob, so -f only picks the direct physics move over the planner.
            bool pathfinding = !unsafeRequested
                && await context.Game.Movement.IsPathfindingAvailableAsync(session.Detached).ConfigureAwait(false);

            _logger?.LogDebug(
                "{Message}",
                _strings?.Format("stepping", goal.X, goal.Y, goal.Z, pathfinding));

            // Hand the claim back for the duration of the move.
            // The lease is not re-entrant and the navigator takes it itself, so holding ours across the call makes every step throw "Movement is held by ...".
            // The navigator owns it while the bot is actually walking.
            ReleaseLease();
            try
            {
                if (pathfinding)
                {
                    await context.Game.Movement
                        .NavigateToAsync(BlockPos.Containing(goal), 1, session.Detached).ConfigureAwait(false);
                }
                else
                    await context.Game.Movement.MoveToAsync(goal, session.Detached).ConfigureAwait(false);

                _logger?.LogDebug("{Message}", _strings?.Format("stepped", goal.X, goal.Y, goal.Z));
            }
            finally
            {
                ReclaimLease(session);
            }
        }
        catch (OperationCanceledException)
        {
            // The session ended mid-step.
        }
        catch (Exception ex) when (ex is DmcbkClientException or DmcbkFeatureDisabledException or InvalidOperationException)
        {
            _logger?.LogDebug(ex, "{Message}", _strings?.Get("step_failed"));
        }
        finally
        {
            lock (_gate)
                _stepping = false;
        }
    }

    /// <summary>Hands the ownership claim back so the navigator can take the lease for the move.</summary>
    private void ReleaseLease()
    {
        IMovementLease? lease;
        lock (_gate)
        {
            lease = _lease;
            _lease = null;
        }

        lease?.Dispose();
    }

    /// <summary>
    /// Retakes the claim after a move.
    /// If another owner won the lease in the gap, following stops: the alternative is walking on without the claim, which is what the lease exists to prevent.
    /// </summary>
    private void ReclaimLease(ISessionScope session)
    {
        bool following;
        lock (_gate)
            following = _target is not null;

        if (!following)
            return;

        IMovementLease? lease = session.TryAcquireMovement(Id);
        if (lease is null)
        {
            _logger?.LogInformation("{Message}", _strings?.Get("movement_lost"));
            StopFollowing();
            return;
        }

        bool keep;
        lock (_gate)
        {
            keep = _target is not null && _lease is null;
            if (keep)
                _lease = lease;
        }

        if (!keep)
            lease.Dispose();
    }

    /// <summary>The goal's chunk column has to be loaded before the bot can be sent there.</summary>
    private static async Task<bool> IsReachableAsync(PluginContext context, ISessionScope session, Vec3d goal)
    {
        PlayerPose pose = await context.Game.Movement.GetPoseAsync(session.Detached).ConfigureAwait(false);
        int deltaX = FollowPolicy.ToChunk(goal.X) - FollowPolicy.ToChunk(pose.Position.X);
        int deltaZ = FollowPolicy.ToChunk(goal.Z) - FollowPolicy.ToChunk(pose.Position.Z);
        int size = FollowPolicy.GridSizeFor(Math.Max(Math.Abs(deltaX), Math.Abs(deltaZ)));
        if (size == 0)
            return false;

        ChunkStatusGrid grid = await context.Game.World
            .GetChunkStatusAsync(size, size, session.Detached).ConfigureAwait(false);
        return FollowPolicy.IsChunkLoaded(grid, FollowPolicy.ToChunk(goal.X), FollowPolicy.ToChunk(goal.Z));
    }

    /// <summary>
    /// Starts following <paramref name="name"/>.
    /// Returns false with an already-localized <paramref name="error"/> when the target is unknown/unreachable or movement is owned elsewhere.
    /// </summary>
    internal bool TryStartFollowing(string name, bool takeRisk, IPluginLocalization strings, out string error)
    {
        error = string.Empty;
        if (!_settings.Enabled)
        {
            error = strings.Get("disabled");
            return false;
        }

        if (!FollowPolicy.IsValidName(name))
        {
            error = strings.Get("invalid_name");
            return false;
        }

        ISessionScope? session;
        lock (_gate)
        {
            session = _session;
            if (_target is not null && string.Equals(_target, name, StringComparison.OrdinalIgnoreCase))
            {
                error = strings.Format("already_following", _target);
                return false;
            }
        }

        if (session is null)
        {
            error = strings.Get("not_connected");
            return false;
        }

        if (!TryFindPlayer(session, name, out int entityId, out Guid uuid, out string resolvedName))
        {
            error = strings.Get("player_not_found");
            return false;
        }

        lock (_gate)
        {
            // Switching targets keeps the lease we already hold; a fresh follow has to take it.
            if (_lease is null)
            {
                _lease = session.TryAcquireMovement(Id);
                if (_lease is null)
                {
                    error = strings.Get("movement_held");
                    return false;
                }
            }

            _target = resolvedName;
            _targetEntityId = entityId;
            _targetUuid = uuid;
            _unsafeRequested = takeRisk;
            _tickCounter = int.MaxValue / 2; // Step immediately rather than waiting out the first throttle.
        }

        _logger?.LogInformation("{Message}", _strings?.Format("started", resolvedName));
        return true;
    }

    /// <summary>Stops following and hands the movement lease back. Safe to call when not following.</summary>
    internal bool StopFollowing()
    {
        IMovementLease? lease;
        bool wasFollowing;
        lock (_gate)
        {
            wasFollowing = _target is not null;
            lease = _lease;
            _lease = null;
            _target = null;
            _targetEntityId = null;
            _targetUuid = Guid.Empty;
            _unsafeRequested = false;
            _stepping = false;
        }

        lease?.Dispose();
        return wasFollowing;
    }

    /// <summary>
    /// Resolves a player name to the tracked entity that is that player.
    /// <see cref="Entity.PlayerProfile"/> is tried first; the name -> tab-list uuid -> entity path is the fallback, because it still works when a spawn beats its own tab-list entry and the profile has not been backfilled yet.
    /// </summary>
    private static bool TryFindPlayer(
        ISessionScope session, string name, out int entityId, out Guid uuid, out string resolvedName)
    {
        entityId = 0;
        uuid = Guid.Empty;
        resolvedName = name;

        foreach (Entity entity in session.State.Entities.All)
        {
            string? candidate = entity.PlayerProfile?.Name;
            if (candidate is not null && string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
            {
                entityId = entity.Id;
                uuid = entity.Uuid;
                resolvedName = candidate;
                return true;
            }
        }

        var tabList = new List<(string Name, Guid Uuid)>();
        foreach (TabListEntry entry in session.State.TabList.Entries)
            tabList.Add((entry.Profile.Name, entry.Uuid));

        return FollowPolicy.TryResolveTarget(
            name,
            tabList,
            id => session.State.Entities.TryGetByUuid(id, out Entity? tracked) ? tracked.Id : null,
            out entityId,
            out uuid,
            out resolvedName);
    }
}

/// <summary>The <c>follow</c> console command.</summary>
public sealed class FollowCommand : CommandBase
{
    private readonly FollowPlayerPlugin _plugin;
    private readonly IPluginLocalization _strings;

    /// <summary>Creates the command bound to its owning plugin instance.</summary>
    /// <param name="plugin">The plugin that owns the follow state and the movement lease.</param>
    /// <param name="strings">The string table this command prints from.</param>
    public FollowCommand(FollowPlayerPlugin plugin, IPluginLocalization strings)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(strings);
        _plugin = plugin;
        _strings = strings;
    }

    /// <inheritdoc/>
    public override string CmdName => "follow";

    /// <inheritdoc/>
    public override string CmdDesc => _strings.Get("command_desc");

    /// <inheritdoc/>
    public override string CmdUsage => _strings.Get("command_usage");

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Plugins;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("start <player>", _strings.Get("usage_start")),
        new("stop", _strings.Get("usage_stop")),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["follow start Steve", "follow stop"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["move", "entity"];

    /// <inheritdoc/>
    public override string? ManTopic => "follow-player";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .ThenLiteral("start", h => h
                .ThenArgument("player", DmcbkArguments.PlayerName(), a => a
                    .Executes(ctx => Start(ctx.Source, ctx.GetArgument<string>("player"), takeRisk: false))
                    .ThenLiteral("-f", f => f
                        .Executes(ctx => Start(ctx.Source, ctx.GetArgument<string>("player"), takeRisk: true)))))
            .ThenLiteral("stop", h => h.Executes(ctx => Stop(ctx.Source)))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private int Start(CommandContext ctx, string name, bool takeRisk)
    {
        if (!ctx.InSession)
            return ctx.Result.Fail(_strings.Get("not_connected"));

        if (!ctx.EntityEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedEntity);

        if (!ctx.TerrainEnabled)
            return ctx.Result.Set(CmdStatus.FailNeedTerrain);

        if (!_plugin.TryStartFollowing(name, takeRisk, _strings, out string error))
            return ctx.Result.Fail(error);

        string note = takeRisk
            ? _strings.Get("note") + "\n" + _strings.Get("unsafe_not_supported")
            : _strings.Get("note");
        return ctx.Result.Ok(_strings.Format("following", name) + "\n" + note);
    }

    private int Stop(CommandContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return _plugin.StopFollowing()
            ? ctx.Result.Ok(_strings.Get("stopping"))
            : ctx.Result.Fail(_strings.Get("already_stopped"));
    }
}
