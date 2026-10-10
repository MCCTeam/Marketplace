// AutoCraft v2 - a small factory controller.
// Chests are registered through the command by sign label or coordinates with an optional item filter, the crafting bench binds itself inside a configurable radius, crafting runs from chat commands (no order file), custom server recipes layer over the built-ins, and info/show print plain ASCII grids.

using System.Globalization;
using System.Text;
using DMCBK.Core.Commands;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Tomlet.Attributes;
using Umpk.Client.Actions;
using Umpk.Client.Events;
using Umpk.Client.Movement;
using Umpk.Commands;
using Umpk.Game.Blocks;
using Umpk.Game.Inventory;
using Umpk.Game.Items;
using Umpk.Geometry;
using Umpk.Nbt;
using Umpk.Pathfinding.Goals;
using Umpk.Text.Serialization;

namespace DMCBK.OfficialPlugins.AutoCraft;

/// <summary>Which crafting grid a configured recipe uses.</summary>
public enum CraftType
{
    /// <summary>The 2x2 grid in the player inventory.</summary>
    Player,

    /// <summary>The 3x3 grid of a crafting table.</summary>
    Table,
}

/// <summary>What to do when a material is missing.</summary>
public enum OnFailure
{
    /// <summary>Stop crafting and clear the grid.</summary>
    Abort,

    /// <summary>Park and resume when the material shows up.</summary>
    Wait,
}

/// <summary>Recipe shape rules and window identification.</summary>
public static class CraftRules
{
    /// <summary>The slot count of an open crafting-table window: 1 result + 9 grid + 27 main + 9 hotbar.</summary>
    public const int CraftingTableSlotCount = 46;

    /// <summary>Grid slot count for a craft type.</summary>
    public static int GridSize(CraftType type) => type == CraftType.Table ? 9 : 4;

    /// <summary>
    /// The lowest slot a source stack may be taken from, so the bot never consumes the result slot, the crafting grid, or (in the player window) the armor slots.
    /// </summary>
    public static int FirstSourceSlot(CraftType type) => type == CraftType.Table ? 10 : 9;

    /// <summary>The last grid slot for a craft type (the grid always starts at slot 1).</summary>
    public static int LastGridSlot(CraftType type) => type == CraftType.Table ? 9 : 4;

    /// <summary>True when an open window with this many slots is a crafting table.</summary>
    public static bool IsCraftingTableWindow(int slotCount) => slotCount == CraftingTableSlotCount;

    /// <summary>Strips a namespace, turns PascalCase into snake_case, and lowercases.</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        string value = name.Trim();
        int colon = value.IndexOf(':');
        if (colon >= 0)
            value = value[(colon + 1)..];

        var builder = new StringBuilder(value.Length + 4);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsUpper(c) && builder.Length > 0 && builder[^1] != '_')
                builder.Append('_');

            builder.Append(char.ToLowerInvariant(c));
        }

        string normalized = builder.ToString();

        // Null, Air and None all spell an empty grid cell.
        return normalized is "null" or "air" or "none" ? string.Empty : normalized;
    }

    /// <summary>Parses a craft type name: player or table.</summary>
    public static CraftType ParseCraftType(string? value)
        => string.Equals(value?.Trim(), "table", StringComparison.OrdinalIgnoreCase) ? CraftType.Table : CraftType.Player;

    /// <summary>Renders a craft type as the config spells it.</summary>
    public static string CraftTypeName(CraftType type) => type == CraftType.Table ? "table" : "player";

    /// <summary>Parses a failure policy name: abort or wait.</summary>
    public static OnFailure ParseFailure(string? value)
        => string.Equals(value?.Trim(), "wait", StringComparison.OrdinalIgnoreCase) ? OnFailure.Wait : OnFailure.Abort;

    /// <summary>Renders a failure policy as the config spells it.</summary>
    public static string FailureName(OnFailure failure) => failure == OnFailure.Wait ? "wait" : "abort";

    /// <summary>Pads or truncates a slot array to the grid size of the craft type. Missing cells are empty.</summary>
    public static string[] FixSlots(string[]? slots, CraftType type)
    {
        int size = GridSize(type);
        var fixedSlots = new string[size];
        for (int i = 0; i < size; i++)
            fixedSlots[i] = slots is not null && i < slots.Length ? slots[i] : string.Empty;

        return fixedSlots;
    }

    /// <summary>
    /// Builds the grid-slot to material map of a recipe: one-based grid positions (left to right, top to bottom) mapped to normalized item ids, skipping empty cells.
    /// </summary>
    public static Dictionary<int, string> BuildMaterials(string[] slots)
    {
        var materials = new Dictionary<int, string>();
        for (int i = 0; i < slots.Length; i++)
        {
            string material = Normalize(slots[i]);
            if (material.Length > 0)
                materials[i + 1] = material;
        }

        return materials;
    }

    /// <summary>
    /// Groups the recipe's grid cells by the material they want, preserving grid order.
    /// One source pickup then serves every cell of that material.
    /// </summary>
    public static List<KeyValuePair<string, List<int>>> GroupByMaterial(IReadOnlyDictionary<int, string> materials)
    {
        var order = new List<string>();
        var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        var cells = new List<int>(materials.Keys);
        cells.Sort();

        foreach (int cell in cells)
        {
            string material = materials[cell];
            if (!groups.TryGetValue(material, out List<int>? list))
            {
                list = [];
                groups[material] = list;
                order.Add(material);
            }

            list.Add(cell);
        }

        var result = new List<KeyValuePair<string, List<int>>>(order.Count);
        foreach (string material in order)
            result.Add(new KeyValuePair<string, List<int>>(material, groups[material]));

        return result;
    }

    /// <summary>Makes recipe names unique and non-blank.</summary>
    public static void EnsureUniqueNames(IReadOnlyList<RecipeConfig> recipes)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int fallback = 1;
        foreach (RecipeConfig recipe in recipes)
        {
            if (string.IsNullOrWhiteSpace(recipe.Name) || !seen.Add(recipe.Name))
            {
                string candidate;
                do
                    candidate = string.Format(CultureInfo.InvariantCulture, "Recipe-{0}", fallback++);
                while (!seen.Add(candidate));

                recipe.Name = candidate;
            }
        }
    }
}

/// <summary>The crafting table position.</summary>
public sealed class TableLocation
{
    /// <summary>The X coordinate.</summary>
    public double X { get; set; }

    /// <summary>The Y coordinate.</summary>
    public double Y { get; set; }

    /// <summary>The Z coordinate.</summary>
    public double Z { get; set; }
}

/// <summary>One configured recipe entry.</summary>
public sealed class RecipeConfig
{
    /// <summary>The recipe name used by the craft command.</summary>
    public string Name { get; set; } = "Recipe-Name-1";

    /// <summary>player (2x2), table (3x3), or any (fits 2x2, crafted in pockets).</summary>
    public string Type { get; set; } = "player";

    /// <summary>The expected result item.</summary>
    public string Result { get; set; } = "StoneBricks";

    /// <summary>How many items one craft produces.</summary>
    public int Count { get; set; } = 1;

    /// <summary>Alternate names that resolve to this recipe.</summary>
    public string[] Aliases { get; set; } = [];

    /// <summary>The grid cells, left to right and top to bottom. Empty or Null means an empty cell.</summary>
    public string[] Slots { get; set; } = ["Stone", "Stone", "Stone", "Stone"];
}

/// <summary>The persisted settings for AutoCraft.</summary>
public sealed class AutoCraftSettings : IValidatablePluginSettings
{
    /// <summary>Master switch.</summary>
    [TomlInlineComment("$settings.enabled$")]
    public bool Enabled { get; set; }

    /// <summary>How far around the bot to look for a crafting table. Empty Bench means automatic.</summary>
    [TomlInlineComment("$settings.bench_radius$")]
    public int BenchRadius { get; set; } = 8;

    /// <summary>Pinned bench as "x y z". Empty means the bot finds one itself.</summary>
    [TomlInlineComment("$settings.bench$")]
    public string Bench { get; set; } = string.Empty;

    /// <summary>abort or wait.</summary>
    [TomlInlineComment("$settings.on_failure$")]
    public string OnFailure { get; set; } = "abort";

    /// <summary>Chests registered through the command. Never hand-edited mid-game.</summary>
    [TomlPrecedingComment("$settings.chests$")]
    public ChestConfig[] Chests { get; set; } = [];

    /// <summary>Custom server recipes, layered over the built-ins.</summary>
    [TomlPrecedingComment("$settings.custom_recipes$")]
    public RecipeConfig[] CustomRecipes { get; set; } = [];

    /// <summary>Fixed table position. Honored only when Bench is empty.</summary>
    [TomlPrecedingComment("$settings.crafting_table$")]
    public TableLocation CraftingTable { get; set; } = new() { X = 123, Y = 65, Z = 456 };

    /// <summary>Configured recipes. Still craftable; prefer built-ins and custom recipes.</summary>
    [TomlPrecedingComment("$settings.recipes$")]
    public RecipeConfig[] Recipes { get; set; } =
    [
        new RecipeConfig
        {
            Name = "Recipe-Name-1",
            Type = "player",
            Result = "StoneBricks",
            Slots = ["Stone", "Stone", "Stone", "Stone"],
        },
    ];

    /// <summary>Normalizes everything on load or reload.</summary>
    public void Validate()
    {
        BenchRadius = Math.Clamp(BenchRadius, 2, 32);
        Bench = NormalizeBench(Bench);
        OnFailure = CraftRules.FailureName(CraftRules.ParseFailure(OnFailure));

        Chests ??= [];
        var kept = new List<ChestConfig>(Chests.Length);
        foreach (ChestConfig chest in Chests)
        {
            chest.Direction = ChestRules.DirectionName(ChestRules.ParseDirection(chest.Direction));
            chest.Kind = ChestRules.KindName(ChestRules.ParseKind(chest.Kind));
            chest.Label = (chest.Label ?? string.Empty).Trim();
            chest.Position = Coords.TryParse(chest.Position, out BlockPos position)
                ? Coords.Format(position)
                : string.Empty;
            chest.Item = CraftRules.Normalize(chest.Item);
            if (chest.Label.Length == 0 && chest.Position.Length == 0)
                continue;

            if (chest.Label.Length > 0 && chest.Position.Length > 0)
                chest.Label = string.Empty;

            kept.Add(chest);
        }

        Chests = kept.ToArray();

        CustomRecipes ??= [];
        CraftRules.EnsureUniqueNames(CustomRecipes);
        foreach (RecipeConfig recipe in CustomRecipes)
            NormalizeRecipe(recipe);

        Recipes ??= [];
        CraftRules.EnsureUniqueNames(Recipes);
        foreach (RecipeConfig recipe in Recipes)
            NormalizeRecipe(recipe);

        CraftingTable ??= new TableLocation();
    }

    /// <summary>The pinned bench, or null for automatic discovery.</summary>
    public BlockPos? PinnedBench()
    {
        if (Coords.TryParse(Bench, out BlockPos pinned))
            return pinned;

        // A non-default table value from an older config becomes the pin so old setups keep working.
        if (Math.Abs(CraftingTable.X - 123) > 0.001
            || Math.Abs(CraftingTable.Y - 65) > 0.001
            || Math.Abs(CraftingTable.Z - 456) > 0.001)
            return new BlockPos((int)Math.Floor(CraftingTable.X), (int)Math.Floor(CraftingTable.Y), (int)Math.Floor(CraftingTable.Z));

        return null;
    }

    /// <summary>Built-ins overridden by configured recipes overridden by custom recipes.</summary>
    public IReadOnlyList<CraftRecipe> EffectiveRecipes()
    {
        var customs = new List<CraftRecipe>(CustomRecipes.Length + Recipes.Length);
        foreach (RecipeConfig recipe in Recipes)
            customs.Add(ToLibraryRecipe(recipe));

        foreach (RecipeConfig recipe in CustomRecipes)
            customs.Add(ToLibraryRecipe(recipe));

        return CraftLibrary.EffectiveRecipes(customs);
    }

    private static void NormalizeRecipe(RecipeConfig recipe)
    {
        CraftStation station = CraftLibrary.ParseStation(recipe.Type);
        if (string.IsNullOrWhiteSpace(recipe.Type))
            station = recipe.Slots?.Length > 4 ? CraftStation.Table : CraftStation.Player;

        CraftType legacy = station == CraftStation.Table ? CraftType.Table : CraftType.Player;
        recipe.Type = CraftLibrary.StationName(station);
        recipe.Slots = CraftRules.FixSlots(recipe.Slots, legacy);
        recipe.Result = CraftRules.Normalize(recipe.Result);
        if (recipe.Count < 1)
            recipe.Count = 1;

        recipe.Aliases ??= [];
    }

    private static CraftRecipe ToLibraryRecipe(RecipeConfig recipe) => new()
    {
        Name = recipe.Name,
        Aliases = recipe.Aliases ?? [],
        Station = CraftLibrary.ParseStation(recipe.Type),
        Result = CraftRules.Normalize(recipe.Result),
        Count = Math.Max(1, recipe.Count),
        Slots = recipe.Slots ?? [],
    };

    private static string NormalizeBench(string? value)
        => Coords.TryParse(value, out BlockPos position) ? Coords.Format(position) : string.Empty;
}

/// <summary>What the bot is doing right now, for the status command.</summary>
public enum CraftPhase
{
    /// <summary>Nothing queued.</summary>
    Idle,

    /// <summary>Crafting the current job.</summary>
    Crafting,

    /// <summary>Scanning for a bench.</summary>
    FindingBench,

    /// <summary>Parked: no bench in range.</summary>
    MissingBench,

    /// <summary>Parked: an ingredient ran out.</summary>
    WaitingMaterials,

    /// <summary>Parked: the output chests took nothing.</summary>
    OutputFull,

    /// <summary>Parked: a keep job is at its stock target.</summary>
    StockFull,
}

/// <summary>One queued job: a recipe plus how many items are wanted.</summary>
public sealed class CraftJob
{
    /// <summary>The resolved recipe snapshot.</summary>
    public CraftRecipe Recipe { get; set; } = new();

    /// <summary>How many items are wanted in total.</summary>
    public int Target { get; set; }

    /// <summary>How many items have been crafted.</summary>
    public int Made { get; set; }

    /// <summary>True for keep (maintain stock) rather than craft once.</summary>
    public bool Keep { get; set; }
}

/// <summary>The AutoCraft plugin entry point.</summary>
public sealed class AutoCraftPlugin : IPlugin
{
    // Act 0.2 s after the last inventory update of a burst, not on each one.
    private const int DebounceTicks = 4;

    // Recheck an empty shelf every 30 s; rescan for a new bench every 5 s.
    private const int ShelfRetryTicks = 600;
    private const int BenchRetryTicks = 100;

    // A bench that fails to open stays ignored for a minute.
    private const int BenchBlacklistTicks = 1200;

    // Withdraw for up to this many crafts per cycle so pockets do the batching, not feet.
    private const int WithdrawBatchCrafts = 8;

    private const double BenchReach = 4.5;

    private readonly object _gate = new();
    private AutoCraftSettings _settings = new();
    private PluginSettings? _settingsStore;
    private ILogger? _logger;
    private IPluginLocalization? _strings;
    private ISessionScope? _session;

    private readonly List<CraftJob> _queue = [];
    private OnFailure _onFailure = OnFailure.Abort;

    private bool _running;
    private bool _busy;
    private bool _parked;
    private bool _waitingForUpdate;
    private bool _waitingForMaterials;
    private int _debounce;
    private int _retryTicks;
    private CraftPhase _phase = CraftPhase.Idle;
    private string _phaseSubject = string.Empty;
    private string _phaseWhere = string.Empty;
    private int _phaseNeed;
    private BlockPos? _benchPos;
    private int _mismatches;
    private readonly Dictionary<BlockPos, int> _benchBlacklist = [];
    private readonly Dictionary<BlockPos, int> _badChests = [];
    private int _tick;

    /// <inheritdoc/>
    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "auto-craft";
        descriptor.Version = "2.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
        descriptor.WithSettings<AutoCraftSettings>();
    }

    /// <inheritdoc/>
    public Task ActivateAsync(PluginContext context)
    {
        _settings = context.Settings.Load<AutoCraftSettings>();
        _settingsStore = context.Settings;
        _logger = context.Logger;
        _strings = context.Strings;
        _onFailure = CraftRules.ParseFailure(_settings.OnFailure);

        context.Commands.Register(new AutoCraftCommand(this, context.Strings));

        context.SessionStarted += (_, e) => OnSessionStarted(e.Session);
        context.SessionEnded += (_, _) =>
        {
            ResetState();
            lock (_gate)
                _session = null;
        };

        return Task.CompletedTask;
    }

    private void Debug(string message) => _logger?.LogDebug("{Message}", message);

    #region Commands surface

    /// <summary>All recipes the craft command accepts right now.</summary>
    internal IReadOnlyList<CraftRecipe> Recipes => _settings.EffectiveRecipes();

    /// <summary>Whether any job is queued or running.</summary>
    internal bool IsRunning => _running;

    /// <summary>Enqueues a craft order like "64 torches". Null on success, else a user message.</summary>
    internal string? Craft(string order, IPluginLocalization strings)
    {
        if (!CraftLibrary.TryParseOrder(order, out int count, out string name) || name.Length == 0)
            return strings.Get("bad_order");

        if (count <= 0)
            count = CraftLibrary.DefaultOrderCount;

        count = Math.Clamp(count, 1, 100000);
        return Enqueue(name, count, keep: false, strings);
    }

    /// <summary>Enqueues a keep order (maintain stock). Null on success, else a user message.</summary>
    internal string? Keep(string order, IPluginLocalization strings)
    {
        if (!CraftLibrary.TryParseOrder(order, out int count, out string name) || name.Length == 0)
            return strings.Get("bad_order");

        if (count <= 0)
            count = CraftLibrary.DefaultOrderCount;

        count = Math.Clamp(count, 1, 100000);
        return Enqueue(name, count, keep: true, strings);
    }

    private string? Enqueue(string name, int count, bool keep, IPluginLocalization strings)
    {
        ISessionScope? session;
        lock (_gate)
            session = _session;

        if (session is null)
            return strings.Get("not_connected");

        if (!session.State.Features.Inventory)
            return strings.Get("inventory_required");

        if (!_settings.Enabled)
            return strings.Get("settings_disabled");

        (CraftRecipe? recipe, string[] suggestions) = CraftLibrary.Resolve(Recipes, name);
        if (recipe is null)
        {
            string message = strings.Format("recipe_not_found", name);
            if (suggestions.Length > 0)
                message += strings.Format("recipe_suggest", string.Join(", ", suggestions));

            return message;
        }

        if (CraftRules.BuildMaterials(recipe.Slots).Count == 0)
            return strings.Format("start_fail", recipe.Name);

        lock (_gate)
        {
            _queue.Add(new CraftJob { Recipe = recipe, Target = count, Keep = keep });
            _running = true;
            _parked = false;
            _badChests.Clear();
        }

        _logger?.LogInformation("{Message}", keep
            ? strings.Format("keeping", recipe.Name, count)
            : strings.Format("crafting", recipe.Name));
        session.Scheduler.Post(() => _ = StepAsync(session));
        return null;
    }

    /// <summary>Cancels one queued job by its 1-based number. Null on success.</summary>
    internal string? Cancel(int number, IPluginLocalization strings)
    {
        lock (_gate)
        {
            if (number < 1 || number > _queue.Count)
                return strings.Format("queue_cancel_bad", number, _queue.Count);

            CraftJob removed = _queue[number - 1];
            _queue.RemoveAt(number - 1);
            if (_queue.Count == 0)
            {
                _running = false;
                _phase = CraftPhase.Idle;
            }

            return null;
        }
    }

    /// <summary>The removed job's display name for the confirmation line.</summary>
    internal string CancelledName(int number)
    {
        lock (_gate)
            return number >= 1 && number <= _queue.Count + 1 ? string.Empty : string.Empty;
    }

    /// <summary>Stops everything and clears the queue.</summary>
    internal void Stop()
    {
        ISessionScope? session;
        lock (_gate)
        {
            session = _session;
            _running = false;
            _queue.Clear();
        }

        if (session is not null)
            session.Scheduler.Post(() => _ = StopAsync(session));
        else
            ResetState();
    }

    /// <summary>Registers a chest. Returns null on success or an error code.</summary>
    internal string? AddChest(
        ChestDirection direction, ChestKind kind, string? label, BlockPos? position, string? item)
    {
        string cleanLabel = (label ?? string.Empty).Trim();
        string cleanPosition = position is { } pos ? Coords.Format(pos) : string.Empty;
        string? error = ChestRules.ValidateEntry(cleanLabel, cleanPosition);
        if (error is not null)
            return error;

        if (cleanLabel.Length > 0 && cleanPosition.Length > 0)
            cleanLabel = string.Empty;

        string cleanItem = CraftRules.Normalize(item);
        lock (_gate)
        {
            foreach (ChestConfig existing in _settings.Chests)
            {
                if (ChestRules.ParseDirection(existing.Direction) == direction
                    && ChestRules.ParseKind(existing.Kind) == kind
                    && string.Equals(existing.Label, cleanLabel, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.Position, cleanPosition, StringComparison.Ordinal)
                    && string.Equals(CraftRules.Normalize(existing.Item), cleanItem, StringComparison.Ordinal))
                    return "duplicate";
            }

            var entries = new List<ChestConfig>(_settings.Chests)
            {
                new ChestConfig
                {
                    Direction = ChestRules.DirectionName(direction),
                    Kind = ChestRules.KindName(kind),
                    Label = cleanLabel,
                    Position = cleanPosition,
                    Item = cleanItem,
                },
            };
            _settings.Chests = entries.ToArray();
            SaveSettings();
        }

        return null;
    }

    /// <summary>Removes a chest entry. True when something was removed.</summary>
    internal bool RemoveChest(ChestDirection direction, ChestKind kind, string? label, BlockPos? position)
    {
        string cleanLabel = (label ?? string.Empty).Trim();
        string cleanPosition = position is { } pos ? Coords.Format(pos) : string.Empty;
        lock (_gate)
        {
            var entries = new List<ChestConfig>(_settings.Chests);
            int removed = entries.RemoveAll(existing =>
                ChestRules.ParseDirection(existing.Direction) == direction
                && ChestRules.ParseKind(existing.Kind) == kind
                && (cleanLabel.Length > 0
                    ? string.Equals(existing.Label, cleanLabel, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(existing.Position, cleanPosition, StringComparison.Ordinal)));
            if (removed == 0)
                return false;

            _settings.Chests = entries.ToArray();
            SaveSettings();
            return true;
        }
    }

    /// <summary>Sets the bench search radius. Always succeeds; the value is clamped.</summary>
    internal int SetBenchRadius(int radius)
    {
        lock (_gate)
        {
            _settings.BenchRadius = Math.Clamp(radius, 2, 32);
            SaveSettings();
            return _settings.BenchRadius;
        }
    }

    /// <summary>Pins the bench to coordinates.</summary>
    internal void PinBench(BlockPos position)
    {
        lock (_gate)
        {
            _settings.Bench = Coords.Format(position);
            _benchPos = null;
            _benchBlacklist.Clear();
            SaveSettings();
        }
    }

    /// <summary>Returns to automatic bench discovery.</summary>
    internal void UnpinBench()
    {
        lock (_gate)
        {
            _settings.Bench = string.Empty;
            _benchPos = null;
            _benchBlacklist.Clear();
            SaveSettings();
        }
    }

    /// <summary>Forgets the bound bench and its blacklist so the next cycle rescans.</summary>
    internal void RescanBench()
    {
        lock (_gate)
        {
            _benchPos = null;
            _benchBlacklist.Clear();
            if (_phase == CraftPhase.MissingBench)
                _parked = false;
        }

        ISessionScope? session;
        lock (_gate)
            session = _session;

        session?.Scheduler.Post(() => _ = StepAsync(session));
    }

    private void SaveSettings()
    {
        _settings.Validate();
        _onFailure = CraftRules.ParseFailure(_settings.OnFailure);
        _settingsStore?.Save(_settings);
    }

    /// <summary>One line per queued job, or the empty-queue message.</summary>
    internal string QueueSummary(IPluginLocalization strings)
    {
        lock (_gate)
        {
            if (_queue.Count == 0)
                return strings.Get("queue_empty");

            var lines = new List<string>(_queue.Count);
            for (int i = 0; i < _queue.Count; i++)
            {
                CraftJob job = _queue[i];
                lines.Add(job.Keep
                    ? strings.Format("queue_line_keep", i + 1, job.Recipe.Name, job.Target, job.Made)
                    : strings.Format("queue_line", i + 1, job.Recipe.Name, job.Target, job.Made));
            }

            return string.Join("\n", lines);
        }
    }

    /// <summary>One line per registered chest, or the empty-storage message.</summary>
    internal string ChestSummary(IPluginLocalization strings)
    {
        lock (_gate)
        {
            if (_settings.Chests.Length == 0)
                return strings.Get("chests_empty");

            var lines = new List<string>(_settings.Chests.Length);
            foreach (ChestConfig chest in _settings.Chests)
            {
                string direction = ChestRules.DirectionName(ChestRules.ParseDirection(chest.Direction));
                string kind = ChestRules.KindName(ChestRules.ParseKind(chest.Kind));
                string where = chest.Label.Length > 0
                    ? strings.Format("chest_where_label", chest.Label)
                    : chest.Position;
                string item = CraftRules.Normalize(chest.Item);
                lines.Add(item.Length == 0
                    ? strings.Format("chest_line_shared", direction, kind, where)
                    : strings.Format("chest_line_filtered", direction, kind, where, item));
            }

            return string.Join("\n", lines);
        }
    }

    /// <summary>Bench mode, radius, and bound position for the bench command.</summary>
    internal string BenchSummary(IPluginLocalization strings)
    {
        lock (_gate)
        {
            string bound = _benchPos is { } pos
                ? strings.Format("bench_bound", Coords.Format(pos))
                : strings.Get("bench_unbound");
            BlockPos? pinned = _settings.PinnedBench();
            if (pinned is { } pin)
                return strings.Format("bench_status_pin", Coords.Format(pin), bound);

            return strings.Format("bench_status_auto", _settings.BenchRadius, bound);
        }
    }

    /// <summary>What the bot is doing right now, in one human line.</summary>
    internal string StatusSummary(IPluginLocalization strings)
    {
        CraftJob? job;
        CraftPhase phase;
        string subject;
        string where;
        int need;
        lock (_gate)
        {
            job = _queue.Count > 0 ? _queue[0] : null;
            phase = _phase;
            subject = _phaseSubject;
            where = _phaseWhere;
            need = _phaseNeed;
        }

        if (job is null)
            return strings.Get("status_idle");

        if (phase == CraftPhase.Idle)
        {
            // Queued but the engine has not picked it up yet: report it as starting.
            return job.Keep
                ? strings.Format("status_keeping", job.Recipe.Name, job.Made, job.Target)
                : strings.Format("status_crafting", job.Recipe.Name, Math.Min(job.Made, job.Target), job.Target);
        }

        return phase switch
        {
            CraftPhase.FindingBench => strings.Format("status_finding_bench", _settings.BenchRadius),
            CraftPhase.MissingBench => strings.Format("status_missing_bench", subject, where),
            CraftPhase.WaitingMaterials => strings.Format("status_waiting", subject, where, need),
            CraftPhase.OutputFull => strings.Format("status_output_full", subject, where),
            CraftPhase.StockFull => strings.Format("status_stock_full", subject, where),
            _ => job.Keep
                ? strings.Format("status_keeping", job.Recipe.Name, job.Made, job.Target)
                : strings.Format("status_crafting", job.Recipe.Name, Math.Min(job.Made, job.Target), job.Target),
        };
    }

    #endregion

    #region Engine

    private void ResetState()
    {
        lock (_gate)
        {
            _running = false;
            _busy = false;
            _parked = false;
            _waitingForUpdate = false;
            _waitingForMaterials = false;
            _debounce = 0;
            _retryTicks = 0;
            _phase = CraftPhase.Idle;
            _phaseSubject = string.Empty;
            _phaseWhere = string.Empty;
            _phaseNeed = 0;
            _benchPos = null;
            _mismatches = 0;
            _benchBlacklist.Clear();
            _badChests.Clear();
            _queue.Clear();
        }
    }

    private void OnSessionStarted(ISessionScope session)
    {
        ResetState();
        lock (_gate)
            _session = session;

        if (!session.State.Features.Inventory)
        {
            _logger?.LogWarning("{Message}", _strings?.Get("inventory_required"));
            return;
        }

        session.Events.Subscribe<ContainerContentChanged>(_ => OnInventoryUpdate());
        session.Events.Subscribe<ContainerOpened>(_ => OnInventoryUpdate());
        session.Scheduler.OnTick(() => OnTick(session));
    }

    private void OnInventoryUpdate()
    {
        lock (_gate)
        {
            if (_waitingForUpdate || _waitingForMaterials)
                _debounce = DebounceTicks;
        }
    }

    private void OnTick(ISessionScope session)
    {
        bool flush = false;
        bool retry = false;
        lock (_gate)
        {
            _tick++;
            foreach (BlockPos pos in new List<BlockPos>(_benchBlacklist.Keys))
            {
                if (_tick >= _benchBlacklist[pos])
                    _benchBlacklist.Remove(pos);
            }

            foreach (BlockPos pos in new List<BlockPos>(_badChests.Keys))
            {
                if (_tick >= _badChests[pos])
                    _badChests.Remove(pos);
            }

            if (_debounce > 0 && --_debounce == 0)
            {
                if (_waitingForUpdate || _waitingForMaterials)
                {
                    _waitingForUpdate = false;
                    _waitingForMaterials = false;
                    flush = true;
                }
            }

            if (_retryTicks > 0 && --_retryTicks == 0 && _parked)
            {
                // A timed retry is a fresh attempt, not a continuation: drop the update latch too, or the gate in StepAsync refuses the very cycle this retry exists to start.
                _parked = false;
                _waitingForMaterials = false;
                _waitingForUpdate = false;
                retry = true;
            }
        }

        if ((flush || retry) && _running)
            _ = StepAsync(session);
    }

    private void Park(CraftPhase phase, int retryTicks, string subject = "", string where = "", int need = 0)
    {
        lock (_gate)
        {
            _phase = phase;
            _phaseSubject = subject;
            _phaseWhere = where;
            _phaseNeed = need;
            _parked = true;
            _retryTicks = retryTicks;
        }
    }

    /// <summary>
    /// Parks and logs the message only when this is a new situation.
    /// Timed retries re-park every 30 seconds, and nobody wants the same warning once per retry until they fix the shelf.
    /// </summary>
    private void ParkOnce(CraftPhase phase, int retryTicks, string? message, string subject = "", string where = "", int need = 0, bool warn = true)
    {
        bool fresh;
        lock (_gate)
            fresh = _phase != phase || _phaseSubject != subject || _phaseWhere != where;

        Park(phase, retryTicks, subject, where, need);
        if (fresh && message is not null)
        {
            if (warn)
                _logger?.LogWarning("{Message}", message);
            else
                _logger?.LogInformation("{Message}", message);
        }
    }

    private async Task StepAsync(ISessionScope session)
    {
        lock (_gate)
        {
            if (!_running || _busy || _waitingForUpdate || _waitingForMaterials || _parked)
                return;

            _busy = true;
        }

        try
        {
            await RunCycleAsync(session).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogWarning(ex, "{Message}", _strings?.Get("step_failed"));
            lock (_gate)
            {
                _running = false;
                _phase = CraftPhase.Idle;
            }
        }
        finally
        {
            lock (_gate)
                _busy = false;
        }
    }

    private async Task RunCycleAsync(ISessionScope session)
    {
        CraftJob? job;
        while (true)
        {
            CraftJob? front;
            lock (_gate)
                front = _queue.Count > 0 ? _queue[0] : null;

            if (front is null)
            {
                lock (_gate)
                {
                    _running = false;
                    _phase = CraftPhase.Idle;
                }

                return;
            }

            if (front.Keep || front.Made < front.Target)
            {
                job = front;
                break;
            }

            // Complete means crafted AND delivered: results stuck in pockets with registered outputs keep the job open instead of reporting success with goods undelivered.
            int left = await CountCarriedAsync(session, stack =>
                string.Equals(CraftRules.Normalize(stack.Item.Id.ToString()), front.Recipe.Result, StringComparison.Ordinal))
                .ConfigureAwait(false);
            if (left > 0 && HasOutputs(front.Recipe.Result))
            {
                await DepositAsync(session, front.Recipe.Result).ConfigureAwait(false);
                left = await CountCarriedAsync(session, stack =>
                    string.Equals(CraftRules.Normalize(stack.Item.Id.ToString()), front.Recipe.Result, StringComparison.Ordinal))
                    .ConfigureAwait(false);
                if (left > 0)
                {
                    ParkOnce(CraftPhase.OutputFull, ShelfRetryTicks,
                        _strings?.Format("output_full", front.Recipe.Name, left),
                        front.Recipe.Name, left.ToString(CultureInfo.InvariantCulture));
                    return;
                }
            }

            lock (_gate)
            {
                if (_queue.Count > 0 && ReferenceEquals(_queue[0], front))
                    _queue.RemoveAt(0);
            }

            _logger?.LogInformation("{Message}", _strings?.Format("job_done", front.Recipe.Name, front.Made));
        }

        var inventory = session.State.Inventory;
        bool needsTable = job.Recipe.Station == CraftStation.Table;
        bool tableOpen = inventory.ContainerSlots is { } open && CraftRules.IsCraftingTableWindow(open.Count);

        if (needsTable && !tableOpen)
        {
            lock (_gate)
                _phase = CraftPhase.FindingBench;

            if (!await EnsureBenchAsync(session).ConfigureAwait(false))
                return;

            tableOpen = session.State.Inventory.ContainerSlots is { } reopened
                && CraftRules.IsCraftingTableWindow(reopened.Count);
            if (!tableOpen)
                return;
        }

        if (!needsTable && inventory.HasOpenContainer)
            await session.Actions.Inventory.CloseAsync(session.Detached).ConfigureAwait(false);

        if (job.Keep)
        {
            int stock = await CountCarriedAsync(session, stack =>
                string.Equals(CraftRules.Normalize(stack.Item.Id.ToString()), job.Recipe.Result, StringComparison.Ordinal))
                .ConfigureAwait(false);
            if (stock >= job.Target)
            {
                ParkOnce(CraftPhase.StockFull, ShelfRetryTicks,
                    _strings?.Format("stock_full", job.Recipe.Name, stock),
                    job.Recipe.Name, stock.ToString(CultureInfo.InvariantCulture), warn: false);
                return;
            }
        }

        Dictionary<string, int> perCraft = PerCraftCounts(job.Recipe);
        int crafts = job.Keep ? WithdrawBatchCrafts : Math.Min(WithdrawBatchCrafts, (job.Target - job.Made + job.Recipe.Count - 1) / job.Recipe.Count);
        crafts = Math.Max(1, crafts);

        var needs = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach ((string material, int one) in perCraft)
        {
            int carried = await CountCarriedAsync(session, stack =>
                string.Equals(CraftRules.Normalize(stack.Item.Id.ToString()), material, StringComparison.Ordinal))
                .ConfigureAwait(false);
            int want = (one * crafts) - carried;
            if (want > 0)
                needs[material] = want;
        }

        if (needs.Count > 0)
        {
            (bool ok, string shortMaterial, int shortBy) = await WithdrawAsync(session, needs).ConfigureAwait(false);
            if (!ok)
            {
                await HandleMissingMaterialAsync(session, job, shortMaterial, shortBy).ConfigureAwait(false);
                return;
            }
        }

        // Chest trips close containers, so a table recipe must rebind here: the fill below writes grid cells that only exist while the 46-slot window is open.
        if (needsTable)
        {
            bool benchOpen = session.State.Inventory.ContainerSlots is { } check
                && CraftRules.IsCraftingTableWindow(check.Count);
            if (!benchOpen && !await EnsureBenchAsync(session).ConfigureAwait(false))
                return;
        }

        // Recheck after the withdraw run: pockets must hold one full grid.
        foreach ((string material, int one) in perCraft)
        {
            int carried = await CountCarriedAsync(session, stack =>
                string.Equals(CraftRules.Normalize(stack.Item.Id.ToString()), material, StringComparison.Ordinal))
                .ConfigureAwait(false);
            if (carried < one)
            {
                await HandleMissingMaterialAsync(session, job, material, one - carried).ConfigureAwait(false);
                return;
            }
        }

        await CraftOnceAsync(session, job).ConfigureAwait(false);

        int free = await FreePocketSlotsAsync(session).ConfigureAwait(false);
        bool jobComplete = !job.Keep && job.Made >= job.Target;
        if (jobComplete || free < 8)
        {
            int deposited = await DepositAsync(session, job.Recipe.Result).ConfigureAwait(false);
            if (deposited == 0)
            {
                int left = await CountCarriedAsync(session, stack =>
                    string.Equals(CraftRules.Normalize(stack.Item.Id.ToString()), job.Recipe.Result, StringComparison.Ordinal))
                    .ConfigureAwait(false);
                if (left > 0 && HasOutputs(job.Recipe.Result))
                {
                    ParkOnce(CraftPhase.OutputFull, ShelfRetryTicks,
                        _strings?.Format("output_full", job.Recipe.Name, left),
                        job.Recipe.Name, left.ToString(CultureInfo.InvariantCulture));
                    return;
                }
            }
        }

        lock (_gate)
        {
            _phase = CraftPhase.Crafting;
            _phaseSubject = job.Recipe.Name;
        }
    }

    private static Dictionary<string, int> PerCraftCounts(CraftRecipe recipe)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string cell in CraftRules.BuildMaterials(recipe.Slots).Values)
        {
            counts.TryGetValue(cell, out int n);
            counts[cell] = n + 1;
        }

        return counts;
    }

    private async Task CraftOnceAsync(ISessionScope session, CraftJob job)
    {
        CraftType grid = job.Recipe.Station == CraftStation.Table ? CraftType.Table : CraftType.Player;
        Dictionary<int, string> materials = CraftRules.BuildMaterials(job.Recipe.Slots);
        IReadOnlyList<ItemStack> slots = ActiveSlots(session);
        int firstSource = CraftRules.FirstSourceSlot(grid);

        // Fill the grid material by material: pick the source stack up once, RIGHT click one item into each cell that wants it, then put the remainder back.
        // Left clicking the stack into a cell would dump the whole stack there and starve every later cell of the same material.
        foreach (KeyValuePair<string, List<int>> group in CraftRules.GroupByMaterial(materials))
        {
            int source = FindSource(slots, group.Key, firstSource);
            if (source < 0)
            {
                await HandleMissingMaterialAsync(session, job, group.Key, 1).ConfigureAwait(false);
                return;
            }

            await session.Actions.Inventory
                .ClickAsync(new ClickAction.Pickup(source, MouseButton.Left), session.Detached).ConfigureAwait(false);

            foreach (int cell in group.Value)
            {
                await session.Actions.Inventory
                    .ClickAsync(new ClickAction.Pickup(cell, MouseButton.Right), session.Detached).ConfigureAwait(false);
            }

            if (!session.State.Inventory.Cursor.IsEmpty)
            {
                await session.Actions.Inventory
                    .ClickAsync(new ClickAction.Pickup(source, MouseButton.Left), session.Detached).ConfigureAwait(false);
            }

            slots = ActiveSlots(session);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(250), session.Detached).ConfigureAwait(false);

        slots = ActiveSlots(session);
        ItemStack result = slots.Count > 0 ? slots[0] : ItemStack.Empty;
        if (result.IsEmpty
            || !string.Equals(CraftRules.Normalize(result.Item.Id.ToString()), job.Recipe.Result, StringComparison.Ordinal))
        {
            string got = result.IsEmpty ? "nothing" : CraftRules.Normalize(result.Item.Id.ToString());
            Debug($"AutoCraft: bench gave {got} instead of {job.Recipe.Result}; grid cleared.");
            await ClearCraftingAreaAsync(session, grid).ConfigureAwait(false);
            lock (_gate)
                _mismatches++;

            if (_mismatches >= 5)
            {
                lock (_gate)
                {
                    _mismatches = 0;
                    _queue.Remove(job);
                    if (_queue.Count == 0)
                    {
                        _running = false;
                        _phase = CraftPhase.Idle;
                    }
                }

                _logger?.LogWarning("{Message}", _strings?.Format("bad_result", job.Recipe.Name, got));
            }

            return;
        }

        await session.Actions.Inventory.QuickMoveAsync(0, session.Detached).ConfigureAwait(false);
        lock (_gate)
        {
            job.Made += job.Recipe.Count;
            _mismatches = 0;
            _waitingForUpdate = true;
            _debounce = DebounceTicks;
        }

        _logger?.LogDebug("{Message}", _strings?.Format("crafted", result.Item.Id.ToString()));
    }

    private IReadOnlyList<ItemStack> ActiveSlots(ISessionScope session)
    {
        var inventory = session.State.Inventory;
        return inventory.HasOpenContainer && inventory.ContainerSlots is { } container
            ? container
            : inventory.PlayerSlots;
    }

    private static int FindSource(IReadOnlyList<ItemStack> slots, string material, int firstSource)
    {
        for (int slot = firstSource; slot < slots.Count; slot++)
        {
            ItemStack stack = slots[slot];
            if (!stack.IsEmpty
                && string.Equals(CraftRules.Normalize(stack.Item.Id.ToString()), material, StringComparison.Ordinal))
                return slot;
        }

        return -1;
    }

    private async Task HandleMissingMaterialAsync(ISessionScope session, CraftJob job, string material, int shortBy)
    {
        string where = DescribeInputs(material);
        if (_onFailure == OnFailure.Abort)
        {
            _logger?.LogInformation("{Message}", _strings?.Format("waiting_for", material, where, shortBy));
            await StopAsync(session).ConfigureAwait(false);
            _logger?.LogInformation("{Message}", _strings?.Get("aborted"));
            return;
        }

        ParkOnce(CraftPhase.WaitingMaterials, ShelfRetryTicks,
            _strings?.Format("waiting_for", material, where, shortBy),
            material, where, shortBy, warn: false);
        lock (_gate)
            _waitingForMaterials = true;
    }

    private string DescribeInputs(string material)
    {
        IReadOnlyList<ChestConfig> entries;
        lock (_gate)
        {
            entries = ChestRules.OrderForJob(
                _settings.Chests, ChestDirection.Input, new HashSet<string>(StringComparer.Ordinal) { material });
        }

        if (entries.Count == 0)
            return _strings?.Get("no_input_chests") ?? "pockets";

        var parts = new List<string>(entries.Count);
        foreach (ChestConfig entry in entries)
            parts.Add(DescribeWhere(entry));

        return string.Join(", ", parts);
    }

    private string DescribeWhere(ChestConfig entry)
    {
        if (_strings is null)
            return entry.Label.Length > 0 ? entry.Label : entry.Position;

        return entry.Label.Length > 0
            ? _strings.Format("chest_where_label", entry.Label)
            : entry.Position;
    }

    private bool HasOutputs(string result)
    {
        lock (_gate)
        {
            return ChestRules.OrderForJob(
                _settings.Chests, ChestDirection.Output, new HashSet<string>(StringComparer.Ordinal) { result }).Count > 0;
        }
    }

    #endregion

    #region Chests

    private async Task<int> CountCarriedAsync(ISessionScope session, Func<ItemStack, bool> matches) =>
        await session.Client.InvokeAsync(
            c => c.State.Inventory.PlayerSlots.Where(matches).Sum(stack => stack.Count), session.Detached)
            .ConfigureAwait(false);

    private async Task<int> FreePocketSlotsAsync(ISessionScope session) =>
        await session.Client.InvokeAsync(
            c => c.State.Inventory.PlayerSlots.Skip(9).Count(stack => stack.IsEmpty), session.Detached)
            .ConfigureAwait(false);

    private static bool IsBad(BlockPos pos, Dictionary<BlockPos, int> blacklist, int tick)
        => blacklist.TryGetValue(pos, out int until) && tick < until;

    private void MarkBad(Dictionary<BlockPos, int> blacklist, BlockPos pos, int forTicks)
    {
        lock (_gate)
            blacklist[pos] = _tick + forTicks;
    }

    /// <summary>Resolves ordered chest candidates for one leg, verifying coordinates live.</summary>
    private async Task<IReadOnlyList<(ChestConfig Entry, BlockPos Position)>> ResolveChestsAsync(
        ISessionScope session, ChestDirection direction, IReadOnlySet<string> needed)
    {
        IReadOnlyList<ChestConfig> ordered;
        int tick;
        lock (_gate)
        {
            ordered = ChestRules.OrderForJob(_settings.Chests, direction, needed);
            tick = _tick;
        }

        var pinned = new List<(ChestConfig, BlockPos)>();
        var labeled = new List<ChestConfig>();
        foreach (ChestConfig entry in ordered)
        {
            if (entry.Position.Length > 0 && Coords.TryParse(entry.Position, out BlockPos pos))
                pinned.Add((entry, pos));
            else if (entry.Label.Length > 0)
                labeled.Add(entry);
        }

        var resolved = new List<(ChestConfig Entry, BlockPos Position)>();
        if (pinned.Count > 0)
        {
            IReadOnlyList<bool> matches = await session.Client.InvokeAsync(c =>
            {
                var checks = new List<bool>(pinned.Count);
                foreach ((ChestConfig entry, BlockPos pos) in pinned)
                {
                    checks.Add(!IsBad(pos, _badChests, tick)
                        && ChestRules.MatchesKind(
                            c.State.World.GetBlock(pos).Block.Id.ToString(),
                            ChestRules.ParseKind(entry.Kind)));
                }

                return (IReadOnlyList<bool>)checks;
            }, session.Detached).ConfigureAwait(false);

            for (int i = 0; i < pinned.Count; i++)
            {
                if (matches[i])
                    resolved.Add(pinned[i]);
            }
        }

        if (labeled.Count > 0)
        {
            Dictionary<string, List<BlockPos>> byLabel = await ScanLabeledChestsAsync(session, labeled).ConfigureAwait(false);
            foreach (ChestConfig entry in labeled)
            {
                if (byLabel.TryGetValue(entry.Label, out List<BlockPos>? positions))
                {
                    foreach (BlockPos pos in positions)
                        resolved.Add((entry, pos));
                }
            }
        }

        // Restore config order across the two groups: filtered entries first, then shared.
        var order = new Dictionary<ChestConfig, int>();
        for (int i = 0; i < ordered.Count; i++)
            order[ordered[i]] = i;

        resolved.Sort((a, b) => order[a.Entry].CompareTo(order[b.Entry]));
        Debug($"AutoCraft: resolved {resolved.Count} {direction} chest(s) for {string.Join(",", needed)}.");
        return resolved;
    }

    /// <summary>One world read mapping sign labels to adjacent matching containers.</summary>
    private async Task<Dictionary<string, List<BlockPos>>> ScanLabeledChestsAsync(
        ISessionScope session, IReadOnlyList<ChestConfig> labeled)
    {
        var wants = new Dictionary<string, ChestKind>(StringComparer.OrdinalIgnoreCase);
        foreach (ChestConfig entry in labeled)
            wants.TryAdd(entry.Label, ChestRules.ParseKind(entry.Kind));

        int tick;
        BlockPos self;
        lock (_gate)
            tick = _tick;

        Vec3d feet = session.State.Self.Position;
        self = new BlockPos((int)Math.Floor(feet.X), (int)Math.Floor(feet.Y), (int)Math.Floor(feet.Z));

        return await session.Client.InvokeAsync(c =>
        {
            var found = new Dictionary<string, List<BlockPos>>(StringComparer.OrdinalIgnoreCase);
            int seen = 0;
            foreach (KeyValuePair<BlockPos, NbtCompound> sign in c.State.World.BlockEntityNbt)
            {
                if (Math.Abs(sign.Key.X - self.X) > 32
                    || Math.Abs(sign.Key.Y - self.Y) > 16
                    || Math.Abs(sign.Key.Z - self.Z) > 32)
                    continue;

                seen++;
                List<string> lines = ReadSignLines(sign.Value);
                foreach (KeyValuePair<string, ChestKind> want in wants)
                {
                    if (!lines.Any(line => string.Equals(line.Trim(), want.Key.Trim(), StringComparison.OrdinalIgnoreCase)))
                        continue;

                    foreach (BlockPos near in Adjacent(sign.Key))
                    {
                        if (IsBad(near, _badChests, tick)
                            || !ChestRules.MatchesKind(c.State.World.GetBlock(near).Block.Id.ToString(), want.Value))
                            continue;

                        if (!found.TryGetValue(want.Key, out List<BlockPos>? list))
                        {
                            list = [];
                            found[want.Key] = list;
                        }

                        if (!list.Contains(near))
                            list.Add(near);
                    }
                }
            }

            Vec3d at = c.State.Self.Position;
            foreach (List<BlockPos> list in found.Values)
                list.Sort((a, b) => DistanceSquared(at, a).CompareTo(DistanceSquared(at, b)));

            Debug($"AutoCraft: sign scan saw {seen} nearby block entities, matched {found.Count} label(s).");
            return found;
        }, session.Detached).ConfigureAwait(false);
    }

    private static double DistanceSquared(Vec3d at, BlockPos pos)
    {
        double dx = at.X - (pos.X + 0.5);
        double dy = at.Y - (pos.Y + 0.5);
        double dz = at.Z - (pos.Z + 0.5);
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    private static IEnumerable<BlockPos> Adjacent(BlockPos position)
    {
        yield return position.Above();
        yield return position.Below();
        yield return position.Offset(Direction.North);
        yield return position.Offset(Direction.South);
        yield return position.Offset(Direction.East);
        yield return position.Offset(Direction.West);
    }

    private static List<string> ReadSignLines(NbtCompound nbt)
    {
        var lines = new List<string>();
        for (int i = 1; i <= 4; i++)
        {
            if (nbt.TryGet($"Text{i}", out NbtString? line))
                lines.Add(CleanSignLine(ReadLegacyComponent(line.Value)));
        }

        foreach (string side in new[] { "front_text", "back_text" })
        {
            if (!nbt.TryGet(side, out NbtCompound? text) || !text.TryGet("messages", out NbtList? messages))
                continue;

            foreach (NbtTag message in messages)
                lines.Add(CleanSignLine(ReadModernComponent(message)));
        }

        return lines;
    }

    /// <summary>
    /// A modern sign line can arrive as a JSON string ('"AC Input"') when the component reader does not recognize the tag shape.
    /// Unquote it so labels match what the player typed.
    /// </summary>
    private static string CleanSignLine(string line)
    {
        string text = line.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            try
            {
                return ComponentJson.Parse(text).ToPlainText();
            }
            catch
            {
                return text[1..^1];
            }
        }

        return text;
    }

    private static string ReadLegacyComponent(string text)
    {
        try { return ComponentJson.Parse(text).ToPlainText(); }
        catch { return text; }
    }

    private static string ReadModernComponent(NbtTag tag)
    {
        try { return ComponentNbt.From(tag).ToPlainText(); }
        catch { return tag.ToString() ?? string.Empty; }
    }

    private async Task<bool> OpenContainerAsync(ISessionScope session, BlockPos position, bool tableMode)
    {
        if (!await WalkToAsync(session, position).ConfigureAwait(false))
            return false;

        await session.Actions.Movement.LookAtAsync(position.Center, session.Detached).ConfigureAwait(false);
        await session.Actions.Interaction
            .PlaceBlockAsync(position, Direction.Up, new Vec3d(0.5, 0.5, 0.5), Hand.Main, false, session.Detached)
            .ConfigureAwait(false);

        for (int i = 0; i < 20; i++)
        {
            bool ready = await session.Client.InvokeAsync(
                c => c.State.Inventory.HasOpenContainer
                    && c.State.Inventory.ContainerSlots is { } slots
                    && (tableMode ? CraftRules.IsCraftingTableWindow(slots.Count) : slots.Count >= 63),
                session.Detached).ConfigureAwait(false);
            if (ready)
                return true;

            await Task.Delay(50, session.Detached).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task CloseContainerAsync(ISessionScope session)
    {
        bool open = await session.Client.InvokeAsync(
            c => c.State.Inventory.HasOpenContainer, session.Detached).ConfigureAwait(false);
        if (open)
            await session.Actions.Inventory.CloseAsync(session.Detached).ConfigureAwait(false);
    }

    private static async Task<bool> SettleAsync(ISessionScope session, ClickResult result)
    {
        if (result.ChangedSlots.Count == 0)
            return false;

        await Task.Delay(75, session.Detached).ConfigureAwait(false);
        return await session.Client.InvokeAsync(
            c => c.State.Inventory.HasOpenContainer, session.Detached).ConfigureAwait(false);
    }

    private async Task<(bool Ok, string Material, int ShortBy)> WithdrawAsync(ISessionScope session, Dictionary<string, int> needs)
    {
        foreach ((string material, int want) in needs)
        {
            int target = await CountCarriedAsync(session, stack =>
                string.Equals(CraftRules.Normalize(stack.Item.Id.ToString()), material, StringComparison.Ordinal))
                .ConfigureAwait(false) + want;

            IReadOnlyList<(ChestConfig Entry, BlockPos Position)> candidates = await ResolveChestsAsync(
                session, ChestDirection.Input, new HashSet<string>(StringComparer.Ordinal) { material })
                .ConfigureAwait(false);

            if (candidates.Count == 0)
                Debug($"AutoCraft: no usable input chest for {material}; check registration and reachability.");

            foreach ((ChestConfig _, BlockPos pos) in candidates)
            {
                if (!await OpenContainerAsync(session, pos, tableMode: false).ConfigureAwait(false))
                {
                    Debug($"AutoCraft: input at {Coords.Format(pos)} would not open; skipping it for a while.");
                    MarkBad(_badChests, pos, BenchBlacklistTicks);
                    continue;
                }

                try
                {
                    for (int guard = 0; guard < 54; guard++)
                    {
                        (int Slot, int Total) source = await session.Client.InvokeAsync(c =>
                        {
                            IReadOnlyList<ItemStack> slots = c.State.Inventory.ContainerSlots ?? [];
                            int top = Math.Max(0, slots.Count - 36);
                            int total = c.State.Inventory.PlayerSlots
                                .Where(stack => !stack.IsEmpty
                                    && string.Equals(CraftRules.Normalize(stack.Item.Id.ToString()), material, StringComparison.Ordinal))
                                .Sum(stack => stack.Count);
                            if (total >= target)
                                return (-1, total);

                            for (int slot = 0; slot < top; slot++)
                            {
                                ItemStack stack = slots[slot];
                                if (!stack.IsEmpty
                                    && string.Equals(CraftRules.Normalize(stack.Item.Id.ToString()), material, StringComparison.Ordinal))
                                    return (slot, total);
                            }

                            return (-1, total);
                        }, session.Detached).ConfigureAwait(false);

                        if (source.Slot < 0)
                            break;

                        ClickResult moved = await session.Actions.Inventory
                            .QuickMoveAsync(source.Slot, session.Detached).ConfigureAwait(false);
                        if (!await SettleAsync(session, moved).ConfigureAwait(false))
                            break;
                    }
                }
                finally
                {
                    await CloseContainerAsync(session).ConfigureAwait(false);
                }

                int have = await CountCarriedAsync(session, stack =>
                    string.Equals(CraftRules.Normalize(stack.Item.Id.ToString()), material, StringComparison.Ordinal))
                    .ConfigureAwait(false);
                if (have >= target)
                    break;
            }

            int carried = await CountCarriedAsync(session, stack =>
                string.Equals(CraftRules.Normalize(stack.Item.Id.ToString()), material, StringComparison.Ordinal))
                .ConfigureAwait(false);
            if (carried < target)
                return (false, material, target - carried);
        }

        return (true, string.Empty, 0);
    }

    private async Task<int> DepositAsync(ISessionScope session, string result)
    {
        int before = await CountCarriedAsync(session, stack =>
            string.Equals(CraftRules.Normalize(stack.Item.Id.ToString()), result, StringComparison.Ordinal))
            .ConfigureAwait(false);
        if (before == 0)
            return 0;

        IReadOnlyList<(ChestConfig Entry, BlockPos Position)> candidates = await ResolveChestsAsync(
            session, ChestDirection.Output, new HashSet<string>(StringComparer.Ordinal) { result })
            .ConfigureAwait(false);

        foreach ((ChestConfig _, BlockPos pos) in candidates)
        {
            if (!await OpenContainerAsync(session, pos, tableMode: false).ConfigureAwait(false))
            {
                Debug($"AutoCraft: output at {Coords.Format(pos)} would not open; skipping it for a while.");
                MarkBad(_badChests, pos, BenchBlacklistTicks);
                continue;
            }

            try
            {
                for (int guard = 0; guard < 36; guard++)
                {
                    int source = await session.Client.InvokeAsync(c =>
                    {
                        IReadOnlyList<ItemStack> player = c.State.Inventory.PlayerSlots;
                        IReadOnlyList<ItemStack> slots = c.State.Inventory.ContainerSlots ?? [];
                        int top = Math.Max(0, slots.Count - 36);
                        if (top == 0)
                            return -1;

                        for (int playerSlot = 9; playerSlot <= 44 && playerSlot < player.Count; playerSlot++)
                        {
                            ItemStack stack = player[playerSlot];
                            if (!stack.IsEmpty
                                && string.Equals(CraftRules.Normalize(stack.Item.Id.ToString()), result, StringComparison.Ordinal))
                                return top + playerSlot - 9;
                        }

                        return -1;
                    }, session.Detached).ConfigureAwait(false);

                    if (source < 0)
                        break;

                    ClickResult moved = await session.Actions.Inventory
                        .QuickMoveAsync(source, session.Detached).ConfigureAwait(false);
                    if (!await SettleAsync(session, moved).ConfigureAwait(false))
                        break;
                }
            }
            finally
            {
                await CloseContainerAsync(session).ConfigureAwait(false);
            }

            int left = await CountCarriedAsync(session, stack =>
                string.Equals(CraftRules.Normalize(stack.Item.Id.ToString()), result, StringComparison.Ordinal))
                .ConfigureAwait(false);
            if (left == 0)
                break;
        }

        int after = await CountCarriedAsync(session, stack =>
            string.Equals(CraftRules.Normalize(stack.Item.Id.ToString()), result, StringComparison.Ordinal))
            .ConfigureAwait(false);
        return Math.Max(0, before - after);
    }

    #endregion

    #region Bench

    private static bool Reachable(Vec3d feet, BlockPos target)
    {
        double eyeY = feet.Y + 1.62;
        double dx = Nearest(feet.X, target.X);
        double dy = Nearest(eyeY, target.Y);
        double dz = Nearest(feet.Z, target.Z);
        return (dx * dx) + (dy * dy) + (dz * dz) <= BenchReach * BenchReach;

        static double Nearest(double value, int min)
            => value < min ? min - value : value > min + 1 ? value - (min + 1) : 0.0;
    }

    private async Task<bool> WalkToAsync(ISessionScope session, BlockPos target)
    {
        try
        {
            (bool Pathfinding, bool InReach) state = await session.Client.InvokeAsync(
                c => (c.State.Features.Pathfinding, Reachable(c.State.Self.Position, target)),
                session.Detached).ConfigureAwait(false);

            if (state.InReach)
                return true;

            if (state.Pathfinding)
            {
                await session.Actions.Movement
                    .NavigateAsync(new GoalNear(target.X, target.Y, target.Z, 2), session.Detached).ConfigureAwait(false);
            }
            else
                await session.Actions.Movement.MoveToVerifiedAsync(target.BottomCenter, session.Detached).ConfigureAwait(false);

            return _running && await session.Client.InvokeAsync(
                c => Reachable(c.State.Self.Position, target), session.Detached).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug($"AutoCraft: could not walk to {Coords.Format(target)}.");
            _logger?.LogDebug(ex, "{Message}", _strings?.Get("navigation_failed") ?? "navigation_failed");
            return false;
        }
    }

    private async Task<bool> EnsureBenchAsync(ISessionScope session)
    {
        BlockPos? pinned;
        int radius;
        BlockPos? bound;
        lock (_gate)
        {
            pinned = _settings.PinnedBench();
            radius = _settings.BenchRadius;
            bound = _benchPos;
        }

        if (bound is { } held)
        {
            bool stillTable = await session.Client.InvokeAsync(
                c => ChestRules.BareBlockId(c.State.World.GetBlock(held).Block.Id.ToString()) == "crafting_table",
                session.Detached).ConfigureAwait(false);
            if (stillTable && await OpenContainerAsync(session, held, tableMode: true).ConfigureAwait(false))
                return true;

            Debug($"AutoCraft: bound bench at {Coords.Format(held)} is gone or shut; rescanning.");
            MarkBad(_benchBlacklist, held, BenchBlacklistTicks);
            lock (_gate)
                _benchPos = null;
        }

        IReadOnlyList<BlockPos> candidates = pinned is { } pin
            ? new List<BlockPos> { pin }
            : await ScanBenchesAsync(session, radius).ConfigureAwait(false);

        int tried = 0;
        foreach (BlockPos candidate in candidates)
        {
            if (tried >= 3)
                break;

            tried++;
            if (!await OpenContainerAsync(session, candidate, tableMode: true).ConfigureAwait(false))
            {
                Debug($"AutoCraft: bench at {Coords.Format(candidate)} would not open; trying the next one.");
                MarkBad(_benchBlacklist, candidate, BenchBlacklistTicks);
                continue;
            }

            lock (_gate)
                _benchPos = candidate;

            return true;
        }

        string center = await session.Client.InvokeAsync(c =>
        {
            Vec3d at = c.State.Self.Position;
            return $"{(int)Math.Floor(at.X)} {(int)Math.Floor(at.Y)} {(int)Math.Floor(at.Z)}";
        }, session.Detached).ConfigureAwait(false);

        ParkOnce(CraftPhase.MissingBench, BenchRetryTicks,
            _strings?.Format("missing_bench", radius, center),
            radius.ToString(CultureInfo.InvariantCulture), center);
        return false;
    }

    private async Task<IReadOnlyList<BlockPos>> ScanBenchesAsync(ISessionScope session, int radius)
    {
        int tick;
        lock (_gate)
            tick = _tick;

        Vec3d feet = session.State.Self.Position;
        var self = new BlockPos((int)Math.Floor(feet.X), (int)Math.Floor(feet.Y), (int)Math.Floor(feet.Z));

        return await session.Client.InvokeAsync(c =>
        {
            var found = new List<(BlockPos Position, double Distance)>();
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -4; dy <= 4; dy++)
                {
                    for (int dz = -radius; dz <= radius; dz++)
                    {
                        var pos = new BlockPos(self.X + dx, self.Y + dy, self.Z + dz);
                        if (IsBad(pos, _benchBlacklist, tick))
                            continue;

                        BlockState state;
                        try
                        {
                            state = c.State.World.GetBlock(pos);
                        }
                        catch
                        {
                            continue;
                        }

                        if (ChestRules.BareBlockId(state.Block.Id.ToString()) != "crafting_table")
                            continue;

                        found.Add((pos, DistanceSquared(c.State.Self.Position, pos)));
                    }
                }
            }

            found.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            var ordered = new List<BlockPos>(Math.Min(12, found.Count));
            foreach ((BlockPos position, _) in found)
            {
                if (ordered.Count >= 12)
                    break;

                ordered.Add(position);
            }

            return (IReadOnlyList<BlockPos>)ordered;
        }, session.Detached).ConfigureAwait(false);
    }

    #endregion

    #region Cleanup

    private async Task ClearCraftingAreaAsync(ISessionScope session, CraftType grid)
    {
        IReadOnlyList<ItemStack> slots = ActiveSlots(session);
        int lastGrid = CraftRules.LastGridSlot(grid);
        int firstSource = CraftRules.FirstSourceSlot(grid);

        var empty = new List<int>();
        for (int slot = firstSource; slot < slots.Count; slot++)
        {
            if (slots[slot].IsEmpty)
                empty.Add(slot);
        }

        for (int slot = 1; slot <= lastGrid && slot < slots.Count; slot++)
        {
            if (slots[slot].IsEmpty)
                continue;

            if (empty.Count > 0)
            {
                await session.Actions.Inventory
                    .ClickAsync(new ClickAction.Pickup(slot, MouseButton.Left), session.Detached).ConfigureAwait(false);
                await session.Actions.Inventory
                    .ClickAsync(new ClickAction.Pickup(empty[0], MouseButton.Left), session.Detached).ConfigureAwait(false);
                empty.RemoveAt(0);
            }
            else
            {
                // Pockets are full and the grid is dirty: park instead of throwing anything away.
                Park(CraftPhase.OutputFull, ShelfRetryTicks, string.Empty, string.Empty);
                return;
            }
        }
    }

    private async Task StopAsync(ISessionScope session)
    {
        CraftJob? job;
        lock (_gate)
        {
            _running = false;
            job = _queue.Count > 0 ? _queue[0] : null;
        }

        try
        {
            if (job is not null)
            {
                CraftType grid = job.Recipe.Station == CraftStation.Table ? CraftType.Table : CraftType.Player;
                await ClearCraftingAreaAsync(session, grid).ConfigureAwait(false);
            }

            await CloseContainerAsync(session).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "{Message}", _strings?.Get("cleanup_failed"));
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
                _parked = false;
                _waitingForUpdate = false;
                _waitingForMaterials = false;
                _busy = false;
                _debounce = 0;
                _retryTicks = 0;
                _phase = CraftPhase.Idle;
            }
        }
    }

    #endregion
}

/// <summary>The <c>autocraft</c> console command.</summary>
internal sealed class AutoCraftCommand : CommandBase
{
    private readonly AutoCraftPlugin _plugin;
    private readonly IPluginLocalization _strings;

    internal AutoCraftCommand(AutoCraftPlugin plugin, IPluginLocalization strings)
    {
        _plugin = plugin;
        _strings = strings;
    }

    /// <inheritdoc/>
    public override string CmdName => "autocraft";

    /// <inheritdoc/>
    public override string CmdDesc => _strings.Get("command_desc");

    /// <inheritdoc/>
    public override string CmdUsage => _strings.Get("command_usage");

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Plugins;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("craft <count> <recipe>", _strings.Get("usage_craft")),
        new("keep <count> <recipe>", _strings.Get("usage_keep")),
        new("recipes [filter]", _strings.Get("usage_recipes")),
        new("info <recipe>", _strings.Get("usage_info")),
        new("show <recipe>", _strings.Get("usage_show")),
        new("add <input|output> <chest|barrel|shulker> <\"sign\"|x y z> [item]", _strings.Get("usage_add")),
        new("remove <input|output> <chest|barrel|shulker> <\"sign\"|x y z>", _strings.Get("usage_remove")),
        new("chests", _strings.Get("usage_chests")),
        new("bench <radius|auto|set|rescan|status>", _strings.Get("usage_bench")),
        new("queue", _strings.Get("usage_queue")),
        new("cancel <n>", _strings.Get("usage_cancel")),
        new("status", _strings.Get("usage_status")),
        new("stop", _strings.Get("usage_stop")),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples =>
    [
        "autocraft craft 64 torches",
        "autocraft keep 128 torches",
        "autocraft add input chest \"Main\"",
        "autocraft add input barrel 12 64 -30 iron_ingot",
        "autocraft bench radius 12",
        "autocraft info \"Steel Gear\"",
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["inventory", "move"];

    /// <inheritdoc/>
    public override string? ManTopic => "auto-craft";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .Executes(ctx => ShowUsage(ctx.Source))
            .ThenLiteral("list", a => a
                .Executes(ctx => Recipes(ctx.Source, string.Empty))
                .ThenArgument("filter", Arguments.GreedyString(), b => b
                    .Executes(ctx => Recipes(ctx.Source, ctx.GetArgument<string>("filter")))))
            .ThenLiteral("recipes", a => a
                .Executes(ctx => Recipes(ctx.Source, string.Empty))
                .ThenArgument("filter", Arguments.GreedyString(), b => b
                    .Executes(ctx => Recipes(ctx.Source, ctx.GetArgument<string>("filter")))))
            .ThenLiteral("craft", a => a
                .ThenArgument("order", Arguments.GreedyString(), b => b
                    .Suggests(SuggestRecipes)
                    .Executes(ctx => StartJob(ctx.Source, ctx.GetArgument<string>("order"), keep: false))))
            .ThenLiteral("keep", a => a
                .ThenArgument("order", Arguments.GreedyString(), b => b
                    .Suggests(SuggestRecipes)
                    .Executes(ctx => StartJob(ctx.Source, ctx.GetArgument<string>("order"), keep: true))))
            .ThenLiteral("start", a => a
                .ThenArgument("order", Arguments.GreedyString(), b => b
                    .Suggests(SuggestRecipes)
                    .Executes(ctx => StartJob(ctx.Source, ctx.GetArgument<string>("order"), keep: false))))
            .ThenLiteral("info", a => a
                .ThenArgument("recipe", Arguments.GreedyString(), b => b
                    .Suggests(SuggestRecipes)
                    .Executes(ctx => Info(ctx.Source, ctx.GetArgument<string>("recipe")))))
            .ThenLiteral("show", a => a
                .ThenArgument("recipe", Arguments.GreedyString(), b => b
                    .Suggests(SuggestRecipes)
                    .Executes(ctx => Info(ctx.Source, ctx.GetArgument<string>("recipe")))))
            .ThenLiteral("add", a => a
                .ThenLiteral("input", d => RegisterChestTarget(d, ChestDirection.Input, add: true))
                .ThenLiteral("output", d => RegisterChestTarget(d, ChestDirection.Output, add: true)))
            .ThenLiteral("remove", a => a
                .ThenLiteral("input", d => RegisterChestTarget(d, ChestDirection.Input, add: false))
                .ThenLiteral("output", d => RegisterChestTarget(d, ChestDirection.Output, add: false)))
            .ThenLiteral("chests", a => a.Executes(ctx => Chests(ctx.Source)))
            .ThenLiteral("bench", a => a
                .Executes(ctx => BenchStatus(ctx.Source))
                .ThenLiteral("status", s => s.Executes(ctx => BenchStatus(ctx.Source)))
                .ThenLiteral("auto", s => s.Executes(ctx => BenchAuto(ctx.Source)))
                .ThenLiteral("rescan", s => s.Executes(ctx => BenchRescan(ctx.Source)))
                .ThenLiteral("radius", s => s
                    .ThenArgument("blocks", Arguments.Integer(2, 32), b => b
                        .Executes(ctx => BenchRadius(ctx.Source, ctx.GetArgument<int>("blocks")))))
                .ThenLiteral("set", s => s
                    .ThenArgument("x", Arguments.Integer(), x => x
                        .ThenArgument("y", Arguments.Integer(), y => y
                            .ThenArgument("z", Arguments.Integer(), z => z
                                .Executes(ctx => BenchSet(ctx.Source, ctx)))))))
            .ThenLiteral("queue", a => a.Executes(ctx => Queue(ctx.Source)))
            .ThenLiteral("cancel", a => a
                .ThenArgument("n", Arguments.Integer(1, 1000000), b => b
                    .Executes(ctx => Cancel(ctx.Source, ctx.GetArgument<int>("n")))))
            .ThenLiteral("status", a => a.Executes(ctx => Status(ctx.Source)))
            .ThenLiteral("stop", a => a.Executes(ctx => Stop(ctx.Source)))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private void RegisterChestTarget(CommandNodeBuilder<CommandContext> direction, ChestDirection chestDirection, bool add)
    {
        foreach (ChestKind kind in new[] { ChestKind.Chest, ChestKind.Barrel, ChestKind.Shulker })
        {
            string word = ChestRules.KindName(kind);
            direction.ThenLiteral(word, k => k
                .ThenArgument("x", Arguments.Integer(), x => x
                    .ThenArgument("y", Arguments.Integer(), y => y
                        .ThenArgument("z", Arguments.Integer(), z => z
                            .Executes(ctx => add
                                ? AddCoords(ctx.Source, chestDirection, kind, ctx, item: null)
                                : RemoveCoords(ctx.Source, chestDirection, kind, ctx))
                            .ThenArgument("item", Arguments.Word(), item => item
                                .Executes(ctx => add
                                    ? AddCoords(ctx.Source, chestDirection, kind, ctx, ctx.GetArgument<string>("item"))
                                    : ctx.Source.Result.Fail(string.Empty))))))
                .ThenArgument("label", Arguments.QuotableString(), s => s
                    .Executes(ctx => add
                        ? AddLabel(ctx.Source, chestDirection, kind, ctx.GetArgument<string>("label"), item: null)
                        : RemoveLabel(ctx.Source, chestDirection, kind, ctx.GetArgument<string>("label")))
                    .ThenArgument("item", Arguments.Word(), item => item
                        .Executes(ctx => add
                            ? AddLabel(ctx.Source, chestDirection, kind, ctx.GetArgument<string>("label"), ctx.GetArgument<string>("item"))
                            : ctx.Source.Result.Fail(string.Empty)))));
        }
    }

    private int Recipes(CommandContext ctx, string filter)
    {
        IReadOnlyList<CraftRecipe> recipes = _plugin.Recipes;
        if (string.IsNullOrWhiteSpace(filter))
        {
            var names = new List<string>(recipes.Count);
            foreach (CraftRecipe recipe in recipes)
                names.Add(recipe.Name);

            return ctx.Result.Ok(_strings.Format("recipes", names.Count, string.Join(", ", names)));
        }

        (CraftRecipe? _, string[] suggestions) = CraftLibrary.Resolve(recipes, filter, 50);
        var hits = new List<string>(suggestions);
        // Resolve already returns substring matches; an exact hit still lists itself.
        if (hits.Count == 0)
            return ctx.Result.Fail(_strings.Format("recipe_not_found", filter));

        return ctx.Result.Ok(_strings.Format("recipes_filtered", filter, hits.Count, string.Join(", ", hits)));
    }

    private int StartJob(CommandContext ctx, string order, bool keep)
    {
        string? failure = keep ? _plugin.Keep(order, _strings) : _plugin.Craft(order, _strings);
        if (failure is null)
            return ctx.Result.Ok(_strings.Get(keep ? "queued_keep" : "queued"));

        return ctx.Result.Fail(failure);
    }

    private int Info(CommandContext ctx, string query)
    {
        (CraftRecipe? recipe, string[] suggestions) = CraftLibrary.Resolve(_plugin.Recipes, query);
        if (recipe is null)
        {
            string message = _strings.Format("recipe_not_found", query);
            if (suggestions.Length > 0)
                message += _strings.Format("recipe_suggest", string.Join(", ", suggestions));

            return ctx.Result.Fail(message);
        }

        return ctx.Result.Ok(CraftLibrary.RenderAscii(recipe));
    }

    private int AddCoords(CommandContext ctx, ChestDirection direction, ChestKind kind, ICommandContext<CommandContext> args, string? item) =>
        Add(ctx, direction, kind, label: null, new BlockPos(args.GetArgument<int>("x"), args.GetArgument<int>("y"), args.GetArgument<int>("z")), item);

    private int RemoveCoords(CommandContext ctx, ChestDirection direction, ChestKind kind, ICommandContext<CommandContext> args)
    {
        bool removed = _plugin.RemoveChest(
            direction, kind, null,
            new BlockPos(args.GetArgument<int>("x"), args.GetArgument<int>("y"), args.GetArgument<int>("z")));
        return removed ? ctx.Result.Ok(_strings.Get("chest_removed")) : ctx.Result.Fail(_strings.Get("chest_not_found"));
    }

    private int AddLabel(CommandContext ctx, ChestDirection direction, ChestKind kind, string label, string? item) =>
        Add(ctx, direction, kind, label, position: null, item);

    private int RemoveLabel(CommandContext ctx, ChestDirection direction, ChestKind kind, string label)
    {
        bool removed = _plugin.RemoveChest(direction, kind, label, position: null);
        return removed ? ctx.Result.Ok(_strings.Get("chest_removed")) : ctx.Result.Fail(_strings.Get("chest_not_found"));
    }

    private int Add(CommandContext ctx, ChestDirection direction, ChestKind kind, string? label, BlockPos? position, string? item)
    {
        string? error = _plugin.AddChest(direction, kind, label, position, item);
        return error switch
        {
            null => ctx.Result.Ok(_strings.Get("chest_added")),
            "duplicate" => ctx.Result.Fail(_strings.Get("chest_duplicate")),
            "bad-position" => ctx.Result.Fail(_strings.Get("chest_bad_position")),
            _ => ctx.Result.Fail(_strings.Get("chest_no_locator")),
        };
    }

    private int Chests(CommandContext ctx) => ctx.Result.Ok(_plugin.ChestSummary(_strings));

    private int BenchRadius(CommandContext ctx, int radius)
    {
        int saved = _plugin.SetBenchRadius(radius);
        return ctx.Result.Ok(_strings.Format("bench_radius_saved", saved));
    }

    private int BenchAuto(CommandContext ctx)
    {
        _plugin.UnpinBench();
        return ctx.Result.Ok(_strings.Get("bench_auto"));
    }

    private int BenchSet(CommandContext ctx, ICommandContext<CommandContext> args)
    {
        var position = new BlockPos(args.GetArgument<int>("x"), args.GetArgument<int>("y"), args.GetArgument<int>("z"));
        _plugin.PinBench(position);
        return ctx.Result.Ok(_strings.Format("bench_pinned", Coords.Format(position)));
    }

    private int BenchRescan(CommandContext ctx)
    {
        _plugin.RescanBench();
        return ctx.Result.Ok(_strings.Get("bench_rescanning"));
    }

    private int BenchStatus(CommandContext ctx) => ctx.Result.Ok(_plugin.BenchSummary(_strings));

    private int Queue(CommandContext ctx) => ctx.Result.Ok(_plugin.QueueSummary(_strings));

    private int Cancel(CommandContext ctx, int number)
    {
        string? failure = _plugin.Cancel(number, _strings);
        return failure is null
            ? ctx.Result.Ok(_strings.Format("queue_cancelled", number))
            : ctx.Result.Fail(failure);
    }

    private int Status(CommandContext ctx) => ctx.Result.Ok(_plugin.StatusSummary(_strings));

    private int Stop(CommandContext ctx)
    {
        if (!_plugin.IsRunning)
            return ctx.Result.Fail(_strings.Get("already_stopped"));

        _plugin.Stop();
        return ctx.Result.Ok(_strings.Get("stopping"));
    }

    private ValueTask SuggestRecipes(ICommandContext<CommandContext> ctx, ISuggestionSink sink)
    {
        foreach (CraftRecipe recipe in _plugin.Recipes)
        {
            if (recipe.Name.StartsWith(sink.Remaining, StringComparison.OrdinalIgnoreCase))
                sink.Suggest(recipe.Name);
        }

        return ValueTask.CompletedTask;
    }
}
