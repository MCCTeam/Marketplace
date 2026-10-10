using System.Reflection;
using ModelContextProtocol.Server;
using DMCBK.OfficialPlugins.McpServer;
using Xunit;
namespace DMCBK.Core.Tests;

/// <summary>
/// Catalog parity: every legacy tool name is registered, plus the deliberate <c>mcc_server_command</c> split, and the destructive-command blocklist plus window-action aliases hold.
/// </summary>
public sealed class McpToolParityTests
{
    private static readonly string[] LegacyTools =
    [
        "mcc_session_status", "mcc_server_info", "mcc_player_state", "mcc_world_state",
        "mcc_chunk_status", "mcc_raycast_block", "mcc_path_preview", "mcc_players_list",
        "mcc_players_detailed", "mcc_player_stats", "mcc_status_effects", "mcc_recent_events",
        "mcc_loaded_bots", "mcc_chat_history", "mcc_internal_commands_list", "mcc_agent_guidance",
        "mcc_materials_list", "mcc_block_types_list", "mcc_entity_types_list", "mcc_send_chat",
        "mcc_quit_client", "mcc_disconnect", "mcc_respawn", "mcc_run_internal_command",
        "mcc_animation", "mcc_toggle_sneak", "mcc_toggle_sprint", "mcc_change_hotbar_slot",
        "mcc_select_item", "mcc_use_item_on_hand", "mcc_use_item_on_block", "mcc_dig_block",
        "mcc_place_block", "mcc_entity_interact", "mcc_entity_attack", "mcc_block_scan",
        "mcc_blocks_find", "mcc_player_nearby", "mcc_player_locate", "mcc_entity_nearest",
        "mcc_can_reach_position", "mcc_move_to", "mcc_move_to_player", "mcc_look_at",
        "mcc_look_direction", "mcc_look_angles", "mcc_inventory_snapshot", "mcc_inventory_search",
        "mcc_inventories_list", "mcc_container_open_at", "mcc_container_close",
        "mcc_inventory_window_action", "mcc_inventory_drop_item", "mcc_container_deposit_item",
        "mcc_container_withdraw_item", "mcc_entities_query", "mcc_entities_list", "mcc_entity_info",
        "mcc_signs_find", "mcc_items_list", "mcc_items_pickup", "mcc_world_block_at",
    ];

    private static IReadOnlyList<string> ToolNames()
        => typeof(McpToolSet).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.Name)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList();

    [Fact]
    public void ToolSet_RegistersAllLegacyNames_PlusServerCommand()
    {
        List<string> names = ToolNames().ToList();
        foreach (string legacy in LegacyTools)
            Assert.Contains(legacy, names);

        Assert.Contains("mcc_server_command", names);
        Assert.Equal(LegacyTools.Length + 1, names.Count);
    }

    [Fact]
    public void ToolSet_HasNoDuplicateNames()
    {
        List<string> names = ToolNames().ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void PromptSet_ServesOperatorPrompt()
    {
        MethodInfo? method = typeof(McpPromptSet).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.GetCustomAttribute<McpServerPromptAttribute>()?.Name == "mcc_operator_prompt");
        Assert.NotNull(method);
    }

    [Fact]
    public void Blocklist_CoversDestructiveCommands()
    {
        foreach (string command in new[] { "plugins", "plugin", "scripts", "script", "connect", "reco", "reload", "set", "exit", "quit", "stop", "send" })
            Assert.Contains(command, McpCapabilities.BlockedInternalCommands);
    }

    [Theory]
    [InlineData("LeftClick", "LeftClick")]
    [InlineData("left", "LeftClick")]
    [InlineData("RightClick", "RightClick")]
    [InlineData("right", "RightClick")]
    [InlineData("ShiftClick", "ShiftClick")]
    [InlineData("shift", "ShiftClick")]
    [InlineData("shiftright", "ShiftClick")]
    [InlineData("drop", "Drop")]
    [InlineData("q", "Drop")]
    [InlineData("dropstack", "DropStack")]
    [InlineData("ctrlq", "DropStack")]
    [InlineData("middle", null)]
    [InlineData("bogus", null)]
    public void WindowActions_Normalize(string input, string? expected)
    {
        Assert.Equal(expected, McpCapabilities.NormalizeWindowAction(input));
    }

    [Theory]
    [InlineData(63, 27)]
    [InlineData(39, 3)]
    [InlineData(36, 0)]
    [InlineData(10, 10)]
    public void ContainerSection_Sizes(int total, int expected)
    {
        var container = new DMCBK.Core.OpenContainerSnapshot(
            1,
            Enumerable.Range(0, total).Select(_ => DMCBK.Core.ItemStackInfo.Empty).ToList(),
            new Dictionary<int, int>(),
            DMCBK.Core.ItemStackInfo.Empty,
            0,
            null,
            0,
            null);
        Assert.Equal(expected, McpCapabilities.ContainerSectionSize(container));
    }

    [Theory]
    [InlineData(27, 9, 27)]
    [InlineData(27, 35, 53)]
    [InlineData(27, 36, 54)]
    [InlineData(27, 44, 62)]
    [InlineData(27, 0, null)]
    [InlineData(27, 5, null)]
    [InlineData(27, 45, null)]
    public void CombinedSlots_MapPlayerSection(int containerSize, int playerSlot, int? expected)
    {
        Assert.Equal(expected, McpCapabilities.ToCombinedSlot(containerSize, playerSlot));
    }

    [Fact]
    public void SectionCounts_SplitWindow()
    {
        var slots = Enumerable.Range(0, 63).Select(i => DMCBK.Core.ItemStackInfo.Empty).ToList();
        slots[0] = new DMCBK.Core.ItemStackInfo("minecraft:dirt", 10, false, null, 0, 0, [], []);
        slots[30] = new DMCBK.Core.ItemStackInfo("minecraft:dirt", 5, false, null, 0, 0, [], []);
        slots[62] = new DMCBK.Core.ItemStackInfo("minecraft:dirt", 64, false, null, 0, 0, [], []);
        var container = new DMCBK.Core.OpenContainerSnapshot(
            1, slots, new Dictionary<int, int>(), DMCBK.Core.ItemStackInfo.Empty, 0, null, 0, null);
        Assert.Equal(10, McpCapabilities.CountContainerSection(container, "minecraft:dirt"));
        Assert.Equal(69, McpCapabilities.CountPlayerSection(container, "minecraft:dirt"));

        var player = Enumerable.Range(0, 46).Select(_ => DMCBK.Core.ItemStackInfo.Empty).ToList();
        player[0] = new DMCBK.Core.ItemStackInfo("minecraft:dirt", 3, false, null, 0, 0, [], []);
        player[20] = new DMCBK.Core.ItemStackInfo("minecraft:dirt", 7, false, null, 0, 0, [], []);
        Assert.Equal(3, McpCapabilities.CountFrozenPrefix(player, "minecraft:dirt"));
    }

    [Theory]
    [InlineData("MainHand", true)]
    [InlineData("main", true)]
    [InlineData("OffHand", true)]
    [InlineData("off", true)]
    [InlineData("bogus", false)]
    public void Hands_Parse(string input, bool expected)
    {
        Assert.Equal(expected, McpCapabilities.TryParseHand(input, out _));
    }

    [Fact]
    public async Task ListCommands_ExposesNameUsageDescription()
    {
        await using var client = new ClientBuilder().UseCommands().UseBeacon()
            .UseUsername("Tester")
            .UseServer("localhost")
            .Build();
        IReadOnlyList<DMCBK.Core.Commands.CommandSummary> commands = client.Commands.ListCommands();
        Assert.NotEmpty(commands);
        Assert.Contains(commands, c => c.Name == "help");
        Assert.All(commands, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Name));
            Assert.False(string.IsNullOrWhiteSpace(c.Description));
        });
    }
}
