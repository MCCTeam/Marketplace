using System.ComponentModel;
using ModelContextProtocol.Server;

namespace DMCBK.OfficialPlugins.McpServer;

/// <summary>
/// The MCP tool surface: the <c>mcc_*</c> names with identical signatures, plus <c>mcc_server_command</c> (server commands split out of <c>mcc_send_chat</c> on purpose).
/// Every method delegates to <see cref="McpCapabilities"/> and returns the shared envelope.
/// </summary>
[McpServerToolType]
public sealed class McpToolSet
{
    private readonly McpCapabilities _capabilities;

    public McpToolSet(McpCapabilities capabilities)
    {
        _capabilities = capabilities;
    }

    [McpServerTool(Name = "mcc_session_status"), Description("Get current MCC session and feature status.")]
    public async Task<object> SessionStatus(CancellationToken ct = default)
        => await _capabilities.GetSessionStatusAsync(ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_server_info"), Description("Get active MCC server connection info and current TPS.")]
    public async Task<object> ServerInfo(CancellationToken ct = default)
        => await _capabilities.GetServerInfoAsync(ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_player_state"), Description("Get current controlled player state.")]
    public async Task<object> PlayerState(CancellationToken ct = default)
        => await _capabilities.GetPlayerStateAsync(ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_world_state"), Description("Get current world state, chunk loading progress, and last observed runtime time values.")]
    public async Task<object> WorldState(CancellationToken ct = default)
        => await _capabilities.GetWorldStateAsync(ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_chunk_status"), Description("Get chunk loading status for the player location or an explicit world coordinate.")]
    public async Task<object> ChunkStatus(double? x = null, double? y = null, double? z = null, CancellationToken ct = default)
        => await _capabilities.GetChunkStatusAsync(x, y, z, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_raycast_block"), Description("Raycast from the player's current view and return the first non-air block hit.")]
    public async Task<object> RaycastBlock(double maxDistance = 8.0, bool includeNeighbors = false, CancellationToken ct = default)
        => await _capabilities.RaycastBlockAsync(maxDistance, includeNeighbors, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_path_preview"), Description("Compute a path preview to a target world coordinate without moving there.")]
    public async Task<object> PathPreview(
        double x, double y, double z, bool allowUnsafe = false,
        int maxOffset = 0, int minOffset = 0, int timeoutMs = 0, int maxWaypoints = 128,
        CancellationToken ct = default)
        => await _capabilities.PreviewPathAsync(x, y, z, allowUnsafe, maxOffset, minOffset, timeoutMs, maxWaypoints, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_players_list"), Description("List currently known online players.")]
    public async Task<object> PlayersList(CancellationToken ct = default)
        => await _capabilities.GetPlayersListAsync(ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_players_detailed"), Description("List online players with UUID, latency, gamemode, and tracked coordinates when available.")]
    public async Task<object> PlayersDetailed(
        bool includeSelf = false, bool includeCoordinates = true, CancellationToken ct = default)
        => await _capabilities.GetPlayersDetailedAsync(includeSelf, includeCoordinates, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_player_stats"), Description("Get current controlled player stats, orientation, and location.")]
    public async Task<object> PlayerStats(CancellationToken ct = default)
        => await _capabilities.GetPlayerStatsAsync(ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_status_effects"), Description("Get active player status effects only.")]
    public async Task<object> StatusEffects(CancellationToken ct = default)
        => await _capabilities.GetStatusEffectsAsync(ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_recent_events"), Description("Get recent high-signal MCP runtime events after a given event ID.")]
    public async Task<object> RecentEvents(
        long afterId = 0, int maxCount = 50, string? typeFilter = null, CancellationToken ct = default)
        => await _capabilities.GetRecentEventsAsync(afterId, maxCount, typeFilter, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_loaded_bots"), Description("List currently loaded MCC plugins (the 2.0 counterpart of the legacy bot list).")]
    public async Task<object> LoadedBots(CancellationToken ct = default)
        => await _capabilities.GetLoadedBotsAsync(ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_chat_history"), Description("Get recent chat/system lines seen by MCC.")]
    public async Task<object> ChatHistory(int maxCount = 50, bool includeJson = false, CancellationToken ct = default)
        => await _capabilities.GetChatHistoryAsync(maxCount, includeJson, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_internal_commands_list"), Description("List available MCC internal commands with usage and description.")]
    public async Task<object> InternalCommandsList(CancellationToken ct = default)
        => await _capabilities.GetInternalCommandsAsync(ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_agent_guidance"), Description("Get the canonical MCC MCP Operator Prompt bundle for external agents using this MCP server.")]
    public async Task<object> AgentGuidance(CancellationToken ct = default)
        => await _capabilities.GetAgentGuidanceAsync(ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_materials_list"), Description("List known item ids with optional filtering.")]
    public async Task<object> MaterialsList(string? filter = null, int maxCount = 500, CancellationToken ct = default)
        => await _capabilities.GetMaterialsListAsync(filter, maxCount, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_block_types_list"), Description("List known block type ids with optional filtering.")]
    public async Task<object> BlockTypesList(string? filter = null, int maxCount = 500, CancellationToken ct = default)
        => await _capabilities.GetBlockTypesListAsync(filter, maxCount, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_entity_types_list"), Description("List known entity type ids with optional filtering.")]
    public async Task<object> EntityTypesList(string? filter = null, int maxCount = 500, CancellationToken ct = default)
        => await _capabilities.GetEntityTypesListAsync(filter, maxCount, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_send_chat"), Description("Send chat text to the connected Minecraft server. Chat only: leading slashes are refused, use mcc_server_command for server commands.")]
    public async Task<object> SendChat(
        [Description("Text to send to server chat (no leading slash).")] string text,
        CancellationToken ct = default)
        => await _capabilities.SendChatAsync(text, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_server_command"), Description("Run one slash command on the Minecraft server as the player.")]
    public async Task<object> ServerCommand(
        [Description("Server command with or without leading slash.")] string command,
        CancellationToken ct = default)
        => await _capabilities.SendServerCommandAsync(command, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_quit_client"), Description("Quit the MCC client cleanly through the host, exactly as a typed quit.")]
    public async Task<object> QuitClient(CancellationToken ct = default)
        => await _capabilities.QuitClientAsync(ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_disconnect"), Description("Disconnect MCC from the current server without stopping the MCP endpoint.")]
    public async Task<object> Disconnect(CancellationToken ct = default)
        => await _capabilities.DisconnectAsync(ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_respawn"), Description("Request a respawn and report what the server observably did.")]
    public async Task<object> Respawn(CancellationToken ct = default)
        => await _capabilities.RespawnAsync(ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_run_internal_command"), Description("Run an internal MCC command (no leading slash). Destructive commands are refused.")]
    public async Task<object> RunInternalCommand(
        [Description("MCC command line without leading slash.")] string command,
        CancellationToken ct = default)
        => await _capabilities.RunInternalCommandAsync(command, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_animation"), Description("Play a hand-swing animation with the selected hand.")]
    public async Task<object> Animation(string hand = "MainHand", CancellationToken ct = default)
        => await _capabilities.PlayAnimationAsync(hand, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_toggle_sneak"), Description("Explicitly enable or disable sneaking.")]
    public async Task<object> ToggleSneak(bool enabled, CancellationToken ct = default)
        => await _capabilities.ToggleSneakAsync(enabled, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_toggle_sprint"), Description("Explicitly send start or stop sprinting entity actions.")]
    public async Task<object> ToggleSprint(bool enabled, CancellationToken ct = default)
        => await _capabilities.ToggleSprintAsync(enabled, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_change_hotbar_slot"), Description("Change active hotbar slot (1-9).")]
    public async Task<object> ChangeHotbarSlot(int slot, CancellationToken ct = default)
        => await _capabilities.ChangeHotbarSlotAsync(slot, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_select_item"), Description("Select a hotbar item by item type without rearranging inventory contents.")]
    public async Task<object> SelectItem(
        string itemType, bool preferLowestSlot = true, CancellationToken ct = default)
        => await _capabilities.SelectHotbarItemAsync(itemType, preferLowestSlot, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_use_item_on_hand"), Description("Use the currently held item.")]
    public async Task<object> UseItemOnHand(CancellationToken ct = default)
        => await _capabilities.UseItemOnHandAsync(ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_use_item_on_block"), Description("Use currently held item on a target block location.")]
    public async Task<object> UseItemOnBlock(double x, double y, double z, CancellationToken ct = default)
        => await _capabilities.UseItemOnBlockAsync(x, y, z, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_dig_block"), Description("Dig a block at target location, reporting what the server observably did.")]
    public async Task<object> DigBlock(
        double x, double y, double z, double durationSeconds = 0, CancellationToken ct = default)
        => await _capabilities.DigBlockAsync(x, y, z, durationSeconds, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_place_block"), Description("Place the currently held block/item at a target block location. Send-only: verify the world afterwards.")]
    public async Task<object> PlaceBlock(
        int x, int y, int z, string face = "Up", string hand = "MainHand", bool lookAtBlock = false,
        CancellationToken ct = default)
        => await _capabilities.PlaceBlockAsync(x, y, z, face, hand, lookAtBlock, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_entity_interact"), Description("Interact with a tracked entity.")]
    public async Task<object> EntityInteract(
        int entityId, string interaction = "Interact", string hand = "MainHand",
        CancellationToken ct = default)
        => await _capabilities.InteractEntityAsync(entityId, interaction, hand, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_entity_attack"), Description("Attack a tracked entity explicitly.")]
    public async Task<object> EntityAttack(int entityId, CancellationToken ct = default)
        => await _capabilities.AttackEntityAsync(entityId, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_block_scan"), Description("Scan nearby blocks around player location.")]
    public async Task<object> BlockScan(
        int radius = 3, int maxCount = 200, string? materialFilter = null, CancellationToken ct = default)
        => await _capabilities.ScanNearbyBlocksAsync(radius, maxCount, materialFilter, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_blocks_find"), Description("Find nearby blocks by block name/type query or block ID.")]
    public async Task<object> BlocksFind(
        string? query = null, int radius = 6, int maxCount = 200, bool exactMatch = false,
        CancellationToken ct = default)
        => await _capabilities.FindBlocksAsync(query, radius, maxCount, exactMatch, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_player_nearby"), Description("Check if any player, or a specific player, is nearby.")]
    public async Task<object> PlayerNearby(
        string? playerName = null, double radius = 32, bool includeSelf = false,
        CancellationToken ct = default)
        => await _capabilities.IsPlayerNearbyAsync(playerName, radius, includeSelf, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_player_locate"), Description("Locate a tracked player entity by name and return exact coordinates when available.")]
    public async Task<object> PlayerLocate(
        string playerName, bool includeSelf = false, CancellationToken ct = default)
        => await _capabilities.LocatePlayerAsync(playerName, includeSelf, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_entity_nearest"), Description("Return the nearest tracked entity matching the requested filters.")]
    public async Task<object> EntityNearest(
        string? typeFilter = null, string? nameFilter = null, double radius = 64.0, bool includePlayers = true,
        CancellationToken ct = default)
        => await _capabilities.FindNearestEntityAsync(typeFilter, nameFilter, radius, includePlayers, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_can_reach_position"), Description("Check whether MCC can currently path to a world coordinate without moving there.")]
    public async Task<object> CanReachPosition(
        double x, double y, double z, bool allowUnsafe = false,
        int maxOffset = 0, int minOffset = 0, int timeoutMs = 0,
        CancellationToken ct = default)
        => await _capabilities.CanReachPositionAsync(x, y, z, allowUnsafe, maxOffset, minOffset, timeoutMs, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_move_to"), Description("Planner navigation to a world coordinate with verified arrival. No teleport exists; allowUnsafe lifts fall-damage guards (lethal falls become planable).")]
    public async Task<object> MoveTo(
        double x, double y, double z, bool allowUnsafe = false, bool allowDirectTeleport = false,
        int maxOffset = 0, int minOffset = 0, int timeoutMs = 0,
        CancellationToken ct = default)
        => await _capabilities.MoveToAsync(x, y, z, allowUnsafe, allowDirectTeleport, maxOffset, minOffset, timeoutMs, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_move_to_player"), Description("Locate a tracked player entity, navigate to them, and verify arrival.")]
    public async Task<object> MoveToPlayer(
        string playerName, bool allowUnsafe = false, bool allowDirectTeleport = false,
        int maxOffset = 0, int minOffset = 0, int timeoutMs = 0,
        CancellationToken ct = default)
        => await _capabilities.MoveToPlayerAsync(playerName, allowUnsafe, allowDirectTeleport, maxOffset, minOffset, timeoutMs, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_look_at"), Description("Rotate player view toward world coordinates.")]
    public async Task<object> LookAt(double x, double y, double z, CancellationToken ct = default)
        => await _capabilities.LookAtAsync(x, y, z, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_look_direction"), Description("Rotate player view to a cardinal direction or straight up/down.")]
    public async Task<object> LookDirection(string direction, CancellationToken ct = default)
        => await _capabilities.LookDirectionAsync(direction, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_look_angles"), Description("Rotate player view to explicit yaw and pitch angles.")]
    public async Task<object> LookAngles(float yaw, float pitch, CancellationToken ct = default)
        => await _capabilities.LookAnglesAsync(yaw, pitch, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_inventory_snapshot"), Description("Get a snapshot of one inventory.")]
    public async Task<object> InventorySnapshot(
        [Description("Inventory ID. 0 is the player inventory.")] int inventoryId = 0,
        CancellationToken ct = default)
        => await _capabilities.GetInventorySnapshotAsync(inventoryId, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_inventory_search"), Description("Search the player inventory and optionally the open container for items matching a query.")]
    public async Task<object> InventorySearch(
        string query, int maxCount = 100, bool exactMatch = false, bool includeContainers = true,
        CancellationToken ct = default)
        => await _capabilities.SearchInventoriesAsync(query, maxCount, exactMatch, includeContainers, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_inventories_list"), Description("List the player inventory and the open container, if any.")]
    public async Task<object> InventoriesList(CancellationToken ct = default)
        => await _capabilities.ListInventoriesAsync(ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_container_open_at"), Description("Open an interactable container block at world coordinates and wait for the container inventory to appear.")]
    public async Task<object> ContainerOpenAt(
        int x, int y, int z, int timeoutMs = 0, bool closeCurrent = true,
        CancellationToken ct = default)
        => await _capabilities.OpenContainerAtAsync(x, y, z, timeoutMs, closeCurrent, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_container_close"), Description("Close an open non-player container. Use inventoryId=-1 to close the active container.")]
    public async Task<object> ContainerClose(
        [Description("Container inventory ID, or -1 for the active non-player container.")] int inventoryId = -1,
        int timeoutMs = 0, CancellationToken ct = default)
        => await _capabilities.CloseContainerAsync(inventoryId, timeoutMs, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_inventory_window_action"), Description("Perform a window action on an inventory slot.")]
    public async Task<object> InventoryWindowAction(
        int inventoryId, int slotId,
        [Description("LeftClick, RightClick, ShiftClick, Drop or DropStack (aliases: left, right, shift, q, ctrlq).")] string actionType,
        CancellationToken ct = default)
        => await _capabilities.InventoryWindowActionAsync(inventoryId, slotId, actionType, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_inventory_drop_item"), Description("Drop an exact item count from the player inventory by item type.")]
    public async Task<object> InventoryDropItem(
        [Description("Item type id (e.g. minecraft:diamond).")] string itemType,
        [Description("Exact number of items to drop.")] int count,
        [Description("Inventory ID. 0 is the player inventory.")] int inventoryId = 0,
        [Description("Prefer dropping from larger stacks first when true.")] bool preferStack = false,
        CancellationToken ct = default)
        => await _capabilities.DropInventoryItemAsync(itemType, count, inventoryId, preferStack, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_container_deposit_item"), Description("Move an exact item count from the player inventory into an open container and verify the transfer.")]
    public async Task<object> ContainerDepositItem(
        [Description("Item type id (e.g. minecraft:diamond).")] string itemType,
        [Description("Exact number of items to move into the container.")] int count,
        [Description("Container inventory ID, or -1 for the active non-player container.")] int inventoryId = -1,
        [Description("Prefer larger source stacks first when true.")] bool preferLargestStack = true,
        CancellationToken ct = default)
        => await _capabilities.DepositContainerItemAsync(itemType, count, inventoryId, preferLargestStack, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_container_withdraw_item"), Description("Move an exact item count from an open container into the player inventory and verify the transfer.")]
    public async Task<object> ContainerWithdrawItem(
        [Description("Item type id (e.g. minecraft:diamond).")] string itemType,
        [Description("Exact number of items to move into the player inventory.")] int count,
        [Description("Container inventory ID, or -1 for the active non-player container.")] int inventoryId = -1,
        [Description("Prefer larger source stacks first when true.")] bool preferLargestStack = true,
        CancellationToken ct = default)
        => await _capabilities.WithdrawContainerItemAsync(itemType, count, inventoryId, preferLargestStack, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_entities_query"), Description("Query tracked entities.")]
    public async Task<object> EntitiesQuery(
        [Description("Maximum entities to return.")] int maxCount = 50,
        CancellationToken ct = default)
        => await _capabilities.QueryEntitiesAsync(maxCount, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_entities_list"), Description("List tracked entities with optional type and radius filtering.")]
    public async Task<object> EntitiesList(
        int maxCount = 100, string? typeFilter = null, double radius = 0,
        CancellationToken ct = default)
        => await _capabilities.ListEntitiesAsync(maxCount, typeFilter, radius, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_entity_info"), Description("Get detailed info for one tracked entity.")]
    public async Task<object> EntityInfo(
        int entityId, bool includeMetadata = false, bool includeEquipment = true, bool includeEffects = true,
        CancellationToken ct = default)
        => await _capabilities.GetEntityInfoAsync(entityId, includeMetadata, includeEquipment, includeEffects, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_signs_find"), Description("Find nearby signs whose text contains the requested text.")]
    public async Task<object> SignsFind(
        string text, bool exactMatch = false, int radius = 16, int maxCount = 50, bool includeBackText = true,
        CancellationToken ct = default)
        => await _capabilities.FindSignsAsync(text, exactMatch, radius, maxCount, includeBackText, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_items_list"), Description("List nearby dropped item entities with optional item type filtering.")]
    public async Task<object> ItemsList(
        string? itemType = null, double radius = 32, int maxCount = 100,
        CancellationToken ct = default)
        => await _capabilities.ListItemEntitiesAsync(itemType, radius, maxCount, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_items_pickup"), Description("Move to and pick up nearby dropped items of a given item type, verified by inventory delta.")]
    public async Task<object> ItemsPickup(
        string itemType, double radius = 32, int maxItems = 20, bool allowUnsafe = false, int timeoutMs = 0,
        CancellationToken ct = default)
        => await _capabilities.PickupItemsAsync(itemType, radius, maxItems, allowUnsafe, timeoutMs, ct).ConfigureAwait(false);

    [McpServerTool(Name = "mcc_world_block_at"), Description("Get block information at world coordinates.")]
    public async Task<object> WorldBlockAt(int x, int y, int z, CancellationToken ct = default)
        => await _capabilities.GetWorldBlockAtAsync(x, y, z, ct).ConfigureAwait(false);
}
