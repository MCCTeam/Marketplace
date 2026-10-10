// AntiAFK: on a random countdown it optionally walks to a nearby standable spot, sends a configured command, and optionally toggles sneak, so the server does not count the client as idle.
// When a walk succeeds and Use_Sneak is on, sneak is reset and no command is sent.

using DMCBK.Core;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Tomlet.Attributes;
using Umpk.Client.Snapshots;
using Umpk.Client.Movement;
using Umpk.Geometry;

namespace DMCBK.OfficialPlugins.AntiAfk;

/// <summary>A minimum and maximum, in seconds.</summary>
public sealed class DelayRange
{
    /// <summary>The minimum delay in seconds.</summary>
    [TomlProperty("min")]
    public double Min { get; set; } = 60.0;

    /// <summary>The maximum delay in seconds.</summary>
    [TomlProperty("max")]
    public double Max { get; set; } = 60.0;
}

/// <summary>The settings for AntiAFK, saved to settings.toml.</summary>
public sealed class AntiAfkSettings : IValidatablePluginSettings
{
    /// <summary>Master switch for the plugin.</summary>
    [TomlInlineComment("$settings.enabled$")]
    public bool Enabled { get; set; } = true;

    /// <summary>The random delay range, in seconds, between two anti-idle actions.</summary>
    [TomlPrecedingComment("$settings.delay$")]
    public DelayRange Delay { get; set; } = new();

    /// <summary>The chat line or command sent as the anti-idle action.</summary>
    [TomlInlineComment("$settings.command$")]
    public string Command { get; set; } = "/ping";

    /// <summary>Whether to toggle the sneak state on every action.</summary>
    [TomlInlineComment("$settings.use_sneak$")]
    public bool Use_Sneak { get; set; }

    /// <summary>Whether to walk to a nearby random spot instead of only sending the command.</summary>
    [TomlInlineComment("$settings.use_terrain_handling$")]
    public bool Use_Terrain_Handling { get; set; }

    /// <summary>The maximum X/Z offset of the random walk goal, in blocks.</summary>
    [TomlInlineComment("$settings.walk_range$")]
    public int Walk_Range { get; set; } = 5;

    /// <summary>How many goals to try before falling back to the command.</summary>
    [TomlInlineComment("$settings.walk_retries$")]
    public int Walk_Retries { get; set; } = 20;

    /// <summary>Normalizes on load and reload.</summary>
    public void Validate()
    {
        Delay ??= new DelayRange();
        Command ??= string.Empty;

        if (Walk_Range <= 0)
            Walk_Range = 5;

        if (Walk_Retries <= 0)
            Walk_Retries = 1;

        Delay.Min = Math.Max(1.0, Delay.Min);
        Delay.Max = Math.Max(1.0, Delay.Max);
        Delay.Min = Math.Min(AntiAfkTiming.MaxDelaySeconds, Delay.Min);
        Delay.Max = Math.Min(AntiAfkTiming.MaxDelaySeconds, Delay.Max);

        if (Delay.Min > Delay.Max)
            (Delay.Min, Delay.Max) = (Delay.Max, Delay.Min);
    }
}

/// <summary>The timing and geometry helpers, kept free of session state so they can be tested.</summary>
public static class AntiAfkTiming
{
    /// <summary>The client tick rate the countdown is measured in.</summary>
    public const int TicksPerSecond = 20;

    /// <summary>The largest delay that still fits the tick counter.</summary>
    public static double MaxDelaySeconds => int.MaxValue / (double)TicksPerSecond;

    /// <summary>Converts seconds to whole client ticks.</summary>
    public static int ToTicks(double seconds)
    {
        double clamped = Math.Min(MaxDelaySeconds, seconds);
        return (int)Math.Round(clamped * TicksPerSecond);
    }

    /// <summary>Draws the next countdown in ticks from the configured range (inclusive of both ends).</summary>
    public static int NextDelayTicks(double minSeconds, double maxSeconds, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        int min = ToTicks(minSeconds);
        int max = ToTicks(maxSeconds);
        if (max <= min)
            return min;

        return random.Next(min, max);
    }

    /// <summary>The largest height difference, in blocks, a random walk goal may have from the player.</summary>
    public const int MaxGoalHeightDelta = 4;

    /// <summary>Counts the columns in a surface sample that carry a real, non-fluid surface block.</summary>
    public static int CountStandable(SurfaceRegionSnapshot region)
    {
        ArgumentNullException.ThrowIfNull(region);
        int count = 0;
        foreach (SurfaceColumn info in region.Columns)
        {
            if (info.Loaded && info.Found && !info.State.IsFluid)
                count++;
        }

        return count;
    }

    /// <summary>
    /// Picks a random standable goal out of a top-down surface sample around the player (see the <see cref="MaxGoalHeightDelta"/> rule).
    /// Returns false when the sample offers nothing to walk to.
    /// </summary>
    public static bool TryPickGoal(SurfaceRegionSnapshot region, Vec3d origin, Random random, out Vec3d goal)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(random);
        goal = origin;

        int originX = (int)Math.Floor(origin.X);
        int originZ = (int)Math.Floor(origin.Z);
        var candidates = new List<Vec3d>();

        for (int row = 0; row < region.Length; row++)
        {
            for (int column = 0; column < region.Width; column++)
            {
                SurfaceColumn info = region.Columns[(row * region.Width) + column];
                if (!info.Loaded || !info.Found || info.State.IsFluid)
                    continue;

                int x = region.OriginX + column;
                int z = region.OriginZ + row;
                if (x == originX && z == originZ)
                    // Never accept the current location as the goal.
                    continue;

                double standY = info.SurfaceY + 1;
                if (Math.Abs(standY - origin.Y) > MaxGoalHeightDelta)
                    continue;

                candidates.Add(new Vec3d(x + 0.5, standY, z + 0.5));
            }
        }

        if (candidates.Count == 0)
            return false;

        goal = candidates[random.Next(candidates.Count)];
        return true;
    }
}

/// <summary>The AntiAFK plugin entry point.</summary>
public sealed class AntiAfkPlugin : IPlugin
{
    private readonly Random _random = new();

    private AntiAfkSettings _settings = new();
    private ILogger? _logger;
    private IPluginLocalization? _strings;
    private PluginContext? _context;

    private int _count;
    private int _nextRun = AntiAfkTiming.ToTicks(5.0);
    private bool _previousSneakState;
    private bool _busy;
    private bool _warnedNoTerrain;
    private bool _warnedNoLease;

    /// <inheritdoc/>
    public void Configure(PluginDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        descriptor.Id = "anti-afk";
        descriptor.Version = "2.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
        descriptor.WithSettings<AntiAfkSettings>();
    }

    /// <inheritdoc/>
    public Task ActivateAsync(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _settings = context.Settings.Load<AntiAfkSettings>();
        _logger = context.Logger;
        _context = context;
        _strings = context.Strings;

        context.SessionStarted += (_, e) => OnSessionStarted(e.Session);
        context.SessionEnded += (_, _) => OnSessionEnded();
        return Task.CompletedTask;
    }

    private void OnSessionStarted(ISessionScope session)
    {
        // Every session restarts the countdown from scratch.
        _count = 0;
        _nextRun = AntiAfkTiming.ToTicks(5.0);
        _previousSneakState = false;
        _busy = false;
        _warnedNoTerrain = false;
        _warnedNoLease = false;

        session.Scheduler.OnTick(() => OnTick(session));
        _logger?.LogDebug(
            "{Message}",
            _strings?.Format("armed", _settings.Delay.Min, _settings.Delay.Max));
    }

    private void OnSessionEnded()
    {
        // The scheduler registration dies with the session scope, so only our own state needs resetting.
        _busy = false;
        _count = 0;
    }

    private void OnTick(ISessionScope session)
    {
        if (!_settings.Enabled || _busy)
            return;

        _count++;
        if (_count < _nextRun)
            return;

        _count = 0;
        _nextRun = AntiAfkTiming.NextDelayTicks(_settings.Delay.Min, _settings.Delay.Max, _random);
        _busy = true;
        _ = session.Scheduler.RunOffLoop(() => RunActionAsync(session), session.Detached);
    }

    private async Task RunActionAsync(ISessionScope session)
    {
        try
        {
            bool walked = await TryWalkAsync(session).ConfigureAwait(false);

            if (walked && _settings.Use_Sneak)
            {
                _previousSneakState = false;
                await SetSneakingAsync(session, false).ConfigureAwait(false);
                return;
            }

            if (_settings.Command.Length > 0 && _context is { } context)
            {
                await context.Game.Chat.SendAsync(_settings.Command, session.Detached).ConfigureAwait(false);
                _logger?.LogDebug("{Message}", _strings?.Format("sent", _settings.Command));
            }

            if (_settings.Use_Sneak)
            {
                await SetSneakingAsync(session, _previousSneakState).ConfigureAwait(false);
                _previousSneakState = !_previousSneakState;
            }
        }
        catch (OperationCanceledException)
        {
            // The session ended mid-action; nothing to do.
        }
        catch (Exception ex) when (ex is DmcbkClientException or InvalidOperationException)
        {
            _logger?.LogDebug(ex, "{Message}", _strings?.Get("action_failed"));
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Picks a reachable random goal within range and walks to it.
    /// Returns true only when the walk actually happened, which is what tells the caller to skip the command.
    /// </summary>
    private async Task<bool> TryWalkAsync(ISessionScope session)
    {
        if (!_settings.Use_Terrain_Handling || _context is not { } context)
            return false;

        if (!session.State.Features.Terrain)
        {
            if (!_warnedNoTerrain)
            {
                _warnedNoTerrain = true;
                _logger?.LogInformation("{Message}", _strings?.Get("terrain_disabled"));
            }

            return false;
        }

        // Acquiring the lease only proves movement is free.
        // It is handed straight back, because the lease is not re-entrant and the navigator acquires it itself for the duration of a move: holding it across the call makes every move throw "Movement is held by ...".
        using (IMovementLease? probe = session.TryAcquireMovement("AntiAFK"))
        {
            if (probe is null)
            {
                if (!_warnedNoLease)
                {
                    _warnedNoLease = true;
                    _logger?.LogInformation("{Message}", _strings?.Get("movement_held"));
                }

                return false;
            }
        }

        _warnedNoLease = false;

        PlayerPose pose = await context.Game.Movement.GetPoseAsync(session.Detached).ConfigureAwait(false);
        bool pathfinding = await context.Game.Movement
            .IsPathfindingAvailableAsync(session.Detached).ConfigureAwait(false);

        int range = _settings.Walk_Range;
        int span = (range * 2) + 1;
        SurfaceRegionSnapshot region = await context.Game.World.GetSurfaceRegionAsync(
                (int)Math.Floor(pose.Position.X) - range,
                (int)Math.Floor(pose.Position.Z) - range,
                span,
                span,
                ceilingY: (int)Math.Ceiling(pose.Position.Y) + AntiAfkTiming.MaxGoalHeightDelta,
                session.Detached)
            .ConfigureAwait(false);

        for (int tries = 0; tries < _settings.Walk_Retries; tries++)
        {
            // No reachable goal in the sample, so give up on walking and let the caller send the command.
            if (!AntiAfkTiming.TryPickGoal(region, pose.Position, _random, out Vec3d goal))
            {
                _logger?.LogDebug(
                    "{Message}",
                    _strings?.Format(
                        "no_goal",
                        region.Columns.Count,
                        AntiAfkTiming.CountStandable(region),
                        pose.Position.Y));
                return false;
            }

            try
            {
                if (pathfinding)
                {
                    await context.Game.Movement
                        .NavigateToAsync(BlockPos.Containing(goal), 1, session.Detached).ConfigureAwait(false);
                }
                else
                    await context.Game.Movement.MoveToAsync(goal, session.Detached).ConfigureAwait(false);

                _logger?.LogDebug("{Message}", _strings?.Format("walked", goal.X, goal.Z));
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is DmcbkClientException or DmcbkFeatureDisabledException or InvalidOperationException)
            {
                _logger?.LogDebug(ex, "{Message}", _strings?.Get("walk_attempt_failed"));
            }
        }

        return false;
    }

    private Task SetSneakingAsync(ISessionScope session, bool sneaking)
        => _context is { } context
            ? context.Game.Movement.SetSneakingAsync(sneaking, session.Detached)
            : Task.CompletedTask;
}
