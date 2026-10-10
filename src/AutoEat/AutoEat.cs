// A template compiled plugin: watches the player's food level and eats from the hotbar when hunger drops to or below the configured threshold.

using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Tomlet.Attributes;
using Umpk.Client.Actions;
using Umpk.Client.Events;
using Umpk.Game.Items;

namespace DMCBK.OfficialPlugins.AutoEat;

/// <summary>The user-editable, persisted settings for AutoEat (serialized to settings.toml).</summary>
public sealed class AutoEatSettings : IValidatablePluginSettings
{
    /// <summary>Master switch for the plugin.</summary>
    [TomlInlineComment("$settings.enabled$")]
    public bool Enabled { get; set; } = true;

    /// <summary>Eat when the food level (0-20) is at or below this value.</summary>
    [TomlInlineComment("$settings.threshold$")]
    public int Threshold { get; set; } = 6;

    /// <summary>Clamps the threshold on load and reload.</summary>
    public void Validate()
    {
        if (Threshold > 20)
            Threshold = 20;
        else if (Threshold < 0)
            Threshold = 0;
    }
}

/// <summary>The AutoEat plugin entry point.</summary>
public sealed class AutoEatPlugin : IPlugin
{
    private static readonly HashSet<string> FoodItemPaths = new(StringComparer.Ordinal)
    {
        "apple", "golden_apple", "enchanted_golden_apple", "bread", "cookie", "melon_slice",
        "carrot", "golden_carrot", "potato", "baked_potato", "poisonous_potato", "beetroot",
        "dried_kelp", "sweet_berries", "glow_berries", "chorus_fruit", "honey_bottle",
        "beef", "cooked_beef", "porkchop", "cooked_porkchop", "chicken", "cooked_chicken",
        "mutton", "cooked_mutton", "rabbit", "cooked_rabbit", "cod", "cooked_cod",
        "salmon", "cooked_salmon", "tropical_fish", "pufferfish", "rotten_flesh", "spider_eye",
        "pumpkin_pie", "mushroom_stew", "rabbit_stew", "beetroot_soup", "suspicious_stew",
    };

    private AutoEatSettings _settings = new();
    private ILogger? _logger;

    private IPluginLocalization? _strings;
    private bool _eating;
    private int _slotToRestore;

    /// <inheritdoc/>
    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "auto-eat";
        descriptor.Version = "2.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
        descriptor.WithSettings<AutoEatSettings>();
    }

    /// <inheritdoc/>
    public Task ActivateAsync(PluginContext context)
    {
        _settings = context.Settings.Load<AutoEatSettings>();
        _logger = context.Logger;
        _strings = context.Strings;

        // Re-subscribe per session so a reconnect keeps AutoEat working.
        context.SessionStarted += (_, e) => OnSessionStarted(e.Session);
        context.SessionEnded += (_, _) => _eating = false;
        return Task.CompletedTask;
    }

    private void OnSessionStarted(ISessionScope session)
    {
        _eating = false;

        // The handler runs on the session loop, so it can read state and fire actions directly.
        session.Events.Subscribe<HealthChanged>(e => OnHealthAsync(session, e.Health, e.Food));
    }

    private async ValueTask OnHealthAsync(ISessionScope session, float health, int food)
    {
        if (!_settings.Enabled || health <= 0)
            return;

        if (!session.State.Features.Inventory)
            return; // No inventory feature: cannot select/see food.

        bool hungry = food <= _settings.Threshold || (food < 20 && health < 20);
        if (hungry && !_eating)
            _eating = await TryEatAsync(session).ConfigureAwait(false);
        else if (food >= 20 && _eating)
        {
            _eating = false;
            await session.Actions.Inventory.SelectHeldSlotAsync(_slotToRestore).ConfigureAwait(false);
        }
    }

    private async ValueTask<bool> TryEatAsync(ISessionScope session)
    {
        IReadOnlyList<ItemStack> slots = session.State.Inventory.PlayerSlots;
        int held = session.State.Self.HeldSlot;
        _slotToRestore = held;

        // Prefer the already-held item to avoid a slot switch.
        int heldIndex = 36 + held;
        if (heldIndex >= 0 && heldIndex < slots.Count && IsFood(slots[heldIndex]))
        {
            await session.Actions.Interaction.UseItemAsync(Hand.Main).ConfigureAwait(false);
            LogEating(slots[heldIndex]);
            return true;
        }

        // Slots 36..44 are hotbar 0..8.
        for (int index = 36; index <= 44 && index < slots.Count; index++)
        {
            if (IsFood(slots[index]))
            {
                await session.Actions.Inventory.SelectHeldSlotAsync(index - 36).ConfigureAwait(false);
                await session.Actions.Interaction.UseItemAsync(Hand.Main).ConfigureAwait(false);
                LogEating(slots[index]);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Announces what is being eaten.
    /// The text goes through a constant "{Message}" template because a translated sentence used as the template would turn any brace in it into a placeholder.
    /// </summary>
    private void LogEating(ItemStack stack)
    {
        if (_logger is { } logger && _strings is { } strings)
            logger.LogInformation("{Message}", strings.Format("eating", stack.Item.Id));
    }

    private static bool IsFood(ItemStack stack)
    {
        if (stack.IsEmpty)
            return false;

        // 1.20.5 and later tag food with the minecraft:food data component.
        // Older versions do not, so fall back to a set of item ids, which are stable across the supported range.
        if (stack.Components.Has(DataComponents.Food))
            return true;

        return FoodItemPaths.Contains(stack.Item.Id.Path);
    }
}
