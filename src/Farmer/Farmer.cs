// Farmer maps a crop field once, then works it in strict whole-field phases: harvest, collect, plant, bone meal.
// Later phases re-read only the mapped sites, and every action waits for the world to confirm it.
//
// Farmer holds the exclusive movement lease while it runs, and releases it around every navigation call, because UMPK's navigator takes that same non-reentrant lease itself.

using System.Globalization;
using System.Runtime.CompilerServices;
using DMCBK.Core.Commands;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Tomlet.Attributes;
using Umpk.Client.Actions;
using Umpk.Client.Events;
using Umpk.Client.Movement;
using Umpk.Commands;
using Umpk.Data.Java;
using Umpk.Game.Blocks;
using Umpk.Game.Entities;
using Umpk.Game.Inventory;
using Umpk.Game.Items;
using Umpk.Geometry;
using Umpk.Nbt;
using Umpk.Pathfinding.Goals;

[assembly: InternalsVisibleTo("DMCBK.OfficialPlugins.Tests")]

namespace DMCBK.OfficialPlugins.Farmer;

/// <summary>The crops Farmer knows how to farm.</summary>
public enum CropType
{
    Beetroot,

    Carrot,

    /// <summary>Melons (harvest the fruit, bone meal the stem).</summary>
    Melon,

    MelonsAndPumpkins,

    /// <summary>Nether wart (soul sand, never bone mealed).</summary>
    NetherWart,

    /// <summary>Pumpkins (harvest the fruit, bone meal the stem).</summary>
    Pumpkin,

    Potato,

    Wheat,

    /// <summary>Sugar cane (preserve the base).</summary>
    SugarCane,

    /// <summary>Bamboo (preserve the base).</summary>
    Bamboo,

    /// <summary>Cactus (preserve the base).</summary>
    Cactus,

    /// <summary>Cocoa pods attached to jungle wood.</summary>
    Cocoa,
}

/// <summary>The farming mechanics Farmer supports.</summary>
public enum FarmMode
{
    BreakAndReplant,
    StemFruit,
    PreserveVerticalBase,
    CocoaPod,
}

/// <summary>Version-independent behavior for one crop.</summary>
public sealed record CropProfile(
    CropType Crop,
    FarmMode Mode,
    string HarvestBlock,
    string? GrowthBlock,
    string? SupportBlock,
    string? PlantItem,
    int MaxAge,
    bool CanBoneMeal,
    IReadOnlySet<string> CollectionItems);

/// <summary>One flavour of a stem and fruit crop.</summary>
public sealed record StemVariant(
    CropType Crop,
    string FruitBlock,
    string StemBlock,
    string SeedItem,
    IReadOnlySet<string> CollectionItems);

/// <summary>How the bounds of a farm were supplied.</summary>
public enum FarmRegionKind
{
    Radius,
    Area,
}

/// <summary>The inclusive block bounds of one farm.</summary>
public sealed record FarmRegion(
    FarmRegionKind Kind,
    BlockPos Min,
    BlockPos Max,
    BlockPos Center,
    int Radius)
{
    /// <summary>Creates a horizontal circular radius with a practical vertical discovery band.</summary>
    public static FarmRegion Around(BlockPos center, int radius)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(radius, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(radius, FarmRules.MaxRadius);
        return new FarmRegion(
            FarmRegionKind.Radius,
            new BlockPos(center.X - radius, center.Y - FarmRules.RadiusVerticalRange, center.Z - radius),
            new BlockPos(center.X + radius, center.Y + FarmRules.RadiusVerticalRange, center.Z + radius),
            center,
            radius);
    }

    /// <summary>Creates a normalized inclusive box from two corners.</summary>
    public static FarmRegion Between(BlockPos first, BlockPos second) => new(
        FarmRegionKind.Area,
        new BlockPos(Math.Min(first.X, second.X), Math.Min(first.Y, second.Y), Math.Min(first.Z, second.Z)),
        new BlockPos(Math.Max(first.X, second.X), Math.Max(first.Y, second.Y), Math.Max(first.Z, second.Z)),
        new BlockPos(
            Midpoint(first.X, second.X),
            Midpoint(first.Y, second.Y),
            Midpoint(first.Z, second.Z)),
        0);

    /// <summary>The number of block cells in the inclusive bounding box.</summary>
    public long CellCount
    {
        get
        {
            long width = (long)Max.X - Min.X + 1;
            long height = (long)Max.Y - Min.Y + 1;
            long depth = (long)Max.Z - Min.Z + 1;
            if (width > long.MaxValue / height)
                return long.MaxValue;

            long plane = width * height;
            return plane > long.MaxValue / depth ? long.MaxValue : plane * depth;
        }
    }

    /// <summary>Whether a block belongs to the selected farm footprint.</summary>
    public bool Contains(BlockPos position)
    {
        if (position.X < Min.X || position.X > Max.X
            || position.Y < Min.Y || position.Y > Max.Y
            || position.Z < Min.Z || position.Z > Max.Z)
            return false;

        if (Kind != FarmRegionKind.Radius)
            return true;

        long dx = position.X - Center.X;
        long dz = position.Z - Center.Z;
        return (dx * dx) + (dz * dz) <= (long)Radius * Radius;
    }

    /// <summary>
    /// Whether a derived crop target belongs to this farm.
    /// Area corners select farm sites such as farmland, soul sand, or a preserved cane base, so the crop worked by that site may be one block above the selected Y range.
    /// Horizontal bounds remain exact and radius mode keeps its discovery band.
    /// </summary>
    public bool ContainsWorkingTarget(BlockPos position)
    {
        if (Kind == FarmRegionKind.Radius)
            return Contains(position);

        return position.X >= Min.X && position.X <= Max.X
            && position.Z >= Min.Z && position.Z <= Max.Z
            && position.Y >= Min.Y
            && (long)position.Y <= (long)Max.Y + 1;
    }

    /// <summary>Whether an entity position is inside the farm, allowing a small drop drift margin.</summary>
    public bool Contains(Vec3d position, double margin)
    {
        if (position.Y < Min.Y - margin || position.Y > Max.Y + 1 + margin)
            return false;

        if (Kind == FarmRegionKind.Radius)
        {
            double dx = position.X - (Center.X + 0.5);
            double dz = position.Z - (Center.Z + 0.5);
            double allowed = Radius + margin;
            return (dx * dx) + (dz * dz) <= allowed * allowed;
        }

        return position.X >= Min.X - margin && position.X <= Max.X + 1 + margin
            && position.Z >= Min.Z - margin && position.Z <= Max.Z + 1 + margin;
    }

    private static int Midpoint(int first, int second) => (int)(((long)first + second) / 2);
}

/// <summary>Crop mapping, growth detection and argument parsing, kept free of session state.</summary>
public static class FarmRules
{
    /// <summary>The default farming radius.</summary>
    public const int DefaultRadius = 30;

    /// <summary>The largest accepted radius.</summary>
    public const int MaxRadius = 150;

    /// <summary>Radius mode discovers farms this many blocks above and below the starting position.</summary>
    public const int RadiusVerticalRange = 8;

    /// <summary>Explicit areas are bounded to keep accidental coordinate typos from scanning billions of cells.</summary>
    public const long MaxAreaCells = 4_000_000;

    /// <summary>Canonical command spellings offered by completion.</summary>
    public static readonly string[] CropSuggestions =
    [
        "beetroot", "beetroot_seeds", "carrot", "melon", "melons_and_pumpkins", "nether_wart",
        "pumpkin", "potato", "wheat", "wheat_seeds", "sugar_cane",
        "bamboo", "cactus", "cocoa", "cocoa_beans",
    ];

    /// <summary>Axe tiers in preference order for melon and pumpkin harvesting.</summary>
    public static readonly string[] HarvestAxes = ["netherite_axe", "diamond_axe", "iron_axe", "golden_axe", "stone_axe"];

    private static readonly IReadOnlyDictionary<CropType, CropProfile> Profiles =
        new Dictionary<CropType, CropProfile>
        {
            [CropType.Beetroot] = Replant(CropType.Beetroot, "beetroots", "farmland", "beetroot_seeds", 3, "beetroot", "beetroot_seeds"),
            [CropType.Carrot] = Replant(CropType.Carrot, "carrots", "farmland", "carrot", 7, "carrot"),
            [CropType.Melon] = Stem(CropType.Melon, "melon", "melon_stem", "melon_seeds", "melon", "melon_slice"),
            [CropType.MelonsAndPumpkins] = Stem(CropType.MelonsAndPumpkins, "melon", "melon_stem", "melon_seeds", "melon", "melon_slice", "pumpkin", "pumpkin_seeds"),
            [CropType.NetherWart] = Replant(CropType.NetherWart, "nether_wart", "soul_sand", "nether_wart", 3, false, "nether_wart"),
            [CropType.Pumpkin] = Stem(CropType.Pumpkin, "pumpkin", "pumpkin_stem", "pumpkin_seeds", "pumpkin"),
            [CropType.Potato] = Replant(CropType.Potato, "potatoes", "farmland", "potato", 7, "potato", "poisonous_potato"),
            [CropType.Wheat] = Replant(CropType.Wheat, "wheat", "farmland", "wheat_seeds", 7, "wheat", "wheat_seeds"),
            [CropType.SugarCane] = Vertical(CropType.SugarCane, "sugar_cane", "sugar_cane", "reeds"),
            [CropType.Bamboo] = Vertical(CropType.Bamboo, "bamboo", "bamboo"),
            [CropType.Cactus] = Vertical(CropType.Cactus, "cactus", "cactus"),
            [CropType.Cocoa] = new(
                CropType.Cocoa,
                FarmMode.CocoaPod,
                "cocoa",
                "cocoa",
                null,
                "cocoa_beans",
                2,
                true,
                Set("cocoa_beans", "dye")),
        };

    private static readonly IReadOnlyDictionary<CropType, StemVariant> StemProfiles =
        new Dictionary<CropType, StemVariant>
        {
            [CropType.Melon] = new(CropType.Melon, "melon", "melon_stem", "melon_seeds", Set("melon", "melon_slice", "melon_seeds")),
            [CropType.Pumpkin] = new(CropType.Pumpkin, "pumpkin", "pumpkin_stem", "pumpkin_seeds", Set("pumpkin", "pumpkin_seeds")),
        };

    // Availability comes from UMPK's version catalog, never from a protocol number written down here.
    private static readonly IReadOnlyDictionary<CropType, Umpk.Protocol.Java.JavaVersion> FirstSupportedVersion =
        new Dictionary<CropType, Umpk.Protocol.Java.JavaVersion>
        {
            [CropType.Beetroot] = JavaVersions.V1_9,
            [CropType.Bamboo] = JavaVersions.V1_14,
        };

    /// <summary>Parses a crop name, case-insensitively, underscores optional.</summary>
    public static bool TryParseCrop(string? value, out CropType crop)
    {
        crop = CropType.Wheat;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        switch (value.Trim().Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant())
        {
            case "beetroot":
            case "beetroots":
            case "beetrootseeds":
                crop = CropType.Beetroot;
                return true;
            case "carrot":
            case "carrots":
                crop = CropType.Carrot;
                return true;
            case "melon":
            case "melonslice":
                crop = CropType.Melon;
                return true;
            case "melonsandpumpkins":
                crop = CropType.MelonsAndPumpkins;
                return true;
            case "netherwart":
                crop = CropType.NetherWart;
                return true;
            case "pumpkin":
                crop = CropType.Pumpkin;
                return true;
            case "potato":
            case "potatoes":
                crop = CropType.Potato;
                return true;
            case "wheat":
            case "wheatseeds":
                crop = CropType.Wheat;
                return true;
            case "sugarcane":
            case "reeds":
                crop = CropType.SugarCane;
                return true;
            case "bamboo":
                crop = CropType.Bamboo;
                return true;
            case "cactus":
                crop = CropType.Cactus;
                return true;
            case "cocoa":
            case "cocoabeans":
                crop = CropType.Cocoa;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Every crop name the start command accepts, for the usage text.</summary>
    public static string CropNames =>
        "Beetroot, Carrot, Melon, MelonsAndPumpkins, NetherWart, Pumpkin, Potato, Wheat, SugarCane, Bamboo, Cactus, Cocoa";

    /// <summary>The behavior profile for a crop.</summary>
    public static CropProfile Profile(CropType crop) => Profiles[crop];

    /// <summary>Gets the stem flavours managed by a crop mode.</summary>
    public static IReadOnlyList<StemVariant> StemVariants(CropType crop) => crop switch
    {
        CropType.MelonsAndPumpkins => [StemProfiles[CropType.Melon], StemProfiles[CropType.Pumpkin]],
        _ when StemProfiles.TryGetValue(crop, out StemVariant? variant) => [variant],
        _ => [],
    };

    /// <summary>Gets the matching stem flavour for a fruit, stem, or seed identifier.</summary>
    public static StemVariant? StemVariantFor(CropType crop, string? id) => StemVariants(crop)
        .FirstOrDefault(variant => string.Equals(BarePath(id), variant.FruitBlock, StringComparison.Ordinal)
            || string.Equals(BarePath(id), variant.StemBlock, StringComparison.Ordinal)
            || string.Equals(BarePath(id), AttachedStemBlock(variant), StringComparison.Ordinal)
            || string.Equals(BarePath(id), variant.SeedItem, StringComparison.Ordinal));

    /// <summary>Whether an id is a fruit block managed by the stem crop mode.</summary>
    public static bool IsStemFruitBlock(CropType crop, string? id) => StemVariants(crop)
        .Any(variant => string.Equals(BarePath(id), variant.FruitBlock, StringComparison.Ordinal));

    /// <summary>Whether an id is a stem block managed by the stem crop mode.</summary>
    public static bool IsStemGrowthBlock(CropType crop, string? id) => StemVariants(crop)
        .Any(variant => string.Equals(BarePath(id), variant.StemBlock, StringComparison.Ordinal)
            || string.Equals(BarePath(id), AttachedStemBlock(variant), StringComparison.Ordinal));

    /// <summary>Whether an id is the modern attached form of a managed stem, which already has fruit.</summary>
    public static bool IsAttachedStemBlock(CropType crop, string? id) => StemVariants(crop)
        .Any(variant => string.Equals(BarePath(id), AttachedStemBlock(variant), StringComparison.Ordinal));

    /// <summary>Whether the crop exists in the connected server's protocol era.</summary>
    public static bool IsAvailableOnProtocol(CropType crop, int protocol) =>
        !FirstSupportedVersion.TryGetValue(crop, out Umpk.Protocol.Java.JavaVersion? first)
        || protocol >= first.Version.Protocol;

    /// <summary>The earliest supported Minecraft release containing the crop.</summary>
    public static string FirstSupportedVersionName(CropType crop) =>
        FirstSupportedVersion.TryGetValue(crop, out Umpk.Protocol.Java.JavaVersion? first)
            ? first.Version.Name
            : JavaVersions.V1_8.Version.Name;

    /// <summary>The block that is harvested for a crop.</summary>
    public static string HarvestBlock(CropType crop) => Profile(crop).HarvestBlock;

    /// <summary>The block that is bone mealed for a crop (stems for melon and pumpkin).</summary>
    public static string BoneMealBlock(CropType crop) => crop switch
    {
        _ => Profile(crop).GrowthBlock ?? HarvestBlock(crop),
    };

    /// <summary>The soil a crop is planted on: soul sand for nether wart, farmland otherwise.</summary>
    public static string SoilBlock(CropType crop) =>
        Profile(crop).SupportBlock ?? throw new InvalidOperationException($"{crop} does not use soil planting.");

    /// <summary>The seed item planted for a crop.</summary>
    public static string SeedItem(CropType crop) =>
        Profile(crop).PlantItem ?? throw new InvalidOperationException($"{crop} does not use a planting item.");

    /// <summary>The harvested item of a crop.</summary>
    public static string CropItem(CropType crop) => Profile(crop).CollectionItems.First();

    /// <summary>True when the crop grows fruit on a stem, so the harvest target is not the crop block.</summary>
    public static bool IsStemCrop(CropType crop) => StemVariants(crop).Count > 0;

    /// <summary>True for a crop whose lowest vertical block must survive routine harvest.</summary>
    public static bool PreservesVerticalBase(CropType crop) =>
        Profile(crop).Mode == FarmMode.PreserveVerticalBase;

    /// <summary>True when the crop can be bone mealed at all (nether wart cannot).</summary>
    public static bool CanBoneMeal(CropType crop) => Profile(crop).CanBoneMeal;

    /// <summary>The conservative number of bone-meal uses budgeted for one immature crop target.</summary>
    public static int BoneMealUsesPerTarget(CropType crop) => crop == CropType.Beetroot ? 6 : 4;

    /// <summary>
    /// Caps a bone-meal refill at five stacks while retaining enough for the currently mapped immature targets.
    /// The per-target budget is deliberately conservative because vanilla growth is random.
    /// </summary>
    public static int BoneMealRestockTarget(CropType crop, int immatureTargets, int cap = 320)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(immatureTargets);
        ArgumentOutOfRangeException.ThrowIfNegative(cap);
        return CanBoneMeal(crop) ? Math.Min(cap, immatureTargets * BoneMealUsesPerTarget(crop)) : 0;
    }

    /// <summary>True when depositing a complete stack still leaves the required planting reserve.</summary>
    internal static bool CanDepositWholeStack(int carriedTotal, int stackCount, int reserve)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(carriedTotal);
        ArgumentOutOfRangeException.ThrowIfNegative(stackCount);
        ArgumentOutOfRangeException.ThrowIfNegative(reserve);
        return stackCount > 0 && carriedTotal - stackCount >= reserve;
    }

    /// <summary>The exact final surplus which cannot be deposited with a whole-stack quick-move.</summary>
    internal static int PartialDepositAmount(int carriedTotal, int reserve)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(carriedTotal);
        ArgumentOutOfRangeException.ThrowIfNegative(reserve);
        return Math.Max(0, carriedTotal - reserve);
    }

    /// <summary>The age value a crop reaches when it is fully grown.</summary>
    public static int MaxAge(CropType crop) => Profile(crop).MaxAge;

    /// <summary>
    /// True when a crop block at the given age is fully grown.
    /// Melon and pumpkin harvest the fruit block itself, which is always ready, so a stem crop asked about the harvest block is always true.
    /// </summary>
    public static bool IsFullyGrown(CropType crop, int age, bool isHarvestBlock)
        => (IsStemCrop(crop) && isHarvestBlock)
           || (PreservesVerticalBase(crop) && isHarvestBlock)
           || age >= MaxAge(crop);

    /// <summary>Whether an item stack is a planting item for the crop, pre-1.13 ids included.</summary>
    public static bool IsPlantingItem(CropType crop, ItemStack stack)
    {
        if (stack.IsEmpty)
            return false;

        string id = BarePath(stack.Item.Id.ToString());
        return crop switch
        {
            CropType.SugarCane => id is "sugar_cane" or "reeds",
            CropType.Cocoa => id == "cocoa_beans" || (id == "dye" && stack.Damage == 3),
            _ when IsStemCrop(crop) => StemVariants(crop).Any(variant => id == variant.SeedItem),
            _ => string.Equals(id, Profile(crop).PlantItem, StringComparison.Ordinal),
        };
    }

    /// <summary>Whether an actual decoded dropped stack is relevant to the selected crop.</summary>
    public static bool IsRelevantDrop(CropType crop, ItemStack stack)
    {
        if (stack.IsEmpty)
            return false;

        string id = BarePath(stack.Item.Id.ToString());
        if (crop == CropType.Cocoa && id == "dye")
            return stack.Damage == 3;

        return IsStemCrop(crop)
            ? StemVariants(crop).Any(variant => variant.CollectionItems.Contains(id))
            : Profile(crop).CollectionItems.Contains(id);
    }

    /// <summary>Whether an entity type is a dropped item, independent of metadata arrival order.</summary>
    public static bool IsDroppedItemEntity(string? entityType) => BarePath(entityType) == "item";

    /// <summary>True when a block id is the selected crop's harvest block, pre-1.13 ids included.</summary>
    public static bool IsHarvestBlock(CropType crop, string? blockId)
    {
        string id = BarePath(blockId);
        return crop == CropType.SugarCane
            ? id is "sugar_cane" or "reeds"
            : IsStemCrop(crop)
                ? IsStemFruitBlock(crop, id)
                : string.Equals(id, HarvestBlock(crop), StringComparison.Ordinal);
    }

    /// <summary>Whether a block can support a Cocoa pod.</summary>
    public static bool IsJungleWood(string? blockId) => BarePath(blockId) is
        "jungle_log" or "jungle_wood" or "stripped_jungle_log" or "stripped_jungle_wood";

    /// <summary>Parses Cocoa's horizontal facing property.</summary>
    public static bool TryParseHorizontalDirection(string? value, out Direction direction)
    {
        direction = value?.ToLowerInvariant() switch
        {
            "north" => Direction.North,
            "south" => Direction.South,
            "west" => Direction.West,
            "east" => Direction.East,
            _ => default,
        };
        return value is not null && direction is Direction.North or Direction.South or Direction.West or Direction.East;
    }

    /// <summary>Whether a player position can interact with a block under UMPK's 4.5-block contract.</summary>
    public static bool IsWithinInteractionReach(Vec3d feet, BlockPos target)
    {
        double eyeY = feet.Y + 1.62;
        double dx = Nearest(feet.X, target.X);
        double dy = Nearest(eyeY, target.Y);
        double dz = Nearest(feet.Z, target.Z);
        return (dx * dx) + (dy * dy) + (dz * dz)
            <= InteractionActions.BlockInteractionRange * InteractionActions.BlockInteractionRange;

        static double Nearest(double value, int min)
            => value < min ? min - value : value > min + 1 ? value - (min + 1) : 0.0;
    }

    /// <summary>Whether this vertical block is the second block above a preserved base.</summary>
    public static bool IsVerticalHarvestLevel(CropType crop, string? here, string? below, string? belowTwo) =>
        PreservesVerticalBase(crop)
        && IsHarvestBlock(crop, here)
        && IsHarvestBlock(crop, below)
        && !IsHarvestBlock(crop, belowTwo);

    /// <summary>
    /// Derives a crop's age from its block-state id and the block's state span.
    /// Every crop block declares exactly one property, <c>age</c>, so its states are the ages in order and the offset into the span is the age.
    /// Below 1.13 the same arithmetic yields the block metadata, which is also the age.
    /// Returns 0 for a state outside the span.
    /// </summary>
    public static int AgeFromState(int stateId, int minStateId, int maxStateId)
        => maxStateId >= minStateId && stateId >= minStateId && stateId <= maxStateId
            ? stateId - minStateId
            : 0;

    /// <summary>Converts a dropped item's reported Y to the air block where a player can stand.</summary>
    public static int DropFeetLevel(double itemY) => (int)Math.Ceiling(itemY - 0.000_001);

    /// <summary>Strips a namespace and lowercases an id.</summary>
    public static string BarePath(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return string.Empty;

        string value = id.Trim();
        int colon = value.IndexOf(':');
        return (colon >= 0 ? value[(colon + 1)..] : value).ToLowerInvariant();
    }

    private static string AttachedStemBlock(StemVariant variant) => variant.StemBlock switch
    {
        "melon_stem" => "attached_melon_stem",
        "pumpkin_stem" => "attached_pumpkin_stem",
        _ => variant.StemBlock,
    };

    private static CropProfile Replant(
        CropType crop,
        string block,
        string support,
        string item,
        int maxAge,
        params string[] collection) => Replant(crop, block, support, item, maxAge, true, collection);

    private static CropProfile Replant(
        CropType crop,
        string block,
        string support,
        string item,
        int maxAge,
        bool boneMeal,
        params string[] collection) => new(
            crop, FarmMode.BreakAndReplant, block, block, support, item, maxAge, boneMeal, Set(collection));

    private static CropProfile Stem(
        CropType crop,
        string fruit,
        string stem,
        string seed,
        params string[] collection) => new(
            crop, FarmMode.StemFruit, fruit, stem, "farmland", seed, 7, true, Set(collection.Append(seed).ToArray()));

    private static CropProfile Vertical(CropType crop, string block, params string[] collection) => new(
        crop, FarmMode.PreserveVerticalBase, block, block, null, null, 0, false, Set(collection));

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);
}

/// <summary>Builds row-contiguous routes over scattered farm sites.</summary>
internal static class FarmTraversal
{
    /// <summary>
    /// Orders the field along its longer horizontal axis, snakes on each row, and chooses the route endpoint nearest to the bot.
    /// This keeps every row contiguous without paying a global TSP cost.
    /// </summary>
    internal static IReadOnlyList<BlockPos> Order(IEnumerable<BlockPos> targets, BlockPos start)
    {
        BlockPos[] distinct = targets.Distinct().ToArray();
        if (distinct.Length <= 1)
            return distinct;

        int spanX = distinct.Max(p => p.X) - distinct.Min(p => p.X);
        int spanZ = distinct.Max(p => p.Z) - distinct.Min(p => p.Z);
        bool travelAlongX = spanX >= spanZ;

        List<BlockPos>[] candidates =
        [
            Build(distinct, travelAlongX, reverseRows: false, reverseFirstRow: false),
            Build(distinct, travelAlongX, reverseRows: false, reverseFirstRow: true),
            Build(distinct, travelAlongX, reverseRows: true, reverseFirstRow: false),
            Build(distinct, travelAlongX, reverseRows: true, reverseFirstRow: true),
        ];

        return candidates
            .OrderBy(route => DistanceSqr(start, route[0]))
            .ThenBy(RouteLength)
            .First();
    }

    private static List<BlockPos> Build(
        IReadOnlyList<BlockPos> targets,
        bool travelAlongX,
        bool reverseRows,
        bool reverseFirstRow)
    {
        IEnumerable<IGrouping<(int Y, int Row), BlockPos>> rows = targets
            .GroupBy(p => (Y: p.Y, Row: travelAlongX ? p.Z : p.X))
            .OrderBy(group => group.Key.Y)
            .ThenBy(group => group.Key.Row);
        if (reverseRows)
            rows = rows.Reverse();

        var route = new List<BlockPos>(targets.Count);
        int rowIndex = 0;
        foreach (IGrouping<(int Y, int Row), BlockPos> row in rows)
        {
            bool reverse = reverseFirstRow ^ (rowIndex % 2 != 0);
            IEnumerable<BlockPos> ordered = travelAlongX
                ? row.OrderBy(p => p.X).ThenBy(p => p.Z)
                : row.OrderBy(p => p.Z).ThenBy(p => p.X);
            route.AddRange(reverse ? ordered.Reverse() : ordered);
            rowIndex++;
        }

        return route;
    }

    private static long RouteLength(IReadOnlyList<BlockPos> route)
    {
        long total = 0;
        for (int i = 1; i < route.Count; i++)
            total += Manhattan(route[i - 1], route[i]);

        return total;
    }

    private static long Manhattan(BlockPos first, BlockPos second) =>
        Math.Abs((long)first.X - second.X)
        + Math.Abs((long)first.Y - second.Y)
        + Math.Abs((long)first.Z - second.Z);

    private static long DistanceSqr(BlockPos first, BlockPos second)
    {
        long dx = first.X - second.X;
        long dy = first.Y - second.Y;
        long dz = first.Z - second.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }
}

/// <summary>
/// Produces one pickup waypoint per occupied block.
/// Items in the same block share a pickup radius, so visiting each entity separately repeats the walk and may target an entity that has already merged.
/// </summary>
internal static class FarmDropTraversal
{
    internal sealed record EntityTarget(int Id, Vec3d Position);

    internal sealed record Waypoint(BlockPos Block, Vec3d Position, IReadOnlyList<int> EntityIds);

    internal static IReadOnlyList<Vec3d> Order(IEnumerable<Vec3d> targets, Vec3d start)
    {
        var byBlock = new Dictionary<BlockPos, Vec3d>();
        foreach (Vec3d target in targets)
        {
            BlockPos block = BlockPos.Containing(target);
            if (!byBlock.ContainsKey(block))
                byBlock.Add(block, target);
        }

        List<List<BlockPos>> clusters = BuildClusters(byBlock.Keys);
        BlockPos current = BlockPos.Containing(start);
        var ordered = new List<Vec3d>(byBlock.Count);
        foreach (List<BlockPos> cluster in clusters
                     .OrderByDescending(cluster => cluster.Count)
                     .ThenBy(cluster => DistanceSqr(current, ClusterCenter(cluster))))
        {
            IReadOnlyList<BlockPos> route = FarmTraversal.Order(cluster, current);
            ordered.AddRange(route.Select(block => byBlock[block]));
            current = route[^1];
        }

        return ordered;
    }

    internal static IReadOnlyList<Waypoint> Order(IEnumerable<EntityTarget> targets, Vec3d start)
    {
        Dictionary<BlockPos, List<EntityTarget>> grouped = targets
            .GroupBy(target => BlockPos.Containing(target.Position))
            .ToDictionary(group => group.Key, group => group.OrderBy(target => target.Id).ToList());
        List<List<BlockPos>> clusters = BuildClusters(grouped.Keys);
        BlockPos current = BlockPos.Containing(start);
        var ordered = new List<Waypoint>(grouped.Count);
        foreach (List<BlockPos> cluster in clusters
                     .OrderByDescending(cluster => cluster.Sum(block => grouped[block].Count))
                     .ThenBy(cluster => DistanceSqr(current, ClusterCenter(cluster))))
        {
            IReadOnlyList<BlockPos> route = FarmTraversal.Order(cluster, current);
            foreach (BlockPos block in route)
            {
                List<EntityTarget> entities = grouped[block];
                ordered.Add(new Waypoint(block, entities[0].Position, entities.Select(entity => entity.Id).ToArray()));
            }

            current = route[^1];
        }

        return ordered;
    }

    private static List<List<BlockPos>> BuildClusters(IEnumerable<BlockPos> targets)
    {
        var pending = new HashSet<BlockPos>(targets);
        var result = new List<List<BlockPos>>();
        while (pending.Count > 0)
        {
            BlockPos seed = pending.First();
            pending.Remove(seed);
            var cluster = new List<BlockPos> { seed };
            var frontier = new Queue<BlockPos>();
            frontier.Enqueue(seed);
            while (frontier.Count > 0)
            {
                BlockPos current = frontier.Dequeue();
                foreach (BlockPos nearby in pending.Where(candidate => HorizontalDistanceSqr(current, candidate) <= 9).ToArray())
                {
                    pending.Remove(nearby);
                    cluster.Add(nearby);
                    frontier.Enqueue(nearby);
                }
            }

            result.Add(cluster);
        }

        return result;
    }

    private static BlockPos ClusterCenter(IReadOnlyList<BlockPos> cluster) => new(
        (int)Math.Round(cluster.Average(position => position.X)),
        (int)Math.Round(cluster.Average(position => position.Y)),
        (int)Math.Round(cluster.Average(position => position.Z)));

    private static long HorizontalDistanceSqr(BlockPos first, BlockPos second)
    {
        long dx = first.X - second.X;
        long dz = first.Z - second.Z;
        return (dx * dx) + (dz * dz);
    }

    private static long DistanceSqr(BlockPos first, BlockPos second)
    {
        long dx = first.X - second.X;
        long dy = first.Y - second.Y;
        long dz = first.Z - second.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }
}

/// <summary>The outcome of one target pass.</summary>
internal sealed record FarmerPassResult(int Succeeded, int Skipped, int Failed, bool StockExhausted);

/// <summary>
/// Runs one snapshot of targets, re-checking stock every second target.
/// A target that fails to prepare or to act is counted and skipped, so it cannot stop the rest of the pass.
/// </summary>
internal static class FarmerTargetPass
{
    internal static async Task<FarmerPassResult> RunAsync<T>(
        IReadOnlyList<T> targets,
        Func<Task<bool>> hasStock,
        Func<T, Task<bool>> prepare,
        Func<T, Task<bool>> actAndVerify,
        Func<bool> keepRunning)
    {
        int succeeded = 0;
        int skipped = 0;
        int failed = 0;
        bool stockExhausted = false;

        for (int index = 0; index < targets.Count && keepRunning(); index++)
        {
            if (index % 2 == 0 && !await hasStock().ConfigureAwait(false))
            {
                stockExhausted = true;
                break;
            }

            T target = targets[index];
            if (!await prepare(target).ConfigureAwait(false))
            {
                skipped++;
                continue;
            }

            if (await actAndVerify(target).ConfigureAwait(false))
                succeeded++;
            else
                failed++;
        }

        return new FarmerPassResult(succeeded, skipped, failed, stockExhausted);
    }
}

/// <summary>The phase barriers of one Farmer cycle.</summary>
internal enum FarmState
{
    Harvesting,
    CollectingAfterHarvest,
    Planting,
    BoneMealing,
}

internal static class FarmerLifecycle
{
    internal static FarmState Next(FarmState completed) => completed switch
    {
        FarmState.Harvesting => FarmState.CollectingAfterHarvest,
        FarmState.CollectingAfterHarvest => FarmState.Planting,
        FarmState.Planting => FarmState.BoneMealing,
        FarmState.BoneMealing => FarmState.Harvesting,
        _ => throw new ArgumentOutOfRangeException(nameof(completed)),
    };
}

internal enum FarmerStorageKind
{
    Bonemeal,
    Seeds,
    Crops,
}

internal static class StorageCoordinates
{
    internal static bool TryParse(string? value, out BlockPos position)
    {
        position = default;
        string[] parts = (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3
            || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out int x)
            || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out int y)
            || !int.TryParse(parts[2], CultureInfo.InvariantCulture, out int z))
            return false;

        position = new BlockPos(x, y, z);
        return true;
    }

    internal static string Format(BlockPos position) =>
        string.Format(CultureInfo.InvariantCulture, "{0} {1} {2}", position.X, position.Y, position.Z);
}

/// <summary>The settings for Farmer, saved to settings.toml.</summary>
public sealed class FarmerSettings : IValidatablePluginSettings
{
    /// <summary>Master switch.</summary>
    [TomlInlineComment("$settings.enabled$")]
    public bool Enabled { get; set; }

    /// <summary>Seconds between cycles; clamped to at least 1.0.</summary>
    [TomlInlineComment("$settings.delay_between_tasks$")]
    public double Delay_Between_Tasks { get; set; } = 1.0;

    /// <summary>Writes per-target diagnostics to the client log while Farmer runs.</summary>
    [TomlInlineComment("$settings.verbose_debug$")]
    public bool Verbose_Debug { get; set; } = true;

    [TomlInlineComment("$settings.storage_search_margin$")]
    public int Storage_Search_Margin { get; set; } = 32;

    [TomlInlineComment("$settings.bonemeal_chest_name$")]
    public string Bonemeal_Chest_Name { get; set; } = "Bonemeal";

    [TomlInlineComment("$settings.seeds_chest_name$")]
    public string Seeds_Chest_Name { get; set; } = "Seeds";

    [TomlInlineComment("$settings.crops_chest_name$")]
    public string Crops_Chest_Name { get; set; } = "Crops";

    [TomlInlineComment("$settings.bonemeal_chest_coordinates$")]
    public string Bonemeal_Chest_Coordinates { get; set; } = string.Empty;
    [TomlInlineComment("$settings.seeds_chest_coordinates$")]
    public string Seeds_Chest_Coordinates { get; set; } = string.Empty;
    [TomlInlineComment("$settings.crops_chest_coordinates$")]
    public string Crops_Chest_Coordinates { get; set; } = string.Empty;

    /// <summary>Normalizes on load and reload.</summary>
    public void Validate()
    {
        if (Delay_Between_Tasks < 1.0)
            Delay_Between_Tasks = 1.0;

        Storage_Search_Margin = Math.Clamp(Storage_Search_Margin, 1, 128);
        Bonemeal_Chest_Name = Bonemeal_Chest_Name.Trim();
        Seeds_Chest_Name = Seeds_Chest_Name.Trim();
        Crops_Chest_Name = Crops_Chest_Name.Trim();
        Bonemeal_Chest_Coordinates = NormalizeCoordinates(Bonemeal_Chest_Coordinates);
        Seeds_Chest_Coordinates = NormalizeCoordinates(Seeds_Chest_Coordinates);
        Crops_Chest_Coordinates = NormalizeCoordinates(Crops_Chest_Coordinates);
    }

    private static string NormalizeCoordinates(string? value) =>
        StorageCoordinates.TryParse(value, out BlockPos position)
            ? StorageCoordinates.Format(position)
            : string.Empty;
}

/// <summary>The Farmer plugin entry point.</summary>
public sealed class FarmerPlugin : IPlugin
{
    private sealed record CocoaSite(BlockPos Pod, BlockPos Support, Direction Facing);

    private sealed record CocoaPlantResult(int Planted, int Invalid, int StockExhausted, int RemainingValid);

    private readonly object _gate = new();
    private FarmerSettings _settings = new();
    private PluginSettings? _settingsStore;
    private ILogger? _logger;
    private IPluginLocalization? _strings;
    private ISessionScope? _session;
    private IMovementLease? _lease;

    private volatile bool _running;
    private FarmState _state = FarmState.Harvesting;
    private CropType _crop = CropType.Wheat;
    private FarmRegion _region = FarmRegion.Around(new BlockPos(0, 0, 0), FarmRules.DefaultRadius);
    private IReadOnlyList<BlockPos>? _farmSites;
    private readonly List<CocoaSite> _pendingCocoa = [];
    private readonly Dictionary<BlockPos, CropType> _stemAssignments = [];
    private readonly HashSet<int> _unreachableDropIds = [];
    private readonly HashSet<(FarmerStorageKind Kind, BlockPos Position)> _unusableStorage = [];

    private int DelayMilliseconds => (int)Math.Round(_settings.Delay_Between_Tasks * 1000);

    /// <inheritdoc/>
    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "farmer";
        descriptor.Version = "2.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
        descriptor.WithSettings<FarmerSettings>();
    }

    /// <inheritdoc/>
    public Task ActivateAsync(PluginContext context)
    {
        _settings = context.Settings.Load<FarmerSettings>();
        _settingsStore = context.Settings;
        _logger = context.Logger;
        _strings = context.Strings;

        context.Commands.Register(new FarmerCommand(this, context.Strings));

        context.SessionStarted += (_, e) =>
        {
            lock (_gate)
                _session = e.Session;

            // Farmer never auto-starts: a new session begins stopped, holding no lease.
            Stop();
            e.Session.Events.Subscribe<Died>(_ => Stop());
        };

        context.SessionEnded += (_, _) =>
        {
            Stop();
            lock (_gate)
                _session = null;
        };

        return Task.CompletedTask;
    }

    internal bool IsRunning => _running;

    /// <summary>The started-on-an-area line, which both the plugin and its command report.</summary>
    internal static string Area(IPluginLocalization strings, CropType crop, FarmRegion region)
        => strings.Format(
            "started_area",
            crop.ToString(),
            region.Min.X, region.Min.Y, region.Min.Z,
            region.Max.X, region.Max.Y, region.Max.Z);

    /// <summary>Starts radius mode around the player's current block position.</summary>
    internal string? StartRadius(CropType crop, int radius, IPluginLocalization strings)
    {
        ISessionScope? session;
        lock (_gate)
            session = _session;

        return session is null
            ? strings.Get("not_connected")
            : Start(crop, FarmRegion.Around(BlockPos.Containing(session.State.Self.Position), radius), strings);
    }

    /// <summary>Starts farming. Returns null on success, or the reason it could not start.</summary>
    internal string? Start(CropType crop, FarmRegion region, IPluginLocalization strings)
    {
        ISessionScope? session;
        lock (_gate)
            session = _session;

        if (session is null)
            return strings.Get("not_connected");

        if (_running)
            return strings.Get("already_running");

        Umpk.Protocol.Java.JavaVersion? serverVersion = session.Client.Session?.Version;
        if (serverVersion is not null && !FarmRules.IsAvailableOnProtocol(crop, serverVersion.Version.Protocol))
        {
            return strings.Format(
                "crop_unavailable",
                crop.ToString(),
                serverVersion.Version.Name,
                FarmRules.FirstSupportedVersionName(crop));
        }

        if (!session.State.Features.Terrain)
            return strings.Get("needs_terrain");

        if (!session.State.Features.Inventory)
            return strings.Get("needs_inventory");

        if (!session.State.Features.Entities)
            return strings.Get("needs_entities");

        if (!session.State.Features.Physics)
            return strings.Get("needs_physics");

        IMovementLease? lease = session.TryAcquireMovement("Farmer");
        if (lease is null)
            return strings.Format("movement_lock_held", strings.Get("another_plugin"));

        lock (_gate)
        {
            _lease = lease;
            _crop = crop;
            _region = region;
            _state = FarmState.Harvesting;
            _farmSites = null;
            _pendingCocoa.Clear();
            _stemAssignments.Clear();
            _unreachableDropIds.Clear();
            _unusableStorage.Clear();
        }

        _running = true;
        _logger?.LogInformation(
            "{Message}",
            region.Kind == FarmRegionKind.Radius
                ? strings.Format("started_radius", crop.ToString(), region.Radius)
                : Area(strings, crop, region));
        _ = session.Scheduler.RunOffLoop(() => MainProcessAsync(session), session.Detached);
        return null;
    }

    /// <summary>Stops farming and releases the movement lease.</summary>
    internal void Stop()
    {
        _running = false;
        IMovementLease? lease;
        lock (_gate)
        {
            lease = _lease;
            _lease = null;
            _stemAssignments.Clear();
            _unreachableDropIds.Clear();
        }

        lease?.Dispose();
    }

    private void Debug(string message)
    {
        if (_settings.Verbose_Debug)
            _logger?.LogInformation("{Message}", _strings?.Format("debug_line", message));
        else
            _logger?.LogDebug("{Message}", message);
    }

    internal string StorageSummary(IPluginLocalization strings) => strings.Format(
        "storage_summary",
        _settings.Bonemeal_Chest_Name, EmptyAsSearch(strings, _settings.Bonemeal_Chest_Coordinates),
        _settings.Seeds_Chest_Name, EmptyAsSearch(strings, _settings.Seeds_Chest_Coordinates),
        _settings.Crops_Chest_Name, EmptyAsSearch(strings, _settings.Crops_Chest_Coordinates),
        _settings.Storage_Search_Margin);

    internal string? SetStorageName(FarmerStorageKind kind, string name, IPluginLocalization strings)
    {
        if (string.IsNullOrWhiteSpace(name)) return strings.Get("storage_label_empty");
        SetStorage(kind, name.Trim(), coordinates: null);
        return null;
    }

    internal string? SetStoragePosition(FarmerStorageKind kind, string value, IPluginLocalization strings)
    {
        if (!StorageCoordinates.TryParse(value, out BlockPos position)) return strings.Get("storage_coordinates_invalid");
        SetStorage(kind, name: null, StorageCoordinates.Format(position));
        return null;
    }

    internal void ClearStoragePosition(FarmerStorageKind kind) => SetStorage(kind, name: null, string.Empty);

    internal string? SetStorageMargin(int margin, IPluginLocalization strings)
    {
        if (margin is < 1 or > 128) return strings.Get("storage_margin_range");
        _settings.Storage_Search_Margin = margin;
        SaveSettings();
        return null;
    }

    private void SetStorage(FarmerStorageKind kind, string? name, string? coordinates)
    {
        switch (kind)
        {
            case FarmerStorageKind.Bonemeal:
                if (name is not null) _settings.Bonemeal_Chest_Name = name;
                if (coordinates is not null) _settings.Bonemeal_Chest_Coordinates = coordinates;
                break;
            case FarmerStorageKind.Seeds:
                if (name is not null) _settings.Seeds_Chest_Name = name;
                if (coordinates is not null) _settings.Seeds_Chest_Coordinates = coordinates;
                break;
            case FarmerStorageKind.Crops:
                if (name is not null) _settings.Crops_Chest_Name = name;
                if (coordinates is not null) _settings.Crops_Chest_Coordinates = coordinates;
                break;
        }
        SaveSettings();
    }

    private void SaveSettings()
    {
        _settings.Validate();
        _settingsStore?.Save(_settings);
    }

    private static string EmptyAsSearch(IPluginLocalization strings, string value)
        => string.IsNullOrEmpty(value) ? strings.Get("storage_unset") : value;

    private async Task MainProcessAsync(ISessionScope session)
    {
        try
        {
            while (_running && !session.Detached.IsCancellationRequested)
            {
                Debug("Farmer: beginning loop iteration and checking the farm map.");
                await EnsureFarmMapAsync(session).ConfigureAwait(false);
                if (!_running)
                    break;

                FarmState state;
                lock (_gate)
                    state = _state;

                Debug($"Farmer: entering phase {state}.");

                switch (state)
                {
                    case FarmState.Harvesting:
                        await HarvestAsync(session).ConfigureAwait(false);
                        break;

                    case FarmState.CollectingAfterHarvest:
                        await CollectAsync(session, FarmState.CollectingAfterHarvest).ConfigureAwait(false);
                        break;

                    case FarmState.Planting:
                        await PlantAsync(session).ConfigureAwait(false);
                        break;

                    case FarmState.BoneMealing:
                        await BoneMealAsync(session).ConfigureAwait(false);
                        break;
                }

                if (!_running)
                    break;

                Debug($"Farmer: phase complete; waiting {DelayMilliseconds} ms before the next phase.");
                await Task.Delay(DelayMilliseconds, session.Detached).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Session ended under us; fall through to cleanup.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "{Message}", _strings?.Get("loop_error"));
        }
        finally
        {
            Stop();
            _logger?.LogInformation("{Message}", _strings?.Get("stopped"));
        }
    }

    private void SetState(FarmState state)
    {
        FarmState previous;
        lock (_gate)
        {
            previous = _state;
            _state = state;
        }

        Debug($"Farmer: state transition {previous} -> {state}.");
    }

    private void AdvanceState(FarmState completed) => SetState(FarmerLifecycle.Next(completed));

    private async Task PlantAsync(ISessionScope session)
    {
        Debug("Farmer: looking for farmland.");
        CropProfile profile = FarmRules.Profile(_crop);
        if (profile.Mode == FarmMode.PreserveVerticalBase)
        {
            await StoreSurplusAsync(session, 0).ConfigureAwait(false);
            AdvanceState(FarmState.Planting);
            return;
        }

        if (profile.Mode == FarmMode.CocoaPod)
        {
            int pending;
            lock (_gate)
                pending = _pendingCocoa.Count;
            await EnsurePlantingStockAsync(session, pending).ConfigureAwait(false);
            CocoaPlantResult result = await PlantPendingCocoaAsync(session).ConfigureAwait(false);
            if (result.RemainingValid > 0)
            {
                Debug($"Farmer: cocoa planting barrier retains {result.RemainingValid} valid face(s); planted={result.Planted}, invalid={result.Invalid}, stock-exhausted={result.StockExhausted}.");
                return;
            }

            await StoreSurplusAsync(session, 0).ConfigureAwait(false);
            AdvanceState(FarmState.Planting);
            return;
        }

        if (_crop == CropType.MelonsAndPumpkins)
        {
            int remaining = await PlantMixedStemAsync(session).ConfigureAwait(false);
            if (remaining > 0)
            {
                Debug($"Farmer: mixed-stem planting barrier retains {remaining} valid empty site(s).");
                return;
            }

            AdvanceState(FarmState.Planting);
            return;
        }

        string soil = FarmRules.SoilBlock(_crop);
        List<BlockPos> soilBlocks = await ReadMappedTargetsAsync(
            session,
            (state, world, position) =>
                string.Equals(FarmRules.BarePath(state.Block.Id.ToString()), soil, StringComparison.Ordinal)
                && world.GetBlock(position.Above()).IsAir).ConfigureAwait(false);

        Debug($"Farmer: planting scan found {soilBlocks.Count} empty mapped site(s).");

        if (soilBlocks.Count == 0)
        {
            Debug("Farmer: no mapped planting sites are empty.");
            await StoreSurplusAsync(session, 0).ConfigureAwait(false);
            AdvanceState(FarmState.Planting);
            return;
        }

        await EnsurePlantingStockAsync(session, soilBlocks.Count).ConfigureAwait(false);
        await StoreSurplusAsync(session, soilBlocks.Count).ConfigureAwait(false);

        if (!await SwitchToCropItemAsync(session, _crop).ConfigureAwait(false))
        {
            Debug("Farmer: no planting item after storage preparation, skipping the planting pass.");
            AdvanceState(FarmState.Planting);
            return;
        }

        FarmerPassResult pass = await FarmerTargetPass.RunAsync(
            soilBlocks,
            () => HasCropItemAsync(session, _crop),
            async position =>
            {
                Debug($"Farmer: validating planting site {position.X} {position.Y} {position.Z}.");
                bool ready = await IsPlantTargetValidAsync(session, position, soil).ConfigureAwait(false)
                    && await WalkToAsync(session, position).ConfigureAwait(false)
                    && await IsPlantTargetValidAsync(session, position, soil).ConfigureAwait(false)
                    && await SwitchToCropItemAsync(session, _crop).ConfigureAwait(false);
                Debug($"Farmer: planting site {position.X} {position.Y} {position.Z} is {(ready ? "ready" : "skipped")}.");
                return ready;
            },
            async position =>
            {
                Debug($"Farmer: sending planting interaction at {position.X} {position.Y} {position.Z}.");
                await session.Actions.Movement.LookAtAsync(position.Center, session.Detached).ConfigureAwait(false);
                await session.Actions.Interaction
                    .PlaceBlockAsync(position, Direction.Up, new Vec3d(0.5, 1.0, 0.5), Hand.Main, false, session.Detached)
                    .ConfigureAwait(false);
                bool planted = await WaitForBlockAsync(
                    session,
                    position.Above(),
                    state => FarmRules.IsHarvestBlock(_crop, state.Block.Id.ToString())
                        || string.Equals(
                            FarmRules.BarePath(state.Block.Id.ToString()),
                            profile.GrowthBlock,
                            StringComparison.Ordinal),
                    1200).ConfigureAwait(false);
                if (!planted)
                    Debug("Farmer: planting request did not produce the expected crop.");
                else
                    Debug($"Farmer: planting confirmed at {position.X} {position.Y} {position.Z}.");

                return planted;
            },
            () => _running).ConfigureAwait(false);

        int planted = pass.Succeeded;
        if (pass.StockExhausted)
            Debug("Farmer: out of seeds.");

        if (planted > 0)
            _logger?.LogInformation("{Message}", _strings?.Format("planted", planted));

        List<BlockPos> remainingEmpty = await ReadMappedTargetsAsync(
            session,
            (state, world, position) =>
                string.Equals(FarmRules.BarePath(state.Block.Id.ToString()), soil, StringComparison.Ordinal)
                && world.GetBlock(position.Above()).IsAir).ConfigureAwait(false);
        if (remainingEmpty.Count > 0)
        {
            Debug($"Farmer: planting barrier retains {remainingEmpty.Count} valid empty site(s); retrying before bone meal.");
            return;
        }

        AdvanceState(FarmState.Planting);
    }

    private async Task<int> PlantMixedStemAsync(ISessionScope session)
    {
        List<BlockPos> soilBlocks = await ReadMappedTargetsAsync(
            session,
            (state, world, position) => FarmRules.BarePath(state.Block.Id.ToString()) == "farmland"
                && world.GetBlock(position.Above()).IsAir).ConfigureAwait(false);
        Dictionary<CropType, List<BlockPos>> byCrop = soilBlocks
            .GroupBy(StemAssignment)
            .ToDictionary(group => group.Key, group => group.ToList());

        foreach (StemVariant variant in FarmRules.StemVariants(_crop))
        {
            int required = byCrop.GetValueOrDefault(variant.Crop)?.Count ?? 0;
            await EnsurePlantingStockAsync(session, variant.Crop, required).ConfigureAwait(false);
        }

        foreach (StemVariant variant in FarmRules.StemVariants(_crop))
        {
            int reserve = byCrop.GetValueOrDefault(variant.Crop)?.Count ?? 0;
            await StoreSurplusAsync(session, variant.Crop, reserve).ConfigureAwait(false);
        }

        int planted = 0;
        foreach (StemVariant variant in FarmRules.StemVariants(_crop))
        {
            if (!byCrop.TryGetValue(variant.Crop, out List<BlockPos>? targets) || targets.Count == 0)
                continue;

            FarmerPassResult pass = await FarmerTargetPass.RunAsync(
                targets,
                () => HasCropItemAsync(session, variant.Crop),
                async position => await IsPlantTargetValidAsync(session, position, "farmland").ConfigureAwait(false)
                    && await WalkToAsync(session, position).ConfigureAwait(false)
                    && await IsPlantTargetValidAsync(session, position, "farmland").ConfigureAwait(false)
                    && await SwitchToCropItemAsync(session, variant.Crop).ConfigureAwait(false),
                async position =>
                {
                    await session.Actions.Movement.LookAtAsync(position.Center, session.Detached).ConfigureAwait(false);
                    await session.Actions.Interaction.PlaceBlockAsync(
                        position, Direction.Up, new Vec3d(0.5, 1.0, 0.5), Hand.Main, false, session.Detached).ConfigureAwait(false);
                    return await WaitForBlockAsync(session, position.Above(), state =>
                        FarmRules.IsStemGrowthBlock(variant.Crop, state.Block.Id.ToString()), 1200).ConfigureAwait(false);
                },
                () => _running).ConfigureAwait(false);
            planted += pass.Succeeded;
            if (pass.StockExhausted)
                Debug($"Farmer: no {variant.SeedItem} remains for assigned mixed-farm sites.");
        }

        if (planted > 0)
            _logger?.LogInformation("{Message}", _strings?.Format("planted", planted));

        return (await ReadMappedTargetsAsync(
            session,
            (state, world, position) => FarmRules.BarePath(state.Block.Id.ToString()) == "farmland"
                && world.GetBlock(position.Above()).IsAir).ConfigureAwait(false)).Count;
    }

    private async Task HarvestAsync(ISessionScope session, bool afterMapRefresh = false)
    {
        Debug("Farmer: searching for crops to break.");
        CropProfile profile = FarmRules.Profile(_crop);
        string block = _crop == CropType.MelonsAndPumpkins ? "melon or pumpkin" : profile.HarvestBlock;

        List<BlockPos> crops = await ReadHarvestTargetsAsync(session).ConfigureAwait(false);
        Debug($"Farmer: harvest scan found {crops.Count} mature target(s).");

        if (crops.Count == 0)
        {
            Debug("Farmer: the harvest pass found no mature crops.");
            AdvanceState(FarmState.Harvesting);
            return;
        }

        if (FarmRules.IsStemCrop(_crop))
        {
            // Melons and pumpkins break much faster with an axe, so take the best tier available.
            foreach (string axe in FarmRules.HarvestAxes)
            {
                if (await SwitchToItemAsync(session, axe).ConfigureAwait(false))
                    break;
            }
        }

        int harvested = 0;
        foreach (BlockPos position in crops)
        {
            if (!_running)
                break;

            CocoaSite? cocoa = null;
            Debug($"Farmer: validating harvest target {position.X} {position.Y} {position.Z}.");
            if (!await ReadHarvestTargetAsync(session, position).ConfigureAwait(false)
                || !await WalkToAsync(session, position).ConfigureAwait(false)
                || !await ReadHarvestTargetAsync(session, position).ConfigureAwait(false))
            {
                Debug($"Farmer: skipped harvest target {position.X} {position.Y} {position.Z}; it became invalid or was unreachable.");
                continue;
            }

            if (profile.Mode == FarmMode.CocoaPod)
            {
                cocoa = await ReadCocoaSiteAsync(session, position).ConfigureAwait(false);
                if (cocoa is null)
                    continue;
            }

            BlockPos? preservedBase = profile.Mode == FarmMode.PreserveVerticalBase ? position.Below() : null;
            string harvestedBlock = _crop == CropType.MelonsAndPumpkins
                ? await session.Client.InvokeAsync(
                    c => FarmRules.BarePath(c.State.World.GetBlock(position).Block.Id.ToString()),
                    session.Detached).ConfigureAwait(false)
                : block;
            if (await DigAsync(session, position, preservedBase).ConfigureAwait(false))
            {
                harvested++;
                Debug($"Farmer: harvest confirmed at {position.X} {position.Y} {position.Z}.");
                if (cocoa is not null)
                {
                    lock (_gate)
                    {
                        _pendingCocoa.RemoveAll(site => site.Pod == cocoa.Pod);
                        _pendingCocoa.Add(cocoa);
                    }
                }

                // A retained console line per block throttles a large farm, so report the first one and then every sixteenth.
                // The total is logged below.
                if (harvested == 1 || harvested % 16 == 0)
                {
                    _logger?.LogInformation(
                        "{Message}",
                        _strings?.Format(
                            "harvested_one", harvestedBlock, position.X, position.Y, position.Z, harvested));
                }
            }
            else
                Debug($"Farmer: harvest was not confirmed at {position.X} {position.Y} {position.Z}.");
        }

        if (harvested > 0)
            _logger?.LogInformation("{Message}", _strings?.Format("harvested", harvested));

        // The first map may have been built while the farm's chunks were still arriving.
        // Rebuild it once before leaving the harvest barrier, so a partial map cannot leave Farmer working one corner of a field that is now fully loaded.
        if (!afterMapRefresh && _running && harvested > 0)
        {
            lock (_gate)
                _farmSites = null;

            await EnsureFarmMapAsync(session).ConfigureAwait(false);
            List<BlockPos> remaining = await ReadHarvestTargetsAsync(session).ConfigureAwait(false);
            if (remaining.Count > 0)
            {
                Debug($"Farmer: refreshed the farm map and found {remaining.Count} additional mature target(s); completing the harvest barrier.");
                await HarvestAsync(session, afterMapRefresh: true).ConfigureAwait(false);
                return;
            }
        }

        AdvanceState(FarmState.Harvesting);
    }

    private async Task BoneMealAsync(ISessionScope session)
    {
        if (!FarmRules.CanBoneMeal(_crop))
        {
            AdvanceState(FarmState.BoneMealing);
            return;
        }

        List<BlockPos> crops = await ReadBoneMealTargetsAsync(session).ConfigureAwait(false);
        if (crops.Count == 0)
        {
            Debug("Farmer: nothing to bone meal; returning to the harvest barrier.");
            AdvanceState(FarmState.BoneMealing);
            return;
        }

        int carried = await RefillBoneMealAsync(session, crops.Count).ConfigureAwait(false);

        if (!await SwitchToItemAsync(session, "bone_meal").ConfigureAwait(false))
        {
            Debug("Farmer: no bone meal is available; returning to the harvest barrier.");
            AdvanceState(FarmState.BoneMealing);
            return;
        }

        int fed = 0;
        for (int index = 0; index < crops.Count; index++)
        {
            if (!_running)
                break;

            BlockPos position = crops[index];
            if (!await HasItemAsync(session, "bone_meal").ConfigureAwait(false))
            {
                int remainingTargets = crops.Count - index;
                carried = await RefillBoneMealAsync(session, remainingTargets).ConfigureAwait(false);
                if (carried == 0)
                {
                    Debug("Farmer: bone meal storage is empty; ending this bone-meal pass.");
                    break;
                }
            }

            if (!await ReadBoneMealTargetAsync(session, position).ConfigureAwait(false)
                || !await WalkToAsync(session, position).ConfigureAwait(false)
                || !await ReadBoneMealTargetAsync(session, position).ConfigureAwait(false))
                continue;

            if (!await SwitchToItemAsync(session, "bone_meal").ConfigureAwait(false))
            {
                carried = await RefillBoneMealAsync(session, crops.Count - index).ConfigureAwait(false);
                if (carried == 0 || !await SwitchToItemAsync(session, "bone_meal").ConfigureAwait(false))
                {
                    Debug("Farmer: bone meal disappeared before use and storage cannot refill it.");
                    break;
                }
            }

            await session.Actions.Movement.LookAtAsync(position.Center, session.Detached).ConfigureAwait(false);
            int beforeAge = await session.Client.InvokeAsync(
                c => ReadAge(c.State.World.GetBlock(position), _crop),
                session.Detached).ConfigureAwait(false);

            // Vanilla growth is random, so spend a small fixed budget rather than waiting for an effect.
            int attempts = FarmRules.BoneMealUsesPerTarget(_crop);
            for (int i = 0; i < attempts; i++)
            {
                await session.Actions.Interaction
                    .PlaceBlockAsync(position, Direction.Up, new Vec3d(0.5, 1.0, 0.5), Hand.Main, false, session.Detached)
                    .ConfigureAwait(false);
            }

            if (await WaitForBlockAsync(
                    session,
                    position,
                    state => (FarmRules.IsStemCrop(_crop)
                                 ? FarmRules.IsStemGrowthBlock(_crop, state.Block.Id.ToString())
                                 : string.Equals(FarmRules.BarePath(state.Block.Id.ToString()), FarmRules.BoneMealBlock(_crop), StringComparison.Ordinal))
                        && ReadAge(state, _crop) > beforeAge,
                    1200).ConfigureAwait(false))
                fed++;
        }

        if (fed > 0)
            _logger?.LogInformation("{Message}", _strings?.Format("bone_mealed", fed));

        AdvanceState(FarmState.BoneMealing);
    }

    /// <summary>
    /// Tops up the carried bone meal for the remaining work, never carrying more than five stacks.
    /// It is called again whenever the supply runs out, so a field is not left half bone-mealed.
    /// </summary>
    private async Task<int> RefillBoneMealAsync(ISessionScope session, int immatureTargets)
    {
        int target = FarmRules.BoneMealRestockTarget(_crop, immatureTargets);
        int carried = await CountItemsAsync(session, stack => Matches(stack, "bone_meal")).ConfigureAwait(false);
        if (carried >= target || target == 0)
            return carried;

        int moved = await WithdrawAsync(session, FarmerStorageKind.Bonemeal, stack => Matches(stack, "bone_meal"), target).ConfigureAwait(false);
        carried = await CountItemsAsync(session, stack => Matches(stack, "bone_meal")).ConfigureAwait(false);
        Debug($"Farmer: bone meal pass has {immatureTargets} remaining target(s), budgets {target} item(s), and storage supplied {moved}; now carrying {carried}.");
        return carried;
    }

    private async Task CollectAsync(ISessionScope session, FarmState completed)
    {
        Debug("Farmer: searching for items to collect.");
        int collected = 0;
        int previousRemaining = int.MaxValue;
        for (int pass = 0; pass < 4 && _running; pass++)
        {
            IReadOnlyList<FarmDropTraversal.Waypoint> drops = await FindDroppedItemsAsync(session).ConfigureAwait(false);
            Debug($"Farmer: collection sweep {pass + 1}/4 has {drops.Count} pickup waypoint(s).");
            if (drops.Count == 0)
            {
                Debug("Farmer: collection sweep found no in-bounds item entities; advancing to the next phase.");
                break;
            }

            for (int index = 0; index < drops.Count; index++)
            {
                if (!_running)
                {
                    Debug("Farmer: collection was interrupted before the next waypoint.");
                    break;
                }

                FarmDropTraversal.Waypoint waypoint = drops[index];
                List<FarmDropTraversal.EntityTarget> live = await LiveDropTargetsAsync(session, waypoint.EntityIds).ConfigureAwait(false);
                if (live.Count == 0)
                {
                    Debug($"Farmer: collection waypoint {index + 1}/{drops.Count} is obsolete; all entity ids disappeared.");
                    continue;
                }

                Vec3d drop = live[0].Position;
                Debug($"Farmer: collection waypoint {index + 1}/{drops.Count} has {live.Count} live entity id(s) at {drop.X:F2} {drop.Y:F2} {drop.Z:F2}.");
                int before = live.Count;
                bool pickedUp = await WalkToDropAsync(session, live).ConfigureAwait(false);
                int after = (await LiveDropTargetsAsync(session, waypoint.EntityIds).ConfigureAwait(false)).Count;
                collected += before - after;
                Debug($"Farmer: collection waypoint {index + 1}/{drops.Count} {(pickedUp ? "removed live items" : "was not collected")}.");
            }

            int remaining = await CountDroppedItemsAsync(session).ConfigureAwait(false);
            Debug($"Farmer: collection sweep {pass + 1}/4 finished; {remaining} in-bounds item entity(s) remain, previous remaining count was {previousRemaining}.");
            if (remaining == 0 || remaining >= previousRemaining)
            {
                Debug(remaining == 0
                    ? "Farmer: all detected item entities were collected."
                    : "Farmer: item count did not decrease; stopping this collection phase to avoid a navigation loop.");
                break;
            }

            previousRemaining = remaining;
        }

        if (collected > 0)
            _logger?.LogInformation("{Message}", _strings?.Format("collected", collected));

        AdvanceState(completed);
    }

    private async Task<IReadOnlyList<FarmDropTraversal.Waypoint>> FindDroppedItemsAsync(ISessionScope session)
    {
        (Vec3d Self, List<FarmDropTraversal.EntityTarget> Targets) snapshot = await session.Client.InvokeAsync(
            c =>
            {
                if (!c.State.Features.Entities)
                    return (c.State.Self.Position, new List<FarmDropTraversal.EntityTarget>());

                Vec3d self = c.State.Self.Position;
                var targets = new List<FarmDropTraversal.EntityTarget>();
                foreach (Entity entity in c.State.Entities.All)
                {
                    if (_region.Contains(entity.Position, margin: 2.0) && IsDroppedItem(entity) && !IsDropQuarantined(entity.Id))
                        targets.Add(new FarmDropTraversal.EntityTarget(entity.Id, entity.Position));
                }

                return (self, targets);
            },
            session.Detached).ConfigureAwait(false);

        Debug($"Farmer: item scan saw {snapshot.Targets.Count} in-bounds item entity(s) from {snapshot.Self.X:F2} {snapshot.Self.Y:F2} {snapshot.Self.Z:F2}.");

        // Route construction is pure CPU work, so it runs off the session loop.
        // On a large farm it would otherwise hold up entity removals and block updates.
        IReadOnlyList<FarmDropTraversal.Waypoint> route = await Task.Run(
            () => FarmDropTraversal.Order(snapshot.Targets, snapshot.Self),
            session.Detached).ConfigureAwait(false);
        Debug($"Farmer: item route reduced the scan to {route.Count} occupied-block pickup waypoint(s).");
        return route;
    }

    private async Task<int> CountDroppedItemsAsync(ISessionScope session)
    {
        int count = await session.Client.InvokeAsync(
            c =>
            {
                if (!c.State.Features.Entities)
                    return 0;

                return c.State.Entities.All.Count(entity =>
                    _region.Contains(entity.Position, margin: 2.0) && IsDroppedItem(entity) && !IsDropQuarantined(entity.Id));
            },
            session.Detached).ConfigureAwait(false);
        Debug($"Farmer: live item recount reports {count} in-bounds item entity(s).");
        return count;
    }

    private async Task<List<FarmDropTraversal.EntityTarget>> LiveDropTargetsAsync(ISessionScope session, IEnumerable<int> ids) =>
        await session.Client.InvokeAsync(c => ids
            .Distinct()
            .Where(id => !IsDropQuarantined(id))
            .Select(id => c.State.Entities.Get(id))
            .Where(entity => entity is not null && _region.Contains(entity.Position, margin: 2.0) && IsDroppedItem(entity))
            .Select(entity => new FarmDropTraversal.EntityTarget(entity!.Id, entity.Position))
            .ToList(), session.Detached).ConfigureAwait(false);

    private bool IsDropQuarantined(int id)
    {
        lock (_gate)
            return _unreachableDropIds.Contains(id);
    }

    private void QuarantineDrops(IEnumerable<FarmDropTraversal.EntityTarget> targets)
    {
        lock (_gate)
        {
            foreach (FarmDropTraversal.EntityTarget target in targets)
            {
                if (_unreachableDropIds.Add(target.Id))
                    Debug($"Farmer: quarantined unreachable item entity {target.Id} at {target.Position.X:F2} {target.Position.Y:F2} {target.Position.Z:F2} for this run.");
            }
        }
    }

    private bool IsHarvestable(BlockState state, Umpk.Game.World.World world, BlockPos position)
    {
        if (!FarmRules.IsHarvestBlock(_crop, state.Block.Id.ToString()))
            return false;

        return FarmRules.Profile(_crop).Mode switch
        {
            FarmMode.PreserveVerticalBase => FarmRules.IsVerticalHarvestLevel(
                _crop,
                state.Block.Id.ToString(),
                world.GetBlock(position.Below()).Block.Id.ToString(),
                world.GetBlock(position.Below(2)).Block.Id.ToString()),
            FarmMode.CocoaPod => ReadAge(state, _crop) >= FarmRules.MaxAge(_crop)
                && TryReadCocoaFacing(state, out Direction facing)
                && IsJungleWood(world.GetBlock(position.Offset(facing))),
            _ => FarmRules.IsFullyGrown(_crop, ReadAge(state, _crop), isHarvestBlock: true),
        };
    }

    private async Task<bool> ReadHarvestTargetAsync(ISessionScope session, BlockPos position) =>
        await session.Client.InvokeAsync(
            c => IsHarvestable(c.State.World.GetBlock(position), c.State.World, position),
            session.Detached).ConfigureAwait(false);

    private async Task<bool> ReadBoneMealTargetAsync(ISessionScope session, BlockPos position) =>
        await session.Client.InvokeAsync(
            c =>
            {
                BlockState state = c.State.World.GetBlock(position);
                return (FarmRules.IsStemCrop(_crop)
                           ? FarmRules.IsStemGrowthBlock(_crop, state.Block.Id.ToString())
                           : string.Equals(FarmRules.BarePath(state.Block.Id.ToString()), FarmRules.BoneMealBlock(_crop), StringComparison.Ordinal))
                       && !FarmRules.IsFullyGrown(_crop, ReadAge(state, _crop), isHarvestBlock: false);
            },
            session.Detached).ConfigureAwait(false);

    private static async Task<bool> IsPlantTargetValidAsync(ISessionScope session, BlockPos support, string supportId) =>
        await session.Client.InvokeAsync(
            c =>
            {
                Umpk.Game.World.World world = c.State.World;
                return string.Equals(
                           FarmRules.BarePath(world.GetBlock(support).Block.Id.ToString()),
                           supportId,
                           StringComparison.Ordinal)
                       && world.GetBlock(support.Above()).IsAir;
            },
            session.Detached).ConfigureAwait(false);

    private async Task<CocoaSite?> ReadCocoaSiteAsync(ISessionScope session, BlockPos pod) =>
        await session.Client.InvokeAsync(
            c =>
            {
                BlockState state = c.State.World.GetBlock(pod);
                if (!FarmRules.IsHarvestBlock(CropType.Cocoa, state.Block.Id.ToString())
                    || ReadAge(state, CropType.Cocoa) < FarmRules.MaxAge(CropType.Cocoa)
                    || !TryReadCocoaFacing(state, out Direction facing))
                    return null;

                BlockPos support = pod.Offset(facing);
                return IsJungleWood(c.State.World.GetBlock(support))
                    ? new CocoaSite(pod, support, facing)
                    : null;
            },
            session.Detached).ConfigureAwait(false);

    private async Task<CocoaPlantResult> PlantPendingCocoaAsync(ISessionScope session)
    {
        List<CocoaSite> pending;
        lock (_gate)
            pending = [.. _pendingCocoa];

        if (pending.Count == 0)
            return new CocoaPlantResult(0, 0, 0, 0);

        int planted = 0;
        int invalid = 0;
        int stockExhausted = 0;
        foreach (CocoaSite site in pending)
        {
            if (!_running)
                break;

            (bool ValidSupport, bool Air, bool AlreadyPlanted) validity = await session.Client.InvokeAsync(
                c =>
                {
                    Umpk.Game.World.World world = c.State.World;
                    BlockState pod = world.GetBlock(site.Pod);
                    bool planted = FarmRules.IsHarvestBlock(CropType.Cocoa, pod.Block.Id.ToString())
                        && TryReadCocoaFacing(pod, out Direction facing)
                        && facing == site.Facing;
                    return (IsJungleWood(world.GetBlock(site.Support)), pod.IsAir, planted);
                },
                session.Detached).ConfigureAwait(false);

            if (validity.AlreadyPlanted)
            {
                RemovePendingCocoa(site.Pod);
                continue;
            }

            if (!validity.ValidSupport || !validity.Air)
            {
                RemovePendingCocoa(site.Pod);
                invalid++;
                continue;
            }

            bool success = false;
            if (!await SwitchToCropItemAsync(session, CropType.Cocoa).ConfigureAwait(false))
            {
                stockExhausted++;
                break;
            }

            if (await WalkToCocoaSiteAsync(session, site).ConfigureAwait(false))
            {
                await session.Actions.Movement.LookAtAsync(site.Pod.Center, session.Detached).ConfigureAwait(false);
                await session.Actions.Interaction.PlaceBlockAsync(
                    site.Support,
                    site.Facing.Opposite(),
                    new Vec3d(0.5, 0.5, 0.5),
                    Hand.Main,
                    false,
                    session.Detached).ConfigureAwait(false);
                success = await WaitForBlockAsync(
                    session,
                    site.Pod,
                    state => FarmRules.IsHarvestBlock(CropType.Cocoa, state.Block.Id.ToString())
                        && TryReadCocoaFacing(state, out Direction facing)
                        && facing == site.Facing,
                    1200).ConfigureAwait(false);
            }

            if (success)
            {
                RemovePendingCocoa(site.Pod);
                planted++;
            }
        }

        int remaining = await RecountValidPendingCocoaAsync(session).ConfigureAwait(false);
        return new CocoaPlantResult(planted, invalid, stockExhausted, remaining);
    }

    private async Task<int> RecountValidPendingCocoaAsync(ISessionScope session)
    {
        List<CocoaSite> pending;
        lock (_gate)
            pending = [.. _pendingCocoa];

        List<BlockPos> invalid = await session.Client.InvokeAsync(c => pending
            .Where(site => !IsJungleWood(c.State.World.GetBlock(site.Support)) || !c.State.World.GetBlock(site.Pod).IsAir)
            .Select(site => site.Pod)
            .ToList(), session.Detached).ConfigureAwait(false);
        foreach (BlockPos pod in invalid)
            RemovePendingCocoa(pod);

        lock (_gate)
            return _pendingCocoa.Count;
    }

    private async Task<bool> WalkToCocoaSiteAsync(ISessionScope session, CocoaSite site)
    {
        IReadOnlyList<BlockPos> candidates = await session.Client.InvokeAsync(c =>
        {
            Umpk.Game.World.World world = c.State.World;
            return CocoaStances(site)
                .Where(candidate => candidate != site.Pod && candidate != site.Support)
                .Where(candidate => world.GetBlock(candidate).IsAir && world.GetBlock(candidate.Above()).IsAir)
                .Where(candidate => FarmRules.IsWithinInteractionReach(candidate.BottomCenter, site.Support))
                .OrderBy(candidate => candidate.Center.Subtract(c.State.Self.Position).LengthSqr())
                .ThenBy(candidate => candidate.Y).ThenBy(candidate => candidate.Z).ThenBy(candidate => candidate.X)
                .ToArray();
        }, session.Detached).ConfigureAwait(false);

        foreach (BlockPos candidate in candidates)
        {
            if (await NavigateToExactAsync(session, candidate).ConfigureAwait(false)
                && await session.Client.InvokeAsync(c => FarmRules.IsWithinInteractionReach(c.State.Self.Position, site.Support), session.Detached).ConfigureAwait(false))
                return true;
        }

        Debug($"Farmer: no reachable cocoa planting stance for {site.Pod.X} {site.Pod.Y} {site.Pod.Z}.");
        return false;
    }

    internal static IReadOnlyList<BlockPos> CocoaStances(BlockPos pod, Direction facing)
    {
        BlockPos outward = pod.Offset(facing.Opposite());
        Direction left = facing is Direction.North or Direction.South ? Direction.West : Direction.North;
        Direction right = left.Opposite();
        var result = new List<BlockPos>();
        foreach (int y in new[] { pod.Y - 1, pod.Y - 2, pod.Y })
        {
            result.Add(new BlockPos(outward.X, y, outward.Z));
            result.Add(new BlockPos(outward.Offset(left).X, y, outward.Offset(left).Z));
            result.Add(new BlockPos(outward.Offset(right).X, y, outward.Offset(right).Z));
        }

        return result.Distinct().ToArray();
    }

    private static IReadOnlyList<BlockPos> CocoaStances(CocoaSite site) => CocoaStances(site.Pod, site.Facing);

    private void RemovePendingCocoa(BlockPos pod)
    {
        lock (_gate)
            _pendingCocoa.RemoveAll(site => site.Pod == pod);
    }

    private static bool IsJungleWood(BlockState state)
    {
        if (FarmRules.IsJungleWood(state.Block.Id.ToString()))
            return true;

        string id = FarmRules.BarePath(state.Block.Id.ToString());
        if (id is not ("log" or "log2"))
            return false;

        if (state.TryGetProperty("variant", out string variant))
            return string.Equals(variant, "jungle", StringComparison.Ordinal);

        return id == "log" && state.LegacyMeta >= 0 && (state.LegacyMeta & 3) == 3;
    }

    private static bool TryReadCocoaFacing(BlockState state, out Direction direction)
    {
        if (state.TryGetProperty("facing", out string facing))
            return FarmRules.TryParseHorizontalDirection(facing, out direction);

        direction = (state.LegacyMeta & 3) switch
        {
            0 => Direction.South,
            1 => Direction.West,
            2 => Direction.North,
            3 => Direction.East,
            _ => default,
        };
        return state.LegacyMeta >= 0;
    }

    private static bool IsDroppedItem(Entity entity) =>
        FarmRules.IsDroppedItemEntity(entity.Type.Id.ToString());

    private static async Task<bool> WaitForBlockAsync(
        ISessionScope session,
        BlockPos position,
        Func<BlockState, bool> predicate,
        int timeoutMilliseconds)
    {
        long deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            bool matched = await session.Client.InvokeAsync(
                c => predicate(c.State.World.GetBlock(position)),
                session.Detached).ConfigureAwait(false);
            if (matched)
                return true;

            await Task.Delay(50, session.Detached).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Reads a crop block's age: the named property first, then the pre-1.13 block metadata, then the block's state span.
    /// All three are load-bearing.
    /// Named properties only exist from the 1.13 flattening onward, the id and metadata decode is real only before it, and the state span is the version-independent backstop for a crop whose registry data carries neither.
    /// </summary>
    private static int ReadAge(BlockState state, CropType crop)
    {
        if (state.TryGetProperty("age", out string value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int age))
            return age;

        int meta = state.LegacyMeta;
        if (meta >= 0)
            return crop == CropType.Cocoa ? (meta >> 2) & 3 : meta;

        Umpk.Game.Registries.BlockDefinition definition = state.Block.Value;
        if (crop == CropType.Cocoa
            && definition.MaxStateId >= definition.MinStateId
            && state.StateId >= definition.MinStateId
            && state.StateId <= definition.MaxStateId)
            return (state.StateId - definition.MinStateId) / 4;

        return FarmRules.AgeFromState(state.StateId, definition.MinStateId, definition.MaxStateId);
    }

    /// <summary>Builds the farm-site map on first use, and again once it has been cleared for a refresh.</summary>
    private async Task EnsureFarmMapAsync(ISessionScope session)
    {
        lock (_gate)
        {
            if (_farmSites is not null)
                return;
        }

        var sites = new HashSet<BlockPos>();
        long scanned = 0;
        FarmRegion region = _region;
        (int MinY, int MaxY) worldBounds = await session.Client.InvokeAsync(
            c => (c.State.World.Dimension.MinY, c.State.World.Dimension.MinY + c.State.World.Dimension.Height - 1),
            session.Detached).ConfigureAwait(false);
        // An area is usually given at the crop layer, and the support block sits one level below it.
        // Scan that support layer too, so a refresh cannot forget empty farmland or nether-wart sites.
        int scanMinY = Math.Max(region.Min.Y - 1, worldBounds.MinY);
        int scanMaxY = Math.Min(region.Max.Y, worldBounds.MaxY);
        for (long y = scanMinY; y <= scanMaxY && _running; y++)
        {
            int layer = (int)y;
            (List<BlockPos> Sites, int Scanned) found = await session.Client.InvokeAsync(
                c =>
                {
                    var layerSites = new HashSet<BlockPos>();
                    int layerScanned = 0;
                    Umpk.Game.World.World world = c.State.World;
                    for (long z = region.Min.Z; z <= region.Max.Z; z++)
                    {
                        for (long x = region.Min.X; x <= region.Max.X; x++)
                        {
                            var position = new BlockPos((int)x, layer, (int)z);
                            bool supportLayer = layer == region.Min.Y - 1;
                            if (!region.Contains(position) && !supportLayer)
                                continue;

                            layerScanned++;
                            BlockState state = world.GetBlock(position);
                            if (!state.IsAir && TryMapSite(world, position, state, out BlockPos site))
                                layerSites.Add(site);
                        }
                    }

                    return (layerSites.ToList(), layerScanned);
                },
                session.Detached).ConfigureAwait(false);
            sites.UnionWith(found.Sites);
            scanned += found.Scanned;
        }

        if (_crop == CropType.Cocoa)
        {
            lock (_gate)
                sites.UnionWith(_pendingCocoa.Select(site => site.Pod));
        }

        BlockPos self = await CurrentBlockAsync(session).ConfigureAwait(false);
        EnsureStemAssignments(sites);
        IReadOnlyList<BlockPos> ordered = FarmTraversal.Order(sites, self);
        lock (_gate)
            _farmSites = ordered;

        _logger?.LogInformation("{Message}", _strings?.Format("mapped", ordered.Count, scanned));
    }

    /// <summary>Maps a matching world block to the stable site re-read by later phases.</summary>
    private bool TryMapSite(
        Umpk.Game.World.World world,
        BlockPos position,
        BlockState state,
        out BlockPos site)
    {
        CropProfile profile = FarmRules.Profile(_crop);
        string id = FarmRules.BarePath(state.Block.Id.ToString());
        site = position;
        switch (profile.Mode)
        {
            case FarmMode.BreakAndReplant:
                if (string.Equals(id, profile.SupportBlock, StringComparison.Ordinal))
                    return true;

                if (FarmRules.IsHarvestBlock(_crop, id))
                {
                    site = position.Below();
                    return true;
                }

                return false;

            case FarmMode.StemFruit:
                if (string.Equals(id, profile.SupportBlock, StringComparison.Ordinal))
                    return true;

                if (FarmRules.IsStemGrowthBlock(_crop, id))
                {
                    site = position.Below();
                    RememberStemAssignment(site, FarmRules.StemVariantFor(_crop, id)?.Crop);
                    return true;
                }

                return FarmRules.IsStemFruitBlock(_crop, id) && TryResolveStemFruitSupport(world, position, out site);

            case FarmMode.PreserveVerticalBase:
                if (!FarmRules.IsHarvestBlock(_crop, id))
                    return false;

                while (FarmRules.IsHarvestBlock(_crop, world.GetBlock(site.Below()).Block.Id.ToString()))
                    site = site.Below();

                return true;

            case FarmMode.CocoaPod:
                return FarmRules.IsHarvestBlock(_crop, id);

            default:
                return false;
        }
    }

    private bool TryResolveStemFruitSupport(Umpk.Game.World.World world, BlockPos fruit, out BlockPos support)
    {
        foreach (Direction direction in new[] { Direction.North, Direction.South, Direction.West, Direction.East })
        {
            BlockPos stem = fruit.Offset(direction);
            StemVariant? variant = FarmRules.StemVariantFor(_crop, world.GetBlock(stem).Block.Id.ToString());
            if (variant is not null && string.Equals(
                    FarmRules.BarePath(world.GetBlock(stem).Block.Id.ToString()), variant.StemBlock, StringComparison.Ordinal))
            {
                support = stem.Below();
                RememberStemAssignment(support, variant.Crop);
                return true;
            }
        }

        support = default;
        return false;
    }

    private void EnsureStemAssignments(IEnumerable<BlockPos> sites)
    {
        if (_crop != CropType.MelonsAndPumpkins)
            return;

        lock (_gate)
        {
            int next = 0;
            foreach (BlockPos site in sites.OrderBy(site => site.X).ThenBy(site => site.Y).ThenBy(site => site.Z))
            {
                if (_stemAssignments.ContainsKey(site))
                    continue;

                _stemAssignments.Add(site, next++ % 2 == 0 ? CropType.Melon : CropType.Pumpkin);
            }
        }
    }

    private void RememberStemAssignment(BlockPos site, CropType? crop)
    {
        if (_crop != CropType.MelonsAndPumpkins || crop is not (CropType.Melon or CropType.Pumpkin))
            return;

        lock (_gate)
            _stemAssignments[site] = crop.Value;
    }

    private CropType StemAssignment(BlockPos site)
    {
        lock (_gate)
        {
            return _stemAssignments.TryGetValue(site, out CropType crop) ? crop : CropType.Melon;
        }
    }

    /// <summary>Filters stable mapped sites and returns them in a row-contiguous route.</summary>
    private async Task<List<BlockPos>> ReadMappedTargetsAsync(
        ISessionScope session,
        Func<BlockState, Umpk.Game.World.World, BlockPos, bool> predicate)
    {
        IReadOnlyList<BlockPos> sites;
        lock (_gate)
            sites = _farmSites ?? [];

        List<BlockPos> targets = await session.Client.InvokeAsync(
            c =>
            {
                Umpk.Game.World.World world = c.State.World;
                return sites.Where(position => predicate(world.GetBlock(position), world, position)).ToList();
            },
            session.Detached).ConfigureAwait(false);
        return await OrderFromCurrentPositionAsync(session, targets).ConfigureAwait(false);
    }

    /// <summary>Derives harvest blocks from stable supports, bases, stems, and Cocoa pod sites.</summary>
    private async Task<List<BlockPos>> ReadHarvestTargetsAsync(ISessionScope session)
    {
        IReadOnlyList<BlockPos> sites;
        lock (_gate)
            sites = _farmSites ?? [];

        List<BlockPos> targets = await session.Client.InvokeAsync(
            c =>
            {
                Umpk.Game.World.World world = c.State.World;
                CropProfile profile = FarmRules.Profile(_crop);
                var candidates = new HashSet<BlockPos>();
                foreach (BlockPos site in sites)
                {
                    switch (profile.Mode)
                    {
                        case FarmMode.BreakAndReplant:
                            candidates.Add(site.Above());
                            break;

                        case FarmMode.StemFruit:
                            BlockPos growth = site.Above();
                            if (!FarmRules.IsStemGrowthBlock(_crop, world.GetBlock(growth).Block.Id.ToString()))
                                break;
                            candidates.Add(new BlockPos(growth.X + 1, growth.Y, growth.Z));
                            candidates.Add(new BlockPos(growth.X - 1, growth.Y, growth.Z));
                            candidates.Add(new BlockPos(growth.X, growth.Y, growth.Z + 1));
                            candidates.Add(new BlockPos(growth.X, growth.Y, growth.Z - 1));
                            break;

                        case FarmMode.PreserveVerticalBase:
                            candidates.Add(site.Above());
                            break;

                        case FarmMode.CocoaPod:
                            candidates.Add(site);
                            break;
                    }
                }

                return candidates
                    .Where(_region.ContainsWorkingTarget)
                    .Where(position => IsHarvestable(world.GetBlock(position), world, position))
                    .ToList();
            },
            session.Detached).ConfigureAwait(false);
        return await OrderFromCurrentPositionAsync(session, targets).ConfigureAwait(false);
    }

    /// <summary>Derives not-fully-grown crop blocks from stable mapped sites.</summary>
    private async Task<List<BlockPos>> ReadBoneMealTargetsAsync(ISessionScope session)
    {
        IReadOnlyList<BlockPos> sites;
        lock (_gate)
            sites = _farmSites ?? [];

        List<BlockPos> targets = await session.Client.InvokeAsync(
            c =>
            {
                Umpk.Game.World.World world = c.State.World;
                CropProfile profile = FarmRules.Profile(_crop);
                var candidates = new HashSet<BlockPos>();
                foreach (BlockPos site in sites)
                {
                    candidates.Add(profile.Mode switch
                    {
                        FarmMode.BreakAndReplant => site.Above(),
                        FarmMode.StemFruit => site.Above(),
                        _ => site,
                    });
                }

                return candidates.Where(position =>
                    {
                        BlockState state = world.GetBlock(position);
                        return (FarmRules.IsStemCrop(_crop)
                                   ? FarmRules.IsStemGrowthBlock(_crop, state.Block.Id.ToString())
                                      && !FarmRules.IsAttachedStemBlock(_crop, state.Block.Id.ToString())
                                   : string.Equals(FarmRules.BarePath(state.Block.Id.ToString()), FarmRules.BoneMealBlock(_crop), StringComparison.Ordinal))
                               && !FarmRules.IsFullyGrown(
                                   _crop,
                                   ReadAge(state, _crop),
                                   isHarvestBlock: false);
                    })
                    .ToList();
            },
            session.Detached).ConfigureAwait(false);
        return await OrderFromCurrentPositionAsync(session, targets).ConfigureAwait(false);
    }

    private async Task<List<BlockPos>> OrderFromCurrentPositionAsync(
        ISessionScope session,
        IEnumerable<BlockPos> targets)
    {
        BlockPos self = await CurrentBlockAsync(session).ConfigureAwait(false);
        return FarmTraversal.Order(targets, self).ToList();
    }

    private static Task<BlockPos> CurrentBlockAsync(ISessionScope session) =>
        session.Client.InvokeAsync(
            c => BlockPos.Containing(c.State.Self.Position),
            session.Detached);

    private async Task<bool> WalkToAsync(ISessionScope session, BlockPos target)
    {
        try
        {
            (bool Pathfinding, bool InReach) state = await session.Client.InvokeAsync(
                c => (c.State.Features.Pathfinding, FarmRules.IsWithinInteractionReach(c.State.Self.Position, target)),
                session.Detached).ConfigureAwait(false);

            if (state.InReach)
                return true;

            ReleaseLease();
            try
            {
                if (state.Pathfinding)
                {
                    await session.Actions.Movement
                        .NavigateAsync(new GoalNear(target.X, target.Y, target.Z, 2), session.Detached).ConfigureAwait(false);
                }
                else
                    await session.Actions.Movement.MoveToVerifiedAsync(target.BottomCenter, session.Detached).ConfigureAwait(false);
            }
            finally
            {
                ReclaimLease(session);
            }

            return _running && await session.Client.InvokeAsync(
                c => FarmRules.IsWithinInteractionReach(c.State.Self.Position, target),
                session.Detached).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug("Farmer: could not walk to a target.");
            _logger?.LogDebug(ex, "{Message}", _strings?.Format("navigation_failed", target));
            return false;
        }
    }

    private async Task<bool> NavigateToExactAsync(ISessionScope session, BlockPos target)
    {
        try
        {
            bool pathfinding = await session.Client.InvokeAsync(c => c.State.Features.Pathfinding, session.Detached).ConfigureAwait(false);
            ReleaseLease();
            try
            {
                if (pathfinding)
                    await session.Actions.Movement.NavigateAsync(new GoalBlock(target), session.Detached).ConfigureAwait(false);
                else
                    await session.Actions.Movement.MoveToVerifiedAsync(target.BottomCenter, session.Detached).ConfigureAwait(false);
            }
            finally
            {
                ReclaimLease(session);
            }

            return _running && await session.Client.InvokeAsync(
                c => BlockPos.Containing(c.State.Self.Position) == target, session.Detached).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "{Message}", _strings?.Format("exact_navigation_failed", target));
            return false;
        }
    }

    private async Task<bool> WalkToDropAsync(
        ISessionScope session,
        IReadOnlyList<FarmDropTraversal.EntityTarget> targets)
    {
        try
        {
            if (targets.Count == 0)
                return true;

            Vec3d target = targets[0].Position;
            int feetY = FarmRules.DropFeetLevel(target.Y);
            bool pathfinding = await session.Client.InvokeAsync(c => c.State.Features.Pathfinding, session.Detached).ConfigureAwait(false);
            BlockPos near = new((int)Math.Floor(target.X), feetY, (int)Math.Floor(target.Z));
            if (pathfinding)
            {
                ReleaseLease();
                try
                {
                    await session.Actions.Movement.NavigateAsync(
                        new GoalNear(near.X, near.Y, near.Z, 1),
                        session.Detached).ConfigureAwait(false);
                }
                finally
                {
                    ReclaimLease(session);
                }
            }

            if (await WaitForDropRemovalAsync(session, targets.Select(target => target.Id)).ConfigureAwait(false))
                return true;

            IReadOnlyList<BlockPos> candidates = await session.Client.InvokeAsync(c => DropStances(target)
                .Where(candidate => c.State.World.GetBlock(candidate).IsAir && c.State.World.GetBlock(candidate.Above()).IsAir)
                .OrderBy(candidate => candidate.Center.Subtract(c.State.Self.Position).LengthSqr())
                .ThenBy(candidate => candidate.Y).ThenBy(candidate => candidate.Z).ThenBy(candidate => candidate.X)
                .ToArray(), session.Detached).ConfigureAwait(false);
            foreach (BlockPos candidate in candidates)
            {
                if (await NavigateToExactAsync(session, candidate).ConfigureAwait(false)
                    && await WaitForDropRemovalAsync(session, targets.Select(target => target.Id)).ConfigureAwait(false))
                    return true;
            }

            List<FarmDropTraversal.EntityTarget> stillLive = await LiveDropTargetsAsync(session, targets.Select(target => target.Id)).ConfigureAwait(false);
            if (stillLive.Count > 0)
                QuarantineDrops(stillLive);

            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "{Message}", _strings?.Get("walk_to_drop_failed"));
            return false;
        }
    }

    internal static IReadOnlyList<BlockPos> DropStances(Vec3d target)
    {
        int y = FarmRules.DropFeetLevel(target.Y);
        int x = (int)Math.Floor(target.X);
        int z = (int)Math.Floor(target.Z);
        var result = new List<BlockPos>();
        foreach (int candidateY in new[] { y, y - 1, y + 1 })
        {
            result.Add(new BlockPos(x, candidateY, z));
            result.Add(new BlockPos(x + 1, candidateY, z));
            result.Add(new BlockPos(x - 1, candidateY, z));
            result.Add(new BlockPos(x, candidateY, z + 1));
            result.Add(new BlockPos(x, candidateY, z - 1));
        }

        return result.Distinct().ToArray();
    }

    private async Task<bool> WaitForDropRemovalAsync(ISessionScope session, IEnumerable<int> ids)
    {
        int[] tracked = ids.Distinct().ToArray();
        for (int i = 0; i < 4; i++)
        {
            bool anyLive = await session.Client.InvokeAsync(c => tracked.Any(id => c.State.Entities.Get(id) is not null), session.Detached).ConfigureAwait(false);
            if (!anyLive)
                return true;

            await Task.Delay(50, session.Detached).ConfigureAwait(false);
        }

        return false;
    }

    private void ReleaseLease()
    {
        IMovementLease? lease;
        lock (_gate)
        {
            lease = _lease;
            _lease = null;
        }

        lease?.Dispose();
    }

    private void ReclaimLease(ISessionScope session)
    {
        if (!_running)
            return;

        IMovementLease? lease = session.TryAcquireMovement("Farmer");
        if (lease is null)
        {
            _logger?.LogInformation(
                "{Message}",
                _strings?.Format("movement_lock_held", _strings.Get("another_plugin")));
            _running = false;
            return;
        }

        lock (_gate)
            _lease = lease;
    }

    private async Task<bool> DigAsync(ISessionScope session, BlockPos position, BlockPos? preservedBase)
    {
        try
        {
            string expected = await session.Client.InvokeAsync(
                c => FarmRules.BarePath(c.State.World.GetBlock(position).Block.Id.ToString()),
                session.Detached).ConfigureAwait(false);
            Debug($"Farmer: sending dig interaction at {position.X} {position.Y} {position.Z}; expected block {expected}.");
            await session.Actions.Movement.LookAtAsync(position.Center, session.Detached).ConfigureAwait(false);
            await session.Actions.Interaction.DigBlockAsync(position, Direction.Down, session.Detached).ConfigureAwait(false);

            // DigBlockAsync already waited for the action acknowledgement.
            // Poll fast so nearby crops can go back-to-back, but keep the one-second bound for servers that send the block update late.
            for (int i = 0; i < 40; i++)
            {
                (bool Gone, bool BasePreserved) result = await session.Client.InvokeAsync(
                    c =>
                    {
                        Umpk.Game.World.World world = c.State.World;
                        bool gone = !string.Equals(
                            FarmRules.BarePath(world.GetBlock(position).Block.Id.ToString()),
                            expected,
                            StringComparison.Ordinal);
                        bool basePreserved = preservedBase is not BlockPos basePos
                            || FarmRules.IsHarvestBlock(_crop, world.GetBlock(basePos).Block.Id.ToString());
                        return (gone, basePreserved);
                    },
                    session.Detached).ConfigureAwait(false);
                if (result.Gone)
                {
                    Debug($"Farmer: dig world update arrived after {i + 1} confirmation poll(s); base preserved={result.BasePreserved}.");
                    return result.BasePreserved;
                }

                await Task.Delay(25, session.Detached).ConfigureAwait(false);
            }

            Debug("Farmer: dig confirmation timed out after 40 polls.");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "{Message}", _strings?.Format("dig_failed", position));
            return false;
        }
    }

    private static async Task<bool> HasItemAsync(ISessionScope session, string itemId)
        => await session.Client.InvokeAsync(
            c => FindItemSlot(c.State.Inventory.PlayerSlots, itemId) >= 0, session.Detached).ConfigureAwait(false);

    private async Task<int> WithdrawAsync(
        ISessionScope session,
        FarmerStorageKind kind,
        Func<ItemStack, bool> matches,
        int targetTotal)
    {
        int before = await CountItemsAsync(session, matches).ConfigureAwait(false);
        if (before >= targetTotal)
            return 0;

        IReadOnlyList<BlockPos> containers = await ResolveStorageAsync(session, kind).ConfigureAwait(false);
        foreach (BlockPos container in containers)
        {
            if (!await OpenStorageAsync(session, container).ConfigureAwait(false))
            {
                Debug($"Farmer: could not open {kind} storage at {StorageCoordinates.Format(container)}; trying the next candidate.");
                MarkStorageUnusable(kind, container);
                continue;
            }

            int carriedBeforeCandidate = await CountItemsAsync(session, matches).ConfigureAwait(false);
            try
            {
                for (int guard = 0; guard < 54; guard++)
                {
                    (int Slot, int Total) source = await session.Client.InvokeAsync(
                        c =>
                        {
                            IReadOnlyList<ItemStack> slots = c.State.Inventory.ContainerSlots ?? [];
                            int topSlots = Math.Max(0, slots.Count - 36);
                            int total = c.State.Inventory.PlayerSlots.Where(matches).Sum(stack => stack.Count);
                            int slot = total >= targetTotal
                                ? -1
                                : Enumerable.Range(0, topSlots).FirstOrDefault(index => matches(slots[index]), -1);
                            return (slot, total);
                        }, session.Detached).ConfigureAwait(false);
                    if (source.Slot < 0 || source.Total >= targetTotal)
                        break;

                    if (!await QuickMoveAndSettleAsync(session, source.Slot).ConfigureAwait(false))
                    {
                        Debug($"Farmer: {kind} storage quick-move predicted no inventory change; stopping this container transfer.");
                        break;
                    }

                    int afterMove = await CountItemsAsync(session, matches).ConfigureAwait(false);
                    if (afterMove <= source.Total)
                    {
                        Debug($"Farmer: {kind} storage quick-move made no withdrawal progress; stopping this container transfer.");
                        break;
                    }
                }
            }
            finally
            {
                await CloseStorageAsync(session).ConfigureAwait(false);
            }

            if (await CountItemsAsync(session, matches).ConfigureAwait(false) == carriedBeforeCandidate)
            {
                Debug($"Farmer: {kind} storage at {StorageCoordinates.Format(container)} supplied no matching items; skipping it for this run.");
                MarkStorageUnusable(kind, container);
            }

            if (await CountItemsAsync(session, matches).ConfigureAwait(false) >= targetTotal)
                break;
        }

        int moved = Math.Max(0, await CountItemsAsync(session, matches).ConfigureAwait(false) - before);
        if (moved == 0)
            Debug($"Farmer: {kind} storage is unavailable or empty; continuing without a restock.");

        return moved;
    }

    private async Task<int> CountItemsAsync(ISessionScope session, Func<ItemStack, bool> matches) =>
        await session.Client.InvokeAsync(
            c => c.State.Inventory.PlayerSlots.Where(matches).Sum(stack => stack.Count), session.Detached).ConfigureAwait(false);

    private Task EnsurePlantingStockAsync(ISessionScope session, int required) =>
        EnsurePlantingStockAsync(session, _crop, required);

    private async Task EnsurePlantingStockAsync(ISessionScope session, CropType crop, int required)
    {
        int carried = await CountItemsAsync(session, stack => FarmRules.IsPlantingItem(crop, stack)).ConfigureAwait(false);
        if (carried >= required)
        {
            Debug($"Farmer: planting reserve already has {carried}/{required} item(s).");
            return;
        }

        FarmerStorageKind source = HasSeparateSeedStorage(crop) ? FarmerStorageKind.Seeds : FarmerStorageKind.Crops;
        int moved = await WithdrawAsync(
            session,
            source,
            stack => FarmRules.IsPlantingItem(crop, stack),
            required).ConfigureAwait(false);
        if (source == FarmerStorageKind.Seeds && carried + moved < required)
        {
            Debug("Farmer: Seeds storage could not fill the planting reserve; falling back to Crops storage.");
            moved += await WithdrawAsync(
                session,
                FarmerStorageKind.Crops,
                stack => FarmRules.IsPlantingItem(crop, stack),
                required).ConfigureAwait(false);
        }
        int after = carried + moved;
        Debug($"Farmer: planting reserve needs {required}; {source} storage supplied {moved}, now carrying {after}.");
    }

    private Task StoreSurplusAsync(ISessionScope session, int reserve) => StoreSurplusAsync(session, _crop, reserve);

    private async Task StoreSurplusAsync(ISessionScope session, CropType crop, int reserve)
    {
        if (HasSeparateSeedStorage(crop))
        {
            int seeds = await DepositAsync(session, FarmerStorageKind.Seeds,
                stack => FarmRules.IsPlantingItem(crop, stack), reserve).ConfigureAwait(false);
            int seedFallback = 0;
            if (await CountItemsAsync(session, stack => FarmRules.IsPlantingItem(crop, stack)).ConfigureAwait(false) > reserve)
            {
                Debug("Farmer: Seeds storage left a surplus; falling back to Crops storage.");
                seedFallback = await DepositAsync(session, FarmerStorageKind.Crops,
                    stack => FarmRules.IsPlantingItem(crop, stack), reserve).ConfigureAwait(false);
            }
            int crops = await DepositAsync(session, FarmerStorageKind.Crops,
                stack => FarmRules.IsRelevantDrop(crop, stack) && !FarmRules.IsPlantingItem(crop, stack), 0).ConfigureAwait(false);
            Debug($"Farmer: storage preparation deposited {seeds + seedFallback} seed item(s) and {crops} crop item(s).");
            return;
        }

        int moved = await DepositAsync(session, FarmerStorageKind.Crops,
            stack => FarmRules.IsRelevantDrop(crop, stack), reserve).ConfigureAwait(false);
        Debug($"Farmer: storage preparation deposited {moved} crop item(s), retaining a planting reserve of {reserve}.");
    }

    private async Task<int> DepositAsync(
        ISessionScope session,
        FarmerStorageKind kind,
        Func<ItemStack, bool> matches,
        int reserve)
    {
        int before = await CountItemsAsync(session, matches).ConfigureAwait(false);
        if (before <= reserve)
            return 0;

        foreach (BlockPos container in await ResolveStorageAsync(session, kind).ConfigureAwait(false))
        {
            if (!await OpenStorageAsync(session, container).ConfigureAwait(false))
            {
                Debug($"Farmer: could not open {kind} storage at {StorageCoordinates.Format(container)}; trying the next candidate.");
                MarkStorageUnusable(kind, container);
                continue;
            }

            int carriedBeforeCandidate = await CountItemsAsync(session, matches).ConfigureAwait(false);
            try
            {
                for (int guard = 0; guard < 36; guard++)
                {
                    (int Slot, int Total) source = await session.Client.InvokeAsync(c =>
                    {
                        IReadOnlyList<ItemStack> player = c.State.Inventory.PlayerSlots;
                        int total = player.Where(matches).Sum(stack => stack.Count);
                        IReadOnlyList<ItemStack> containerSlots = c.State.Inventory.ContainerSlots ?? [];
                        int topSlots = Math.Max(0, containerSlots.Count - 36);
                        if (total <= reserve || topSlots == 0)
                            return (-1, total);

                        for (int playerSlot = 9; playerSlot <= 44; playerSlot++)
                        {
                            ItemStack stack = player[playerSlot];
                            if (matches(stack) && FarmRules.CanDepositWholeStack(total, stack.Count, reserve))
                                return (topSlots + playerSlot - 9, total);
                        }

                        // Quick-move cannot split the final partial stack.
                        // Keep it rather than risk the replant reserve; the next harvest usually makes it a whole stack again.
                        return (-1, total);
                    }, session.Detached).ConfigureAwait(false);
                    if (source.Slot < 0)
                        break;

                    if (!await QuickMoveAndSettleAsync(session, source.Slot).ConfigureAwait(false))
                    {
                        Debug($"Farmer: {kind} storage quick-move predicted no inventory change; stopping this container transfer.");
                        break;
                    }

                    int afterMove = await CountItemsAsync(session, matches).ConfigureAwait(false);
                    if (afterMove >= source.Total)
                    {
                        Debug($"Farmer: {kind} storage quick-move made no deposit progress; stopping this container transfer.");
                        break;
                    }
                }

                int partial = await DepositPartialSurplusAsync(session, matches, reserve).ConfigureAwait(false);
                if (partial > 0)
                    Debug($"Farmer: {kind} storage accepted the final {partial} surplus item(s) from a partial stack.");
            }
            finally
            {
                await CloseStorageAsync(session).ConfigureAwait(false);
            }

            if (await CountItemsAsync(session, matches).ConfigureAwait(false) == carriedBeforeCandidate)
            {
                Debug($"Farmer: {kind} storage at {StorageCoordinates.Format(container)} accepted no items; skipping it for this run.");
                MarkStorageUnusable(kind, container);
            }

            if (await CountItemsAsync(session, matches).ConfigureAwait(false) <= reserve)
                break;
        }

        return Math.Max(0, before - await CountItemsAsync(session, matches).ConfigureAwait(false));
    }

    /// <summary>
    /// Shift-click can only move a whole stack.
    /// When that leaves a partial stack above the planting reserve, this moves the surplus one item at a time into a compatible container slot, then puts the retained reserve back in its original player slot.
    /// </summary>
    private async Task<int> DepositPartialSurplusAsync(
        ISessionScope session,
        Func<ItemStack, bool> matches,
        int reserve)
    {
        (int Source, int Destination, int Amount) plan = await session.Client.InvokeAsync(c =>
        {
            IReadOnlyList<ItemStack> player = c.State.Inventory.PlayerSlots;
            IReadOnlyList<ItemStack> slots = c.State.Inventory.ContainerSlots ?? [];
            int topSlots = Math.Max(0, slots.Count - 36);
            int total = player.Where(matches).Sum(stack => stack.Count);
            int amount = FarmRules.PartialDepositAmount(total, reserve);
            if (amount <= 0 || topSlots == 0)
                return (-1, -1, 0);

            int playerSlot = Enumerable.Range(9, 36).FirstOrDefault(slot => matches(player[slot]) && player[slot].Count >= amount, -1);
            if (playerSlot < 0)
                return (-1, -1, 0);

            int destination = Enumerable.Range(0, topSlots).FirstOrDefault(slot =>
            {
                ItemStack stack = slots[slot];
                return stack.IsEmpty || (matches(stack) && stack.Count + amount <= stack.MaxStackSize);
            }, -1);
            return destination < 0 ? (-1, -1, 0) : (topSlots + playerSlot - 9, destination, amount);
        }, session.Detached).ConfigureAwait(false);

        if (plan.Source < 0)
            return 0;

        if (!await ClickAndSettleAsync(session, new ClickAction.Pickup(plan.Source, MouseButton.Left)).ConfigureAwait(false))
            return 0;

        int moved = 0;
        for (; moved < plan.Amount; moved++)
        {
            if (!await ClickAndSettleAsync(session, new ClickAction.Pickup(plan.Destination, MouseButton.Right)).ConfigureAwait(false))
                break;
        }

        bool returned = await ClickAndSettleAsync(session, new ClickAction.Pickup(plan.Source, MouseButton.Left)).ConfigureAwait(false);
        return returned ? moved : 0;
    }

    /// <summary>
    /// Sends one predicted quick-move, then yields briefly so a server correction can be reconciled before the transfer loop reads its next source.
    /// A container click the server accepts is silent, so the changed-slot prediction is the only success signal there is.
    /// </summary>
    private async Task<bool> QuickMoveAndSettleAsync(ISessionScope session, int sourceSlot)
    {
        ClickResult result = await session.Actions.Inventory.QuickMoveAsync(sourceSlot, session.Detached).ConfigureAwait(false);
        return await SettlePredictedClickAsync(session, result).ConfigureAwait(false);
    }

    private async Task<bool> ClickAndSettleAsync(ISessionScope session, ClickAction action)
    {
        ClickResult result = await session.Actions.Inventory.ClickAsync(action, session.Detached).ConfigureAwait(false);
        return await SettlePredictedClickAsync(session, result).ConfigureAwait(false);
    }

    private static async Task<bool> SettlePredictedClickAsync(ISessionScope session, ClickResult result)
    {
        if (result.ChangedSlots.Count == 0)
            return false;

        await Task.Delay(75, session.Detached).ConfigureAwait(false);
        return await session.Client.InvokeAsync(
            c => c.State.Inventory.HasOpenContainer, session.Detached).ConfigureAwait(false);
    }

    private static bool HasSeparateSeedStorage(CropType crop) => crop is
        CropType.Wheat or CropType.Beetroot or CropType.Melon or CropType.Pumpkin;

    private async Task<IReadOnlyList<BlockPos>> ResolveStorageAsync(ISessionScope session, FarmerStorageKind kind)
    {
        string configured = StorageCoordinatesFor(kind);
        string label = StorageLabelFor(kind);
        IReadOnlyList<BlockPos> discovered = await session.Client.InvokeAsync(
            c => c.State.World.BlockEntityNbt
                .Where(entry => IsInsideStorageSearch(entry.Key))
                .Where(entry => SignMatches(entry.Value, label))
                .SelectMany(entry => Adjacent(entry.Key))
                .Where(position => IsStorageBlock(c.State.World.GetBlock(position)))
                .Where(position => !IsStorageUnusable(kind, position))
                .OrderBy(position => position.Center.Subtract(c.State.Self.Position).LengthSqr())
                .Distinct()
                .ToList(), session.Detached).ConfigureAwait(false);

        if (!StorageCoordinates.TryParse(configured, out BlockPos exact))
            return discovered;

        if (!await IsStorageBlockAsync(session, exact).ConfigureAwait(false))
        {
            Debug($"Farmer: configured {kind} coordinates {configured} are not a supported container; searching signs instead.");
            return discovered;
        }

        Debug($"Farmer: trying configured {kind} storage at {StorageCoordinates.Format(exact)} before sign discovery.");
        return IsStorageUnusable(kind, exact)
            ? discovered
            : [exact, .. discovered.Where(position => position != exact)];
    }

    private bool IsStorageUnusable(FarmerStorageKind kind, BlockPos position)
    {
        lock (_gate)
            return _unusableStorage.Contains((kind, position));
    }

    private void MarkStorageUnusable(FarmerStorageKind kind, BlockPos position)
    {
        lock (_gate)
            _unusableStorage.Add((kind, position));
    }

    private async Task<bool> IsStorageBlockAsync(ISessionScope session, BlockPos position) =>
        await session.Client.InvokeAsync(c => IsStorageBlock(c.State.World.GetBlock(position)), session.Detached).ConfigureAwait(false);

    private async Task<bool> OpenStorageAsync(ISessionScope session, BlockPos position)
    {
        if (!await WalkToAsync(session, position).ConfigureAwait(false)) return false;
        await session.Actions.Movement.LookAtAsync(position.Center, session.Detached).ConfigureAwait(false);
        await session.Actions.Interaction.PlaceBlockAsync(position, Direction.Up, new Vec3d(0.5, 0.5, 0.5), Hand.Main, false, session.Detached).ConfigureAwait(false);
        for (int i = 0; i < 20; i++)
        {
            bool ready = await session.Client.InvokeAsync(
                c => c.State.Inventory.HasOpenContainer
                     && c.State.Inventory.ContainerSlots is { Count: >= 63 },
                session.Detached).ConfigureAwait(false);
            if (ready) return true;
            await Task.Delay(50, session.Detached).ConfigureAwait(false);
        }
        Debug($"Farmer: storage at {StorageCoordinates.Format(position)} opened without a complete 27/54 + 36 slot snapshot.");
        return false;
    }

    private static async Task CloseStorageAsync(ISessionScope session)
    {
        bool open = await session.Client.InvokeAsync(c => c.State.Inventory.HasOpenContainer, session.Detached).ConfigureAwait(false);
        if (open) await session.Actions.Inventory.CloseAsync(session.Detached).ConfigureAwait(false);
    }

    private string StorageLabelFor(FarmerStorageKind kind) => kind switch
    {
        FarmerStorageKind.Bonemeal => _settings.Bonemeal_Chest_Name,
        FarmerStorageKind.Seeds => _settings.Seeds_Chest_Name,
        _ => _settings.Crops_Chest_Name,
    };

    private string StorageCoordinatesFor(FarmerStorageKind kind) => kind switch
    {
        FarmerStorageKind.Bonemeal => _settings.Bonemeal_Chest_Coordinates,
        FarmerStorageKind.Seeds => _settings.Seeds_Chest_Coordinates,
        _ => _settings.Crops_Chest_Coordinates,
    };

    private bool IsInsideStorageSearch(BlockPos position) =>
        position.X >= _region.Min.X - _settings.Storage_Search_Margin && position.X <= _region.Max.X + _settings.Storage_Search_Margin
        && position.Y >= _region.Min.Y - _settings.Storage_Search_Margin && position.Y <= _region.Max.Y + _settings.Storage_Search_Margin
        && position.Z >= _region.Min.Z - _settings.Storage_Search_Margin && position.Z <= _region.Max.Z + _settings.Storage_Search_Margin;

    private static bool IsStorageBlock(BlockState state) => FarmRules.BarePath(state.Block.Id.ToString()) is "chest" or "trapped_chest" or "barrel";

    private static IEnumerable<BlockPos> Adjacent(BlockPos position) =>
    [position.Above(), position.Below(), position.Offset(Direction.North), position.Offset(Direction.South), position.Offset(Direction.East), position.Offset(Direction.West)];

    private static bool SignMatches(NbtCompound nbt, string label) => ReadSignLines(nbt)
        .Any(line => string.Equals(line.Trim(), label.Trim(), StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> ReadSignLines(NbtCompound nbt)
    {
        for (int i = 1; i <= 4; i++)
        {
            if (nbt.TryGet($"Text{i}", out NbtString? line)) yield return ReadLegacyComponent(line.Value);
        }
        foreach (string side in new[] { "front_text", "back_text" })
        {
            if (!nbt.TryGet(side, out NbtCompound? text) || !text.TryGet("messages", out NbtList? messages))
                continue;

            foreach (NbtTag message in messages)
                yield return ReadModernComponent(message);
        }
    }

    private static string ReadLegacyComponent(string text)
    {
        try { return Umpk.Text.Serialization.ComponentJson.Parse(text).ToPlainText(); }
        catch { return text; }
    }

    private static string ReadModernComponent(NbtTag tag)
    {
        try { return Umpk.Text.Serialization.ComponentNbt.From(tag).ToPlainText(); }
        catch { return tag.ToString(); }
    }

    private static async Task<bool> HasCropItemAsync(ISessionScope session, CropType crop)
        => await session.Client.InvokeAsync(
            c => FindItemSlot(c.State.Inventory.PlayerSlots, stack => FarmRules.IsPlantingItem(crop, stack)) >= 0,
            session.Detached).ConfigureAwait(false);

    private static Task<bool> SwitchToCropItemAsync(ISessionScope session, CropType crop) =>
        SwitchToItemAsync(session, stack => FarmRules.IsPlantingItem(crop, stack));

    /// <summary>
    /// Selects the item into the main hand: keep it if it is already held, else pick it from the hotbar,
    /// else swap the first inventory copy into hotbar slot 0.
    /// </summary>
    private static async Task<bool> SwitchToItemAsync(ISessionScope session, string itemId)
        => await SwitchToItemAsync(session, stack => Matches(stack, itemId)).ConfigureAwait(false);

    private static async Task<bool> SwitchToItemAsync(ISessionScope session, Func<ItemStack, bool> predicate)
    {
        (int Held, int Hotbar, int Any) found = await session.Client.InvokeAsync(
            c =>
            {
                IReadOnlyList<ItemStack> slots = c.State.Inventory.PlayerSlots;
                int heldSlot = 36 + c.State.Self.HeldSlot;
                int held = heldSlot < slots.Count && predicate(slots[heldSlot]) ? heldSlot : -1;

                int hotbar = -1;
                for (int slot = 36; slot <= 44 && slot < slots.Count; slot++)
                {
                    if (predicate(slots[slot]))
                    {
                        hotbar = slot;
                        break;
                    }
                }

                return (held, hotbar, FindItemSlot(slots, predicate));
            },
            session.Detached).ConfigureAwait(false);

        if (found.Held >= 0)
            return true;

        if (found.Hotbar >= 0)
        {
            await session.Actions.Inventory.SelectHeldSlotAsync(found.Hotbar - 36, session.Detached).ConfigureAwait(false);
            return await WaitForHeldItemAsync(session, predicate).ConfigureAwait(false);
        }

        if (found.Any < 0)
            return false;

        await session.Actions.Inventory
            .ClickAsync(new ClickAction.Swap(found.Any, 0), session.Detached).ConfigureAwait(false);
        await session.Actions.Inventory.SelectHeldSlotAsync(0, session.Detached).ConfigureAwait(false);
        return await WaitForHeldItemAsync(session, predicate).ConfigureAwait(false);
    }

    private static async Task<bool> WaitForHeldItemAsync(ISessionScope session, Func<ItemStack, bool> predicate)
    {
        for (int i = 0; i < 10; i++)
        {
            bool held = await session.Client.InvokeAsync(
                c =>
                {
                    IReadOnlyList<ItemStack> slots = c.State.Inventory.PlayerSlots;
                    int slot = 36 + c.State.Self.HeldSlot;
                    return slot < slots.Count && predicate(slots[slot]);
                },
                session.Detached).ConfigureAwait(false);
            if (held)
                return true;

            await Task.Delay(50, session.Detached).ConfigureAwait(false);
        }

        return false;
    }

    private static int FindItemSlot(IReadOnlyList<ItemStack> slots, string itemId)
        => FindItemSlot(slots, stack => Matches(stack, itemId));

    private static int FindItemSlot(IReadOnlyList<ItemStack> slots, Func<ItemStack, bool> predicate)
    {
        for (int slot = 0; slot < slots.Count; slot++)
        {
            if (predicate(slots[slot]))
                return slot;
        }

        return -1;
    }

    private static bool Matches(ItemStack stack, string itemId)
        => !stack.IsEmpty
           && string.Equals(FarmRules.BarePath(stack.Item.Id.ToString()), itemId, StringComparison.Ordinal);
}

/// <summary>The <c>farmer</c> command.</summary>
internal sealed class FarmerCommand : CommandBase
{
    private readonly FarmerPlugin _plugin;
    private readonly IPluginLocalization _strings;

    internal FarmerCommand(FarmerPlugin plugin, IPluginLocalization strings)
    {
        _plugin = plugin;
        _strings = strings;
    }

    /// <inheritdoc/>
    public override string CmdName => "farmer";

    /// <inheritdoc/>
    public override string CmdDesc => _strings.Get("command_desc");

    /// <inheritdoc/>
    public override string CmdUsage => _strings.Get("command_usage");

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Plugins;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("start <crop> radius [1-150]", _strings.Get("usage_start_radius")),
        new("start <crop> area <x1> <y1> <z1> <x2> <y2> <z2>", _strings.Get("usage_start_area")),
        new("storage <bonemeal|seeds|crops> <name|position> ...", _strings.Get("usage_storage")),
        new("storage margin <blocks>", _strings.Get("usage_storage_margin")),
        new("storage list", _strings.Get("usage_storage_list")),
        new("stop", _strings.Get("usage_stop")),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples =>
    [
        "farmer start sugar_cane radius 40",
        "farmer start cocoa area 100 63 200 132 72 240",
        "farmer storage bonemeal position 120 64 220",
        "farmer stop",
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["move", "inventory"];

    /// <inheritdoc/>
    public override string? ManTopic => "farmer";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .Executes(ctx => ShowUsage(ctx.Source))
            .ThenLiteral("stop", a => a.Executes(ctx => Stop(ctx.Source)))
            .ThenLiteral("storage", s => RegisterStorage(s))
            .ThenLiteral("start", a => a
                .ThenArgument("crop", Arguments.Word(), b => b
                    .Suggests(SuggestCrops)
                    .ThenLiteral("radius", r => r
                        .Executes(ctx => StartRadius(
                            ctx.Source,
                            ctx.GetArgument<string>("crop"),
                            FarmRules.DefaultRadius))
                        .ThenArgument("blocks", Arguments.Integer(1, FarmRules.MaxRadius), blocks => blocks
                            .Executes(ctx => StartRadius(
                                ctx.Source,
                                ctx.GetArgument<string>("crop"),
                                ctx.GetArgument<int>("blocks")))))
                    .ThenLiteral("area", area => area
                        .ThenArgument("x1", Arguments.Integer(), x1 => x1
                            .ThenArgument("y1", Arguments.Integer(), y1 => y1
                                .ThenArgument("z1", Arguments.Integer(), z1 => z1
                                    .ThenArgument("x2", Arguments.Integer(), x2 => x2
                                        .ThenArgument("y2", Arguments.Integer(), y2 => y2
                                            .ThenArgument("z2", Arguments.Integer(), z2 => z2
                                                .Executes(ctx => StartArea(ctx.Source, ctx)))))))))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private void RegisterStorage(CommandNodeBuilder<CommandContext> storage) => storage
        .Executes(ctx => ctx.Source.Result.Ok(_plugin.StorageSummary(_strings)))
        .ThenLiteral("list", l => l.Executes(ctx => ctx.Source.Result.Ok(_plugin.StorageSummary(_strings))))
        .ThenLiteral("margin", m => m.ThenArgument("blocks", Arguments.Integer(1, 128), blocks => blocks
            .Executes(ctx => SetMargin(ctx.Source, ctx.GetArgument<int>("blocks")))))
        .ThenLiteral("bonemeal", kind => RegisterStorageKind(kind, FarmerStorageKind.Bonemeal))
        .ThenLiteral("seeds", kind => RegisterStorageKind(kind, FarmerStorageKind.Seeds))
        .ThenLiteral("crops", kind => RegisterStorageKind(kind, FarmerStorageKind.Crops));

    private void RegisterStorageKind(CommandNodeBuilder<CommandContext> builder, FarmerStorageKind kind) => builder
        .ThenLiteral("name", n => n.ThenArgument("label", Arguments.GreedyString(), label => label
            .Executes(ctx => SetName(ctx.Source, kind, ctx.GetArgument<string>("label")))))
        .ThenLiteral("position", p => p
            .ThenLiteral("clear", c => c.Executes(ctx => ClearPosition(ctx.Source, kind)))
            .ThenArgument("x", Arguments.Integer(), x => x
                .ThenArgument("y", Arguments.Integer(), y => y
                    .ThenArgument("z", Arguments.Integer(), z => z
                        .Executes(ctx => SetPosition(ctx.Source, kind, new BlockPos(
                            ctx.GetArgument<int>("x"), ctx.GetArgument<int>("y"), ctx.GetArgument<int>("z"))))))));

    private int SetName(CommandContext ctx, FarmerStorageKind kind, string label) =>
        Result(ctx, _plugin.SetStorageName(kind, label, _strings), _strings.Get("storage_label_saved"));

    private int SetPosition(CommandContext ctx, FarmerStorageKind kind, BlockPos position) =>
        Result(
            ctx,
            _plugin.SetStoragePosition(kind, StorageCoordinates.Format(position), _strings),
            _strings.Get("storage_position_saved"));

    private int ClearPosition(CommandContext ctx, FarmerStorageKind kind)
    {
        _plugin.ClearStoragePosition(kind);
        return ctx.Result.Ok(_strings.Get("storage_position_cleared"));
    }

    private int SetMargin(CommandContext ctx, int margin) =>
        Result(ctx, _plugin.SetStorageMargin(margin, _strings), _strings.Get("storage_margin_saved"));

    private static int Result(CommandContext ctx, string? failure, string success) =>
        failure is null ? ctx.Result.Ok(success) : ctx.Result.Fail(failure);

    private static ValueTask SuggestCrops(ICommandContext<CommandContext> ctx, ISuggestionSink sink)
    {
        foreach (string crop in FarmRules.CropSuggestions)
        {
            if (crop.StartsWith(sink.Remaining, StringComparison.OrdinalIgnoreCase))
                sink.Suggest(crop);
        }

        return ValueTask.CompletedTask;
    }

    private int StartRadius(CommandContext ctx, string crop, int radius)
    {
        if (!FarmRules.TryParseCrop(crop, out CropType cropType))
            return ctx.Result.Fail(_strings.Format("unknown_crop", crop, FarmRules.CropNames));

        string? failure = _plugin.StartRadius(cropType, radius, _strings);
        return failure is null
            ? ctx.Result.Ok(_strings.Format("started_radius", cropType.ToString(), radius))
            : ctx.Result.Fail(failure);
    }

    private int StartArea(CommandContext command, ICommandContext<CommandContext> args)
    {
        string crop = args.GetArgument<string>("crop");
        if (!FarmRules.TryParseCrop(crop, out CropType cropType))
            return command.Result.Fail(_strings.Format("unknown_crop", crop, FarmRules.CropNames));

        FarmRegion region = FarmRegion.Between(
            new BlockPos(
                args.GetArgument<int>("x1"),
                args.GetArgument<int>("y1"),
                args.GetArgument<int>("z1")),
            new BlockPos(
                args.GetArgument<int>("x2"),
                args.GetArgument<int>("y2"),
                args.GetArgument<int>("z2")));
        if (region.CellCount > FarmRules.MaxAreaCells)
        {
            return command.Result.Fail(
                _strings.Format("area_too_large", region.CellCount, FarmRules.MaxAreaCells));
        }

        string? failure = _plugin.Start(cropType, region, _strings);
        return failure is null
            ? command.Result.Ok(FarmerPlugin.Area(_strings, cropType, region))
            : command.Result.Fail(failure);
    }

    private int Stop(CommandContext ctx)
    {
        if (!_plugin.IsRunning)
            return ctx.Result.Fail(_strings.Get("already_stopped"));

        _plugin.Stop();
        return ctx.Result.Ok(_strings.Get("stopping"));
    }
}
