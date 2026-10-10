using DropNames = DMCBK.OfficialPlugins.AutoDrop.ItemNames;
using DMCBK.OfficialPlugins.AutoAttack;
using DMCBK.OfficialPlugins.AutoCraft;
using DMCBK.OfficialPlugins.AutoDig;
using DMCBK.OfficialPlugins.AutoDrop;
using DMCBK.OfficialPlugins.AutoFishing;
using DMCBK.OfficialPlugins.Farmer;
using DMCBK.OfficialPlugins.ItemsCollector;
using Umpk.Game.Blocks;
using Umpk.Game.Registries;
using Umpk.Game.World;
using Umpk.Geometry;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The headless-testable logic of the gameplay plugins: item and block name matching, drop and dig predicates, attack classification and cooldown maths, crafting grid shape, fishing bite detection and the movement cycler, crop growth rules and the farmer argument parser, and collection ordering.
/// These are the parts of each legacy bot that decide behavior without touching a live session.
/// </summary>
public sealed class GameplayPluginRulesTests
{
    #region AutoDrop

    [Theory]
    [InlineData("Cobblestone", "cobblestone")]
    [InlineData("minecraft:cobblestone", "cobblestone")]
    [InlineData("NetheriteIngot", "netherite_ingot")]
    [InlineData("netherite_ingot", "netherite_ingot")]
    [InlineData("  Dirt  ", "dirt")]
    [InlineData(null, "")]
    public void ItemNames_Normalize_AcceptsLegacyAndModernSpellings(string? input, string expected)
        => Assert.Equal(expected, DropNames.Normalize(input));

    [Fact]
    public void ItemNames_NormalizeAll_DropsBlanksAndDuplicates()
    {
        string[] result = DropNames.NormalizeAll(["Dirt", "minecraft:dirt", "  ", "Cobblestone"]);
        Assert.Equal(["dirt", "cobblestone"], result);
    }

    [Theory]
    [InlineData("include", DropMode.Include)]
    [InlineData("EXCLUDE", DropMode.Exclude)]
    [InlineData("everything", DropMode.Everything)]
    public void ItemNames_TryParseMode_AcceptsLegacySpellings(string input, DropMode expected)
    {
        Assert.True(DropNames.TryParseMode(input, out DropMode mode));
        Assert.Equal(expected, mode);
    }

    [Fact]
    public void ItemNames_TryParseMode_RejectsUnknown() => Assert.False(DropNames.TryParseMode("nope", out _));

    [Fact]
    public void ShouldDrop_NeverDropsTheCraftingResultSlot()
    {
        string[] items = ["dirt"];
        Assert.False(DropNames.ShouldDrop(0, "minecraft:dirt", DropMode.Everything, items));
        Assert.True(DropNames.ShouldDrop(1, "minecraft:dirt", DropMode.Everything, items));
    }

    [Fact]
    public void ShouldDrop_HonorsIncludeExcludeAndEverything()
    {
        string[] items = ["dirt"];
        Assert.True(DropNames.ShouldDrop(5, "minecraft:dirt", DropMode.Include, items));
        Assert.False(DropNames.ShouldDrop(5, "minecraft:stone", DropMode.Include, items));
        Assert.False(DropNames.ShouldDrop(5, "minecraft:dirt", DropMode.Exclude, items));
        Assert.True(DropNames.ShouldDrop(5, "minecraft:stone", DropMode.Exclude, items));
        Assert.True(DropNames.ShouldDrop(5, "minecraft:stone", DropMode.Everything, items));
    }

    [Fact]
    public void AutoDropSettings_Validate_NormalizesTheMode()
    {
        var settings = new AutoDropSettings { Mode = "NOPE" };
        settings.Validate();
        Assert.Equal("include", settings.Mode);
    }

    #endregion
    #region AutoAttack

    [Theory]
    [InlineData("minecraft:zombie", true)]
    [InlineData("Zombie", true)]
    [InlineData("minecraft:cow", false)]
    public void AttackTargets_IsHostile_MatchesTheLegacySet(string typeId, bool expected)
        => Assert.Equal(expected, AttackTargets.IsHostile(typeId));

    [Theory]
    [InlineData("minecraft:cow", true)]
    [InlineData("minecraft:zombie", false)]
    public void AttackTargets_IsPassive_MatchesTheLegacySet(string typeId, bool expected)
        => Assert.Equal(expected, AttackTargets.IsPassive(typeId));

    [Fact]
    public void IsAllowedToAttack_EmptyList_UsesOnlyTheClassFlags()
    {
        Assert.True(AttackTargets.IsAllowedToAttack("minecraft:zombie", true, false, ListType.Whitelist, []));
        Assert.False(AttackTargets.IsAllowedToAttack("minecraft:cow", true, false, ListType.Whitelist, []));
        Assert.True(AttackTargets.IsAllowedToAttack("minecraft:cow", true, true, ListType.Whitelist, []));
    }

    [Fact]
    public void IsAllowedToAttack_Whitelist_RestrictsToTheList()
    {
        string[] list = ["cow"];
        Assert.True(AttackTargets.IsAllowedToAttack("minecraft:cow", false, false, ListType.Whitelist, list));
        Assert.False(AttackTargets.IsAllowedToAttack("minecraft:zombie", true, false, ListType.Whitelist, list));
    }

    [Fact]
    public void IsAllowedToAttack_Blacklist_SubtractsFromTheClassFlags()
    {
        string[] list = ["zombie"];
        Assert.False(AttackTargets.IsAllowedToAttack("minecraft:zombie", true, false, ListType.Blacklist, list));
        Assert.True(AttackTargets.IsAllowedToAttack("minecraft:creeper", true, false, ListType.Blacklist, list));
    }

    [Theory]
    [InlineData(0.25, 6)]
    [InlineData(1.5, 31)]
    [InlineData(0.0, 1)]
    public void SecondsToCooldownTicks_MatchesTheLegacyFormula(double seconds, int expected)
        => Assert.Equal(expected, AttackTargets.SecondsToCooldownTicks(seconds));

    [Fact]
    public void AutoAttackSettings_Validate_ClampsRangeAndSwapsInvertedCooldown()
    {
        var settings = new AutoAttackSettings
        {
            Attack_Range = 9.0,
            Cooldown_Time = new CooldownConfig { Custom = true, Min = 4.0, Max = 1.0 },
        };
        settings.Validate();

        Assert.Equal(4.0, settings.Attack_Range);
        Assert.Equal(1.0, settings.Cooldown_Time.Min);
        Assert.Equal(4.0, settings.Cooldown_Time.Max);

        settings.Attack_Range = 0.1;
        settings.Validate();
        Assert.Equal(1.0, settings.Attack_Range);
    }

    [Fact]
    public void AutoAttackSettings_OnlyInFront_DefaultsToTrue()
        => Assert.True(new AutoAttackSettings().Only_In_Front);

    [Fact]
    public void AutoAttackSettings_FrontFov_DefaultsToHemisphere()
        => Assert.Equal(180.0, new AutoAttackSettings().Front_FOV);

    [Theory]
    [InlineData(0.1, 1.0)]
    [InlineData(-5.0, 1.0)]
    [InlineData(400.0, 180.0)]
    [InlineData(90.0, 90.0)]
    public void AutoAttackSettings_Validate_ClampsFrontFov(double input, double expected)
    {
        var settings = new AutoAttackSettings { Front_FOV = input };
        settings.Validate();
        Assert.Equal(expected, settings.Front_FOV);
    }

    [Fact]
    public void AutoAttackSettings_Validate_NanFrontFovRestoresHemisphere()
    {
        var settings = new AutoAttackSettings { Front_FOV = double.NaN };
        settings.Validate();
        Assert.Equal(180.0, settings.Front_FOV);
    }

    [Theory]
    [InlineData(180.0, 180.0)]
    [InlineData(0.5, 1.0)]
    [InlineData(500.0, 180.0)]
    public void AttackTargets_ClampFov_MatchesValidate(double input, double expected)
        => Assert.Equal(expected, AttackTargets.ClampFov(input));

    [Fact]
    public void AttackTargets_ClampFov_NanRestoresHemisphere()
        => Assert.Equal(180.0, AttackTargets.ClampFov(double.NaN));

    /// <summary>
    /// The facing hemisphere: yaw 0 looks down +Z, yaw 90 down -X, pitch -90 straight up.
    /// Self feet are the origin, so the eye sits 1.62 above every target's feet.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0, 0, 5, true)]      // ahead
    [InlineData(0, 0, 0, 0, -5, false)]    // behind
    [InlineData(180, 0, 0, 0, -5, true)]   // turned around
    [InlineData(180, 0, 0, 0, 5, false)]
    [InlineData(90, 0, -5, 0, 0, true)]    // yaw 90 faces -X
    [InlineData(90, 0, 5, 0, 0, false)]
    [InlineData(-90, 0, 5, 0, 0, true)]    // yaw -90 faces +X
    [InlineData(360, 0, 0, 0, 5, true)]    // yaw wraps without normalizing
    [InlineData(-180, 0, 0, 0, -5, true)]
    [InlineData(0, -90, 0, 10, 0, true)]   // looking straight up
    [InlineData(0, -90, 0, -10, 0, false)]
    [InlineData(0, 90, 0, -10, 0, true)]   // looking straight down
    [InlineData(0, 0, 5, 0, 0, false)]     // exactly side-on is not ahead (strict hemisphere)
    [InlineData(0, 0, 5, 0, 0.5, true)]    // a hair ahead of side-on is
    [InlineData(0, 0, 0, 0, 0.5, true)]    // close and ahead, eye far above the feet
    public void AttackTargets_IsInFront_IsAHemisphereAroundTheLookVector(
        float yaw, float pitch, double x, double y, double z, bool expected)
        => Assert.Equal(
            expected,
            AttackTargets.IsInFront(new Vec3d(0, 0, 0), yaw, pitch, new Vec3d(x, y, z)));

    /// <summary>
    /// The sight ray runs eye (feet + 1.62) to mid-torso (feet + 0.9): facing +Z from the origin, a three-high stone wall at z = 2 blocks a target at z = 5, while the same wall one lane over, a water wall, or no wall at all leaves it visible.
    /// </summary>
    [Fact]
    public void AttackTargets_HasLineOfSight_WallBlocksButNearbyLanesAndWaterDoNot()
    {
        var self = new Vec3d(0, 0, 0);
        var target = new Vec3d(0, 0, 5);

        World open = VisibilityWorldFixture.NewWorld();
        Assert.True(AttackTargets.HasLineOfSight(open, self, yaw: 0, pitch: 0, target));

        World walled = VisibilityWorldFixture.NewWorld();
        for (int y = 0; y <= 2; y++)
            VisibilityWorldFixture.SetStone(walled, new BlockPos(0, y, 2));

        Assert.False(AttackTargets.HasLineOfSight(walled, self, yaw: 0, pitch: 0, target));

        World sideWall = VisibilityWorldFixture.NewWorld();
        for (int y = 0; y <= 2; y++)
            VisibilityWorldFixture.SetStone(sideWall, new BlockPos(5, y, 2));

        Assert.True(AttackTargets.HasLineOfSight(sideWall, self, yaw: 0, pitch: 0, target));

        World water = VisibilityWorldFixture.NewWorld();
        for (int y = 0; y <= 2; y++)
            VisibilityWorldFixture.SetWater(water, new BlockPos(0, y, 2));

        Assert.True(AttackTargets.HasLineOfSight(water, self, yaw: 0, pitch: 0, target));
    }

    [Fact]
    public void AttackTargets_HasLineOfSight_BehindStaysInvisibleWithOrWithoutWalls()
    {
        var self = new Vec3d(0, 0, 0);
        var behind = new Vec3d(0, 0, -5);

        Assert.False(AttackTargets.HasLineOfSight(VisibilityWorldFixture.NewWorld(), self, 0, 0, behind));
        Assert.False(AttackTargets.HasLineOfSight(null, self, yaw: 0, pitch: 0, behind));
    }

    [Fact]
    public void AttackTargets_HasLineOfSight_NullWorldFallsBackToFacing()
    {
        var self = new Vec3d(0, 0, 0);
        Assert.True(AttackTargets.HasLineOfSight(null, self, yaw: 0, pitch: 0, new Vec3d(0, 0, 5)));
    }

    /// <summary>
    /// The FOV cone: targets are measured at eye height (feet + 1.62) so the angle is purely horizontal.
    /// Facing +Z, a target at (x, 1.62, 5) sits at atan(|x| / 5) off the crosshair: x = 5 is 45 degrees, x = 2.886 is 30 degrees.
    /// 180 keeps the hemisphere, 90 keeps 45 degrees each side, 60 keeps 30 degrees each side.
    /// </summary>
    [Theory]
    [InlineData(180.0, 0.0, 1.62, 5.0, true)]    // dead ahead, always in
    [InlineData(90.0, 0.0, 1.62, 5.0, true)]     // dead ahead, narrow cone still in
    [InlineData(60.0, 0.0, 1.62, 5.0, true)]
    [InlineData(180.0, 5.0, 1.62, 5.0, true)]    // 45 deg off: hemisphere keeps it
    [InlineData(90.0, 5.0, 1.62, 5.0, true)]     // 45 deg off: boundary of the 90 cone is inclusive
    [InlineData(60.0, 5.0, 1.62, 5.0, false)]    // 45 deg off: outside the 60 cone
    [InlineData(60.0, 2.886, 1.62, 5.0, true)]   // 30 deg off: boundary of the 60 cone is inclusive
    [InlineData(60.0, 3.5, 1.62, 5.0, false)]    // 35 deg off: outside the 60 cone
    [InlineData(90.0, 5.5, 1.62, 5.0, false)]    // 47.7 deg off: outside the 90 cone
    [InlineData(90.0, 0.0, 1.62, -5.0, false)]   // behind stays out whatever the cone
    [InlineData(60.0, 0.0, 1.62, -5.0, false)]
    public void AttackTargets_IsInFront_FovTrimsThePeriphery(
        double fov, double x, double y, double z, bool expected)
        => Assert.Equal(
            expected,
            AttackTargets.IsInFront(new Vec3d(0, 0, 0), yaw: 0, pitch: 0, new Vec3d(x, y, z), fov));

    [Fact]
    public void AttackTargets_IsInFront_DefaultFovKeepsTheHemisphere()
    {
        var self = new Vec3d(0, 0, 0);
        // A hair ahead of side-on: true with the default, false once the cone narrows.
        Assert.True(AttackTargets.IsInFront(self, 0, 0, new Vec3d(5, 0, 0.5)));
        Assert.False(AttackTargets.IsInFront(self, 0, 0, new Vec3d(5, 0, 0.5), 90.0));
    }

    [Fact]
    public void AttackTargets_HasLineOfSight_FovAppliesBeforeTheWallCheck()
    {
        var self = new Vec3d(0, 0, 0);
        var peripheral = new Vec3d(5.0, 1.62, 5.0);

        // Open ground: the cone alone decides.
        Assert.True(AttackTargets.HasLineOfSight(VisibilityWorldFixture.NewWorld(), self, 0, 0, peripheral, fovDegrees: 180.0));
        Assert.False(AttackTargets.HasLineOfSight(VisibilityWorldFixture.NewWorld(), self, 0, 0, peripheral, fovDegrees: 60.0));
        Assert.False(AttackTargets.HasLineOfSight(null, self, yaw: 0, pitch: 0, peripheral, fovDegrees: 60.0));
    }

    [Fact]
    public void AttackTargets_EyeHeight_SneakingLowersTheEye()
    {
        Assert.Equal(1.62, AttackTargets.EyeHeight(sneaking: false));
        Assert.Equal(1.27, AttackTargets.EyeHeight(sneaking: true));

        Vec3d eye = AttackTargets.EyePosition(new Vec3d(1, 5, 3), sneaking: true);
        Assert.Equal(1.0, eye.X, 9);
        Assert.Equal(6.27, eye.Y, 9);
        Assert.Equal(3.0, eye.Z, 9);
    }

    /// <summary>
    /// A fixed-shape stub: every queried state answers the same outline boxes, so a test can stand in a slab, a legged block, or any other partial shape without pulling version data.
    /// </summary>
    private sealed class BoxShapes(params Aabb[] boxes) : IBlockShapeSource
    {
        private readonly Aabb[] _boxes = boxes;

        public ReadOnlySpan<Aabb> GetCollisionShapes(BlockState state) => _boxes;

        public ReadOnlySpan<Aabb> GetCollisionShapes(int stateId) => _boxes;

        public ReadOnlySpan<Aabb> GetOutlineShapes(BlockState state) => _boxes;

        public ReadOnlySpan<Aabb> GetOutlineShapes(int stateId) => _boxes;
    }

    /// <summary>
    /// Issue 2: a crouching player under a top-slab ceiling.
    /// The slab fills the upper half of the block above the feet, so the standing eye (1.62) starts inside the slab and every ray is born occluded, while the sneaking eye (1.27) starts below it and sees the target ahead.
    /// Standing a half block lower (on a slab instead of a full block) moves even the standing eye out, which is why only one of the two reported setups worked.
    /// </summary>
    [Fact]
    public void AttackTargets_HasLineOfSight_SneakingSeesUnderASlabCeiling()
    {
        World world = VisibilityWorldFixture.NewWorld();
        var self = new Vec3d(0.5, 0, 0.5);
        var target = new Vec3d(0.5, 0, 5);
        VisibilityWorldFixture.SetStone(world, new BlockPos(0, 1, 0));

        // The ceiling block is a top slab: solid only in local y 0.5..1.0.
        var ceiling = new BoxShapes(new Aabb(0, 0.5, 0, 1, 1, 1));

        Assert.False(AttackTargets.HasLineOfSight(world, self, 0, 0, target, ceiling, 180.0, sneaking: false));
        Assert.True(AttackTargets.HasLineOfSight(world, self, 0, 0, target, ceiling, 180.0, sneaking: true));
    }

    /// <summary>
    /// Issue 1: a block with open lanes underneath (a cauldron stands on legs with the middle lanes open below local y 0.188) with the target below the player.
    /// The torso ray hits the body of the block; the low sample passes through the under-gap, so the target counts as visible.
    /// </summary>
    [Fact]
    public void AttackTargets_HasLineOfSight_LowSampleSeesThroughAnUnderGap()
    {
        World world = VisibilityWorldFixture.NewWorld();
        var self = new Vec3d(0.5, 2, 0.5);
        var target = new Vec3d(0.5, 0, 4);
        VisibilityWorldFixture.SetStone(world, new BlockPos(0, 2, 2));

        // Solid only in local y 0.2..1.0: open lanes below, like a cauldron's legs.
        var legged = new BoxShapes(new Aabb(0, 0.2, 0, 1, 1, 1));

        // Pin the mechanism: the legacy single torso ray is blocked, the low ray is not.
        Vec3d eye = AttackTargets.EyePosition(self, sneaking: false);
        Assert.True(AttackTargets.IsOccluded(world, eye, target.Add(0, 0.9, 0), legged));
        Assert.False(AttackTargets.IsOccluded(world, eye, target.Add(0, 0.2, 0), legged));

        Assert.True(AttackTargets.HasLineOfSight(world, self, 0, 0, target, legged, 180.0, sneaking: false));
    }

    /// <summary>
    /// Issue 3: a close target under a narrow cone.
    /// The feet sit steeply below the crosshair and fall outside a 60-degree cone, while the torso samples are inside it.
    /// Visibility is decided per sample, so looking at the mob's body counts even when its feet do not.
    /// </summary>
    [Fact]
    public void AttackTargets_HasLineOfSight_TorsoSampleKeepsCloseTargetsInANarrowCone()
    {
        World world = VisibilityWorldFixture.NewWorld();
        var self = new Vec3d(0, 0, 0);
        var close = new Vec3d(0, 0, 1.5);

        // The feet alone are outside the 60-degree cone; the open world leaves the ray to the torso.
        Assert.True(AttackTargets.HasLineOfSight(world, self, 0, 0, close, shapes: null, fovDegrees: 60.0));
        Assert.False(AttackTargets.HasLineOfSight(world, self, 0, 0, new Vec3d(0, 0, -1.5), shapes: null, fovDegrees: 60.0));
    }

    #endregion
    #region AutoDig

    [Fact]
    public void DigRules_IsAllowed_HonorsWhitelistAndBlacklist()
    {
        string[] blocks = ["stone"];
        Assert.True(DigRules.IsAllowed("minecraft:stone", BlockListType.Whitelist, blocks));
        Assert.False(DigRules.IsAllowed("minecraft:dirt", BlockListType.Whitelist, blocks));
        Assert.False(DigRules.IsAllowed("minecraft:stone", BlockListType.Blacklist, blocks));
        Assert.True(DigRules.IsAllowed("minecraft:dirt", BlockListType.Blacklist, blocks));
    }

    [Theory]
    [InlineData("minecraft:stone", ToolClass.Pickaxe)]
    [InlineData("minecraft:deepslate_diamond_ore", ToolClass.Pickaxe)]
    [InlineData("minecraft:oak_log", ToolClass.Axe)]
    [InlineData("minecraft:dirt", ToolClass.Shovel)]
    [InlineData("minecraft:oak_leaves", ToolClass.Hoe)]
    [InlineData("minecraft:white_wool", ToolClass.Shears)]
    [InlineData("minecraft:torch", ToolClass.None)]
    public void DigRules_RecommendedTool_MapsTheCommonFamilies(string blockId, ToolClass expected)
        => Assert.Equal(expected, DigRules.RecommendedTool(blockId));

    [Fact]
    public void DigRules_ToolItemIds_KeepsTheLegacyTierPreference()
    {
        string[] ids = DigRules.ToolItemIds(ToolClass.Pickaxe);
        Assert.Equal(
            ["netherite_pickaxe", "diamond_pickaxe", "iron_pickaxe", "golden_pickaxe", "stone_pickaxe", "wooden_pickaxe"],
            ids);
        Assert.Equal(["shears"], DigRules.ToolItemIds(ToolClass.Shears));
        Assert.Empty(DigRules.ToolItemIds(ToolClass.None));
    }

    [Fact]
    public void DigRules_Durability_TreatsUndamageableItemsAsInfinite()
    {
        Assert.Equal(int.MaxValue, DigRules.RemainingDurability(0, 0));
        Assert.Equal(10, DigRules.RemainingDurability(49, 59));
        Assert.True(DigRules.HasEnoughDurability(58, 59, 0));
        Assert.False(DigRules.HasEnoughDurability(58, 59, 2));
        Assert.True(DigRules.HasEnoughDurability(50, 59, 2));
    }

    [Fact]
    public void AutoDigSettings_Validate_ClampsTheLegacyRanges()
    {
        var settings = new AutoDigSettings
        {
            Durability_Limit = -5,
            Auto_Start_Delay = 0.0,
            Dig_Timeout = 0.0,
            Mode = "NOPE",
            List_Type = "NOPE",
            Location_Order = "NOPE",
        };
        settings.Validate();

        Assert.Equal(0, settings.Durability_Limit);
        Assert.Equal(0.1, settings.Auto_Start_Delay);
        Assert.Equal(0.1, settings.Dig_Timeout);
        Assert.Equal("lookat", settings.Mode);
        Assert.Equal("whitelist", settings.List_Type);
        Assert.Equal("distance", settings.Location_Order);
    }

    #endregion
    #region AutoCraft

    [Fact]
    public void CraftRules_GridShape_MatchesTheLegacyWindows()
    {
        Assert.Equal(4, CraftRules.GridSize(CraftType.Player));
        Assert.Equal(9, CraftRules.GridSize(CraftType.Table));
        Assert.Equal(9, CraftRules.FirstSourceSlot(CraftType.Player));
        Assert.Equal(10, CraftRules.FirstSourceSlot(CraftType.Table));
        Assert.True(CraftRules.IsCraftingTableWindow(46));
        Assert.False(CraftRules.IsCraftingTableWindow(63));
    }

    [Fact]
    public void CraftRules_FixSlots_PadsAndTruncatesLikeTheLegacyValidator()
    {
        string[] padded = CraftRules.FixSlots(["Stone"], CraftType.Table);
        Assert.Equal(9, padded.Length);
        Assert.Equal("Stone", padded[0]);
        Assert.Equal(string.Empty, padded[8]);

        string[] truncated = CraftRules.FixSlots(new string[9], CraftType.Player);
        Assert.Equal(4, truncated.Length);
    }

    [Fact]
    public void CraftRules_BuildMaterials_UsesOneBasedGridPositionsAndSkipsEmptyCells()
    {
        Dictionary<int, string> materials = CraftRules.BuildMaterials(["Stone", "Null", "", "minecraft:stone"]);
        Assert.Equal(2, materials.Count);
        Assert.Equal("stone", materials[1]);
        Assert.Equal("stone", materials[4]);
    }

    [Fact]
    public void CraftRules_GroupByMaterial_CollectsTheCellsEachMaterialFills()
    {
        // A 3x3 recipe with two materials: one source pickup must serve every cell that wants it.
        var materials = new Dictionary<int, string> { [1] = "stone", [2] = "stick", [4] = "stone", [5] = "stone" };
        List<KeyValuePair<string, List<int>>> groups = CraftRules.GroupByMaterial(materials);

        Assert.Equal(2, groups.Count);
        Assert.Equal("stone", groups[0].Key);
        Assert.Equal([1, 4, 5], groups[0].Value);
        Assert.Equal("stick", groups[1].Key);
        Assert.Equal([2], groups[1].Value);
    }

    [Fact]
    public void CraftRules_GroupByMaterial_KeepsGridOrderAndHandlesEmptyRecipes()
    {
        Assert.Empty(CraftRules.GroupByMaterial(new Dictionary<int, string>()));

        var single = new Dictionary<int, string> { [4] = "stone", [1] = "stone" };
        List<KeyValuePair<string, List<int>>> groups = CraftRules.GroupByMaterial(single);
        Assert.Single(groups);
        Assert.Equal([1, 4], groups[0].Value);
    }

    [Fact]
    public void CraftRules_EnsureUniqueNames_RepairsBlankAndDuplicateNames()
    {
        var recipes = new List<RecipeConfig>
        {
            new() { Name = "  " },
            new() { Name = "same" },
            new() { Name = "same" },
        };
        CraftRules.EnsureUniqueNames(recipes);

        Assert.NotEqual("  ", recipes[0].Name);
        Assert.Equal("same", recipes[1].Name);
        Assert.NotEqual("same", recipes[2].Name);
        Assert.Equal(3, new HashSet<string>(recipes.ConvertAll(r => r.Name)).Count);
    }

    [Fact]
    public void AutoCraftSettings_Validate_FixesRecipeShapeAndFailurePolicy()
    {
        var settings = new AutoCraftSettings
        {
            OnFailure = "NOPE",
            Recipes = [new RecipeConfig { Name = "r", Type = "table", Slots = ["Stone"] }],
        };
        settings.Validate();

        Assert.Equal("abort", settings.OnFailure);
        Assert.Equal(9, settings.Recipes[0].Slots.Length);
    }

    #endregion
    #region AutoFishing

    [Theory]
    [InlineData("minecraft:fishing_bobber", true)]
    [InlineData("minecraft:fishing_hook", true)]
    [InlineData("minecraft:item", false)]
    public void FishingRules_IsBobber_AcceptsTheEraSpellings(string typeId, bool expected)
        => Assert.Equal(expected, FishingRules.IsBobber(typeId));

    [Fact]
    public void FishingRules_IsBiteByMotion_MatchesTheLegacyThresholdRule()
    {
        // Stationary horizontally, dropping vertically: a bite.
        Assert.True(FishingRules.IsBiteByMotion(0.0001, -0.4, 0.0001, 0.001, 0.2));

        // Drifting horizontally: not a bite even with a big vertical delta.
        Assert.False(FishingRules.IsBiteByMotion(0.05, -0.4, 0.0001, 0.001, 0.2));

        // Barely moving at all: not a bite.
        Assert.False(FishingRules.IsBiteByMotion(0.0001, -0.01, 0.0001, 0.001, 0.2));
    }

    [Fact]
    public void FishingRules_IsBiteByVelocity_TriggersOnADownwardYank()
    {
        Assert.True(FishingRules.IsBiteByVelocity(-0.4, -0.2));
        Assert.False(FishingRules.IsBiteByVelocity(-0.1, -0.2));
    }

    [Fact]
    public void FishingRules_NextCastTimeout_DoublesUpToTheCap()
    {
        Assert.Equal(24, FishingRules.NextCastTimeout(12));
        Assert.Equal(6000, FishingRules.NextCastTimeout(3000));
        Assert.Equal(6000, FishingRules.NextCastTimeout(6000));
    }

    [Fact]
    public void FishingRules_ClampCursor_PingPongsOverTheMovementsList()
    {
        // Runs off the end: turn around one entry short of the edge.
        Assert.Equal((1, -1), FishingRules.ClampCursor(3, 1, 3));

        // Runs off the start: turn around at index 1.
        Assert.Equal((1, 1), FishingRules.ClampCursor(-1, -1, 3));

        // Inside the list: unchanged.
        Assert.Equal((1, 1), FishingRules.ClampCursor(1, 1, 3));

        // A single entry stays put.
        Assert.Equal((0, -1), FishingRules.ClampCursor(1, 1, 1));
    }

    [Fact]
    public void FishingRules_RodDurability_FallsBackToTheVanillaRodMaximum()
    {
        Assert.Equal(64, FishingRules.RodDurability(0, 0));
        Assert.Equal(60, FishingRules.RodDurability(4, 64));
    }

    [Fact]
    public void FishingRules_Gained_NamesTheCatchByInventoryDiff()
    {
        var before = new Dictionary<string, int> { ["minecraft:cod"] = 1, ["minecraft:string"] = 2 };
        var after = new Dictionary<string, int> { ["minecraft:cod"] = 3, ["minecraft:string"] = 2, ["minecraft:bone"] = 1 };

        Dictionary<string, int> gained = FishingRules.Gained(before, after);
        Assert.Equal(2, gained.Count);
        Assert.Equal(2, gained["minecraft:cod"]);
        Assert.Equal(1, gained["minecraft:bone"]);
    }

    [Fact]
    public void AutoFishingSettings_Validate_ClampsAndFlipsTheLegacyRanges()
    {
        var settings = new AutoFishingSettings
        {
            Cast_Delay = -1,
            Durability_Limit = 999,
            Stationary_Threshold = -0.5,
            Velocity_Hook_Threshold = 0.3,
            Detection_Warmup = -2,
        };
        settings.Validate();

        Assert.Equal(0, settings.Cast_Delay);
        Assert.Equal(64, settings.Durability_Limit);
        Assert.Equal(0.5, settings.Stationary_Threshold);
        Assert.Equal(-0.3, settings.Velocity_Hook_Threshold);
        Assert.Equal(0, settings.Detection_Warmup);
    }

    #endregion
    #region Farmer

    [Theory]
    [InlineData("wheat", CropType.Wheat)]
    [InlineData("NetherWart", CropType.NetherWart)]
    [InlineData("nether_wart", CropType.NetherWart)]
    [InlineData("Beetroots", CropType.Beetroot)]
    [InlineData("POTATO", CropType.Potato)]
    public void FarmRules_TryParseCrop_AcceptsTheLegacySpellings(string input, CropType expected)
    {
        Assert.True(FarmRules.TryParseCrop(input, out CropType crop));
        Assert.Equal(expected, crop);
    }

    [Fact]
    public void FarmRules_TryParseCrop_RejectsUnknown() => Assert.False(FarmRules.TryParseCrop("turnip", out _));

    [Fact]
    public void FarmRules_BlockAndItemMapping_MatchesTheLegacyTables()
    {
        Assert.Equal("wheat", FarmRules.HarvestBlock(CropType.Wheat));
        Assert.Equal("wheat_seeds", FarmRules.SeedItem(CropType.Wheat));
        Assert.Equal("farmland", FarmRules.SoilBlock(CropType.Wheat));

        Assert.Equal("melon", FarmRules.HarvestBlock(CropType.Melon));
        Assert.Equal("melon_stem", FarmRules.BoneMealBlock(CropType.Melon));
        Assert.Equal("melon_seeds", FarmRules.SeedItem(CropType.Melon));

        Assert.Equal("nether_wart", FarmRules.HarvestBlock(CropType.NetherWart));
        Assert.Equal("soul_sand", FarmRules.SoilBlock(CropType.NetherWart));
        Assert.False(FarmRules.CanBoneMeal(CropType.NetherWart));
        Assert.True(FarmRules.CanBoneMeal(CropType.Wheat));
    }

    [Fact]
    public void FarmRules_IsFullyGrown_UsesTheCropSpecificMaxAge()
    {
        Assert.True(FarmRules.IsFullyGrown(CropType.Wheat, 7, isHarvestBlock: true));
        Assert.False(FarmRules.IsFullyGrown(CropType.Wheat, 6, isHarvestBlock: true));
        Assert.True(FarmRules.IsFullyGrown(CropType.Beetroot, 3, isHarvestBlock: true));
        Assert.False(FarmRules.IsFullyGrown(CropType.Beetroot, 2, isHarvestBlock: true));
        Assert.True(FarmRules.IsFullyGrown(CropType.NetherWart, 3, isHarvestBlock: true));

        // The melon fruit block is always ready, but a melon stem at age 3 is not.
        Assert.True(FarmRules.IsFullyGrown(CropType.Melon, 0, isHarvestBlock: true));
        Assert.False(FarmRules.IsFullyGrown(CropType.Melon, 3, isHarvestBlock: false));
        Assert.True(FarmRules.IsFullyGrown(CropType.Melon, 7, isHarvestBlock: false));
    }

    [Fact]
    public void FarmRules_AgeFromState_ReadsTheAgeOffTheBlockStateSpan()
    {
        // 1.21.5 wheat occupies states 4342-4349, one per age, so the offset into the span is the age.
        Assert.Equal(0, FarmRules.AgeFromState(4342, 4342, 4349));
        Assert.Equal(7, FarmRules.AgeFromState(4349, 4342, 4349));
        Assert.Equal(3, FarmRules.AgeFromState(4345, 4342, 4349));

        // 1.21.5 beetroots occupy 13532-13535 (ages 0-3).
        Assert.Equal(3, FarmRules.AgeFromState(13535, 13532, 13535));

        // Out of span, or a nonsense span, reads as age 0 rather than throwing.
        Assert.Equal(0, FarmRules.AgeFromState(9999, 4342, 4349));
        Assert.Equal(0, FarmRules.AgeFromState(4342, 4349, 4342));
    }

    [Fact]
    public void FarmRules_AgeFromState_FeedsTheFullyGrownRule()
    {
        int wheatAge = FarmRules.AgeFromState(4349, 4342, 4349);
        Assert.True(FarmRules.IsFullyGrown(CropType.Wheat, wheatAge, isHarvestBlock: true));

        int youngWheat = FarmRules.AgeFromState(4346, 4342, 4349);
        Assert.False(FarmRules.IsFullyGrown(CropType.Wheat, youngWheat, isHarvestBlock: true));
    }

    [Fact]
    public void FarmRegion_RadiusUsesACircularFootprintAndBoundedVerticalBand()
    {
        FarmRegion region = FarmRegion.Around(new BlockPos(100, 64, 200), 30);

        Assert.Equal(FarmRegionKind.Radius, region.Kind);
        Assert.True(region.Contains(new BlockPos(130, 64, 200)));
        Assert.False(region.Contains(new BlockPos(130, 64, 230)));
        Assert.True(region.Contains(new BlockPos(100, 64 + FarmRules.RadiusVerticalRange, 200)));
        Assert.False(region.Contains(new BlockPos(100, 65 + FarmRules.RadiusVerticalRange, 200)));
    }

    [Fact]
    public void FarmRegion_AreaNormalizesBothCornersAndCountsInclusiveCells()
    {
        FarmRegion region = FarmRegion.Between(
            new BlockPos(12, 70, 25),
            new BlockPos(10, 69, 20));

        Assert.Equal(new BlockPos(10, 69, 20), region.Min);
        Assert.Equal(new BlockPos(12, 70, 25), region.Max);
        Assert.Equal(36L, region.CellCount);
        Assert.True(region.Contains(new BlockPos(11, 70, 23)));
        Assert.False(region.Contains(new BlockPos(13, 70, 23)));
    }

    [Fact]
    public void FarmRules_RadiusContractKeepsTheDefaultAndRaisesTheMaximum()
    {
        Assert.Equal(30, FarmRules.DefaultRadius);
        Assert.Equal(150, FarmRules.MaxRadius);
        Assert.Throws<ArgumentOutOfRangeException>(() => FarmRegion.Around(new BlockPos(0, 0, 0), 151));
    }

    [Fact]
    public void FarmerSettings_Validate_ClampsTheCycleDelay()
    {
        var settings = new FarmerSettings { Delay_Between_Tasks = 0.1 };
        settings.Validate();
        Assert.Equal(1.0, settings.Delay_Between_Tasks);
    }

    #endregion
    #region ItemsCollector

    [Theory]
    [InlineData("minecraft:item", true)]
    [InlineData("item", true)]
    [InlineData("minecraft:item_frame", false)]
    [InlineData("minecraft:zombie", false)]
    public void CollectionPlanner_IsItemEntity_OnlyMatchesTheDroppedItem(string typeId, bool expected)
        => Assert.Equal(expected, CollectionPlanner.IsItemEntity(typeId));

    [Fact]
    public void CollectionPlanner_Centroid_AveragesThePositions()
    {
        Vec3d centroid = CollectionPlanner.Centroid([new Vec3d(0, 0, 0), new Vec3d(2, 4, 6)]);
        Assert.Equal(1.0, centroid.X, 6);
        Assert.Equal(2.0, centroid.Y, 6);
        Assert.Equal(3.0, centroid.Z, 6);
        Assert.Equal(Vec3d.Zero, CollectionPlanner.Centroid([]));
    }

    [Fact]
    public void CollectionPlanner_Order_NearestFirstByDefault()
    {
        var self = new Vec3d(0, 0, 0);
        List<Vec3d> ordered = CollectionPlanner.Order(
            [new Vec3d(10, 0, 0), new Vec3d(1, 0, 0), new Vec3d(5, 0, 0)], self, prioritizeClusters: false);

        Assert.Equal(1.0, ordered[0].X, 6);
        Assert.Equal(5.0, ordered[1].X, 6);
        Assert.Equal(10.0, ordered[2].X, 6);
    }

    [Fact]
    public void CollectionPlanner_Order_ClusterModeStartsAtTheDensestPoint()
    {
        // Two drops sit together far away, one sits close to the player: cluster mode goes for the pair.
        var self = new Vec3d(0, 0, 0);
        List<Vec3d> ordered = CollectionPlanner.Order(
            [new Vec3d(1, 0, 0), new Vec3d(20, 0, 0), new Vec3d(21, 0, 0)], self, prioritizeClusters: true);

        Assert.Equal(20.0, ordered[0].X, 6);
        Assert.Equal(21.0, ordered[1].X, 6);
        Assert.Equal(1.0, ordered[2].X, 6);
    }

    [Fact]
    public void CollectionPlanner_ClampDelay_KeepsTheLegacyFloor()
    {
        Assert.Equal(100, CollectionPlanner.ClampDelay(10));
        Assert.Equal(300, CollectionPlanner.ClampDelay(300));
    }

    [Fact]
    public void ItemsCollectorSettings_Validate_ClampsDelayAndRadius()
    {
        var settings = new ItemsCollectorSettings { Delay_Between_Tasks = 5, Collection_Radius = -1 };
        settings.Validate();
        Assert.Equal(100, settings.Delay_Between_Tasks);
        Assert.Equal(30.0, settings.Collection_Radius);
    }
    #endregion
}
