// ChestStore - the storage side of AutoCraft v2: chest entries added through the command (direction, container kind, sign label or coordinates, optional item filter), plus the pure helpers for validating, ordering, and describing them.
// Session access lives in AutoCraft.cs; everything here is headlessly testable.

using Umpk.Geometry;

namespace DMCBK.OfficialPlugins.AutoCraft;

/// <summary>Whether a chest supplies ingredients or takes finished goods.</summary>
public enum ChestDirection
{
    /// <summary>Ingredients come out of this chest.</summary>
    Input,

    /// <summary>Finished goods go into this chest.</summary>
    Output,
}

/// <summary>The container block kind.</summary>
public enum ChestKind
{
    /// <summary>A chest or trapped chest.</summary>
    Chest,

    /// <summary>A barrel.</summary>
    Barrel,

    /// <summary>A placed shulker box of any color.</summary>
    Shulker,
}

/// <summary>One registered chest. Exactly one of <see cref="Label"/> / <see cref="Position"/> is set.</summary>
public sealed class ChestConfig
{
    /// <summary>input or output.</summary>
    public string Direction { get; set; } = "input";

    /// <summary>chest, barrel, or shulker.</summary>
    public string Kind { get; set; } = "chest";

    /// <summary>Sign label, empty when the position is used.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>"x y z", empty when the label is used.</summary>
    public string Position { get; set; } = string.Empty;

    /// <summary>Normalized item filter, empty for a shared chest.</summary>
    public string Item { get; set; } = string.Empty;
}

/// <summary>Integer block coordinates written as "x y z".</summary>
public static class Coords
{
    /// <summary>Parses "x y z" into a block position.</summary>
    public static bool TryParse(string? value, out BlockPos position)
    {
        position = default;
        string[] parts = (value ?? string.Empty).Split(
            ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3
            || !int.TryParse(parts[0], out int x)
            || !int.TryParse(parts[1], out int y)
            || !int.TryParse(parts[2], out int z))
            return false;

        position = new BlockPos(x, y, z);
        return true;
    }

    /// <summary>Formats a block position as "x y z".</summary>
    public static string Format(BlockPos position) => $"{position.X} {position.Y} {position.Z}";
}

/// <summary>Pure rules for chest entries: parsing, validation, and candidate ordering.</summary>
public static class ChestRules
{
    /// <summary>Error codes from <see cref="ValidateEntry"/>; each maps to a lang key.</summary>
    public const string NoLocator = "no-locator";
    public const string BadPosition = "bad-position";

    /// <summary>Parses a direction word. Unknown words fall back to input.</summary>
    public static ChestDirection ParseDirection(string? value)
        => string.Equals(value?.Trim(), "output", StringComparison.OrdinalIgnoreCase)
            ? ChestDirection.Output
            : ChestDirection.Input;

    /// <summary>Renders a direction as the command spells it.</summary>
    public static string DirectionName(ChestDirection direction)
        => direction == ChestDirection.Output ? "output" : "input";

    /// <summary>Parses a container word. Unknown words fall back to chest.</summary>
    public static ChestKind ParseKind(string? value)
    {
        string word = (value ?? string.Empty).Trim().ToLowerInvariant();
        return word switch
        {
            "barrel" => ChestKind.Barrel,
            "shulker" => ChestKind.Shulker,
            _ => ChestKind.Chest,
        };
    }

    /// <summary>Renders a container kind as the command spells it.</summary>
    public static string KindName(ChestKind kind) => kind switch
    {
        ChestKind.Barrel => "barrel",
        ChestKind.Shulker => "shulker",
        _ => "chest",
    };

    /// <summary>True when the chest takes any item rather than one filtered item.</summary>
    public static bool IsShared(ChestConfig entry) => string.IsNullOrEmpty(CraftRules.Normalize(entry.Item));

    /// <summary>True when this entry may supply (or take) the given normalized item.</summary>
    public static bool AcceptsItem(ChestConfig entry, string normalizedItem)
        => IsShared(entry)
            || string.Equals(CraftRules.Normalize(entry.Item), normalizedItem, StringComparison.Ordinal);

    /// <summary>Validates a new entry. Null means valid; otherwise one of the error-code constants.</summary>
    public static string? ValidateEntry(string? label, string? position)
    {
        bool hasLabel = !string.IsNullOrWhiteSpace(label);
        bool hasPosition = !string.IsNullOrWhiteSpace(position);
        if (!hasLabel && !hasPosition)
            return NoLocator;

        if (hasPosition && !Coords.TryParse(position, out _))
            return BadPosition;

        return null;
    }

    /// <summary>
    /// Orders entries for one job leg: chests filtered to one of the needed items first (in config order), then shared chests (in config order).
    /// Output routing passes the result as the single need.
    /// </summary>
    public static IReadOnlyList<ChestConfig> OrderForJob(
        IReadOnlyList<ChestConfig> entries,
        ChestDirection direction,
        IReadOnlySet<string> needed)
    {
        var filtered = new List<ChestConfig>();
        var shared = new List<ChestConfig>();
        foreach (ChestConfig entry in entries)
        {
            if (ParseDirection(entry.Direction) != direction)
                continue;

            string item = CraftRules.Normalize(entry.Item);
            if (item.Length == 0)
                shared.Add(entry);
            else if (needed.Contains(item))
                filtered.Add(entry);
        }

        filtered.AddRange(shared);
        return filtered;
    }

    /// <summary>True when a world block id can serve as the given container kind.</summary>
    public static bool MatchesKind(string? blockId, ChestKind kind)
    {
        string bare = BareBlockId(blockId);
        return kind switch
        {
            ChestKind.Barrel => bare == "barrel",
            ChestKind.Shulker => bare.EndsWith("shulker_box", StringComparison.Ordinal),
            _ => bare is "chest" or "trapped_chest",
        };
    }

    /// <summary>Strips the namespace from a block id and lowercases it.</summary>
    public static string BareBlockId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return string.Empty;

        string value = id.Trim();
        int colon = value.IndexOf(':');
        return (colon >= 0 ? value[(colon + 1)..] : value).ToLowerInvariant();
    }
}
