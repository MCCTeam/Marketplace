using DMCBK.Core;
using DMCBK.PluginSdk;
using Umpk.Client.Actions;
using Umpk.Game.Inventory;

namespace DMCBK.OfficialPlugins.McpServer;

/// <summary>
/// Inventory and container tools.
/// Transfers are server-routed (shift-click/quick-move): the server decides where a shifted stack lands per the menu's own semantics, so no client-side slot-range table is needed and no menu kind is mis-handled.
/// Every transfer verifies by recounting.
/// </summary>
public sealed partial class McpCapabilities
{
    private static bool MatchesItem(string itemId, string query, bool exactMatch)
    {
        string bare = itemId.StartsWith("minecraft:", StringComparison.Ordinal)
            ? itemId["minecraft:".Length..]
            : itemId;
        return exactMatch
            ? string.Equals(itemId, query, StringComparison.OrdinalIgnoreCase)
                || string.Equals(bare, query, StringComparison.OrdinalIgnoreCase)
            : itemId.Contains(query, StringComparison.OrdinalIgnoreCase)
                || bare.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountOf(IReadOnlyList<ItemStackInfo> slots, string itemType, bool exact)
    {
        int total = 0;
        foreach (ItemStackInfo slot in slots)
        {
            if (!slot.IsEmpty && MatchesItem(slot.ItemId, itemType, exact))
                total += slot.Count;
        }

        return total;
    }

    /// <summary>Splits the combined window: the container section is everything before the last 36 slots. Public so the windowing math is pinned by tests.</summary>
    public static int ContainerSectionSize(OpenContainerSnapshot container)
        => container.Slots.Count >= 36 ? container.Slots.Count - 36 : container.Slots.Count;

    /// <summary>Counts one item type inside the container section only (never the attached player section).</summary>
    /// <summary>Counts an item type inside the container section only. Public so the section accounting is pinned by tests.</summary>
    public static int CountContainerSection(OpenContainerSnapshot container, string itemType)
    {
        int size = ContainerSectionSize(container);
        int total = 0;
        for (int i = 0; i < size; i++)
        {
            ItemStackInfo slot = container.Slots[i];
            if (!slot.IsEmpty && MatchesItem(slot.ItemId, itemType, true))
                total += slot.Count;
        }

        return total;
    }

    /// <summary>
    /// Counts an item type in the attached player section (the last 36 combined slots, mirroring player slots 9-44).
    /// This mirror is what stays fresh after container-window clicks: single-slot server updates land in the open window, while the player-window snapshot can lag behind.
    /// Public so the recount discipline is pinned by tests.
    /// </summary>
    public static int CountPlayerSection(OpenContainerSnapshot container, string itemType)
    {
        int start = ContainerSectionSize(container);
        int total = 0;
        for (int i = start; i < container.Slots.Count; i++)
        {
            ItemStackInfo slot = container.Slots[i];
            if (!slot.IsEmpty && MatchesItem(slot.ItemId, itemType, true))
                total += slot.Count;
        }

        return total;
    }

    /// <summary>
    /// Counts an item type in the player slots that have no counterpart in a container window (crafting 0-8 and offhand 45).
    /// Read once per transfer: with an empty cursor no click can change them mid-transfer.
    /// Public so the recount discipline is pinned by tests.
    /// </summary>
    public static int CountFrozenPrefix(IReadOnlyList<ItemStackInfo> playerSlots, string itemType)
    {
        int total = 0;
        for (int i = 0; i < playerSlots.Count; i++)
        {
            if (i is >= 9 and <= 44)
                continue;

            ItemStackInfo slot = playerSlots[i];
            if (!slot.IsEmpty && MatchesItem(slot.ItemId, itemType, true))
                total += slot.Count;
        }

        return total;
    }

    /// <summary>Maps a player-window slot into the combined window. Public so the mapping is pinned by tests.</summary>
    public static int? ToCombinedSlot(int containerSize, int playerSlot)
    {
        if (playerSlot is >= 9 and <= 35)
            return containerSize + (playerSlot - 9);

        if (playerSlot is >= 36 and <= 44)
            return containerSize + 27 + (playerSlot - 36);

        return null;
    }

    /// <summary>
    /// Polls the player inventory until <paramref name="settled"/> reads true or the budget expires.
    /// Drops and player-window clicks echo back the same way container clicks do.
    /// </summary>
    private async Task<PlayerInventorySnapshot?> WaitForPlayerAsync(
        Func<PlayerInventorySnapshot, bool> settled, int timeoutMs, CancellationToken ct)
    {
        PlayerInventorySnapshot? latest = null;
        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            latest = await _context.Game.Inventory.GetPlayerInventoryAsync(ct).ConfigureAwait(false);
            if (settled(latest))
                return latest;

            if (Environment.TickCount64 >= deadline)
                return latest;

            await Task.Delay(150, ct).ConfigureAwait(false);
        }
    }

    /// <summary>mcc_change_hotbar_slot: selects hotbar slot 1-9.</summary>
    public async Task<McpResult> ChangeHotbarSlotAsync(int slot, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.Inventory);
        if (!gate.Success)
            return gate;

        if (slot is < 1 or > 9)
            return McpResult.Fail(McpErrorCodes.InvalidArgs, "slot must be within 1-9.");

        try
        {
            await _context.Game.Inventory.SelectHeldSlotAsync(slot - 1, ct).ConfigureAwait(false);
            return McpResult.Ok(new { success = true, slot });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_select_item: selects a hotbar slot holding the item, without rearranging anything.</summary>
    public async Task<McpResult> SelectHotbarItemAsync(string itemType, bool preferLowestSlot, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.Inventory);
        if (!gate.Success)
            return gate;

        if (string.IsNullOrWhiteSpace(itemType))
            return McpResult.Fail(McpErrorCodes.InvalidArgs, "itemType is required.");

        try
        {
            PlayerInventorySnapshot inv =
                await _context.Game.Inventory.GetPlayerInventoryAsync(ct).ConfigureAwait(false);
            var candidates = new List<(int Hotbar, int Slot, int Count)>();
            for (int hotbar = 0; hotbar < 9; hotbar++)
            {
                int slot = 36 + hotbar;
                if (slot >= inv.Slots.Count)
                    break;

                ItemStackInfo stack = inv.Slots[slot];
                if (!stack.IsEmpty && MatchesItem(stack.ItemId, itemType.Trim(), true))
                    candidates.Add((hotbar, slot, stack.Count));
            }

            if (candidates.Count == 0)
            {
                return McpResult.Fail(
                    McpErrorCodes.InvalidState, $"No hotbar slot holds '{itemType}'.");
            }

            candidates.Sort((a, b) => preferLowestSlot
                ? a.Slot.CompareTo(b.Slot)
                : b.Count.CompareTo(a.Count));
            (int Hotbar, int Slot, int Count) pick = candidates[0];
            await _context.Game.Inventory.SelectHeldSlotAsync(pick.Hotbar, ct).ConfigureAwait(false);
            return McpResult.Ok(new
            {
                success = true,
                itemType,
                inventorySlot = pick.Slot,
                selectedSlot = pick.Hotbar + 1,
                count = pick.Count,
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_inventory_snapshot: player inventory (id 0) or the open container by window id.</summary>
    public async Task<McpResult> GetInventorySnapshotAsync(int inventoryId, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.Inventory);
        if (!gate.Success)
            return gate;

        try
        {
            if (inventoryId == 0)
            {
                PlayerInventorySnapshot inv =
                    await _context.Game.Inventory.GetPlayerInventoryAsync(ct).ConfigureAwait(false);
                return McpResult.Ok(new
                {
                    id = 0,
                    type = "player",
                    slotCount = inv.Slots.Count,
                    heldSlot = inv.HeldSlot,
                    stateId = inv.StateId,
                    cursor = new { type = inv.Cursor.ItemId, count = inv.Cursor.Count },
                    slots = inv.Slots.Select((s, i) => new
                    {
                        slot = i,
                        type = s.ItemId,
                        count = s.Count,
                        empty = s.IsEmpty,
                    }).ToList(),
                });
            }

            OpenContainerSnapshot? container =
                await _context.Game.Inventory.GetOpenContainerAsync(ct).ConfigureAwait(false);
            if (container is null || container.WindowId != inventoryId)
            {
                return McpResult.Fail(
                    McpErrorCodes.InvalidState,
                    $"No open container with id {inventoryId}. Only the player inventory (0) and the currently open container are visible.");
            }

            return McpResult.Ok(ContainerPayload(container));
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    private static object ContainerPayload(OpenContainerSnapshot container)
    {
        int containerSize = ContainerSectionSize(container);
        return new
        {
            id = container.WindowId,
            type = container.MenuType ?? container.LegacyWindowType ?? "unknown",
            title = container.Title,
            slotCount = container.Slots.Count,
            containerSlotCount = containerSize,
            playerOffset = containerSize,
            slotsNote = "Combined window indices: slots 0..containerSlotCount-1 are the container, the last 36 are the attached player slots. Window actions and clicks address these combined indices.",
            stateId = container.StateId,
            cursor = new { type = container.Cursor.ItemId, count = container.Cursor.Count },
            slots = container.Slots.Select((s, i) => new
            {
                slot = i,
                section = i < containerSize ? "container" : "player",
                type = s.ItemId,
                count = s.Count,
                empty = s.IsEmpty,
            }).ToList(),
        };
    }

    /// <summary>mcc_inventory_search: finds stacks by item query in the player inventory and, optionally, the open container.</summary>
    public async Task<McpResult> SearchInventoriesAsync(
        string query, int maxCount, bool exactMatch, bool includeContainers, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.Inventory);
        if (!gate.Success)
            return gate;

        if (string.IsNullOrWhiteSpace(query))
            return McpResult.Fail(McpErrorCodes.InvalidArgs, "query is required.");

        maxCount = Math.Clamp(maxCount, 1, 1000);

        try
        {
            var matches = new List<object>();
            PlayerInventorySnapshot inv =
                await _context.Game.Inventory.GetPlayerInventoryAsync(ct).ConfigureAwait(false);
            for (int i = 0; i < inv.Slots.Count && matches.Count < maxCount; i++)
            {
                ItemStackInfo stack = inv.Slots[i];
                if (!stack.IsEmpty && MatchesItem(stack.ItemId, query.Trim(), exactMatch))
                {
                    matches.Add(new
                    {
                        inventoryId = 0,
                        inventoryType = "player",
                        slot = i,
                        itemType = stack.ItemId,
                        count = stack.Count,
                        isPlayerInventory = true,
                        hotbarSlot = i is >= 36 and <= 44 ? i - 36 + 1 : (int?)null,
                    });
                }
            }

            string? containerTitle = null;
            if (includeContainers && matches.Count < maxCount)
            {
                OpenContainerSnapshot? container =
                    await _context.Game.Inventory.GetOpenContainerAsync(ct).ConfigureAwait(false);
                if (container is not null)
                {
                    containerTitle = container.Title;
                    for (int i = 0; i < container.Slots.Count && matches.Count < maxCount; i++)
                    {
                        ItemStackInfo stack = container.Slots[i];
                        if (!stack.IsEmpty && MatchesItem(stack.ItemId, query.Trim(), exactMatch))
                        {
                            matches.Add(new
                            {
                                inventoryId = container.WindowId,
                                inventoryType = container.MenuType ?? container.LegacyWindowType ?? "unknown",
                                slot = i,
                                itemType = stack.ItemId,
                                count = stack.Count,
                                isPlayerInventory = false,
                                hotbarSlot = (int?)null,
                            });
                        }
                    }
                }
            }

            return McpResult.Ok(new
            {
                query,
                exactMatch,
                includeContainers,
                containerTitle,
                count = matches.Count,
                matches,
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_inventories_list: the player inventory plus the open container, if any.</summary>
    public async Task<McpResult> ListInventoriesAsync(CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.Inventory);
        if (!gate.Success)
            return gate;

        try
        {
            PlayerInventorySnapshot inv =
                await _context.Game.Inventory.GetPlayerInventoryAsync(ct).ConfigureAwait(false);
            OpenContainerSnapshot? container =
                await _context.Game.Inventory.GetOpenContainerAsync(ct).ConfigureAwait(false);
            var rows = new List<object>
            {
                new
                {
                    id = 0,
                    type = "player",
                    title = (string?)null,
                    slotCount = inv.Slots.Count,
                    nonEmptySlots = inv.Slots.Count(s => !s.IsEmpty),
                    active = container is null,
                },
            };

            if (container is not null)
            {
                rows.Add(new
                {
                    id = container.WindowId,
                    type = container.MenuType ?? container.LegacyWindowType ?? "unknown",
                    title = container.Title,
                    slotCount = container.Slots.Count,
                    nonEmptySlots = container.Slots.Count(s => !s.IsEmpty),
                    active = true,
                });
            }

            return McpResult.Ok(new { count = rows.Count, inventories = rows });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_container_open_at: uses the block and waits for the window. A null return is an honest timeout, not an open.</summary>
    public async Task<McpResult> OpenContainerAtAsync(
        int x, int y, int z, int timeoutMs, bool closeCurrent, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.Inventory);
        if (!gate.Success)
            return gate;

        int waitMs = timeoutMs <= 0 ? 5000 : Math.Clamp(timeoutMs, 250, 20000);
        try
        {
            if (closeCurrent)
            {
                try
                {
                    await _context.Game.Inventory.CloseAsync(ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(waitMs));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            OpenContainerSnapshot? container;
            try
            {
                container = await _context.Game.Inventory.OpenContainerAtAsync(
                    new Umpk.Geometry.BlockPos(x, y, z), linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                container = null;
            }

            if (container is null)
            {
                return McpResult.Fail(
                    McpErrorCodes.ActionIncomplete,
                    $"No container window opened at ({x}, {y}, {z}) within {waitMs} ms.",
                    new { x, y, z, openAccepted = true, opened = false });
            }

            return McpResult.Ok(new
            {
                success = true,
                openAccepted = true,
                opened = true,
                inventory = ContainerPayload(container),
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_container_close: closes the open container (-1) or a specific window id.</summary>
    public async Task<McpResult> CloseContainerAsync(int inventoryId, int timeoutMs, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.Inventory);
        if (!gate.Success)
            return gate;

        try
        {
            OpenContainerSnapshot? container =
                await _context.Game.Inventory.GetOpenContainerAsync(ct).ConfigureAwait(false);
            if (container is null)
                return McpResult.Ok(new { success = true, closed = false });

            if (inventoryId != -1 && container.WindowId != inventoryId)
            {
                return McpResult.Fail(
                    McpErrorCodes.InvalidState, $"No open container with id {inventoryId}.");
            }

            int waitMs = timeoutMs <= 0 ? 5000 : Math.Clamp(timeoutMs, 250, 20000);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(waitMs));
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await _context.Game.Inventory.CloseAsync(linked.Token).ConfigureAwait(false);

            OpenContainerSnapshot? after;
            try
            {
                after = await _context.Game.Inventory.GetOpenContainerAsync(ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                after = null;
            }

            return McpResult.Ok(new { success = true, closed = after is null });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_inventory_window_action: one raw click. Prefer deposit/withdraw for container work.</summary>
    public async Task<McpResult> InventoryWindowActionAsync(
        int inventoryId, int slotId, string actionType, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.Inventory);
        if (!gate.Success)
            return gate;

        string? normalized = NormalizeWindowAction(actionType);
        if (normalized is null)
        {
            return McpResult.Fail(
                McpErrorCodes.InvalidArgs,
                "actionType must be LeftClick, RightClick, ShiftClick, Drop or DropStack (aliases: left, right, shift, shiftright, drop/q, dropstack/ctrlq).");
        }

        try
        {
            int windowSlot = await ToWindowSlotAsync(inventoryId, slotId, ct).ConfigureAwait(false);
            switch (normalized)
            {
                case "LeftClick":
                    await _context.Game.Inventory.PickupAsync(windowSlot, MouseButton.Left, ct).ConfigureAwait(false);
                    break;
                case "RightClick":
                    await _context.Game.Inventory.PickupAsync(windowSlot, MouseButton.Right, ct).ConfigureAwait(false);
                    break;
                case "ShiftClick":
                    await _context.Game.Inventory.QuickMoveAsync(windowSlot, ct).ConfigureAwait(false);
                    break;
                case "Drop":
                    await _context.Game.Inventory.DropAsync(windowSlot, false, ct).ConfigureAwait(false);
                    break;
                case "DropStack":
                    await _context.Game.Inventory.DropAsync(windowSlot, true, ct).ConfigureAwait(false);
                    break;
            }

            return McpResult.Ok(new { success = true, normalizedActionType = normalized });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>Normalizes a window-action alias. Public so the alias table is pinned by tests.</summary>
    public static string? NormalizeWindowAction(string actionType)
        => actionType.Trim().ToLowerInvariant() switch
        {
            "leftclick" or "left" or "l" => "LeftClick",
            "rightclick" or "right" or "r" => "RightClick",
            "shiftclick" or "shift" => "ShiftClick",
            "shiftrightclick" or "shiftright" => "ShiftClick",
            "drop" or "q" => "Drop",
            "dropstack" or "ctrlq" or "drop_stack" => "DropStack",
            _ => null,
        };

    /// <summary>
    /// Maps an inventory-local slot to the combined window index clicks need.
    /// Container slots address directly; player-inventory slots (id 0) map into the attached player section while a container is open, and address directly when only the player inventory is open.
    /// </summary>
    private async Task<int> ToWindowSlotAsync(int inventoryId, int slotId, CancellationToken ct)
    {
        OpenContainerSnapshot? container =
            await _context.Game.Inventory.GetOpenContainerAsync(ct).ConfigureAwait(false);
        if (container is null)
            return slotId;

        int containerSize = ContainerSectionSize(container);
        if (inventoryId == 0)
        {
            int? combined = ToCombinedSlot(containerSize, slotId);
            if (combined is null)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(slotId), "Crafting, armor and offhand slots have no counterpart in the open container window.");
            }

            return combined.Value;
        }

        // Raw container-window clicks address the combined window directly: the container section first, then the attached 36 player slots.
        // Only out-of-window indices are refused.
        if (slotId < 0 || slotId >= container.Slots.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slotId), $"Slot {slotId} is outside the open window (0-{container.Slots.Count - 1}).");
        }

        return slotId;
    }

    /// <summary>mcc_inventory_drop_item: drops an exact count of one item type from the player inventory.</summary>
    public async Task<McpResult> DropInventoryItemAsync(
        string itemType, int count, int inventoryId, bool preferStack, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.Inventory);
        if (!gate.Success)
            return gate;

        if (string.IsNullOrWhiteSpace(itemType) || count <= 0)
            return McpResult.Fail(McpErrorCodes.InvalidArgs, "itemType and a positive count are required.");

        if (inventoryId != 0)
        {
            return McpResult.Fail(
                McpErrorCodes.InvalidArgs, "Only the player inventory (0) supports dropping in this version.");
        }

        try
        {
            PlayerInventorySnapshot before =
                await _context.Game.Inventory.GetPlayerInventoryAsync(ct).ConfigureAwait(false);
            int beforeCount = CountOf(before.Slots, itemType.Trim(), true);
            if (beforeCount < count)
            {
                return McpResult.Fail(
                    McpErrorCodes.InvalidState,
                    $"Only {beforeCount} of '{itemType}' available; {count} requested.",
                    new { beforeCount, requestedCount = count });
            }

            // Slot 0 is the crafting result: the server ignores drops from it, so it is skipped.
            // Clicks address the open window: while a container is open, player slots map into the combined window; otherwise player indices address the player window directly.
            OpenContainerSnapshot? openForDrop =
                await _context.Game.Inventory.GetOpenContainerAsync(ct).ConfigureAwait(false);
            int? dropContainerSize = openForDrop is null ? null : ContainerSectionSize(openForDrop);

            int remaining = count;
            var touched = new List<int>();
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                // Re-locate before every stack: drops shrink stacks in place and snapshots can lag the echo behind, so a precomputed slot list clicks where the stack used to be.
                // With a container open the source comes from the fresh combined player section (see the deposit path); otherwise the player snapshot addresses directly.
                PlayerInventorySnapshot relocate =
                    await _context.Game.Inventory.GetPlayerInventoryAsync(ct).ConfigureAwait(false);
                OpenContainerSnapshot? relocateWindow =
                    await _context.Game.Inventory.GetOpenContainerAsync(ct).ConfigureAwait(false);
                int windowSlot;
                int reportSlot;
                int available;
                if (relocateWindow is null)
                {
                    (ItemStackInfo Stack, int Slot)? pick = relocate.Slots
                        .Select((s, i) => (Stack: s, Slot: i))
                        .Where(t => t.Slot != 0 && !t.Stack.IsEmpty && MatchesItem(t.Stack.ItemId, itemType.Trim(), true))
                        .OrderByDescending(t => preferStack ? t.Stack.Count : -t.Slot)
                        .ThenBy(t => t.Slot)
                        .Select(t => (ValueTuple<ItemStackInfo, int>?)t)
                        .FirstOrDefault();
                    if (pick is null)
                        break;

                    windowSlot = pick.Value.Slot;
                    reportSlot = pick.Value.Slot;
                    available = pick.Value.Stack.Count;
                }
                else
                {
                    int relocateSize = ContainerSectionSize(relocateWindow);
                    (ItemStackInfo Stack, int Combined)? pick = relocateWindow.Slots
                        .Skip(relocateSize)
                        .Take(36)
                        .Select((s, k) => (Stack: s, Combined: relocateSize + k))
                        .Where(t => !t.Stack.IsEmpty && MatchesItem(t.Stack.ItemId, itemType.Trim(), true))
                        .OrderByDescending(t => preferStack ? t.Stack.Count : -t.Combined)
                        .ThenBy(t => t.Combined)
                        .Select(t => (ValueTuple<ItemStackInfo, int>?)t)
                        .FirstOrDefault();
                    if (pick is null)
                        break;

                    windowSlot = pick.Value.Combined;
                    reportSlot = 9 + (pick.Value.Combined - relocateSize);
                    available = pick.Value.Stack.Count;
                }

                int fromSlot = Math.Min(available, remaining);
                for (int i = 0; i < fromSlot; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    await _context.Game.Inventory.DropAsync(windowSlot, false, ct).ConfigureAwait(false);
                    remaining--;
                    if (!touched.Contains(reportSlot))
                        touched.Add(reportSlot);
                }
            }

            PlayerInventorySnapshot after =
                await _context.Game.Inventory.GetPlayerInventoryAsync(ct).ConfigureAwait(false);
            int want = beforeCount - (count - remaining);
            int afterCount;
            if (dropContainerSize.HasValue)
            {
                // Hybrid recount while a container is open: frozen prefix plus the fresh player-section mirror (see the transfer methods for why the plain snapshot lags).
                // Wait for the drop echo first: back-to-back single-item drops each need their own server round trip.
                afterCount = beforeCount;
                long dropDeadline = Environment.TickCount64 + 5000;
                while (afterCount > want)
                {
                    ct.ThrowIfCancellationRequested();
                    if (Environment.TickCount64 >= dropDeadline)
                        break;

                    await Task.Delay(150, ct).ConfigureAwait(false);
                    after = await _context.Game.Inventory.GetPlayerInventoryAsync(ct).ConfigureAwait(false);
                    OpenContainerSnapshot? afterWindow =
                        await _context.Game.Inventory.GetOpenContainerAsync(ct).ConfigureAwait(false);
                    afterCount = CountFrozenPrefix(after.Slots, itemType.Trim())
                        + (afterWindow is null
                            ? CountOf(after.Slots, itemType.Trim(), true)
                            : CountPlayerSection(afterWindow, itemType.Trim()));
                }
            }
            else
            {
                after = await WaitForPlayerAsync(
                    p => CountOf(p.Slots, itemType.Trim(), true) <= want,
                    5000, ct).ConfigureAwait(false) ?? after;
                afterCount = CountOf(after.Slots, itemType.Trim(), true);
            }

            int dropped = beforeCount - afterCount;
            if (dropped < count)
            {
                return McpResult.Fail(
                    McpErrorCodes.ActionIncomplete,
                    $"Dropped {dropped} of {count} requested.",
                    new
                    {
                        itemType,
                        requestedCount = count,
                        droppedCount = dropped,
                        beforeCount,
                        afterCount,
                        touchedSlots = touched,
                    });
            }

            return McpResult.Ok(new
            {
                success = true,
                itemType,
                requestedCount = count,
                droppedCount = dropped,
                beforeCount,
                afterCount,
                touchedSlots = touched,
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>
    /// Polls the open container until <paramref name="settled"/> reads true or the budget expires.
    /// Clicks send instantly but the server echo lands later; a single immediate recount mistakes latency for failure (and a blind retry ping-pongs the stack back), so transfers wait for the world they asked for instead of guessing from the first read.
    /// </summary>
    /// <returns>The fresh snapshot when settled, or the latest read on timeout.</returns>
    private async Task<OpenContainerSnapshot?> WaitForContainerAsync(
        Func<OpenContainerSnapshot, bool> settled, int timeoutMs, CancellationToken ct)
    {
        OpenContainerSnapshot? latest = null;
        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            latest = await _context.Game.Inventory.GetOpenContainerAsync(ct).ConfigureAwait(false);
            if (latest is null || settled(latest))
                return latest;

            if (Environment.TickCount64 >= deadline)
                return latest;

            await Task.Delay(150, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The click-by-click transfer engine both container directions share.
    /// Shift-clicks move whole stacks but cannot split one, and blind exact-count retries ping-pong stacks back and forth, so every partial count goes pickup/place with cursor verification: grab the source stack, right-click singles onto the destination, put the remainder back.
    /// Each step reads back what the click did; a mismatch stops the transfer with the cursor contents reported instead of pressing on into an unknown state.
    /// </summary>
    /// <returns>The cursor after the put-back, which must be empty on success.</returns>
    private async Task<ItemStackInfo> SplitMoveAsync(
        int sourceSlot, int haveCount, int destSlot, int take, CancellationToken ct)
    {
        // Grab the whole source stack.
        await _context.Game.Inventory.PickupAsync(
            sourceSlot, Umpk.Game.Inventory.MouseButton.Left, ct).ConfigureAwait(false);
        ItemStackInfo grabbed = await _context.Game.Inventory.GetCursorAsync(ct).ConfigureAwait(false);
        if (grabbed.Count != haveCount || grabbed.IsEmpty)
            return grabbed;

        // Place singles onto the destination.
        for (int i = 0; i < take; i++)
        {
            ct.ThrowIfCancellationRequested();
            await _context.Game.Inventory.PickupAsync(
                destSlot, Umpk.Game.Inventory.MouseButton.Right, ct).ConfigureAwait(false);
        }

        ItemStackInfo afterPlace = await _context.Game.Inventory.GetCursorAsync(ct).ConfigureAwait(false);
        if (afterPlace.Count != haveCount - take)
        {
            // Destination filled early or clicks landed nowhere: put everything back and report.
            await _context.Game.Inventory.PickupAsync(
                sourceSlot, Umpk.Game.Inventory.MouseButton.Left, ct).ConfigureAwait(false);
            return await _context.Game.Inventory.GetCursorAsync(ct).ConfigureAwait(false);
        }

        if (afterPlace.IsEmpty)
            return afterPlace;

        // Return the remainder to the (now empty) source slot.
        await _context.Game.Inventory.PickupAsync(
            sourceSlot, Umpk.Game.Inventory.MouseButton.Left, ct).ConfigureAwait(false);
        return await _context.Game.Inventory.GetCursorAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves a whole stack cursor-free in two clicks: grab with the left button, drop with the left button.
    /// Verified by the caller through the section recount.
    /// </summary>
    private async Task MoveWholeStackAsync(int sourceSlot, int destSlot, CancellationToken ct)
    {
        await _context.Game.Inventory.PickupAsync(
            sourceSlot, Umpk.Game.Inventory.MouseButton.Left, ct).ConfigureAwait(false);
        await _context.Game.Inventory.PickupAsync(
            destSlot, Umpk.Game.Inventory.MouseButton.Left, ct).ConfigureAwait(false);
    }

    /// <summary>Finds a destination slot for incoming items: a same-type partial stack first, then the first empty slot.</summary>
    internal static int? FindDestSlot(IReadOnlyList<ItemStackInfo> section, string itemType)
    {
        for (int i = 0; i < section.Count; i++)
        {
            if (!section[i].IsEmpty
                && MatchesItem(section[i].ItemId, itemType, true)
                && section[i].Count < 64)
                return i;
        }

        for (int i = 0; i < section.Count; i++)
        {
            if (section[i].IsEmpty)
                return i;
        }

        return null;
    }

    /// <summary>Builds the transfer result payload, with cursor contents whenever relevant.</summary>
    private static object TransferPayload(
        string direction, string itemType, int count, int moved,
        int beforePlayer, int afterPlayer, int beforeContainer, int afterContainer,
        List<int> attempted, ItemStackInfo? cursor = null)
        => new
        {
            direction,
            itemType,
            requestedCount = count,
            movedCount = moved,
            beforePlayerCount = beforePlayer,
            afterPlayerCount = afterPlayer < 0 ? (int?)null : afterPlayer,
            beforeContainerCount = beforeContainer,
            afterContainerCount = afterContainer < 0 ? (int?)null : afterContainer,
            attemptedSlots = attempted,
            cursorType = cursor is null || cursor.IsEmpty ? null : cursor.ItemId,
            cursorCount = cursor is null || cursor.IsEmpty ? (int?)null : cursor.Count,
        };

    /// <summary>mcc_container_deposit_item: moves an exact item count into the open container, verified.</summary>
    public async Task<McpResult> DepositContainerItemAsync(
        string itemType, int count, int inventoryId, bool preferLargestStack, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.Inventory);
        if (!gate.Success)
            return gate;

        if (string.IsNullOrWhiteSpace(itemType) || count <= 0)
            return McpResult.Fail(McpErrorCodes.InvalidArgs, "itemType and a positive count are required.");

        try
        {
            OpenContainerSnapshot? container =
                await _context.Game.Inventory.GetOpenContainerAsync(ct).ConfigureAwait(false);
            if (container is null || (inventoryId != -1 && container.WindowId != inventoryId))
            {
                return McpResult.Fail(
                    McpErrorCodes.InvalidState, "No matching open container. Open one with mcc_container_open_at first.");
            }

            ItemStackInfo cursor =
                await _context.Game.Inventory.GetCursorAsync(ct).ConfigureAwait(false);
            if (!cursor.IsEmpty)
            {
                return McpResult.Fail(
                    McpErrorCodes.InvalidState, "The cursor holds an item; transfers refuse rather than lose it.");
            }

            PlayerInventorySnapshot player =
                await _context.Game.Inventory.GetPlayerInventoryAsync(ct).ConfigureAwait(false);
            int beforePlayer = CountOf(player.Slots, itemType.Trim(), true);
            int beforeContainer = CountContainerSection(container, itemType.Trim());
            if (beforePlayer < count)
            {
                return McpResult.Fail(
                    McpErrorCodes.InvalidState,
                    $"Only {beforePlayer} of '{itemType}' in the player inventory; {count} requested.");
            }

            int frozen = CountFrozenPrefix(player.Slots, itemType.Trim());

            int moved = 0;
            var attempted = new List<int>();
            int stuckRounds = 0;
            while (moved < count)
            {
                ct.ThrowIfCancellationRequested();
                // Re-locate from the FRESH combined snapshot every iteration: its player section and container size come from one read, so the clicked index can never disagree with the section it was derived from (separate player-window reads can lag server-side moves behind, and clicking a slot the stack already left is a silent no-op the server ignores).
                OpenContainerSnapshot? locate =
                    await _context.Game.Inventory.GetOpenContainerAsync(ct).ConfigureAwait(false);
                if (locate is null)
                {
                    return McpResult.Fail(
                        McpErrorCodes.ActionIncomplete,
                        "The container closed mid-transfer.",
                        TransferPayload("deposit", itemType, count, moved, beforePlayer, -1, beforeContainer, -1, attempted));
                }

                int locateSize = ContainerSectionSize(locate);
                (ItemStackInfo Stack, int Combined)? pick = locate.Slots
                    .Skip(locateSize)
                    .Take(36)
                    .Select((s, k) => (Stack: s, Combined: locateSize + k))
                    .Where(t => !t.Stack.IsEmpty && MatchesItem(t.Stack.ItemId, itemType.Trim(), true))
                    .OrderByDescending(t => preferLargestStack ? t.Stack.Count : -t.Combined)
                    .ThenBy(t => t.Combined)
                    .Select(t => (ValueTuple<ItemStackInfo, int>?)t)
                    .FirstOrDefault();
                if (pick is null)
                    break;

                int need = count - moved;
                int take = Math.Min(pick.Value.Stack.Count, need);
                attempted.Add(pick.Value.Combined);

                // Destination inside the container section: empty slots for whole stacks (a partial would overflow), partial-or-empty for splits.
                int? dest = null;
                IReadOnlyList<ItemStackInfo> section = locate.Slots.Take(locateSize).ToList();
                if (take == pick.Value.Stack.Count)
                {
                    for (int i = 0; i < section.Count && dest is null; i++)
                    {
                        if (section[i].IsEmpty)
                            dest = i;
                    }
                }
                else
                    dest = FindDestSlot(section, itemType.Trim());

                if (dest is null)
                {
                    return McpResult.Fail(
                        McpErrorCodes.InvalidState,
                        "The container has no room for this transfer.",
                        TransferPayload("deposit", itemType, count, moved, beforePlayer, -1, beforeContainer, -1, attempted));
                }

                ItemStackInfo stepCursor;
                if (take == pick.Value.Stack.Count)
                {
                    await MoveWholeStackAsync(pick.Value.Combined, dest.Value, ct).ConfigureAwait(false);
                    stepCursor = await _context.Game.Inventory.GetCursorAsync(ct).ConfigureAwait(false);
                    if (!stepCursor.IsEmpty)
                    {
                        return McpResult.Fail(
                            McpErrorCodes.ActionIncomplete,
                            "The cursor is holding items after a whole-stack move.",
                            TransferPayload("deposit", itemType, count, moved, beforePlayer, -1, beforeContainer, -1, attempted, stepCursor));
                    }
                }
                else
                {
                    stepCursor = await SplitMoveAsync(
                        pick.Value.Combined, pick.Value.Stack.Count, dest.Value, take, ct).ConfigureAwait(false);
                    if (!stepCursor.IsEmpty)
                    {
                        return McpResult.Fail(
                            McpErrorCodes.ActionIncomplete,
                            "The cursor is holding items after a split move.",
                            TransferPayload("deposit", itemType, count, moved, beforePlayer, -1, beforeContainer, -1, attempted, stepCursor));
                    }
                }

                OpenContainerSnapshot? recount = await WaitForContainerAsync(
                    c => CountContainerSection(c, itemType.Trim()) != beforeContainer + moved,
                    4000, ct).ConfigureAwait(false);
                if (recount is null)
                {
                    return McpResult.Fail(
                        McpErrorCodes.ActionIncomplete,
                        "The container closed mid-transfer.",
                        TransferPayload("deposit", itemType, count, moved, beforePlayer, -1, beforeContainer, -1, attempted));
                }

                int fresh = CountContainerSection(recount, itemType.Trim()) - beforeContainer;
                if (fresh == moved)
                {
                    // No observable progress even though every click verified: stop instead of looping the same no-op forever.
                    stuckRounds++;
                    if (stuckRounds >= 2)
                        break;
                }
                else
                    stuckRounds = 0;

                moved = fresh;
            }

            OpenContainerSnapshot? afterContainer =
                await _context.Game.Inventory.GetOpenContainerAsync(ct).ConfigureAwait(false);
            int afterContainerCount = afterContainer is null
                ? beforeContainer
                : CountContainerSection(afterContainer, itemType.Trim());
            moved = afterContainerCount - beforeContainer;
            int afterPlayerCount = frozen
                + (afterContainer is null
                    ? beforePlayer - frozen
                    : CountPlayerSection(afterContainer, itemType.Trim()));

            // No overshoot step: whole stacks move whole (take == stack) and splits place exact singles, so moved can never exceed count.
            // The old shift-everything-then-correct pattern ping-ponged stacks precisely because shift-clicks cannot split.
            object payload = TransferPayload(
                "deposit", itemType, count, moved, beforePlayer, afterPlayerCount,
                beforeContainer, afterContainerCount, attempted);

            return moved == count
                ? McpResult.Ok(payload)
                : McpResult.Fail(
                    McpErrorCodes.ActionIncomplete,
                    $"Moved {moved} of {count} requested.",
                    payload);
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_container_withdraw_item: shift-clicks stacks out of the open container and verifies the counts.</summary>
    public async Task<McpResult> WithdrawContainerItemAsync(
        string itemType, int count, int inventoryId, bool preferLargestStack, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.Inventory);
        if (!gate.Success)
            return gate;

        if (string.IsNullOrWhiteSpace(itemType) || count <= 0)
            return McpResult.Fail(McpErrorCodes.InvalidArgs, "itemType and a positive count are required.");

        try
        {
            OpenContainerSnapshot? container =
                await _context.Game.Inventory.GetOpenContainerAsync(ct).ConfigureAwait(false);
            if (container is null || (inventoryId != -1 && container.WindowId != inventoryId))
            {
                return McpResult.Fail(
                    McpErrorCodes.InvalidState, "No matching open container. Open one with mcc_container_open_at first.");
            }

            ItemStackInfo cursor =
                await _context.Game.Inventory.GetCursorAsync(ct).ConfigureAwait(false);
            if (!cursor.IsEmpty)
            {
                return McpResult.Fail(
                    McpErrorCodes.InvalidState, "The cursor holds an item; transfers refuse rather than lose it.");
            }

            PlayerInventorySnapshot player =
                await _context.Game.Inventory.GetPlayerInventoryAsync(ct).ConfigureAwait(false);
            int beforePlayer = CountOf(player.Slots, itemType.Trim(), true);
            int frozen = CountFrozenPrefix(player.Slots, itemType.Trim());
            int beforeContainer = CountContainerSection(container, itemType.Trim());
            if (beforeContainer < count)
            {
                return McpResult.Fail(
                    McpErrorCodes.InvalidState,
                    $"Only {beforeContainer} of '{itemType}' in the container; {count} requested.");
            }

            int moved = 0;
            var attempted = new List<int>();
            int stuckRounds = 0;
            while (moved < count)
            {
                ct.ThrowIfCancellationRequested();
                // Re-locate from the FRESH combined snapshot every iteration (see the deposit path).
                OpenContainerSnapshot? relocate =
                    await _context.Game.Inventory.GetOpenContainerAsync(ct).ConfigureAwait(false);
                if (relocate is null)
                {
                    return McpResult.Fail(
                        McpErrorCodes.ActionIncomplete,
                        "The container closed mid-transfer.",
                        TransferPayload("withdraw", itemType, count, moved, beforePlayer, -1, beforeContainer, -1, attempted));
                }

                int relocateSize = ContainerSectionSize(relocate);
                (ItemStackInfo Stack, int Slot)? pick = relocate.Slots
                    .Take(relocateSize)
                    .Select((s, i) => (Stack: s, Slot: i))
                    .Where(t => !t.Stack.IsEmpty && MatchesItem(t.Stack.ItemId, itemType.Trim(), true))
                    .OrderByDescending(t => preferLargestStack ? t.Stack.Count : -t.Slot)
                    .ThenBy(t => t.Slot)
                    .Select(t => (ValueTuple<ItemStackInfo, int>?)t)
                    .FirstOrDefault();
                if (pick is null)
                    break;

                int need = count - moved;
                int take = Math.Min(pick.Value.Stack.Count, need);
                attempted.Add(pick.Value.Slot);

                // Destination inside the attached player section: empty slots for whole stacks, partial-or-empty for splits.
                int? dest = null;
                IReadOnlyList<ItemStackInfo> section = relocate.Slots.Skip(relocateSize).Take(36).ToList();
                if (take == pick.Value.Stack.Count)
                {
                    for (int i = 0; i < section.Count && dest is null; i++)
                    {
                        if (section[i].IsEmpty)
                            dest = relocateSize + i;
                    }
                }
                else
                {
                    for (int i = 0; i < section.Count && dest is null; i++)
                    {
                        if (!section[i].IsEmpty
                            && MatchesItem(section[i].ItemId, itemType.Trim(), true)
                            && section[i].Count < 64)
                            dest = relocateSize + i;
                    }

                    if (dest is null)
                    {
                        for (int i = 0; i < section.Count && dest is null; i++)
                        {
                            if (section[i].IsEmpty)
                                dest = relocateSize + i;
                        }
                    }
                }

                if (dest is null)
                {
                    return McpResult.Fail(
                        McpErrorCodes.InvalidState,
                        "The player inventory has no room for this transfer.",
                        TransferPayload("withdraw", itemType, count, moved, beforePlayer, -1, beforeContainer, -1, attempted));
                }

                ItemStackInfo stepCursor;
                if (take == pick.Value.Stack.Count)
                {
                    await MoveWholeStackAsync(pick.Value.Slot, dest.Value, ct).ConfigureAwait(false);
                    stepCursor = await _context.Game.Inventory.GetCursorAsync(ct).ConfigureAwait(false);
                    if (!stepCursor.IsEmpty)
                    {
                        return McpResult.Fail(
                            McpErrorCodes.ActionIncomplete,
                            "The cursor is holding items after a whole-stack move.",
                            TransferPayload("withdraw", itemType, count, moved, beforePlayer, -1, beforeContainer, -1, attempted, stepCursor));
                    }
                }
                else
                {
                    stepCursor = await SplitMoveAsync(
                        pick.Value.Slot, pick.Value.Stack.Count, dest.Value, take, ct).ConfigureAwait(false);
                    if (!stepCursor.IsEmpty)
                    {
                        return McpResult.Fail(
                            McpErrorCodes.ActionIncomplete,
                            "The cursor is holding items after a split move.",
                            TransferPayload("withdraw", itemType, count, moved, beforePlayer, -1, beforeContainer, -1, attempted, stepCursor));
                    }
                }

                OpenContainerSnapshot? recount = await WaitForContainerAsync(
                    c => CountContainerSection(c, itemType.Trim()) != beforeContainer - moved,
                    4000, ct).ConfigureAwait(false);
                if (recount is null)
                {
                    return McpResult.Fail(
                        McpErrorCodes.ActionIncomplete,
                        "The container closed mid-transfer.",
                        TransferPayload("withdraw", itemType, count, moved, beforePlayer, -1, beforeContainer, -1, attempted));
                }

                int fresh = beforeContainer - CountContainerSection(recount, itemType.Trim());
                if (fresh == moved)
                {
                    stuckRounds++;
                    if (stuckRounds >= 2)
                        break;
                }
                else
                    stuckRounds = 0;

                moved = fresh;
            }

            PlayerInventorySnapshot afterPlayer =
                await _context.Game.Inventory.GetPlayerInventoryAsync(ct).ConfigureAwait(false);
            OpenContainerSnapshot? afterContainer =
                await _context.Game.Inventory.GetOpenContainerAsync(ct).ConfigureAwait(false);
            int afterContainerCount = afterContainer is null
                ? beforeContainer
                : CountContainerSection(afterContainer, itemType.Trim());
            // Hybrid player count: the frozen prefix plus the fresh player-section mirror.
            // The plain player-window snapshot can lag container-window clicks behind by a server update.
            int afterPlayerCount = frozen
                + (afterContainer is null
                    ? CountOf(afterPlayer.Slots, itemType.Trim(), true) - frozen
                    : CountPlayerSection(afterContainer, itemType.Trim()));
            moved = afterPlayerCount - (frozen + CountPlayerSection(container, itemType.Trim()));

            object payload = TransferPayload(
                "withdraw", itemType, count, moved, beforePlayer, afterPlayerCount,
                beforeContainer, afterContainerCount, attempted);

            return moved == count
                ? McpResult.Ok(payload)
                : McpResult.Fail(
                    McpErrorCodes.ActionIncomplete,
                    $"Moved {moved} of {count} requested.",
                    payload);
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }
}
