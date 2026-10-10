// CraftLibrary - the recipe side of AutoCraft v2: built-in recipes with human names, tolerant name matching, custom recipe merging, order-count parsing, and the ASCII grid renderer used by the info/show commands.
// Everything here is pure logic with no session access, so it is covered headlessly in DMCBK.Core.Tests.

using System.Text;

namespace DMCBK.OfficialPlugins.AutoCraft;

/// <summary>Which crafting surface a recipe needs.</summary>
public enum CraftStation
{
    /// <summary>The 2x2 grid in the player inventory. No walking.</summary>
    Player,

    /// <summary>The 3x3 grid of a crafting table. The bot finds and binds one itself.</summary>
    Table,

    /// <summary>Fits the 2x2 grid and is crafted there even when a table is bound.</summary>
    Any,
}

/// <summary>One recipe the bot can craft, built-in or custom.</summary>
public sealed class CraftRecipe
{
    /// <summary>The human-readable name, matched case-insensitively.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Alternate spellings that resolve to this recipe.</summary>
    public string[] Aliases { get; set; } = [];

    /// <summary>Where this recipe is crafted.</summary>
    public CraftStation Station { get; set; } = CraftStation.Player;

    /// <summary>The normalized result item id.</summary>
    public string Result { get; set; } = string.Empty;

    /// <summary>How many items one craft produces.</summary>
    public int Count { get; set; } = 1;

    /// <summary>Grid cells left to right, top to bottom. 4 cells for 2x2, 9 for 3x3.</summary>
    public string[] Slots { get; set; } = [];
}

/// <summary>Built-in recipes, name matching, order parsing, and ASCII rendering.</summary>
public static class CraftLibrary
{
    /// <summary>Default item count when an order names no count.</summary>
    public const int DefaultOrderCount = 64;

    /// <summary>Every recipe shipped with the plugin. Custom recipes layer on top.</summary>
    public static readonly IReadOnlyList<CraftRecipe> BuiltIn = new List<CraftRecipe>
    {
        new() { Name = "Torch", Aliases = ["torches"], Station = CraftStation.Any, Result = "torch", Count = 4,
            Slots = ["coal", "", "stick", ""] },
        new() { Name = "Stick", Aliases = ["sticks"], Station = CraftStation.Player, Result = "stick", Count = 4,
            Slots = ["oak_planks", "", "oak_planks", ""] },
        new() { Name = "Oak Planks", Aliases = ["planks", "oak planks", "wooden planks"], Station = CraftStation.Player, Result = "oak_planks", Count = 4,
            Slots = ["oak_log", "", "", ""] },
        new() { Name = "Crafting Table", Aliases = ["crafting table", "workbench", "table"], Station = CraftStation.Player, Result = "crafting_table", Count = 1,
            Slots = ["oak_planks", "oak_planks", "oak_planks", "oak_planks"] },
        new() { Name = "Stone Bricks", Aliases = ["stone bricks", "stone brick"], Station = CraftStation.Player, Result = "stone_bricks", Count = 4,
            Slots = ["stone", "stone", "stone", "stone"] },
        new() { Name = "Iron Ingot", Aliases = ["iron ingots"], Station = CraftStation.Player, Result = "iron_ingot", Count = 9,
            Slots = ["iron_block", "", "", ""] },
        new() { Name = "Gold Ingot", Aliases = ["gold ingots"], Station = CraftStation.Player, Result = "gold_ingot", Count = 9,
            Slots = ["gold_block", "", "", ""] },
        new() { Name = "Furnace", Aliases = ["furnaces"], Station = CraftStation.Table, Result = "furnace", Count = 1,
            Slots = ["cobblestone", "cobblestone", "cobblestone", "cobblestone", "", "cobblestone", "cobblestone", "cobblestone", "cobblestone"] },
        new() { Name = "Chest", Aliases = ["chests"], Station = CraftStation.Table, Result = "chest", Count = 1,
            Slots = ["oak_planks", "oak_planks", "oak_planks", "oak_planks", "", "oak_planks", "oak_planks", "oak_planks", "oak_planks"] },
        new() { Name = "Iron Block", Aliases = ["block of iron"], Station = CraftStation.Table, Result = "iron_block", Count = 1,
            Slots = ["iron_ingot", "iron_ingot", "iron_ingot", "iron_ingot", "iron_ingot", "iron_ingot", "iron_ingot", "iron_ingot", "iron_ingot"] },
        new() { Name = "Gold Block", Aliases = ["block of gold"], Station = CraftStation.Table, Result = "gold_block", Count = 1,
            Slots = ["gold_ingot", "gold_ingot", "gold_ingot", "gold_ingot", "gold_ingot", "gold_ingot", "gold_ingot", "gold_ingot", "gold_ingot"] },
        new() { Name = "Rail", Aliases = ["rails", "minecart rails", "minecart rail"], Station = CraftStation.Table, Result = "rail", Count = 16,
            Slots = ["iron_ingot", "", "iron_ingot", "iron_ingot", "stick", "iron_ingot", "iron_ingot", "", "iron_ingot"] },
        new() { Name = "Powered Rail", Aliases = ["powered rails"], Station = CraftStation.Table, Result = "powered_rail", Count = 6,
            Slots = ["gold_ingot", "", "gold_ingot", "gold_ingot", "stick", "gold_ingot", "gold_ingot", "redstone", "gold_ingot"] },
        new() { Name = "Piston", Aliases = ["pistons"], Station = CraftStation.Table, Result = "piston", Count = 1,
            Slots = ["oak_planks", "oak_planks", "oak_planks", "cobblestone", "iron_ingot", "cobblestone", "cobblestone", "redstone", "cobblestone"] },
        new() { Name = "Dispenser", Aliases = ["dispensers"], Station = CraftStation.Table, Result = "dispenser", Count = 1,
            Slots = ["cobblestone", "cobblestone", "cobblestone", "cobblestone", "bow", "cobblestone", "cobblestone", "redstone", "cobblestone"] },
        new() { Name = "Bookshelf", Aliases = ["bookshelves", "bookcase", "book shelf"], Station = CraftStation.Table, Result = "bookshelf", Count = 1,
            Slots = ["oak_planks", "oak_planks", "oak_planks", "book", "book", "book", "oak_planks", "oak_planks", "oak_planks"] },
        new() { Name = "Iron Plate", Aliases = ["iron plates", "plate"], Station = CraftStation.Player, Result = "iron_plate", Count = 1,
            Slots = ["iron_ingot", "iron_ingot", "", ""] },
        new() { Name = "Steel Gear", Aliases = ["steel gears", "gear", "gears"], Station = CraftStation.Table, Result = "steel_gear", Count = 1,
            Slots = ["", "steel_ingot", "", "steel_ingot", "iron_block", "steel_ingot", "", "steel_ingot", ""] },
        new() { Name = "Advanced Circuit", Aliases = ["advanced circuits", "circuit", "circuits"], Station = CraftStation.Table, Result = "advanced_circuit", Count = 1,
            Slots = ["redstone", "iron_plate", "redstone", "iron_plate", "steel_gear", "iron_plate", "redstone", "iron_plate", "redstone"] },
    };

    /// <summary>Normalizes a recipe name for matching: lowercase, separators become spaces.</summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var builder = new StringBuilder(name.Trim().Length);
        bool lastWasSpace = true;
        foreach (char c in name.Trim())
        {
            if (c is '_' or '-' or ' ' or '\t')
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }
            }
            else
            {
                builder.Append(char.ToLowerInvariant(c));
                lastWasSpace = false;
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>Merges custom recipes over the built-ins. A custom with the same name wins.</summary>
    public static IReadOnlyList<CraftRecipe> EffectiveRecipes(IReadOnlyList<CraftRecipe>? customs)
    {
        if (customs is null || customs.Count == 0)
            return BuiltIn;

        var overridden = new HashSet<string>(StringComparer.Ordinal);
        foreach (CraftRecipe custom in customs)
            overridden.Add(NormalizeName(custom.Name));

        var merged = new List<CraftRecipe>(BuiltIn.Count + customs.Count);
        foreach (CraftRecipe recipe in BuiltIn)
        {
            if (!overridden.Contains(NormalizeName(recipe.Name)))
                merged.Add(recipe);
        }

        merged.AddRange(customs);
        return merged;
    }

    /// <summary>
    /// Resolves a query to a recipe.
    /// Exact name or alias match wins; otherwise up to <paramref name="maxSuggestions"/> substring candidates come back for the error message.
    /// </summary>
    public static (CraftRecipe? Recipe, string[] Suggestions) Resolve(
        IReadOnlyList<CraftRecipe> recipes, string? query, int maxSuggestions = 5)
    {
        string want = NormalizeName(query);
        if (want.Length == 0)
            return (null, []);

        var suggestions = new List<string>();
        foreach (CraftRecipe recipe in recipes)
        {
            if (NormalizeName(recipe.Name) == want
                || recipe.Aliases.Any(alias => NormalizeName(alias) == want))
                return (recipe, []);
        }

        foreach (CraftRecipe recipe in recipes)
        {
            if (suggestions.Count >= maxSuggestions)
                break;

            if (NormalizeName(recipe.Name).Contains(want, StringComparison.Ordinal)
                || recipe.Aliases.Any(alias => NormalizeName(alias).Contains(want, StringComparison.Ordinal)))
                suggestions.Add(recipe.Name);
        }

        // Typo fallback: names a keystroke or two away still get suggested.
        if (suggestions.Count < maxSuggestions)
        {
            foreach (CraftRecipe recipe in recipes)
            {
                if (suggestions.Count >= maxSuggestions)
                    break;

                if (suggestions.Contains(recipe.Name, StringComparer.Ordinal))
                    continue;

                if (Distance(want, NormalizeName(recipe.Name)) <= 2
                    || recipe.Aliases.Any(alias => Distance(want, NormalizeName(alias)) <= 2))
                    suggestions.Add(recipe.Name);
            }
        }

        return (null, suggestions.ToArray());
    }

    /// <summary>Levenshtein distance over short names. Case already normalized by the caller.</summary>
    public static int Distance(string a, string b)
    {
        if (a.Length == 0)
            return b.Length;

        if (b.Length == 0)
            return a.Length;

        int[] previous = new int[b.Length + 1];
        int[] current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>
    /// Splits an order like "64 torches" or "2s stone bricks" into an item count and a recipe query.
    /// A trailing "s" after the number means stacks of 64.
    /// No leading number means the count is unspecified (<c>-1</c>) and the caller applies its default.
    /// </summary>
    public static bool TryParseOrder(string? text, out int count, out string name)
    {
        count = -1;
        name = (text ?? string.Empty).Trim();
        if (name.Length == 0)
            return false;

        int i = 0;
        bool hasX = name.StartsWith("x", StringComparison.OrdinalIgnoreCase);
        if (hasX)
            i = 1;

        int digits = 0;
        while (i < name.Length && char.IsDigit(name[i]))
        {
            digits = (digits * 10) + (name[i] - '0');
            i++;
        }

        bool hasDigits = hasX ? i > 1 : i > 0;
        if (!hasDigits)
            return true;

        bool stacks = false;
        if (i < name.Length && (name[i] is 's' or 'S'))
        {
            stacks = true;
            i++;
        }

        if (i >= name.Length || !char.IsWhiteSpace(name[i]))
        {
            if (hasDigits)
            {
                // A bare number ("64", "2s") with no recipe name is not an order.
                count = -1;
                name = (text ?? string.Empty).Trim();
                return false;
            }

            // Not a count prefix at all ("xray" is a name, not a count).
            // Whole text is the query.
            count = -1;
            name = name.Trim();
            return true;
        }

        count = stacks ? digits * 64 : digits;
        name = name[i..].Trim();
        return name.Length > 0;
    }

    /// <summary>The three-letter code for an item id inside the ASCII grid.</summary>
    public static string CodeFor(string? itemId)
    {
        string id = CraftRules.Normalize(itemId);
        if (id.Length == 0)
            return "   ";

        string[] parts = id.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
            return parts[0].Length >= 3 ? parts[0][..3] : parts[0].PadRight(3);

        var code = new StringBuilder(3);
        foreach (string part in parts)
        {
            if (code.Length >= 2)
                break;

            code.Append(part[0]);
        }

        string last = parts[^1];
        for (int k = 1; k < last.Length && code.Length < 3; k++)
            code.Append(last[k]);

        while (code.Length < 3)
            code.Append(' ');

        return code.ToString();
    }

    /// <summary>
    /// Renders a recipe as plain ASCII: pipes and dashes only, so it survives every console.
    /// 2x2 recipes print two columns, 3x3 print three.
    /// </summary>
    public static string RenderAscii(CraftRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        int width = recipe.Slots.Length <= 4 ? 2 : 3;
        int rows = recipe.Slots.Length / width;

        var output = new StringBuilder();
        output.Append(recipe.Name).Append(" x").Append(recipe.Count).Append("  [")
            .Append(StationName(recipe.Station)).Append(']').Append('\n');

        string border = width == 2 ? "+---+---+" : "+---+---+---+";
        var seen = new List<string>();
        var needs = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int row = 0; row < rows; row++)
        {
            output.Append(border).Append('\n');
            output.Append('|');
            for (int col = 0; col < width; col++)
            {
                int index = (row * width) + col;
                string cell = index < recipe.Slots.Length ? CraftRules.Normalize(recipe.Slots[index]) : string.Empty;
                output.Append(cell.Length == 0 ? "   " : CodeFor(cell)).Append('|');
                if (cell.Length > 0)
                {
                    if (!needs.TryGetValue(cell, out int n))
                    {
                        seen.Add(cell);
                        n = 0;
                    }

                    needs[cell] = n + 1;
                }
            }

            output.Append('\n');
        }

        output.Append(border);
        if (needs.Count > 0)
        {
            output.Append('\n').Append("needs: ");
            for (int k = 0; k < seen.Count; k++)
            {
                if (k > 0)
                    output.Append(", ");

                output.Append(seen[k]).Append(" x").Append(needs[seen[k]]);
            }
        }

        return output.ToString();
    }

    /// <summary>Renders a craft station as the word the commands and manual use.</summary>
    public static string StationName(CraftStation station) => station switch
    {
        CraftStation.Table => "table",
        CraftStation.Any => "any",
        _ => "player",
    };

    /// <summary>Parses a station word. Unknown words mean the 2x2 player grid.</summary>
    public static CraftStation ParseStation(string? value)
    {
        string word = (value ?? string.Empty).Trim().ToLowerInvariant();
        return word switch
        {
            "table" => CraftStation.Table,
            "any" => CraftStation.Any,
            _ => CraftStation.Player,
        };
    }
}
