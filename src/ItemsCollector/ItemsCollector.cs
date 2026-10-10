// Walks onto dropped items within Collection_Radius, nearest first (or by distance to the cluster centroid when Prioritize_Clusters is on), then returns to the start position if nothing is left.
//
// Items_Whitelist is currently inert: UMPK never decodes a dropped item's ItemStack (Entity.Metadata is unpopulated), so there is nothing to match it against.
// Collect_All_Item_Types stays on by default; turning it off just logs a one-time warning and collects everything anyway.

using DMCBK.Core.Commands;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Tomlet.Attributes;
using Umpk.Client.Events;
using Umpk.Client.Movement;
using Umpk.Commands;
using Umpk.Game.Entities;
using Umpk.Geometry;
using Umpk.Pathfinding.Goals;

namespace DMCBK.OfficialPlugins.ItemsCollector;

/// <summary>Target ordering for the collection loop. Pure logic, unit tested.</summary>
public static class CollectionPlanner
{
    /// <summary>The registry path of the dropped-item entity type.</summary>
    public const string ItemEntityPath = "item";

    /// <summary>True when an entity type id is the dropped-item entity.</summary>
    public static bool IsItemEntity(string? typeId)
    {
        if (string.IsNullOrWhiteSpace(typeId))
            return false;

        string value = typeId.Trim();
        int colon = value.IndexOf(':');
        if (colon >= 0)
            value = value[(colon + 1)..];

        return string.Equals(value, ItemEntityPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The centroid (arithmetic mean) of a set of positions; Zero for an empty set.</summary>
    public static Vec3d Centroid(IReadOnlyList<Vec3d> positions)
    {
        if (positions.Count == 0)
            return Vec3d.Zero;

        double x = 0, y = 0, z = 0;
        for (int i = 0; i < positions.Count; i++)
        {
            x += positions[i].X;
            y += positions[i].Y;
            z += positions[i].Z;
        }

        return new Vec3d(x / positions.Count, y / positions.Count, z / positions.Count);
    }

    /// <summary>
    /// Orders collection targets: by distance to the cluster centroid when <paramref name="prioritizeClusters"/> is on and there is more than one target, otherwise nearest to the player first.
    /// </summary>
    public static List<Vec3d> Order(IReadOnlyList<Vec3d> targets, Vec3d self, bool prioritizeClusters)
    {
        var ordered = new List<Vec3d>(targets);
        Vec3d origin = prioritizeClusters && targets.Count > 1 ? Centroid(targets) : self;
        ordered.Sort((a, b) => Distance(a, origin).CompareTo(Distance(b, origin)));
        return ordered;
    }

    /// <summary>Euclidean distance between two positions.</summary>
    public static double Distance(Vec3d a, Vec3d b) => a.Subtract(b).Length();

    /// <summary>Clamps the inter-cycle delay to at least 100 ms.</summary>
    public static int ClampDelay(int milliseconds) => milliseconds < 100 ? 100 : milliseconds;
}

/// <summary>The user-editable, persisted settings for ItemsCollector (serialized to settings.toml).</summary>
public sealed class ItemsCollectorSettings : IValidatablePluginSettings
{
    /// <summary>Master switch.</summary>
    [TomlInlineComment("$settings.enabled$")]
    public bool Enabled { get; set; }

    /// <summary>Collect every dropped item instead of only the whitelist.</summary>
    [TomlInlineComment("$settings.collect_all_item_types$")]
    public bool Collect_All_Item_Types { get; set; } = true;

    /// <summary>The item whitelist. Inert: a dropped item's contents are not decoded.</summary>
    [TomlPrecedingComment("$settings.items_whitelist$")]
    public string[] Items_Whitelist { get; set; } = ["Diamond", "NetheriteIngot"];

    /// <summary>Milliseconds between collection cycles; clamped to at least 100.</summary>
    [TomlInlineComment("$settings.delay_between_tasks$")]
    public int Delay_Between_Tasks { get; set; } = 300;

    /// <summary>The search radius in blocks.</summary>
    [TomlInlineComment("$settings.collection_radius$")]
    public double Collection_Radius { get; set; } = 30.0;

    /// <summary>Return to the starting position when nothing is in range.</summary>
    [TomlInlineComment("$settings.always_return_to_start$")]
    public bool Always_Return_To_Start { get; set; } = true;

    /// <summary>Order targets by distance to the cluster centroid instead of to the player.</summary>
    [TomlInlineComment("$settings.prioritize_clusters$")]
    public bool Prioritize_Clusters { get; set; }

    /// <summary>Clamps and normalizes on load or reload.</summary>
    public void Validate()
    {
        Items_Whitelist ??= [];
        Delay_Between_Tasks = CollectionPlanner.ClampDelay(Delay_Between_Tasks);
        if (Collection_Radius <= 0)
            Collection_Radius = 30.0;
    }
}

/// <summary>The ItemsCollector plugin entry point.</summary>
public sealed class ItemsCollectorPlugin : IPlugin
{
    private readonly object _gate = new();
    private ItemsCollectorSettings _settings = new();
    private ILogger? _logger;
    private IPluginLocalization? _strings;
    private ISessionScope? _session;
    private IMovementLease? _lease;
    private volatile bool _running;
    private bool _warnedWhitelist;

    /// <inheritdoc/>
    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "items-collector";
        descriptor.Version = "2.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
        descriptor.WithSettings<ItemsCollectorSettings>();
    }

    /// <inheritdoc/>
    public Task ActivateAsync(PluginContext context)
    {
        _settings = context.Settings.Load<ItemsCollectorSettings>();
        _logger = context.Logger;
        _strings = context.Strings;

        context.Commands.Register(new ItemsCollectorCommand(this, context.Strings));

        context.SessionStarted += (_, e) =>
        {
            lock (_gate)
                _session = e.Session;

            // Never auto-starts: a fresh session always begins stopped.
            Stop();
            e.Session.Events.Subscribe<Died>(_ => Stop());
        };

        context.SessionEnded += (_, _) =>
        {
            Stop();
            lock (_gate)
                _session = null;
        };

        return Task.CompletedTask;
    }

    /// <summary>Whether the collection loop is running.</summary>
    internal bool IsRunning => _running;

    /// <summary>
    /// Starts the collection loop.
    /// Returns null on success, or the reason it could not start.
    /// </summary>
    internal string? Start(IPluginLocalization strings)
    {
        ISessionScope? session;
        lock (_gate)
            session = _session;

        if (session is null)
            return strings.Get("not_connected");

        if (_running)
            return strings.Get("already_collecting");

        if (!session.State.Features.Entities || !session.State.Features.Terrain)
            return strings.Get("requirements_missing");

        IMovementLease? lease = session.TryAcquireMovement("Items Collector");
        if (lease is null)
            return strings.Format("movement_lock_held", strings.Get("another_plugin"));

        lock (_gate)
            _lease = lease;

        _running = true;
        _ = session.Scheduler.RunOffLoop(() => MainProcessAsync(session), session.Detached);
        return null;
    }

    /// <summary>Stops the collection loop and releases the movement lease.</summary>
    internal void Stop()
    {
        _running = false;
        IMovementLease? lease;
        lock (_gate)
        {
            lease = _lease;
            _lease = null;
        }

        lease?.Dispose();
    }

    private async Task MainProcessAsync(ISessionScope session)
    {
        Vec3d start = await session.Client.InvokeAsync(c => c.State.Self.Position, session.Detached).ConfigureAwait(false);

        if (!_settings.Collect_All_Item_Types && !_warnedWhitelist)
        {
            _warnedWhitelist = true;
            _logger?.LogWarning("{Message}", _strings?.Get("whitelist_unavailable"));
        }

        try
        {
            while (_running && !session.Detached.IsCancellationRequested)
            {
                (Vec3d self, List<Vec3d> targets) = await session.Client.InvokeAsync(
                    c => ReadTargets(c.State.Self.Position, c.State.Entities, _settings.Collection_Radius),
                    session.Detached).ConfigureAwait(false);

                if (targets.Count > 0)
                {
                    List<Vec3d> ordered = CollectionPlanner.Order(targets, self, _settings.Prioritize_Clusters);
                    _logger?.LogInformation("{Message}", _strings?.Format("collecting", ordered.Count));
                    foreach (Vec3d target in ordered)
                    {
                        if (!_running)
                            break;

                        await WalkToAsync(session, target).ConfigureAwait(false);
                    }
                }
                else if (_settings.Always_Return_To_Start && CollectionPlanner.Distance(start, self) > 2.0)
                    await WalkToAsync(session, start).ConfigureAwait(false);

                if (!_running)
                    break;

                await Task.Delay(CollectionPlanner.ClampDelay(_settings.Delay_Between_Tasks), session.Detached)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Session ended under us; fall through to the shared cleanup.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "{Message}", _strings?.Get("loop_error"));
        }
        finally
        {
            Stop();
            _logger?.LogInformation("{Message}", _strings?.Get("stopped"));
        }
    }

    private static (Vec3d Self, List<Vec3d> Targets) ReadTargets(Vec3d self, EntityStore entities, double radius)
    {
        var targets = new List<Vec3d>();
        foreach (Entity entity in entities.Nearby(self, radius))
        {
            if (CollectionPlanner.IsItemEntity(entity.Type.Id.ToString()))
                targets.Add(entity.Position);
        }

        return (self, targets);
    }

    /// <summary>
    /// Walks onto one drop, handing the movement lease to the navigator for the duration.
    /// </summary>
    /// <remarks>
    /// Releasing the lease first is required, not cleanup: <c>Navigator.MoveToAsync</c> and <c>NavigateAsync</c> take the lease themselves and throw if it is already held, even by the caller.
    /// Keeping it across the walk means every walk throws against its own claim, gets swallowed by the catch below, and the bot stands still while logging that it is walking.
    /// </remarks>
    private async Task WalkToAsync(ISessionScope session, Vec3d target)
    {
        ReleaseLease();
        try
        {
            bool pathfinding = await session.Client
                .InvokeAsync(c => c.State.Features.Pathfinding, session.Detached).ConfigureAwait(false);
            // Walk ONTO the drop, not next to it.
            // GoalNear(..., 1) is satisfied a whole block away and the pickup radius does not reach that far, so pathfind to the item's block and then close the last fraction of a block directly.
            if (pathfinding)
            {
                BlockPos block = BlockPos.Containing(target);
                await session.Actions.Movement
                    .NavigateAsync(new GoalNear(block.X, block.Y, block.Z, 0), session.Detached).ConfigureAwait(false);
            }

            await session.Actions.Movement.MoveToAsync(target, session.Detached).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unreachable drop (lava, a wall, a ledge) is normal, so this stays non-fatal.
            // Log it anyway: silence makes a walk that fails on every target look like one that keeps missing.
            _logger?.LogDebug(ex, "{Message}", _strings?.Format("walk_failed", target));
        }
        finally
        {
            ReclaimLease(session);
        }
    }

    /// <summary>Hands the movement claim back without stopping the loop.</summary>
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
    /// Retakes the claim after a walk.
    /// If another owner won it in the gap, collecting stops rather than walking without a claim.
    /// </summary>
    private void ReclaimLease(ISessionScope session)
    {
        if (!_running)
            return;

        IMovementLease? lease = session.TryAcquireMovement("Items Collector");
        if (lease is null)
        {
            _logger?.LogInformation(
                "{Message}",
                _strings?.Format("movement_lock_held", _strings.Get("another_plugin")));
            _running = false;
            return;
        }

        lock (_gate)
            _lease = lease;
    }
}

/// <summary>The <c>itemscollector</c> console command.</summary>
internal sealed class ItemsCollectorCommand : CommandBase
{
    private readonly ItemsCollectorPlugin _plugin;
    private readonly IPluginLocalization _strings;

    internal ItemsCollectorCommand(ItemsCollectorPlugin plugin, IPluginLocalization strings)
    {
        _plugin = plugin;
        _strings = strings;
    }

    /// <inheritdoc/>
    public override string CmdName => "itemscollector";

    /// <inheritdoc/>
    public override string CmdDesc => _strings.Get("command_desc");

    /// <inheritdoc/>
    public override string CmdUsage => _strings.Get("command_usage");

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Plugins;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("start", _strings.Get("usage_start")),
        new("stop", _strings.Get("usage_stop")),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["itemscollector start", "itemscollector stop"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["move", "inventory"];

    /// <inheritdoc/>
    public override string? ManTopic => "items-collector";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .Executes(ctx => ShowUsage(ctx.Source))
            .ThenLiteral("start", a => a.Executes(ctx => Start(ctx.Source)))
            .ThenLiteral("stop", a => a.Executes(ctx => Stop(ctx.Source)))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private int Start(CommandContext ctx)
    {
        string? failure = _plugin.Start(_strings);
        return failure is null ? ctx.Result.Ok(_strings.Get("started")) : ctx.Result.Fail(failure);
    }

    private int Stop(CommandContext ctx)
    {
        if (!_plugin.IsRunning)
            return ctx.Result.Fail(_strings.Get("not_collecting"));

        _plugin.Stop();
        return ctx.Result.Ok(_strings.Get("stopping"));
    }
}
