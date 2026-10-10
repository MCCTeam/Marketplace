// DiscordVitals - pretty Discord renders for vitals, effects and death: emoji, Unicode bars, roman numerals, short durations.
// Pure: no client, no network, no locale.
// Labels come in as parameters so the plugin's lang/ table owns the words and the tests pin the shapes.

using System.Globalization;
using System.Text;

namespace DMCBK.OfficialPlugins.DiscordBridge;

/// <summary>Localized labels for one vitals card.</summary>
public sealed record VitalsWords(
    string Title, string Hp, string Food, string Saturation, string Level, string Xp);

/// <summary>Localized labels for one effects card.</summary>
public sealed record EffectsWords(string Title, string None);

/// <summary>
/// One effect as the card needs it: the raw id for the emoji, the display name, the level suffix (0 hides it), and the remaining seconds (-1 for unlimited).
/// </summary>
public sealed record ListedEffect(string EffectId, string Name, int Level, int Seconds);

/// <summary>One Discord embed's content, without the Discord types.</summary>
public sealed record DiscordCard(string Title, string Body, int Color);

/// <summary>Pretty, testable renders for the health, effects and death replies.</summary>
public static class DiscordVitals
{
    /// <summary>The death embed color: full red.</summary>
    public const int DeathColor = 0xFF0000;

    /// <summary>The effects card color: purple, distinct from health bands and teleport blue.</summary>
    public const int EffectsColor = 0x9B59B6;

    /// <summary>Bar cells per line. Ten reads well on a phone without wrapping.</summary>
    public const int BarWidth = 10;

    private static readonly char[] PartialBlocks = ['▏', '▎', '▍', '▌', '▋', '▊', '▉'];

    private static readonly (string Symbol, int Value)[] RomanNumerals =
    [
        ("M", 1000), ("CM", 900), ("D", 500), ("CD", 400), ("C", 100), ("XC", 90),
        ("L", 50), ("XL", 40), ("X", 10), ("IX", 9), ("V", 5), ("IV", 4), ("I", 1),
    ];

    /// <summary>A width-cell bar: full blocks, one partial block for the remainder, empty shades.</summary>
    public static string Bar(double fraction, int width = BarWidth)
    {
        width = Math.Max(1, width);
        double cells = Math.Clamp(fraction, 0, 1) * width;
        int full = Math.Min((int)Math.Floor(cells), width);
        int part = full >= width ? 0 : (int)Math.Floor((cells - full) * 8);

        var sb = new StringBuilder(width + 1);
        sb.Append('█', full);
        if (full < width)
        {
            if (part > 0)
                sb.Append(PartialBlocks[part - 1]);

            sb.Append('░', width - full - (part > 0 ? 1 : 0));
        }

        return sb.ToString();
    }

    /// <summary>Embed color by band: red below 40%, gold to 90%, green above. Same bands as the console.</summary>
    public static int BandColor(double fraction)
        => fraction < 0.4 ? 0xFF5555 : fraction < 0.9 ? 0xFFAA00 : 0x55FF55;

    /// <summary>Renders a positive integer the way MCC renders an effect level.</summary>
    public static string ToRoman(int number)
    {
        var sb = new StringBuilder();
        foreach ((string symbol, int value) in RomanNumerals)
        {
            while (number >= value)
            {
                sb.Append(symbol);
                number -= value;
            }
        }

        return sb.ToString();
    }

    /// <summary>Short duration: 45s, 3m 05s, 2h 04m, or infinity for unlimited and day-plus.</summary>
    public static string FormatDuration(int seconds)
    {
        if (seconds < 0 || seconds >= 24 * 3600)
            return "∞";

        if (seconds < 60)
            return $"{seconds}s";

        if (seconds < 3600)
        {
            int minutes = seconds / 60;
            int rest = seconds % 60;
            return rest == 0 ? $"{minutes}m" : $"{minutes}m {rest:D2}s";
        }

        int hours = seconds / 3600;
        int restMinutes = seconds % 3600 / 60;
        return restMinutes == 0 ? $"{hours}h" : $"{hours}h {restMinutes:D2}m";
    }

    /// <summary>One emoji per effect, by path segment. Unknown effects get sparkles.</summary>
    public static string EffectEmoji(string effectId)
    {
        string id = (effectId ?? string.Empty).Trim().ToLowerInvariant();
        int separator = id.IndexOf(':');
        string path = separator >= 0 ? id[(separator + 1)..] : id;
        return path switch
        {
            "speed" => "💨",
            "slowness" => "🐌",
            "haste" => "⚡",
            "mining_fatigue" => "⛏️",
            "strength" => "🗡️",
            "weakness" => "🥀",
            "instant_health" => "❤️",
            "instant_damage" => "💔",
            "jump_boost" => "🐇",
            "nausea" => "🌀",
            "regeneration" => "💗",
            "resistance" => "🛡️",
            "fire_resistance" => "🔥",
            "water_breathing" => "💧",
            "invisibility" => "👻",
            "blindness" => "🙈",
            "night_vision" => "🌙",
            "hunger" => "🍖",
            "poison" => "☠️",
            "wither" => "💀",
            "health_boost" => "💪",
            "absorption" => "💛",
            "saturation" => "🍰",
            "glowing" => "✨",
            "levitation" => "🎈",
            "luck" => "🍀",
            "unluck" => "🌧️",
            "slow_falling" => "🪂",
            "conduit_power" => "🔱",
            "dolphins_grace" => "🐬",
            "bad_omen" => "🌩️",
            "hero_of_the_village" => "🏅",
            "darkness" => "🌑",
            "wind_charged" => "🌪️",
            "infested" => "🕷️",
            "oozing" => "🟢",
            "weaving" => "🕸️",
            _ => "✨",
        };
    }

    /// <summary>Builds the vitals card: emoji labels, numbers, and one bar per meter.</summary>
    public static DiscordCard Vitals(
        float health, float maxHealth, int food, int maxFood, float saturation,
        int level, int totalXp, float xpProgress, VitalsWords words)
    {
        ArgumentNullException.ThrowIfNull(words);
        double hp = maxHealth <= 0 ? 0 : health / maxHealth;
        double fd = maxFood <= 0 ? 0 : (double)food / maxFood;

        string hpNum = health.ToString("0.##", CultureInfo.InvariantCulture);
        string hpMax = maxHealth.ToString("0.##", CultureInfo.InvariantCulture);
        string sat = saturation.ToString("0.#", CultureInfo.InvariantCulture);

        var body = new StringBuilder();
        body.Append("❤️ ").Append(words.Hp).Append(' ').Append(hpNum).Append('/').Append(hpMax).Append('\n');
        body.Append('`').Append(Bar(hp)).Append('`').Append('\n');
        body.Append("🍖 ").Append(words.Food).Append(' ').Append(food).Append('/').Append(maxFood)
            .Append(" (").Append(words.Saturation).Append(' ').Append(sat).Append(')').Append('\n');
        body.Append('`').Append(Bar(fd)).Append('`').Append('\n');
        body.Append("✨ ").Append(words.Level).Append(' ').Append(level)
            .Append(" · ").Append(totalXp).Append(' ').Append(words.Xp).Append('\n');
        body.Append('`').Append(Bar(xpProgress)).Append('`');

        return new DiscordCard(words.Title, body.ToString(), BandColor(hp));
    }

    /// <summary>Builds the effects card: one emoji line per effect, or the none text when clear.</summary>
    public static DiscordCard Effects(IReadOnlyList<ListedEffect> effects, EffectsWords words)
    {
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(words);

        if (effects.Count == 0)
            return new DiscordCard(words.Title, words.None, EffectsColor);

        var body = new StringBuilder();
        foreach (ListedEffect effect in effects)
        {
            if (body.Length > 0)
                body.Append('\n');

            body.Append(EffectEmoji(effect.EffectId)).Append(' ').Append(effect.Name);
            if (effect.Level > 0)
                body.Append(' ').Append(ToRoman(effect.Level));

            body.Append(" — ").Append(FormatDuration(effect.Seconds));
        }

        return new DiscordCard(words.Title, body.ToString(), EffectsColor);
    }

    /// <summary>Builds the death card: red, with block coordinates.</summary>
    public static DiscordCard Death(string title, double x, double y, double z)
        => new(title, $"`{(long)Math.Floor(x)}, {(long)Math.Floor(y)}, {(long)Math.Floor(z)}`", DeathColor);

    /// <summary>One tracked effect: the namespaced id and display level (0 hides the suffix).</summary>
    public sealed record TrackedEffect(string Id, int Level);

    /// <summary>
    /// Diffs the tracked set against a fresh snapshot.
    /// A relevel counts as gained: "Speed II" landing on top of "Speed I" is worth one line.
    /// Callers refresh their set from the current list afterwards.
    /// </summary>
    public static (IReadOnlyList<TrackedEffect> Gained, IReadOnlyList<TrackedEffect> Lost) DiffEffects(
        IReadOnlyDictionary<string, int> known, IReadOnlyList<TrackedEffect> current)
    {
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(current);

        var now = current
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Level, StringComparer.Ordinal);
        var gained = now
            .Where(kv => !known.TryGetValue(kv.Key, out int level) || level != kv.Value)
            .Select(kv => new TrackedEffect(kv.Key, kv.Value))
            .ToList();
        var lost = known.Keys
            .Where(id => !now.ContainsKey(id))
            .Select(id => new TrackedEffect(id, known[id]))
            .ToList();
        return (gained, lost);
    }
}
