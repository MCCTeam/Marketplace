// AutoDrop: throws away configured item types as they arrive, in three modes (include, exclude, everything).
// Slot 0, the crafting result, is never dropped.
//
// A pass runs 0.2 s after the last inventory update, so one server burst of slot updates produces one pass instead of one per packet.
// The largest updated window id wins, because a container update also implies the player inventory (window 0).

using System.Text;
using DMCBK.Core.Commands;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Tomlet.Attributes;
using Umpk.Client.Events;
using Umpk.Commands;
using Umpk.Game.Inventory;
using Umpk.Game.Items;

namespace DMCBK.OfficialPlugins.AutoDrop;

/// <summary>The drop mode: which items of the configured list are thrown away.</summary>
public enum DropMode
{
    /// <summary>Items in the list are dropped.</summary>
    Include,

    /// <summary>Items in the list are kept, everything else is dropped.</summary>
    Exclude,

    /// <summary>Everything is dropped, the list is ignored.</summary>
    Everything,
}

/// <summary>Item-name normalization and matching.</summary>
public static class ItemNames
{
    /// <summary>
    /// Normalizes an item name to the bare snake_case registry path: strips a namespace, converts a PascalCase spelling (<c>NetheriteIngot</c>) to snake_case, and lowercases.
    /// Blank input gives an empty string.
    /// </summary>
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
            if (char.IsUpper(c) && i > 0 && builder.Length > 0 && builder[^1] != '_')
                builder.Append('_');

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>Normalizes every entry of a list, dropping blanks and duplicates (order preserved).</summary>
    public static string[] NormalizeAll(IEnumerable<string>? names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        if (names is null)
            return [];

        foreach (string name in names)
        {
            string normalized = Normalize(name);
            if (normalized.Length > 0 && seen.Add(normalized))
                result.Add(normalized);
        }

        return [.. result];
    }

    /// <summary>True when the given registry item id matches one of the already-normalized names.</summary>
    public static bool Matches(string itemId, IReadOnlyList<string> normalizedNames)
    {
        string bare = Normalize(itemId);
        for (int i = 0; i < normalizedNames.Count; i++)
        {
            if (string.Equals(bare, normalizedNames[i], StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>Parses a drop mode name (case-insensitive); returns false for an unknown value.</summary>
    public static bool TryParseMode(string? value, out DropMode mode)
    {
        mode = DropMode.Include;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        switch (value.Trim().ToLowerInvariant())
        {
            case "include":
                mode = DropMode.Include;
                return true;
            case "exclude":
                mode = DropMode.Exclude;
                return true;
            case "everything":
                mode = DropMode.Everything;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Renders a drop mode in lowercase.</summary>
    public static string ModeName(DropMode mode) => mode switch
    {
        DropMode.Exclude => "exclude",
        DropMode.Everything => "everything",
        _ => "include",
    };

    /// <summary>
    /// Decides whether the item in a slot should be dropped.
    /// Slot 0, the crafting result, is never dropped, and empty slots are skipped.
    /// </summary>
    public static bool ShouldDrop(int slot, string itemId, DropMode mode, IReadOnlyList<string> normalizedItems)
    {
        if (slot == 0 || string.IsNullOrEmpty(itemId))
            return false;

        return mode switch
        {
            DropMode.Everything => true,
            DropMode.Exclude => !Matches(itemId, normalizedItems),
            _ => Matches(itemId, normalizedItems),
        };
    }
}

/// <summary>The user-editable, persisted settings for AutoDrop (serialized to settings.toml).</summary>
public sealed class AutoDropSettings : IValidatablePluginSettings
{
    /// <summary>Master switch for the plugin.</summary>
    [TomlInlineComment("$settings.enabled$")]
    public bool Enabled { get; set; }

    /// <summary>The drop mode: include, exclude or everything.</summary>
    [TomlInlineComment("$settings.mode$")]
    public string Mode { get; set; } = "include";

    /// <summary>The item list. Accepts Cobblestone, cobblestone or minecraft:cobblestone.</summary>
    [TomlPrecedingComment("$settings.items$")]
    public string[] Items { get; set; } = ["Cobblestone", "Dirt"];

    /// <summary>Clamps/normalizes on load and reload.</summary>
    public void Validate()
    {
        Items ??= [];
        if (!ItemNames.TryParseMode(Mode, out DropMode mode))
            mode = DropMode.Include;

        Mode = ItemNames.ModeName(mode);
    }
}

/// <summary>The AutoDrop plugin entry point.</summary>
public sealed class AutoDropPlugin : IPlugin
{
    // 0.2 s after the last inventory update of a burst.
    private const int DebounceTicks = 4;

    private readonly object _gate = new();
    private AutoDropSettings _settings = new();
    private ILogger? _logger;
    private IPluginLocalization? _strings;
    private string[] _items = [];
    private DropMode _mode = DropMode.Include;
    private volatile bool _enabled;
    private int _debounce;
    private int _windowUpdated = -1;

    /// <inheritdoc/>
    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "auto-drop";
        descriptor.Version = "2.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
        descriptor.WithSettings<AutoDropSettings>();
    }

    /// <inheritdoc/>
    public Task ActivateAsync(PluginContext context)
    {
        _settings = context.Settings.Load<AutoDropSettings>();
        _logger = context.Logger;
        _strings = context.Strings;
        ApplySettings();

        context.Commands.Register(new AutoDropCommand(this, context.Strings));

        context.SessionStarted += (_, e) => OnSessionStarted(e.Session);
        context.SessionEnded += (_, _) => Reset();
        return Task.CompletedTask;
    }

    /// <summary>Whether the bot is currently on.</summary>
    internal bool IsEnabled => _enabled;

    /// <summary>The current mode.</summary>
    internal DropMode Mode
    {
        get
        {
            lock (_gate)
                return _mode;
        }
    }

    /// <summary>Turns the bot on or off. Turning it on schedules an immediate pass.</summary>
    internal void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (enabled)
        {
            lock (_gate)
            {
                _windowUpdated = 0;
                _debounce = 1;
            }
        }
    }

    /// <summary>Switches the mode and schedules an immediate pass.</summary>
    internal void SetMode(DropMode mode)
    {
        lock (_gate)
        {
            _mode = mode;
            _windowUpdated = 0;
            _debounce = 1;
        }
    }

    /// <summary>Adds an item to the list; returns the normalized name.</summary>
    internal string AddItem(string item)
    {
        string normalized = ItemNames.Normalize(item);
        lock (_gate)
        {
            if (normalized.Length > 0 && Array.IndexOf(_items, normalized) < 0)
                _items = [.. _items, normalized];
        }

        return normalized;
    }

    /// <summary>Removes an item from the list; false when it was not present.</summary>
    internal bool RemoveItem(string item)
    {
        string normalized = ItemNames.Normalize(item);
        lock (_gate)
        {
            int index = Array.IndexOf(_items, normalized);
            if (index < 0)
                return false;

            var list = new List<string>(_items);
            list.RemoveAt(index);
            _items = [.. list];
            return true;
        }
    }

    /// <summary>A snapshot of the current item list.</summary>
    internal string[] Items
    {
        get
        {
            lock (_gate)
                return [.. _items];
        }
    }

    private void ApplySettings()
    {
        lock (_gate)
        {
            _items = ItemNames.NormalizeAll(_settings.Items);
            _mode = ItemNames.TryParseMode(_settings.Mode, out DropMode mode) ? mode : DropMode.Include;
        }

        _enabled = _settings.Enabled;
    }

    private void Reset()
    {
        lock (_gate)
        {
            _debounce = 0;
            _windowUpdated = -1;
        }
    }

    private void OnSessionStarted(ISessionScope session)
    {
        Reset();
        if (!session.State.Features.Inventory)
        {
            _logger?.LogWarning("{Message}", _strings?.Get("inventory_required"));
            return;
        }

        session.Events.Subscribe<ContainerContentChanged>(e => OnContainerChanged(e.WindowId));
        session.Scheduler.OnTick(() => OnTick(session));
    }

    private void OnContainerChanged(int windowId)
    {
        if (!_enabled)
            return;

        lock (_gate)
        {
            _debounce = DebounceTicks;

            // A container update always implies the player inventory too, so the larger window wins.
            if (windowId >= _windowUpdated)
                _windowUpdated = windowId;
        }
    }

    private void OnTick(ISessionScope session)
    {
        int window;
        lock (_gate)
        {
            if (_debounce <= 0)
                return;

            if (--_debounce > 0)
                return;

            window = _windowUpdated;
            _windowUpdated = -1;
        }

        if (window >= 0 && _enabled)
            _ = FlushAsync(session, window);
    }

    private async Task FlushAsync(ISessionScope session, int windowId)
    {
        DropMode mode;
        string[] items;
        lock (_gate)
        {
            mode = _mode;
            items = _items;
        }

        var inventory = session.State.Inventory;
        IReadOnlyList<ItemStack> slots =
            windowId != 0 && inventory.HasOpenContainer && inventory.OpenWindowId == windowId && inventory.ContainerSlots is { } container
                ? container
                : inventory.PlayerSlots;

        var targets = new List<int>();
        for (int slot = 0; slot < slots.Count; slot++)
        {
            ItemStack stack = slots[slot];
            if (stack.IsEmpty)
                continue;

            if (ItemNames.ShouldDrop(slot, stack.Item.Id.ToString(), mode, items))
                targets.Add(slot);
        }

        if (targets.Count == 0)
            return;

        foreach (int slot in targets)
        {
            try
            {
                await session.Actions.Inventory.ClickAsync(new ClickAction.Throw(slot, WholeStack: true), session.Detached)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogDebug(ex, "{Message}", _strings?.Get("click_failed"));
            }
        }

        _logger?.LogInformation("{Message}", _strings?.Format("dropped", targets.Count, windowId));
    }
}

/// <summary>The <c>autodrop</c> console command.</summary>
internal sealed class AutoDropCommand : CommandBase
{
    private readonly AutoDropPlugin _plugin;
    private readonly IPluginLocalization _strings;

    internal AutoDropCommand(AutoDropPlugin plugin, IPluginLocalization strings)
    {
        _plugin = plugin;
        _strings = strings;
    }

    /// <inheritdoc/>
    public override string CmdName => "autodrop";

    /// <inheritdoc/>
    public override string CmdDesc => _strings.Get("command_desc");

    /// <inheritdoc/>
    public override string CmdUsage => _strings.Get("command_usage");

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Plugins;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("on", _strings.Get("usage_on")),
        new("off", _strings.Get("usage_off")),
        new("add <item>", _strings.Get("usage_add")),
        new("remove <item>", _strings.Get("usage_remove")),
        new("list", _strings.Get("usage_list")),
        new("mode <include|exclude>", _strings.Get("usage_mode")),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["autodrop on", "autodrop add cobblestone", "autodrop mode exclude"];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["dropitem", "inventory"];

    /// <inheritdoc/>
    public override string? ManTopic => "auto-drop";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .Executes(ctx => ShowUsage(ctx.Source))
            .ThenLiteral("on", a => a.Executes(ctx => Enable(ctx.Source, true)))
            .ThenLiteral("off", a => a.Executes(ctx => Enable(ctx.Source, false)))
            .ThenLiteral("list", a => a.Executes(ctx => List(ctx.Source)))
            .ThenLiteral("add", a => a
                .ThenArgument("item", Arguments.Word(), b => b.Executes(ctx => Add(ctx.Source, ctx.GetArgument<string>("item")))))
            .ThenLiteral("remove", a => a
                .ThenArgument("item", Arguments.Word(), b => b.Executes(ctx => Remove(ctx.Source, ctx.GetArgument<string>("item")))))
            .ThenLiteral("mode", a => a
                .ThenArgument("mode", Arguments.Word(), b => b.Executes(ctx => Mode(ctx.Source, ctx.GetArgument<string>("mode")))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private int Enable(CommandContext ctx, bool enable)
    {
        _plugin.SetEnabled(enable);
        return ctx.Result.Ok(_strings.Get(enable ? "on" : "off"));
    }

    private int Add(CommandContext ctx, string item)
    {
        string added = _plugin.AddItem(item);
        return added.Length == 0
            ? ctx.Result.Fail(_strings.Get("no_item"))
            : ctx.Result.Ok(_strings.Format("added", added));
    }

    private int Remove(CommandContext ctx, string item)
        => _plugin.RemoveItem(item)
            ? ctx.Result.Ok(_strings.Format("removed", ItemNames.Normalize(item)))
            : ctx.Result.Fail(_strings.Get("not_in_list"));

    private int List(CommandContext ctx)
    {
        string[] items = _plugin.Items;
        return items.Length == 0
            ? ctx.Result.Fail(_strings.Get("no_item"))
            : ctx.Result.Ok(_strings.Format("list", items.Length, string.Join(", ", items)));
    }

    private int Mode(CommandContext ctx, string value)
    {
        if (!ItemNames.TryParseMode(value, out DropMode mode))
            return ctx.Result.Fail(_strings.Format("unknown_mode", value));

        _plugin.SetMode(mode);
        return ctx.Result.Ok(_strings.Format("switched", ItemNames.ModeName(mode)));
    }
}
