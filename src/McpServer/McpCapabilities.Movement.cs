using DMCBK.Core;
using DMCBK.PluginSdk;
using Umpk.Client.Actions;
using Umpk.Data.Java;
using Umpk.Geometry;
using Umpk.Pathfinding;
using Umpk.Pathfinding.Core;
using Umpk.Pathfinding.Goals;

namespace DMCBK.OfficialPlugins.McpServer;

/// <summary>Movement, look, path-preview and block-interaction tools.</summary>
public sealed partial class McpCapabilities
{
    private static readonly SemaphoreSlim MoveGate = new(1, 1);

    private static int ClampTimeout(int timeoutMs, int @default)
        => timeoutMs <= 0 ? @default : Math.Clamp(timeoutMs, 250, 15000);

    private ISessionScope? CurrentScope() => _context.CurrentSession;

    private static BlockPos FloorBlock(double x, double y, double z)
        => new((int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z));

    private sealed record PreviewOutcome(
        bool PathFound, bool Partial, bool ExactReachable,
        Vec3d Start, BlockPos FinalWaypoint, double FinalDistance,
        IReadOnlyList<object> Waypoints, bool Truncated, int WaypointTotal);

    private async Task<PreviewOutcome?> PlanPreviewAsync(
        Vec3d target, bool allowUnsafe, int range, int timeoutMs, int maxWaypoints, CancellationToken ct)
    {
        ISessionScope? scope = CurrentScope();
        if (scope is null)
            return null;

        int protocol;
        try
        {
            protocol = scope.Client.Session?.Version.Version.Protocol ?? 0;
        }
        catch (Exception)
        {
            return null;
        }

        if (protocol <= 0)
            return null;

        var goalBlock = FloorBlock(target.X, target.Y, target.Z);
        PlanningWorldView view;
        Vec3d start;
        try
        {
            (view, start) = await scope.Client.InvokeAsync(client =>
            {
                Vec3d self = client.State.Self.Position;
                var startBlock = BlockPos.Containing(self);
                PlanningWorldView captured = PlanningWorldView.Capture(
                    client.State.World,
                    JavaGameData.BlockShapes(protocol),
                    startBlock, goalBlock, margin: 16);
                return (captured, self);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }

        var options = allowUnsafe ? PathfinderOptions.UnsafeFalls : PathfinderOptions.Default;
        var goal = new GoalNear(goalBlock.X, goalBlock.Y, goalBlock.Z, Math.Clamp(range, 0, 16));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(ClampTimeout(timeoutMs, 5000)));
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(ct, scope.Detached, timeout.Token);

        PathResult result;
        try
        {
            result = await PathPlanner.FindPathAsync(
                view, options, BlockPos.Containing(start), goal, ct: linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        int total = result.Path.Count;
        int take = Math.Clamp(maxWaypoints, 1, 1000);
        var waypoints = result.Path
            .Take(take)
            .Select(n => (object)new { x = n.X, y = n.Y, z = n.Z })
            .ToList();
        BlockPos final = result.Path.Count > 0 ? new BlockPos(
            result.Path[^1].X, result.Path[^1].Y, result.Path[^1].Z) : BlockPos.Containing(start);
        double finalDistance = Math.Sqrt(
            Math.Pow(final.X + 0.5 - target.X, 2)
            + Math.Pow(final.Y - target.Y, 2)
            + Math.Pow(final.Z + 0.5 - target.Z, 2));
        bool exact = result.Status == PathStatus.Success
            && final.X == goalBlock.X && final.Y == goalBlock.Y && final.Z == goalBlock.Z;

        return new PreviewOutcome(
            result.Status != PathStatus.Failed,
            result.Status == PathStatus.Partial,
            exact, start, final, Math.Round(finalDistance, 2),
            waypoints, total > take, total);
    }

    /// <summary>mcc_path_preview: plan without moving. A partial path reports partial:true, never reachable.</summary>
    public async Task<McpResult> PreviewPathAsync(
        double x, double y, double z, bool allowUnsafe,
        int maxOffset, int minOffset, int timeoutMs, int maxWaypoints, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.Movement);
        if (!gate.Success)
            return gate;

        if (!GameplayEnabled(g => g.Pathfinding))
            return McpResult.Fail(McpErrorCodes.FeatureDisabled, "The Pathfinding gameplay feature is disabled.");

        try
        {
            int range = Math.Max(0, Math.Max(maxOffset, minOffset));
            PreviewOutcome? preview = await PlanPreviewAsync(
                new Vec3d(x, y, z), allowUnsafe, range, timeoutMs, maxWaypoints, ct).ConfigureAwait(false);
            if (preview is null)
                return McpResult.Fail(McpErrorCodes.Disconnected, "No live session to plan from.");

            return McpResult.Ok(new
            {
                pathFound = preview.PathFound,
                partial = preview.Partial,
                exactReachable = preview.ExactReachable,
                target = new { x, y, z },
                startLocation = Loc(preview.Start.X, preview.Start.Y, preview.Start.Z),
                finalWaypoint = new { x = preview.FinalWaypoint.X, y = preview.FinalWaypoint.Y, z = preview.FinalWaypoint.Z },
                finalDistance = preview.FinalDistance,
                waypointCount = preview.WaypointTotal,
                truncated = preview.Truncated,
                waypoints = preview.Waypoints,
                allowUnsafe,
                previewNote = "Planning evidence only. A preview is not proof of arrival; verify with mcc_move_to.",
            });
        }
        catch (OperationCanceledException)
        {
            return McpResult.Fail(McpErrorCodes.Busy, "The preview timed out or the session ended.");
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_can_reach_position: reachability check without moving.</summary>
    public async Task<McpResult> CanReachPositionAsync(
        double x, double y, double z, bool allowUnsafe,
        int maxOffset, int minOffset, int timeoutMs, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.Movement);
        if (!gate.Success)
            return gate;

        if (!GameplayEnabled(g => g.Pathfinding))
            return McpResult.Fail(McpErrorCodes.FeatureDisabled, "The Pathfinding gameplay feature is disabled.");

        try
        {
            int range = Math.Max(0, Math.Max(maxOffset, minOffset));
            PreviewOutcome? preview = await PlanPreviewAsync(
                new Vec3d(x, y, z), allowUnsafe, range, timeoutMs, 1, ct).ConfigureAwait(false);
            if (preview is null)
                return McpResult.Fail(McpErrorCodes.Disconnected, "No live session to plan from.");

            return McpResult.Ok(new
            {
                reachable = preview.PathFound,
                partial = preview.Partial,
                exactReachable = preview.ExactReachable,
                target = new { x, y, z },
                startLocation = Loc(preview.Start.X, preview.Start.Y, preview.Start.Z),
                finalWaypoint = new { x = preview.FinalWaypoint.X, y = preview.FinalWaypoint.Y, z = preview.FinalWaypoint.Z },
                finalDistance = preview.FinalDistance,
            });
        }
        catch (OperationCanceledException)
        {
            return McpResult.Fail(McpErrorCodes.Busy, "The check timed out or the session ended.");
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_move_to: planner navigation with verified arrival. No teleport exists: anything asking for one is refused.</summary>
    public async Task<McpResult> MoveToAsync(
        double x, double y, double z, bool allowUnsafe, bool allowDirectTeleport,
        int maxOffset, int minOffset, int timeoutMs, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.Movement);
        if (!gate.Success)
            return gate;

        if (allowDirectTeleport)
        {
            return McpResult.Fail(
                McpErrorCodes.InvalidArgs,
                "allowDirectTeleport is not supported: the client has no teleport primitive, only planner navigation.");
        }

        if (!GameplayEnabled(g => g.Pathfinding))
            return McpResult.Fail(McpErrorCodes.FeatureDisabled, "The Pathfinding gameplay feature is disabled.");

        ISessionScope? scope = CurrentScope();
        if (scope is null)
            return McpResult.Fail(McpErrorCodes.Disconnected, "MCC is not connected to a server.");

        if (scope.MovementOwner is { } owner)
        {
            return McpResult.Fail(
                McpErrorCodes.MovementHeld, $"Movement is held by '{owner}'.");
        }

        int waitMs = timeoutMs <= 0 ? 30000 : Math.Clamp(timeoutMs, 1000, 120000);
        bool entered = await MoveGate.WaitAsync(0, ct).ConfigureAwait(false);
        if (!entered)
        {
            return McpResult.Fail(
                McpErrorCodes.MovementHeld, "Another MCP move is already running.");
        }

        try
        {
            // No explicit lease here: NavigateVerifiedAsync acquires the exclusive movement lease itself and releases it when the navigation ends.
            // Acquiring one around it would self-deadlock (the inner acquire sees our own hold and refuses).
            // The precheck above names a foreign holder; the catch below maps a lost race to the same envelope.
            var target = new Vec3d(x, y, z);
            var goalBlock = FloorBlock(x, y, z);
            int range = Math.Clamp(Math.Max(maxOffset, minOffset), 0, 16);
            var goal = new GoalNear(goalBlock.X, goalBlock.Y, goalBlock.Z, range);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(waitMs));
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(ct, scope.Detached, timeout.Token);

            PlayerPose before = await _context.Game.Movement.GetPoseAsync(linked.Token).ConfigureAwait(false);
            Umpk.Client.Navigation.MoveResult result;
            try
            {
                // The MCC facade honors the movement lease internally (same call the old direct session action made), with the planner options allowUnsafe selects.
                result = await _context.Game.Movement.NavigateVerifiedAsync(
                    goal, target, allowUnsafe, linked.Token).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("held", StringComparison.OrdinalIgnoreCase))
            {
                return McpResult.Fail(McpErrorCodes.MovementHeld, ex.Message);
            }
            catch (OperationCanceledException) when (scope.Detached.IsCancellationRequested)
            {
                return McpResult.Fail(McpErrorCodes.Disconnected, "The session ended mid-move.");
            }
            catch (OperationCanceledException)
            {
                return McpResult.Fail(McpErrorCodes.ActionIncomplete, "The move timed out.", new
                {
                    target = new { x, y, z },
                    verifyWaitMs = waitMs,
                });
            }

            PlayerPose after = await _context.Game.Movement.GetPoseAsync(ct).ConfigureAwait(false);
            double tolerance = Math.Max(1.5, Math.Max(maxOffset, minOffset) + 1);
            double finalDistance = Math.Sqrt(
                Math.Pow(after.Position.X - x, 2)
                + Math.Pow(after.Position.Y - y, 2)
                + Math.Pow(after.Position.Z - z, 2));
            // Arrival is tolerance-based: Reached is the planner's exact verdict, but a goal with a nonzero range asks for nearness, and standing inside the tolerance counts as arrived.
            // The raw outcome rides along so agents can tell exact arrivals from near ones.
            bool arrived = result.Reached || finalDistance <= tolerance;

            if (!arrived)
            {
                return McpResult.Fail(
                    McpErrorCodes.ActionIncomplete,
                    "The move did not verify: the player stopped short of the target.",
                    new
                    {
                        pathFound = true,
                        arrived = false,
                        tolerance,
                        verifyWaitMs = waitMs,
                        target = new { x, y, z },
                        startLocation = Loc(before.Position.X, before.Position.Y, before.Position.Z),
                        finalLocation = Loc(after.Position.X, after.Position.Y, after.Position.Z),
                        finalDistance = Math.Round(finalDistance, 2),
                        outcome = result.Outcome.ToString(),
                    });
            }

            double moved = Math.Sqrt(
                Math.Pow(after.Position.X - before.Position.X, 2)
                + Math.Pow(after.Position.Y - before.Position.Y, 2)
                + Math.Pow(after.Position.Z - before.Position.Z, 2));
            return McpResult.Ok(new
            {
                pathFound = true,
                arrived = true,
                tolerance,
                verifyWaitMs = waitMs,
                allowUnsafe,
                unsafeNote = allowUnsafe
                    ? "Planned with fall-damage guards lifted: this route may kill the player on landing."
                    : null,
                target = new { x, y, z },
                startLocation = Loc(before.Position.X, before.Position.Y, before.Position.Z),
                finalLocation = Loc(after.Position.X, after.Position.Y, after.Position.Z),
                finalDistance = Math.Round(finalDistance, 2),
                distanceMoved = Math.Round(moved, 2),
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
        finally
        {
            MoveGate.Release();
        }
    }

    /// <summary>mcc_move_to_player: resolve a tracked player, then navigate to them.</summary>
    public async Task<McpResult> MoveToPlayerAsync(
        string playerName, bool allowUnsafe, bool allowDirectTeleport,
        int maxOffset, int minOffset, int timeoutMs, CancellationToken ct)
    {
        McpResult located = await LocatePlayerAsync(playerName, false, ct).ConfigureAwait(false);
        if (!located.Success || located.Data is null)
            return located;

        double x, y, z;
        try
        {
            dynamic data = located.Data;
            x = (double)data.x;
            y = (double)data.y;
            z = (double)data.z;
        }
        catch (Exception)
        {
            return McpResult.Fail(McpErrorCodes.ActionFailed, "Could not read the located player position.");
        }

        McpResult moved = await MoveToAsync(
            x, y, z, allowUnsafe, allowDirectTeleport, maxOffset, minOffset, timeoutMs, ct).ConfigureAwait(false);
        if (!moved.Success)
            return moved;

        return McpResult.Ok(new
        {
            playerName,
            target = new { x, y, z },
            move = moved.Data,
        });
    }

    /// <summary>mcc_look_at: face a world position.</summary>
    public async Task<McpResult> LookAtAsync(double x, double y, double z, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.Movement);
        if (!gate.Success)
            return gate;

        try
        {
            await _context.Game.Movement.LookAtAsync(new Vec3d(x, y, z), ct).ConfigureAwait(false);
            PlayerPose pose = await _context.Game.Movement.GetPoseAsync(ct).ConfigureAwait(false);
            return McpResult.Ok(new
            {
                success = true,
                yaw = pose.Yaw,
                pitch = pose.Pitch,
                target = new { x, y, z },
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_look_direction: face a cardinal direction or straight up/down.</summary>
    public async Task<McpResult> LookDirectionAsync(string direction, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.Movement);
        if (!gate.Success)
            return gate;

        (float Yaw, float Pitch)? angles = direction.Trim().ToLowerInvariant() switch
        {
            "south" => (0f, 0f),
            "west" => (90f, 0f),
            "north" => (180f, 0f),
            "east" => (-90f, 0f),
            _ => null,
        };

        try
        {
            if (angles.HasValue)
            {
                await _context.Game.Movement
                    .SetRotationAsync(angles.Value.Yaw, angles.Value.Pitch, ct).ConfigureAwait(false);
            }
            else if (string.Equals(direction, "up", StringComparison.OrdinalIgnoreCase)
                || string.Equals(direction, "down", StringComparison.OrdinalIgnoreCase))
            {
                PlayerPose pose = await _context.Game.Movement.GetPoseAsync(ct).ConfigureAwait(false);
                float pitch = string.Equals(direction, "up", StringComparison.OrdinalIgnoreCase) ? -90f : 90f;
                await _context.Game.Movement.SetRotationAsync(pose.Yaw, pitch, ct).ConfigureAwait(false);
            }
            else
            {
                return McpResult.Fail(
                    McpErrorCodes.InvalidArgs, "direction must be Up, Down, North, South, East or West.");
            }

            PlayerPose after = await _context.Game.Movement.GetPoseAsync(ct).ConfigureAwait(false);
            return McpResult.Ok(new { success = true, yaw = after.Yaw, pitch = after.Pitch });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_look_angles: set explicit yaw/pitch.</summary>
    public async Task<McpResult> LookAnglesAsync(float yaw, float pitch, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.Movement);
        if (!gate.Success)
            return gate;

        try
        {
            await _context.Game.Movement.SetRotationAsync(yaw, pitch, ct).ConfigureAwait(false);
            PlayerPose pose = await _context.Game.Movement.GetPoseAsync(ct).ConfigureAwait(false);
            return McpResult.Ok(new { success = true, yaw = pose.Yaw, pitch = pose.Pitch });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_use_item_on_hand: right-click with the held item. Send-only; the effect is unknown.</summary>
    public async Task<McpResult> UseItemOnHandAsync(CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.Movement);
        if (!gate.Success)
            return gate;

        try
        {
            await _context.Game.World.UseItemAsync(Hand.Main, ct).ConfigureAwait(false);
            return McpResult.Ok(new
            {
                success = true,
                effectNote = "Completion is the send, not the effect.",
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_use_item_on_block: use the held item against a block. Send-only; the effect is unknown.</summary>
    public async Task<McpResult> UseItemOnBlockAsync(double x, double y, double z, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.Movement);
        if (!gate.Success)
            return gate;

        try
        {
            BlockPos pos = FloorBlock(x, y, z);
            await _context.Game.World.PlaceBlockAsync(
                pos, Direction.Up, new Vec3d(0.5, 1.0, 0.5), Hand.Main, ct).ConfigureAwait(false);
            return McpResult.Ok(new
            {
                success = true,
                target = new { x = pos.X, y = pos.Y, z = pos.Z },
                effectNote = "A use-on-block is whatever the target block decides it is (open a container, flip a lever, place a block, or nothing). Completion is the send; verify the world afterwards.",
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_dig_block: breaks a block through the verified dig sequence and reports what the server observably did.</summary>
    public async Task<McpResult> DigBlockAsync(double x, double y, double z, double durationSeconds, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.Movement);
        if (!gate.Success)
            return gate;

        if (!GameplayEnabled(g => g.Terrain))
            return McpResult.Fail(McpErrorCodes.FeatureDisabled, "The Terrain gameplay feature is disabled.");

        try
        {
            BlockPos pos = FloorBlock(x, y, z);
            PlayerPose pose = await _context.Game.Movement.GetPoseAsync(ct).ConfigureAwait(false);
            var eye = new Vec3d(pose.Position.X, pose.Position.Y + 1.62, pose.Position.Z);
            var center = new Vec3d(pos.X + 0.5, pos.Y + 0.5, pos.Z + 0.5);
            double distance = Math.Sqrt(
                Math.Pow(eye.X - center.X, 2) + Math.Pow(eye.Y - center.Y, 2) + Math.Pow(eye.Z - center.Z, 2));
            if (distance > 5.0)
            {
                return McpResult.Fail(
                    McpErrorCodes.InvalidState,
                    $"Target is {Math.Round(distance, 2)} blocks away; dig reach is 5.0.",
                    new { distance = Math.Round(distance, 2) });
            }

            BlockInfo before = await _context.Game.World.GetBlockAsync(pos, ct).ConfigureAwait(false);
            if (before.IsAir)
                return McpResult.Fail(McpErrorCodes.InvalidState, "Target is air; nothing to dig.");

            DigOutcome outcome = await _context.Game.World
                .DigBlockAsync(pos, Direction.Down, ct).ConfigureAwait(false);
            BlockInfo after;
            try
            {
                after = await _context.Game.World.GetBlockAsync(pos, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                after = before;
            }

            object payload = new
            {
                target = new { x = pos.X, y = pos.Y, z = pos.Z },
                beforeBlock = before.BlockId,
                afterBlock = after.BlockId,
                destroyed = after.IsAir,
                outcome = outcome.ToString(),
                distance = Math.Round(distance, 2),
                durationNote = durationSeconds > 0
                    ? "durationSeconds has no counterpart in the verified dig; the dig runs until the server confirms or the window closes."
                    : null,
            };

            return outcome switch
            {
                DigOutcome.Broken => McpResult.Ok(payload),
                DigOutcome.NotBroken => McpResult.Fail(
                    McpErrorCodes.ActionFailed, "The server re-asserted the block: an observed refusal.", payload),
                _ => McpResult.Fail(
                    McpErrorCodes.ActionIncomplete,
                    "No confirmation arrived inside the window; the client does not know what happened.", payload),
            };
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_place_block: uses the held item against a block face. Send-only; the effect is unknown.</summary>
    public async Task<McpResult> PlaceBlockAsync(
        int x, int y, int z, string face, string hand, bool lookAtBlock, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.Movement);
        if (!gate.Success)
            return gate;

        if (!TryParseDirection(face, out Direction direction))
        {
            return McpResult.Fail(
                McpErrorCodes.InvalidArgs, "face must be Up, Down, North, South, East or West.");
        }

        if (!TryParseHand(hand, out Hand parsedHand))
            return McpResult.Fail(McpErrorCodes.InvalidArgs, "hand must be MainHand or OffHand.");

        try
        {
            var pos = new BlockPos(x, y, z);
            if (lookAtBlock)
            {
                await _context.Game.Movement
                    .LookAtAsync(new Vec3d(x + 0.5, y + 0.5, z + 0.5), ct).ConfigureAwait(false);
            }

            await _context.Game.World.PlaceBlockAsync(
                pos, direction, new Vec3d(0.5, 0.5, 0.5), parsedHand, ct).ConfigureAwait(false);
            return McpResult.Ok(new
            {
                success = true,
                target = new { x, y, z },
                face,
                hand,
                effectNote = "Completion is the send (plus the version's action acknowledgement where one exists), not the effect. Verify the world afterwards.",
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>Parses a face/direction name. Public so the accepted vocabulary is pinned by tests.</summary>
    public static bool TryParseDirection(string face, out Direction direction)
    {
        string normalized = face.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "up":
            case "top":
                direction = Direction.Up;
                return true;
            case "down":
            case "bottom":
                direction = Direction.Down;
                return true;
            case "north":
                direction = Direction.North;
                return true;
            case "south":
                direction = Direction.South;
                return true;
            case "east":
                direction = Direction.East;
                return true;
            case "west":
                direction = Direction.West;
                return true;
            default:
                direction = default;
                return false;
        }
    }
}
