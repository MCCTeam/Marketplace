using DMCBK.Core;
using DMCBK.PluginSdk;

namespace DMCBK.OfficialPlugins.McpServer;

/// <summary>Entity, player-proximity, sign and item read tools.</summary>
public sealed partial class McpCapabilities
{
    private static object ProjectEntity(EntitySnapshot e, bool includeDetails)
    {
        return new
        {
            id = e.Id,
            type = e.TypeId,
            uuid = e.Uuid,
            name = e.PlayerName,
            customName = e.CustomName,
            x = Math.Round(e.Position.X, 2),
            y = Math.Round(e.Position.Y, 2),
            z = Math.Round(e.Position.Z, 2),
            yaw = e.Yaw,
            pitch = e.Pitch,
            onGround = e.OnGround,
            pose = e.Pose,
            details = includeDetails ? new
            {
                velocity = new { x = e.Position.X, y = e.Position.Y, z = e.Position.Z },
                equipment = e.Equipment.ToDictionary(k => k.Key, v => v.Value.ItemId),
                effects = e.Effects.Select(x => x.EffectId).ToList(),
                passengers = e.PassengerIds.ToList(),
                vehicleId = e.VehicleId,
            } : null,
        };
    }

    private static double DistanceTo(EntitySnapshot e, double x, double y, double z)
    {
        double dx = e.Position.X - x;
        double dy = e.Position.Y - y;
        double dz = e.Position.Z - z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>mcc_entities_query: first N tracked entities.</summary>
    public async Task<McpResult> QueryEntitiesAsync(int maxCount, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.EntityWorld);
        if (!gate.Success)
            return gate;

        maxCount = Math.Clamp(maxCount, 1, 1000);
        try
        {
            IReadOnlyList<EntitySnapshot> entities =
                await _context.Game.Entities.AllAsync(ct).ConfigureAwait(false);
            var rows = entities.Take(maxCount).Select(e => ProjectEntity(e, false)).ToList();
            return McpResult.Ok(new { count = rows.Count, entities = rows });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_entities_list: type/radius-filtered entities sorted by distance.</summary>
    public async Task<McpResult> ListEntitiesAsync(int maxCount, string? typeFilter, double radius, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.EntityWorld);
        if (!gate.Success)
            return gate;

        maxCount = Math.Clamp(maxCount, 1, 1000);
        try
        {
            PlayerStatus player = await _context.Game.Player.GetStatusAsync(ct).ConfigureAwait(false);
            IReadOnlyList<EntitySnapshot> entities =
                await _context.Game.Entities.AllAsync(ct).ConfigureAwait(false);
            var rows = entities
                .Where(e => string.IsNullOrWhiteSpace(typeFilter)
                    || e.TypeId.Contains(typeFilter, StringComparison.OrdinalIgnoreCase))
                .Select(e => (Entity: e, Distance: DistanceTo(e, player.Position.X, player.Position.Y, player.Position.Z)))
                .Where(t => radius <= 0 || t.Distance <= radius)
                .OrderBy(t => t.Distance)
                .Take(maxCount)
                .Select(t => new
                {
                    id = t.Entity.Id,
                    type = t.Entity.TypeId,
                    uuid = t.Entity.Uuid,
                    name = t.Entity.PlayerName,
                    customName = t.Entity.CustomName,
                    x = Math.Round(t.Entity.Position.X, 2),
                    y = Math.Round(t.Entity.Position.Y, 2),
                    z = Math.Round(t.Entity.Position.Z, 2),
                    distance = Math.Round(t.Distance, 2),
                    pose = t.Entity.Pose,
                })
                .ToList<object>();
            return McpResult.Ok(new { totalTracked = entities.Count, count = rows.Count, entities = rows });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_entity_info: full detail for one tracked entity.</summary>
    public async Task<McpResult> GetEntityInfoAsync(
        int entityId, bool includeMetadata, bool includeEquipment, bool includeEffects, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.EntityWorld);
        if (!gate.Success)
            return gate;

        try
        {
            EntitySnapshot? entity = await _context.Game.Entities.ByIdAsync(entityId, ct).ConfigureAwait(false);
            if (entity is null)
                return McpResult.Fail(McpErrorCodes.InvalidState, $"No tracked entity with id {entityId}.");

            return McpResult.Ok(new
            {
                id = entity.Id,
                type = entity.TypeId,
                uuid = entity.Uuid,
                name = entity.PlayerName,
                customName = entity.CustomName,
                x = Math.Round(entity.Position.X, 2),
                y = Math.Round(entity.Position.Y, 2),
                z = Math.Round(entity.Position.Z, 2),
                yaw = entity.Yaw,
                pitch = entity.Pitch,
                headYaw = entity.HeadYaw,
                onGround = entity.OnGround,
                pose = entity.Pose,
                equipment = includeEquipment
                    ? entity.Equipment.ToDictionary(k => k.Key, v => new { type = v.Value.ItemId, count = v.Value.Count })
                    : null,
                effects = includeEffects
                    ? entity.Effects.Select(e => new
                    {
                        id = e.EffectId,
                        amplifier = e.Amplifier,
                        remainingTicks = e.Duration,
                        isInfinite = e.IsInfinite,
                    }).ToList()
                    : null,
                metadataNote = includeMetadata
                    ? "Raw entity metadata has no counterpart in the new snapshots; identity, pose, equipment and effects are reported instead."
                    : null,
                passengers = entity.PassengerIds.ToList(),
                vehicleId = entity.VehicleId,
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_entity_nearest: closest tracked entity matching type/name filters.</summary>
    public async Task<McpResult> FindNearestEntityAsync(
        string? typeFilter, string? nameFilter, double radius, bool includePlayers, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.EntityWorld);
        if (!gate.Success)
            return gate;

        if (radius is <= 0 or > 1024)
            return McpResult.Fail(McpErrorCodes.InvalidArgs, "radius must be within (0, 1024].");

        try
        {
            PlayerStatus player = await _context.Game.Player.GetStatusAsync(ct).ConfigureAwait(false);
            IReadOnlyList<EntitySnapshot> entities =
                await _context.Game.Entities.AllAsync(ct).ConfigureAwait(false);
            var best = entities
                .Where(e => includePlayers || e.PlayerName is null)
                .Where(e => string.IsNullOrWhiteSpace(typeFilter)
                    || e.TypeId.Contains(typeFilter, StringComparison.OrdinalIgnoreCase))
                .Where(e => string.IsNullOrWhiteSpace(nameFilter)
                    || (e.PlayerName?.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (e.CustomName?.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ?? false))
                .Select(e => (Entity: e, Distance: DistanceTo(e, player.Position.X, player.Position.Y, player.Position.Z)))
                .Where(t => t.Distance <= radius)
                .OrderBy(t => t.Distance)
                .FirstOrDefault();

            if (best.Entity is null)
                return McpResult.Ok(new { found = false });

            return McpResult.Ok(new
            {
                found = true,
                id = best.Entity.Id,
                type = best.Entity.TypeId,
                name = best.Entity.PlayerName,
                customName = best.Entity.CustomName,
                x = Math.Round(best.Entity.Position.X, 2),
                y = Math.Round(best.Entity.Position.Y, 2),
                z = Math.Round(best.Entity.Position.Z, 2),
                distance = Math.Round(best.Distance, 2),
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_player_nearby: whether any (or a named) player is within radius.</summary>
    public async Task<McpResult> IsPlayerNearbyAsync(
        string? playerName, double radius, bool includeSelf, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.EntityWorld);
        if (!gate.Success)
            return gate;

        if (radius is <= 0 or > 1024)
            return McpResult.Fail(McpErrorCodes.InvalidArgs, "radius must be within (0, 1024].");

        try
        {
            PlayerStatus player = await _context.Game.Player.GetStatusAsync(ct).ConfigureAwait(false);
            string self = await UsernameAsync(ct).ConfigureAwait(false);
            IReadOnlyList<EntitySnapshot> entities =
                await _context.Game.Entities.AllAsync(ct).ConfigureAwait(false);
            var rows = entities
                .Where(e => e.PlayerName is not null)
                .Where(e => includeSelf || !string.Equals(e.PlayerName, self, StringComparison.OrdinalIgnoreCase))
                .Where(e => string.IsNullOrWhiteSpace(playerName)
                    || string.Equals(e.PlayerName, playerName, StringComparison.OrdinalIgnoreCase))
                .Select(e => (Entity: e, Distance: DistanceTo(e, player.Position.X, player.Position.Y, player.Position.Z)))
                .Where(t => t.Distance <= radius)
                .OrderBy(t => t.Distance)
                .Select(t => new
                {
                    entityId = t.Entity.Id,
                    name = t.Entity.PlayerName,
                    x = Math.Round(t.Entity.Position.X, 2),
                    y = Math.Round(t.Entity.Position.Y, 2),
                    z = Math.Round(t.Entity.Position.Z, 2),
                    distance = Math.Round(t.Distance, 2),
                })
                .ToList<object>();

            return McpResult.Ok(new
            {
                radius,
                playerName,
                includeSelf,
                anyNearby = rows.Count > 0,
                count = rows.Count,
                players = rows,
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_player_locate: exact coordinates of a tracked player by name.</summary>
    public async Task<McpResult> LocatePlayerAsync(string playerName, bool includeSelf, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.EntityWorld);
        if (!gate.Success)
            return gate;

        if (string.IsNullOrWhiteSpace(playerName))
            return McpResult.Fail(McpErrorCodes.InvalidArgs, "playerName is required.");

        try
        {
            PlayerStatus player = await _context.Game.Player.GetStatusAsync(ct).ConfigureAwait(false);
            string self = await UsernameAsync(ct).ConfigureAwait(false);
            IReadOnlyList<EntitySnapshot> entities =
                await _context.Game.Entities.AllAsync(ct).ConfigureAwait(false);
            var match = entities
                .Where(e => e.PlayerName is not null)
                .Where(e => includeSelf || !string.Equals(e.PlayerName, self, StringComparison.OrdinalIgnoreCase))
                .Where(e => e.PlayerName!.Contains(playerName, StringComparison.OrdinalIgnoreCase)
                    || (e.CustomName?.Contains(playerName, StringComparison.OrdinalIgnoreCase) ?? false))
                .Select(e => (Entity: e, Distance: DistanceTo(e, player.Position.X, player.Position.Y, player.Position.Z)))
                .OrderBy(t => t.Distance)
                .FirstOrDefault();

            if (match.Entity is null)
            {
                return McpResult.Fail(
                    McpErrorCodes.InvalidState,
                    $"No tracked player matches '{playerName}'.",
                    new { playerName });
            }

            return McpResult.Ok(new
            {
                playerName,
                matchedName = match.Entity.PlayerName,
                entityId = match.Entity.Id,
                uuid = match.Entity.Uuid,
                x = Math.Round(match.Entity.Position.X, 2),
                y = Math.Round(match.Entity.Position.Y, 2),
                z = Math.Round(match.Entity.Position.Z, 2),
                distance = Math.Round(match.Distance, 2),
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_signs_find: signs whose joined text contains the needle, nearest first.</summary>
    public async Task<McpResult> FindSignsAsync(
        string text, bool exactMatch, int radius, int maxCount, bool includeBackText, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.EntityWorld);
        if (!gate.Success)
            return gate;

        if (string.IsNullOrWhiteSpace(text))
            return McpResult.Fail(McpErrorCodes.InvalidArgs, "text is required.");

        radius = Math.Clamp(radius, 1, 32);
        maxCount = Math.Clamp(maxCount, 1, 500);

        try
        {
            IReadOnlyList<SignInfo> signs = await _context.Game.World
                .FindSignsAsync(text.Trim(), radius, maxCount, ct).ConfigureAwait(false);
            string needle = text.Trim();
            var rows = signs
                .Where(s => exactMatch
                    ? s.Text.Split('\n').Any(line =>
                        string.Equals(line.Trim(), needle, StringComparison.OrdinalIgnoreCase))
                    : true)
                .Select(s => new
                {
                    x = s.Position.X,
                    y = s.Position.Y,
                    z = s.Position.Z,
                    text = s.Text,
                    backText = includeBackText ? (string?)null : null,
                    backTextNote = includeBackText
                        ? "Front/back faces are joined; the new snapshots do not separate them."
                        : null,
                })
                .ToList<object>();
            return McpResult.Ok(new
            {
                text,
                exactMatch,
                radius,
                count = rows.Count,
                signs = rows,
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>One identified dropped-item entity.</summary>
    internal sealed record ItemHit(
        int EntityId, string ItemType, int Count, double X, double Y, double Z, double Distance);

    /// <summary>Lists identified dropped-item entities nearest-first.</summary>
    internal async Task<(IReadOnlyList<ItemHit> Hits, int Unidentified)> FindItemEntitiesAsync(
        string? itemType, double radius, int maxCount, CancellationToken ct)
    {
        PlayerStatus player = await _context.Game.Player.GetStatusAsync(ct).ConfigureAwait(false);
        IReadOnlyList<EntitySnapshot> entities =
            await _context.Game.Entities.AllAsync(ct).ConfigureAwait(false);
        var hits = new List<(ItemHit Hit, double Distance)>();
        int unidentified = 0;
        foreach (EntitySnapshot e in entities)
        {
            if (!string.Equals(e.TypeId, "minecraft:item", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(e.TypeId, "item", StringComparison.OrdinalIgnoreCase))
                continue;

            double distance = DistanceTo(e, player.Position.X, player.Position.Y, player.Position.Z);
            if (distance > radius)
                continue;

            if (e.CarriedItem is null || e.CarriedItem.IsEmpty)
            {
                unidentified++;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(itemType)
                && !MatchesItem(e.CarriedItem.ItemId, itemType.Trim(), true))
                continue;

            hits.Add((new ItemHit(
                e.Id, e.CarriedItem.ItemId, e.CarriedItem.Count,
                Math.Round(e.Position.X, 2), Math.Round(e.Position.Y, 2), Math.Round(e.Position.Z, 2),
                Math.Round(distance, 2)), distance));
        }

        var ordered = hits
            .OrderBy(h => h.Distance)
            .Take(Math.Clamp(maxCount, 1, 500))
            .Select(h => h.Hit)
            .ToList();
        return (ordered, unidentified);
    }

    /// <summary>mcc_items_list: nearby dropped-item entities with identified contents.</summary>
    public async Task<McpResult> ListItemEntitiesAsync(string? itemType, double radius, int maxCount, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.EntityWorld);
        if (!gate.Success)
            return gate;

        if (radius is <= 0 or > 1024)
            return McpResult.Fail(McpErrorCodes.InvalidArgs, "radius must be within (0, 1024].");

        try
        {
            (IReadOnlyList<ItemHit> hits, int unidentified) =
                await FindItemEntitiesAsync(itemType, radius, maxCount, ct).ConfigureAwait(false);
            return McpResult.Ok(new
            {
                itemType,
                radius,
                count = hits.Count,
                unidentifiedNearby = unidentified,
                unidentifiedNote = unidentified > 0
                    ? "Item entities whose contents are not yet known are counted, not listed."
                    : null,
                items = hits.Select(h => new
                {
                    entityId = h.EntityId,
                    itemType = h.ItemType,
                    typeLabel = h.ItemType,
                    count = h.Count,
                    x = h.X,
                    y = h.Y,
                    z = h.Z,
                    distance = h.Distance,
                }).ToList(),
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_items_pickup: walks to dropped items and verifies pickup via inventory delta.</summary>
    public async Task<McpResult> PickupItemsAsync(
        string itemType, double radius, int maxItems, bool allowUnsafe, int timeoutMs, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.Movement);
        if (!gate.Success)
            return gate;

        if (string.IsNullOrWhiteSpace(itemType))
            return McpResult.Fail(McpErrorCodes.InvalidArgs, "itemType is required.");

        if (radius is <= 0 or > 1024)
            return McpResult.Fail(McpErrorCodes.InvalidArgs, "radius must be within (0, 1024].");

        maxItems = Math.Clamp(maxItems, 1, 50);
        int budgetMs = timeoutMs <= 0 ? 60000 : Math.Clamp(timeoutMs, 5000, 120000);

        ISessionScope? scope = CurrentScope();
        if (scope is null)
            return McpResult.Fail(McpErrorCodes.Disconnected, "MCC is not connected to a server.");

        if (scope.MovementOwner is { } owner)
            return McpResult.Fail(McpErrorCodes.MovementHeld, $"Movement is held by '{owner}'.");

        bool entered = await MoveGate.WaitAsync(0, ct).ConfigureAwait(false);
        if (!entered)
            return McpResult.Fail(McpErrorCodes.MovementHeld, "Another MCP move is already running.");

        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(budgetMs));
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(ct, scope.Detached, budget.Token);

            McpResult listed = await ListItemEntitiesAsync(itemType, radius, maxItems, linked.Token).ConfigureAwait(false);
            if (!listed.Success || listed.Data is null)
                return listed;

            // Candidates re-resolved per attempt below; the initial list bounds the work.
            PlayerInventorySnapshot before =
                await _context.Game.Inventory.GetPlayerInventoryAsync(linked.Token).ConfigureAwait(false);
            int initialCount = CountOf(before.Slots, itemType.Trim(), true);

            var attempts = new List<object>();
            int successful = 0;
            for (int i = 0; i < maxItems; i++)
            {
                linked.Token.ThrowIfCancellationRequested();
                (IReadOnlyList<ItemHit> candidates, _) =
                    await FindItemEntitiesAsync(itemType, radius, 1, linked.Token).ConfigureAwait(false);
                if (candidates.Count == 0)
                    break;

                ItemHit target = candidates[0];
                var goal = new Umpk.Pathfinding.Goals.GoalNear(
                    (int)Math.Floor(target.X), (int)Math.Floor(target.Y), (int)Math.Floor(target.Z), 1);
                bool moved = false;
                bool gone = false;
                try
                {
                    await _context.Game.Movement.NavigateVerifiedAsync(
                        goal, new Umpk.Geometry.Vec3d(target.X, target.Y, target.Z), allowUnsafe, linked.Token).ConfigureAwait(false);
                    moved = true;
                    // The planner stops at the goal edge, which can sit outside the pickup magnet.
                    // A best-effort straight-line step onto the item itself closes the last gap.
                    try
                    {
                        await _context.Game.Movement.MoveToVerifiedAsync(
                            new Umpk.Geometry.Vec3d(target.X, target.Y, target.Z), linked.Token).ConfigureAwait(false);
                    }
                    catch (Exception stepEx) when (stepEx is not OperationCanceledException)
                    {
                    }

                    gone = await WaitForEntityGoneAsync(target.EntityId, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (scope.Detached.IsCancellationRequested)
                {
                    return McpResult.Fail(McpErrorCodes.Disconnected, "The session ended mid-pickup.");
                }
                catch (OperationCanceledException)
                {
                    attempts.Add(new { entityId = target.EntityId, moved, pickedUp = false, note = "Per-item budget spent." });
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    attempts.Add(new { entityId = target.EntityId, moved = false, pickedUp = false, error = ex.Message });
                    continue;
                }

                if (gone)
                    successful++;

                attempts.Add(new { entityId = target.EntityId, moved, pickedUp = gone });
            }

            PlayerInventorySnapshot after;
            if (successful > 0)
            {
                // Let the last magnet collection echo in before the final count.
                after = await WaitForPlayerCountAsync(
                    itemType, initialCount + 1, 5000, ct).ConfigureAwait(false)
                    ?? await _context.Game.Inventory.GetPlayerInventoryAsync(ct).ConfigureAwait(false);
            }
            else
                after = await _context.Game.Inventory.GetPlayerInventoryAsync(ct).ConfigureAwait(false);
            int finalCount = CountOf(after.Slots, itemType.Trim(), true);
            (IReadOnlyList<ItemHit> rest, _) =
                await FindItemEntitiesAsync(itemType, radius, maxItems, ct).ConfigureAwait(false);

            object payload = new
            {
                itemType,
                radius,
                maxItems,
                allowUnsafe,
                timeoutMs = budgetMs,
                attempted = attempts.Count,
                successfulPickups = successful,
                collectedCount = finalCount - initialCount,
                initialInventoryCount = initialCount,
                finalInventoryCount = finalCount,
                remainingNearby = rest.Count,
                attempts,
            };

            if (attempts.Count == 0)
                return McpResult.Ok(payload);

            return successful > 0
                ? McpResult.Ok(payload)
                : McpResult.Fail(McpErrorCodes.ActionIncomplete, "No items were picked up.", payload);
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

    private async Task<bool> WaitForEntityGoneAsync(int entityId, CancellationToken ct)
    {
        long deadline = Environment.TickCount64 + 5000;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            EntitySnapshot? entity = await _context.Game.Entities.ByIdAsync(entityId, ct).ConfigureAwait(false);
            if (entity is null)
                return true;

            if (Environment.TickCount64 >= deadline)
                return false;

            await Task.Delay(200, ct).ConfigureAwait(false);
        }
    }

    private async Task<PlayerInventorySnapshot?> WaitForPlayerCountAsync(
        string itemType, int atLeast, int timeoutMs, CancellationToken ct)
    {
        PlayerInventorySnapshot? latest = null;
        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            latest = await _context.Game.Inventory.GetPlayerInventoryAsync(ct).ConfigureAwait(false);
            if (CountOf(latest.Slots, itemType.Trim(), true) >= atLeast)
                return latest;

            if (Environment.TickCount64 >= deadline)
                return latest;

            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    /// <summary>mcc_entity_interact: right-click a tracked entity.</summary>
    public async Task<McpResult> InteractEntityAsync(int entityId, string interaction, string hand, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.Movement);
        if (!gate.Success)
            return gate;

        if (!string.Equals(interaction, "Interact", StringComparison.OrdinalIgnoreCase))
        {
            return McpResult.Fail(
                McpErrorCodes.InvalidArgs, "Only the Interact interaction is supported in this version.");
        }

        if (!TryParseHand(hand, out Umpk.Client.Actions.Hand parsedHand))
            return McpResult.Fail(McpErrorCodes.InvalidArgs, "hand must be MainHand or OffHand.");

        try
        {
            EntitySnapshot? entity = await _context.Game.Entities.ByIdAsync(entityId, ct).ConfigureAwait(false);
            if (entity is null)
                return McpResult.Fail(McpErrorCodes.InvalidState, $"No tracked entity with id {entityId}.");

            await _context.Game.Entities.InteractAsync(entityId, parsedHand, ct).ConfigureAwait(false);
            return McpResult.Ok(new
            {
                entityId,
                interaction = "Interact",
                hand,
                sent = true,
                effectNote = "Completion is the send, not the effect: the server reports nothing back about what the interaction did.",
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_entity_attack: left-click a tracked entity.</summary>
    public async Task<McpResult> AttackEntityAsync(int entityId, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.Movement);
        if (!gate.Success)
            return gate;

        try
        {
            EntitySnapshot? entity = await _context.Game.Entities.ByIdAsync(entityId, ct).ConfigureAwait(false);
            if (entity is null)
                return McpResult.Fail(McpErrorCodes.InvalidState, $"No tracked entity with id {entityId}.");

            await _context.Game.Entities.AttackAsync(entityId, ct).ConfigureAwait(false);
            return McpResult.Ok(new { entityId, sent = true });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>Parses a hand name. Public so the accepted vocabulary is pinned by tests.</summary>
    public static bool TryParseHand(string hand, out Umpk.Client.Actions.Hand parsed)
    {
        if (string.Equals(hand, "MainHand", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hand, "Main", StringComparison.OrdinalIgnoreCase))
        {
            parsed = Umpk.Client.Actions.Hand.Main;
            return true;
        }

        if (string.Equals(hand, "OffHand", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hand, "Off", StringComparison.OrdinalIgnoreCase))
        {
            parsed = Umpk.Client.Actions.Hand.Off;
            return true;
        }

        parsed = default;
        return false;
    }
}
