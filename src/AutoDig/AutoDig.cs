// AutoDig: digs the block at the crosshair, or the positions listed in settings.toml, driven by a four-state machine at 20 TPS (wait for join, wait to start, digging, stopping).
//
// Apply_Efficiency_Enchantments and Apply_Haste_Effects are inert.
// They only fed a client-side mining timer, and the dig action now sends the start and the finish atomically instead of timing the break.

using DMCBK.Core;
using DMCBK.Core.Commands;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Tomlet.Attributes;
using Umpk.Client.Events;
using Umpk.Commands;
using Umpk.Game.Blocks;
using Umpk.Game.Inventory;
using Umpk.Game.Items;
using Umpk.Geometry;

namespace DMCBK.OfficialPlugins.AutoDig;

/// <summary>Where AutoDig looks for the next block.</summary>
public enum DigMode
{
    /// <summary>Dig whatever the crosshair is on.</summary>
    LookAt,

    /// <summary>Dig the configured Locations.</summary>
    FixedPos,

    /// <summary>Dig the crosshair block, but only when it is one of the configured Locations.</summary>
    Both,
}

/// <summary>How the configured block list is applied.</summary>
public enum BlockListType
{
    /// <summary>Only listed blocks are dug.</summary>
    Whitelist,

    /// <summary>Listed blocks are never dug.</summary>
    Blacklist,
}

/// <summary>The order the fixed positions are visited in.</summary>
public enum LocationOrder
{
    /// <summary>Nearest first.</summary>
    Distance,

    /// <summary>Config order.</summary>
    Index,
}

/// <summary>Tool classes AutoDig can switch to.</summary>
public enum ToolClass
{
    /// <summary>No tool is recommended for the block.</summary>
    None,

    /// <summary>Pickaxe blocks.</summary>
    Pickaxe,

    /// <summary>Axe blocks.</summary>
    Axe,

    /// <summary>Shovel blocks.</summary>
    Shovel,

    /// <summary>Hoe blocks.</summary>
    Hoe,

    /// <summary>Shears blocks.</summary>
    Shears,
}

/// <summary>Block naming, list matching and tool selection.</summary>
public static class DigRules
{
    // Tool tiers, best first.
    private static readonly string[] TierOrder = ["netherite", "diamond", "iron", "golden", "stone", "wooden"];

    private static readonly string[] ShovelKeywords =
    [
        "dirt", "grass_block", "sand", "gravel", "clay", "soul_sand", "soul_soil", "snow", "farmland",
        "dirt_path", "mycelium", "podzol", "concrete_powder", "mud", "rooted_dirt", "coarse_dirt", "suspicious",
    ];

    private static readonly string[] AxeKeywords =
    [
        "log", "wood", "planks", "stem", "hyphae", "fence", "door", "trapdoor", "sign", "chest", "barrel",
        "crafting_table", "bookshelf", "ladder", "banner", "pumpkin", "melon", "bamboo", "campfire",
        "lectern", "loom", "beehive", "bee_nest", "cartography_table", "fletching_table", "smithing_table",
        "composter", "note_block", "jukebox", "mangrove_roots", "scaffolding", "shelf",
    ];

    private static readonly string[] HoeKeywords =
    [
        "leaves", "hay_block", "sponge", "target", "moss", "nether_wart_block", "shroomlight",
        "dried_kelp_block", "sculk",
    ];

    private static readonly string[] ShearsKeywords = ["wool", "cobweb", "vine", "carpet"];

    private static readonly string[] PickaxeKeywords =
    [
        "stone", "ore", "deepslate", "cobble", "brick", "concrete", "terracotta", "obsidian", "netherrack",
        "quartz", "prismarine", "purpur", "sandstone", "rail", "anvil", "furnace", "blast_furnace", "smoker",
        "iron_block", "gold_block", "diamond_block", "emerald_block", "redstone_block", "lapis_block",
        "netherite_block", "copper", "amethyst", "basalt", "blackstone", "calcite", "tuff", "dripstone",
        "andesite", "diorite", "granite", "magma_block", "glazed", "hopper", "cauldron", "bell", "grindstone",
        "stonecutter", "piston", "observer", "dispenser", "dropper", "enchanting_table", "ice", "shulker_box",
        "spawner", "end_stone", "resin",
    ];

    /// <summary>Strips a namespace, converts a PascalCase spelling to snake_case, and lowercases.</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        string value = name.Trim();
        int colon = value.IndexOf(':');
        if (colon >= 0)
            value = value[(colon + 1)..];

        var builder = new System.Text.StringBuilder(value.Length + 4);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsUpper(c) && builder.Length > 0 && builder[^1] != '_')
                builder.Append('_');

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>Normalizes a list of block names, dropping blanks and duplicates.</summary>
    public static string[] NormalizeAll(IEnumerable<string>? names)
    {
        if (names is null)
            return [];

        var result = new List<string>();
        foreach (string name in names)
        {
            string bare = Normalize(name);
            if (bare.Length > 0 && !result.Contains(bare))
                result.Add(bare);
        }

        return [.. result];
    }

    /// <summary>Whitelist means the block must be listed, blacklist means it must not be.</summary>
    public static bool IsAllowed(string blockId, BlockListType listType, IReadOnlyList<string> blocks)
    {
        string bare = Normalize(blockId);
        bool inList = false;
        for (int i = 0; i < blocks.Count; i++)
        {
            if (string.Equals(bare, blocks[i], StringComparison.Ordinal))
            {
                inList = true;
                break;
            }
        }

        return listType == BlockListType.Whitelist ? inList : !inList;
    }

    /// <summary>The tool class recommended for a block id.</summary>
    public static ToolClass RecommendedTool(string blockId)
    {
        string bare = Normalize(blockId);
        if (bare.Length == 0)
            return ToolClass.None;

        // Tested first so they win over the axe and pickaxe keywords they share.
        if (ContainsAny(bare, ShearsKeywords))
            return ToolClass.Shears;

        if (ContainsAny(bare, HoeKeywords))
            return ToolClass.Hoe;

        if (ContainsAny(bare, ShovelKeywords))
            return ToolClass.Shovel;

        if (ContainsAny(bare, AxeKeywords))
            return ToolClass.Axe;

        return ContainsAny(bare, PickaxeKeywords) ? ToolClass.Pickaxe : ToolClass.None;
    }

    /// <summary>The item ids of a tool class, best tier first.</summary>
    public static string[] ToolItemIds(ToolClass tool)
    {
        if (tool == ToolClass.None)
            return [];

        if (tool == ToolClass.Shears)
            return ["shears"];

        string suffix = tool switch
        {
            ToolClass.Axe => "_axe",
            ToolClass.Shovel => "_shovel",
            ToolClass.Hoe => "_hoe",
            _ => "_pickaxe",
        };

        var ids = new string[TierOrder.Length];
        for (int i = 0; i < TierOrder.Length; i++)
            ids[i] = TierOrder[i] + suffix;

        return ids;
    }

    /// <summary>Remaining durability of a stack; int.MaxValue when the item is not damageable.</summary>
    public static int RemainingDurability(int damage, int maxDamage)
        => maxDamage > 0 ? maxDamage - damage : int.MaxValue;

    /// <summary>A limit of zero or less disables the durability check.</summary>
    public static bool HasEnoughDurability(int damage, int maxDamage, int limit)
        => limit <= 0 || RemainingDurability(damage, maxDamage) >= limit;

    private static bool ContainsAny(string haystack, string[] needles)
    {
        for (int i = 0; i < needles.Length; i++)
        {
            if (haystack.Contains(needles[i], StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>Parses a dig mode name: lookat, fixedpos or both.</summary>
    public static DigMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "fixedpos" => DigMode.FixedPos,
        "both" => DigMode.Both,
        _ => DigMode.LookAt,
    };

    /// <summary>Renders a dig mode in lowercase.</summary>
    public static string ModeName(DigMode mode) => mode switch
    {
        DigMode.FixedPos => "fixedpos",
        DigMode.Both => "both",
        _ => "lookat",
    };

    /// <summary>Parses a list type name: whitelist or blacklist.</summary>
    public static BlockListType ParseListType(string? value)
        => string.Equals(value?.Trim(), "blacklist", StringComparison.OrdinalIgnoreCase)
            ? BlockListType.Blacklist
            : BlockListType.Whitelist;

    /// <summary>Renders a list type in lowercase.</summary>
    public static string ListTypeName(BlockListType listType)
        => listType == BlockListType.Blacklist ? "blacklist" : "whitelist";

    /// <summary>Parses a location order name: distance or index.</summary>
    public static LocationOrder ParseOrder(string? value)
        => string.Equals(value?.Trim(), "index", StringComparison.OrdinalIgnoreCase)
            ? LocationOrder.Index
            : LocationOrder.Distance;

    /// <summary>Renders a location order in lowercase.</summary>
    public static string OrderName(LocationOrder order) => order == LocationOrder.Index ? "index" : "distance";

    /// <summary>Converts seconds to whole client ticks at 20 TPS.</summary>
    public static int SecondsToTicks(double seconds) => (int)Math.Round(seconds * 20.0);
}

/// <summary>One configured dig position.</summary>
public sealed class DigLocation
{
    /// <summary>The X coordinate.</summary>
    public double x { get; set; }

    /// <summary>The Y coordinate.</summary>
    public double y { get; set; }

    /// <summary>The Z coordinate.</summary>
    public double z { get; set; }
}

/// <summary>The user-editable, persisted settings for AutoDig (serialized to settings.toml).</summary>
public sealed class AutoDigSettings : IValidatablePluginSettings
{
    /// <summary>Master switch for the plugin.</summary>
    [TomlInlineComment("$settings.enabled$")]
    public bool Enabled { get; set; }

    /// <summary>Switch to the recommended tool before digging.</summary>
    [TomlInlineComment("$settings.auto_tool_switch$")]
    public bool Auto_Tool_Switch { get; set; }

    /// <summary>Inert: the dig action has no client-side mining timer to feed.</summary>
    [TomlInlineComment("$settings.apply_efficiency_enchantments$")]
    public bool Apply_Efficiency_Enchantments { get; set; } = true;

    /// <summary>Inert: the dig action has no client-side mining timer to feed.</summary>
    [TomlInlineComment("$settings.apply_haste_effects$")]
    public bool Apply_Haste_Effects { get; set; } = true;

    /// <summary>Never use a tool with less remaining durability than this; 0 disables the check.</summary>
    [TomlInlineComment("$settings.durability_limit$")]
    public int Durability_Limit { get; set; } = 2;

    /// <summary>Drop a worn-out tool after switching away from it.</summary>
    [TomlInlineComment("$settings.drop_low_durability_tools$")]
    public bool Drop_Low_Durability_Tools { get; set; }

    /// <summary>lookat, fixedpos or both.</summary>
    [TomlInlineComment("$settings.mode$")]
    public string Mode { get; set; } = "lookat";

    /// <summary>The fixed dig positions.</summary>
    [TomlPrecedingComment("$settings.locations$")]
    public DigLocation[] Locations { get; set; } =
    [
        new DigLocation { x = 123.5, y = 64, z = 234.5 },
        new DigLocation { x = 124.5, y = 63, z = 235.5 },
    ];

    /// <summary>distance or index.</summary>
    [TomlInlineComment("$settings.location_order$")]
    public string Location_Order { get; set; } = "distance";

    /// <summary>Seconds to wait after joining or respawning before the first dig.</summary>
    [TomlInlineComment("$settings.auto_start_delay$")]
    public double Auto_Start_Delay { get; set; } = 3.0;

    /// <summary>Seconds before a stuck dig re-arms the state machine.</summary>
    [TomlInlineComment("$settings.dig_timeout$")]
    public double Dig_Timeout { get; set; } = 60.0;

    /// <summary>Log every dig and every reason for not digging.</summary>
    [TomlInlineComment("$settings.log_block_dig$")]
    public bool Log_Block_Dig { get; set; } = true;

    /// <summary>whitelist or blacklist.</summary>
    [TomlInlineComment("$settings.list_type$")]
    public string List_Type { get; set; } = "whitelist";

    /// <summary>The block list.</summary>
    [TomlPrecedingComment("$settings.blocks$")]
    public string[] Blocks { get; set; } = ["Cobblestone", "Stone"];

    /// <summary>Clamps and normalizes on load and reload.</summary>
    public void Validate()
    {
        Blocks ??= [];
        Locations ??= [];
        Durability_Limit = Math.Max(0, Durability_Limit);

        if (Auto_Start_Delay >= 0)
            Auto_Start_Delay = Math.Max(0.1, Auto_Start_Delay);

        if (Dig_Timeout >= 0)
            Dig_Timeout = Math.Max(0.1, Dig_Timeout);

        Mode = DigRules.ModeName(DigRules.ParseMode(Mode));
        List_Type = DigRules.ListTypeName(DigRules.ParseListType(List_Type));
        Location_Order = DigRules.OrderName(DigRules.ParseOrder(Location_Order));
    }
}

/// <summary>The AutoDig plugin entry point.</summary>
public sealed class AutoDigPlugin : IPlugin
{
    private enum DigState
    {
        WaitJoinGame,
        WaitingStart,
        Digging,
        Stopping,
    }

    private readonly object _gate = new();
    private AutoDigSettings _settings = new();
    private PluginContext? _context;
    private ILogger? _logger;
    private IPluginLocalization? _strings;
    private string[] _blocks = [];
    private BlockPos[] _locations = [];
    private DigMode _mode = DigMode.LookAt;
    private BlockListType _listType = BlockListType.Whitelist;
    private LocationOrder _order = LocationOrder.Distance;
    private DigState _state = DigState.WaitJoinGame;
    private int _counter;
    private bool _alreadyWaiting;
    private bool _digPending;
    private BlockPos? _currentDig;

    /// <inheritdoc/>
    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "auto-dig";
        descriptor.Version = "2.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
        descriptor.WithSettings<AutoDigSettings>();
    }

    /// <inheritdoc/>
    public Task ActivateAsync(PluginContext context)
    {
        _settings = context.Settings.Load<AutoDigSettings>();
        _context = context;
        _logger = context.Logger;
        _strings = context.Strings;

        _blocks = DigRules.NormalizeAll(_settings.Blocks);
        _mode = DigRules.ParseMode(_settings.Mode);
        _listType = DigRules.ParseListType(_settings.List_Type);
        _order = DigRules.ParseOrder(_settings.Location_Order);
        _locations = new BlockPos[_settings.Locations.Length];
        for (int i = 0; i < _settings.Locations.Length; i++)
        {
            DigLocation location = _settings.Locations[i];
            _locations[i] = BlockPos.Containing(location.x, location.y, location.z);
        }

        context.Commands.Register(new AutoDigCommand(this, context.Strings));

        context.SessionStarted += (_, e) => OnSessionStarted(e.Session);
        context.SessionEnded += (_, _) => StopDigging();
        return Task.CompletedTask;
    }

    /// <summary>Arms the state machine.</summary>
    internal void StartDigging()
    {
        lock (_gate)
        {
            _counter = 0;
            _alreadyWaiting = false;
            _state = DigState.WaitingStart;
        }
    }

    /// <summary>Disarms the state machine.</summary>
    internal void StopDigging()
    {
        lock (_gate)
        {
            _state = DigState.Stopping;
            _digPending = false;
            _currentDig = null;
        }
    }

    private void AutoStart()
    {
        if (!_settings.Enabled)
            return;

        if (_settings.Auto_Start_Delay > 0)
        {
            _logger?.LogInformation("{Message}", _strings?.Format("start_delay", _settings.Auto_Start_Delay));
            lock (_gate)
            {
                _counter = DigRules.SecondsToTicks(_settings.Auto_Start_Delay);
                _alreadyWaiting = false;
                _state = DigState.WaitingStart;
            }
        }
        else
        {
            lock (_gate)
                _state = DigState.WaitJoinGame;
        }
    }

    private void OnSessionStarted(ISessionScope session)
    {
        lock (_gate)
        {
            _state = DigState.WaitJoinGame;
            _counter = 0;
            _digPending = false;
            _currentDig = null;
        }

        if (!session.State.Features.Terrain)
        {
            _logger?.LogWarning("{Message}", _strings?.Get("terrain_required"));
            return;
        }

        if (!session.State.Features.Inventory && _settings.Auto_Tool_Switch)
            _logger?.LogWarning("{Message}", _strings?.Get("no_inventory_handling"));

        session.Events.Subscribe<BlockChanged>(e => OnBlockChanged(e.Position));
        session.Events.Subscribe<Respawned>(_ => AutoStart());
        session.Events.Subscribe<Died>(_ => StopDigging());
        session.Scheduler.OnTick(() => OnTick(session));
        AutoStart();
    }

    private void OnBlockChanged(BlockPos position)
    {
        lock (_gate)
        {
            if (_state == DigState.Digging && _currentDig == position)
            {
                _currentDig = null;
                _counter = 0;
                _state = DigState.WaitingStart;
            }
        }
    }

    private void OnTick(ISessionScope session)
    {
        bool startDig = false;
        lock (_gate)
        {
            switch (_state)
            {
                case DigState.WaitingStart:
                    if (!_digPending && --_counter < 0)
                    {
                        _digPending = true;
                        startDig = true;
                    }

                    break;

                case DigState.Digging:
                    if (++_counter > DigRules.SecondsToTicks(_settings.Dig_Timeout))
                    {
                        _logger?.LogInformation("{Message}", _strings?.Get("dig_timeout"));
                        _state = DigState.WaitingStart;
                        _counter = 0;
                    }

                    break;
            }
        }

        if (startDig)
            _ = RunDigCycleAsync(session);
    }

    private async Task RunDigCycleAsync(ISessionScope session)
    {
        bool started;
        try
        {
            started = await DoDiggingAsync(session).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "{Message}", _strings?.Get("cycle_failed"));
            started = false;
        }

        lock (_gate)
        {
            _digPending = false;
            if (_state is DigState.Stopping or DigState.WaitJoinGame)
                return;

            if (started)
            {
                _alreadyWaiting = false;
                _counter = 0;
                _state = DigState.Digging;
            }
            else
            {
                _counter = 0;
                _state = DigState.WaitingStart;
            }
        }
    }

    private async Task<bool> DoDiggingAsync(ISessionScope session)
    {
        if (_mode is DigMode.LookAt or DigMode.Both)
            return await DigLookedAtAsync(session).ConfigureAwait(false);

        return await DigFixedAsync(session).ConfigureAwait(false);
    }

    private async Task<bool> DigLookedAtAsync(ISessionScope session)
    {
        GameApi game = _context!.Game;
        BlockRaycastHit? hit = await game.World.RaycastAsync(4.5, false, session.Detached).ConfigureAwait(false);
        if (hit is null)
        {
            Waiting(_strings?.Get("too_far"));
            return false;
        }

        if (!DigRules.IsAllowed(hit.BlockId, _listType, _blocks))
        {
            Waiting(_strings?.Get("not_allowed"));
            return false;
        }

        if (_mode == DigMode.Both && Array.IndexOf(_locations, hit.Position) < 0)
        {
            Waiting(_strings?.Get("not_allowed"));
            return false;
        }

        return await DigAsync(session, hit.Position, hit.BlockId, lookAt: false).ConfigureAwait(false);
    }

    private async Task<bool> DigFixedAsync(ISessionScope session)
    {
        (BlockPos Position, string BlockId)? target = await session.Client
            .InvokeAsync(_ => PickFixedTarget(session), session.Detached).ConfigureAwait(false);
        if (target is null)
        {
            Waiting(_strings?.Get("no_block"));
            return false;
        }

        return await DigAsync(session, target.Value.Position, target.Value.BlockId, lookAt: true).ConfigureAwait(false);
    }

    private (BlockPos Position, string BlockId)? PickFixedTarget(ISessionScope session)
    {
        Vec3d self = session.State.Self.Position;
        BlockPos? best = null;
        string bestId = string.Empty;
        double bestDistance = double.MaxValue;

        foreach (BlockPos position in _locations)
        {
            BlockState state = session.State.World.GetBlock(position);
            if (state.IsAir)
                continue;

            string blockId = state.Block.Id.ToString();
            if (!DigRules.IsAllowed(blockId, _listType, _blocks))
                continue;

            if (_order == LocationOrder.Index)
                return (position, blockId);

            double distance = new Vec3d(position.X, position.Y, position.Z).Subtract(self).Length();
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = position;
                bestId = blockId;
            }
        }

        // The distance order only digs a position within 6 blocks.
        if (best is null || bestDistance > 6.0)
        {
            _logger?.LogDebug(
                "{Message}",
                _strings?.Format("fixed_scan", self, _locations.Length, best, bestDistance));
            return null;
        }

        return (best.Value, bestId);
    }

    private async Task<bool> DigAsync(ISessionScope session, BlockPos position, string blockId, bool lookAt)
    {
        if (!await EnsureSuitableToolAsync(session, blockId).ConfigureAwait(false))
            return false;

        try
        {
            if (lookAt)
                await session.Actions.Movement.LookAtAsync(position.Center, session.Detached).ConfigureAwait(false);

            lock (_gate)
                _currentDig = position;

            // Logged as the dig is issued: the block-changed acknowledgement this awaits can lag well behind the request on a slow server.
            if (_settings.Log_Block_Dig)
            {
                _logger?.LogInformation(
                    "{Message}",
                    _strings?.Format("digging", position.X, position.Y, position.Z, blockId));
            }

            await session.Actions.Interaction.DigBlockAsync(position, Direction.Down, session.Detached)
                .ConfigureAwait(false);

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogInformation("{Message}", _strings?.Get("dig_fail"));
            _logger?.LogDebug(ex, "{Message}", _strings?.Get("dig_threw"));
            return false;
        }
    }

    private async Task<bool> EnsureSuitableToolAsync(ISessionScope session, string blockId)
    {
        if (!_settings.Auto_Tool_Switch || !session.State.Features.Inventory)
            return true;

        ToolClass tool = DigRules.RecommendedTool(blockId);
        string[] wanted = DigRules.ToolItemIds(tool);
        if (wanted.Length == 0)
            return true;

        IReadOnlyList<ItemStack> slots = session.State.Inventory.PlayerSlots;
        int handSlot = 36 + session.State.Self.HeldSlot;
        ItemStack held = handSlot >= 0 && handSlot < slots.Count ? slots[handSlot] : ItemStack.Empty;
        string heldId = held.IsEmpty ? string.Empty : DigRules.Normalize(held.Item.Id.ToString());
        bool heldIsRecommended = Array.IndexOf(wanted, heldId) >= 0;
        bool heldHasDurability = DigRules.HasEnoughDurability(held.Damage, held.MaxDamage, _settings.Durability_Limit);

        if (heldIsRecommended && heldHasDurability)
            return true;

        foreach (string wantedId in wanted)
        {
            for (int slot = 0; slot < slots.Count; slot++)
            {
                if (slot == handSlot || slots[slot].IsEmpty)
                    continue;

                ItemStack candidate = slots[slot];
                if (!string.Equals(DigRules.Normalize(candidate.Item.Id.ToString()), wantedId, StringComparison.Ordinal)
                    || !DigRules.HasEnoughDurability(candidate.Damage, candidate.MaxDamage, _settings.Durability_Limit))
                    continue;

                if (!await SwapIntoHandAsync(session, slot, handSlot).ConfigureAwait(false))
                    return false;

                _logger?.LogInformation(
                    "{Message}",
                    _strings?.Format("switched", candidate.Item.Id.ToString(), slot));

                if (_settings.Drop_Low_Durability_Tools && !held.IsEmpty && !heldHasDurability)
                {
                    await session.Actions.Inventory
                        .ClickAsync(new ClickAction.Throw(slot, WholeStack: true), session.Detached).ConfigureAwait(false);
                    _logger?.LogInformation(
                        "{Message}",
                        _strings?.Format("dropped_worn", held.Item.Id.ToString(), slot));
                }

                return true;
            }
        }

        // Nothing better available: dig anyway unless the held tool is below the durability limit.
        return held.IsEmpty || heldHasDurability;
    }

    private static async Task<bool> SwapIntoHandAsync(ISessionScope session, int sourceSlot, int handSlot)
    {
        try
        {
            // One hotbar-swap packet, so the exchange cannot desync half way through.
            await session.Actions.Inventory
                .ClickAsync(new ClickAction.Swap(sourceSlot, handSlot - 36), session.Detached).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private void Waiting(string? message)
    {
        lock (_gate)
        {
            if (_alreadyWaiting)
                return;

            _alreadyWaiting = true;
        }

        if (_settings.Log_Block_Dig)
            _logger?.LogInformation("{Message}", message);
    }
}

/// <summary>The <c>autodig</c> console command and its <c>digbot</c> alias.</summary>
internal sealed class AutoDigCommand : CommandBase
{
    private readonly AutoDigPlugin _plugin;
    private readonly IPluginLocalization _strings;

    internal AutoDigCommand(AutoDigPlugin plugin, IPluginLocalization strings)
    {
        _plugin = plugin;
        _strings = strings;
    }

    /// <inheritdoc/>
    public override string CmdName => "autodig";

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
    public override IReadOnlyList<string> Examples => ["autodig start", "autodig stop"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["dig", "blockinfo"];

    /// <inheritdoc/>
    public override string? ManTopic => "auto-dig";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .Executes(ctx => ShowUsage(ctx.Source))
            .ThenLiteral("start", a => a.Executes(ctx => Start(ctx.Source)))
            .ThenLiteral("stop", a => a.Executes(ctx => Stop(ctx.Source)))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));

        builder.Literal("digbot", l => l
            .Executes(ctx => ShowUsage(ctx.Source))
            .ThenLiteral("start", a => a.Executes(ctx => Start(ctx.Source)))
            .ThenLiteral("stop", a => a.Executes(ctx => Stop(ctx.Source))));
    }

    private int Start(CommandContext ctx)
    {
        _plugin.StartDigging();
        return ctx.Result.Ok(_strings.Get("started"));
    }

    private int Stop(CommandContext ctx)
    {
        _plugin.StopDigging();
        return ctx.Result.Ok(_strings.Get("stopped"));
    }
}
