using DMCBK.OfficialPlugins.Farmer;
using Umpk;
using Umpk.Game.Entities;
using Umpk.Game.Items;
using Umpk.Game.Items.Components;
using Umpk.Game.Registries;
using Umpk.Geometry;
using Umpk.Data.Java;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>Focused regression coverage for Farmer's crop profiles and bounded multi-target pass.</summary>
public sealed class FarmerRegressionTests
{
    public static TheoryData<string, CropType> CropAliases => new()
    {
        { "beetroot", CropType.Beetroot },
        { "beetroot_seeds", CropType.Beetroot },
        { "carrots", CropType.Carrot },
        { "melon", CropType.Melon },
        { "melons_and_pumpkins", CropType.MelonsAndPumpkins },
        { "nether_wart", CropType.NetherWart },
        { "pumpkin", CropType.Pumpkin },
        { "potatoes", CropType.Potato },
        { "wheat", CropType.Wheat },
        { "wheat_seeds", CropType.Wheat },
        { "sugar_cane", CropType.SugarCane },
        { "reeds", CropType.SugarCane },
        { "bamboo", CropType.Bamboo },
        { "cactus", CropType.Cactus },
        { "cocoa_beans", CropType.Cocoa },
    };

    [Theory]
    [MemberData(nameof(CropAliases))]
    public void EverySupportedCrop_HasACommandSpelling(string input, CropType expected)
    {
        Assert.True(FarmRules.TryParseCrop(input, out CropType actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CompletionOffersCanonicalCropAndPlantingItemNames()
    {
        Assert.Contains("sugar_cane", FarmRules.CropSuggestions);
        Assert.Contains("cocoa_beans", FarmRules.CropSuggestions);
        Assert.Contains("wheat_seeds", FarmRules.CropSuggestions);
        Assert.Contains("melons_and_pumpkins", FarmRules.CropSuggestions);
        Assert.DoesNotContain("melon_seeds", FarmRules.CropSuggestions);
        Assert.DoesNotContain("pumpkin_seeds", FarmRules.CropSuggestions);
        Assert.DoesNotContain("radius:30", FarmRules.CropSuggestions);
    }

    [Fact]
    public void CropAvailability_RejectsOnlyCropsMissingFromTheConnectedEra()
    {
        Assert.False(FarmRules.IsAvailableOnProtocol(CropType.Beetroot, JavaVersions.V1_8.Version.Protocol));
        Assert.False(FarmRules.IsAvailableOnProtocol(CropType.Bamboo, JavaVersions.V1_8.Version.Protocol));
        Assert.True(FarmRules.IsAvailableOnProtocol(CropType.Beetroot, JavaVersions.V1_9.Version.Protocol));
        Assert.True(FarmRules.IsAvailableOnProtocol(CropType.Bamboo, JavaVersions.V1_14.Version.Protocol));
        Assert.True(FarmRules.IsAvailableOnProtocol(CropType.Wheat, JavaVersions.V1_8.Version.Protocol));
        Assert.True(FarmRules.IsAvailableOnProtocol(CropType.MelonsAndPumpkins, JavaVersions.V1_8.Version.Protocol));
        Assert.Equal("1.9", FarmRules.FirstSupportedVersionName(CropType.Beetroot));
        Assert.Equal("1.14", FarmRules.FirstSupportedVersionName(CropType.Bamboo));
    }

    [Fact]
    public void AreaWorkingTargets_IncludeTheCropLayerAboveMappedSupports()
    {
        FarmRegion area = FarmRegion.Between(
            new BlockPos(1033, 63, 477),
            new BlockPos(1011, 63, 463));

        Assert.True(area.ContainsWorkingTarget(new BlockPos(1011, 63, 463)));
        Assert.True(area.ContainsWorkingTarget(new BlockPos(1033, 64, 477)));
        Assert.False(area.ContainsWorkingTarget(new BlockPos(1010, 64, 463)));
        Assert.False(area.ContainsWorkingTarget(new BlockPos(1033, 64, 478)));
        Assert.False(area.ContainsWorkingTarget(new BlockPos(1033, 65, 477)));
    }

    [Fact]
    public void RadiusWorkingTargets_RetainTheConfiguredVerticalBand()
    {
        FarmRegion radius = FarmRegion.Around(new BlockPos(0, 64, 0), 10);

        Assert.True(radius.ContainsWorkingTarget(new BlockPos(0, 72, 0)));
        Assert.False(radius.ContainsWorkingTarget(new BlockPos(0, 73, 0)));
    }

    [Fact]
    public void StorageCoordinates_ParseNormalizeAndRejectInvalidValues()
    {
        Assert.True(StorageCoordinates.TryParse(" 12  64 -9 ", out BlockPos position));
        Assert.Equal(new BlockPos(12, 64, -9), position);
        Assert.Equal("12 64 -9", StorageCoordinates.Format(position));
        Assert.False(StorageCoordinates.TryParse("12 64", out _));
        Assert.False(StorageCoordinates.TryParse("12 y -9", out _));
    }

    [Fact]
    public void StorageSettings_ClampMarginAndClearInvalidCoordinateValues()
    {
        var settings = new FarmerSettings
        {
            Storage_Search_Margin = 999,
            Bonemeal_Chest_Coordinates = "1 2 3",
            Seeds_Chest_Coordinates = "not coordinates",
        };

        settings.Validate();

        Assert.Equal(128, settings.Storage_Search_Margin);
        Assert.Equal("1 2 3", settings.Bonemeal_Chest_Coordinates);
        Assert.Equal(string.Empty, settings.Seeds_Chest_Coordinates);
    }

    [Theory]
    [InlineData(CropType.Beetroot, FarmMode.BreakAndReplant, "beetroots", "farmland", "beetroot_seeds", 3, true)]
    [InlineData(CropType.Carrot, FarmMode.BreakAndReplant, "carrots", "farmland", "carrot", 7, true)]
    [InlineData(CropType.Melon, FarmMode.StemFruit, "melon", "farmland", "melon_seeds", 7, true)]
    [InlineData(CropType.NetherWart, FarmMode.BreakAndReplant, "nether_wart", "soul_sand", "nether_wart", 3, false)]
    [InlineData(CropType.Pumpkin, FarmMode.StemFruit, "pumpkin", "farmland", "pumpkin_seeds", 7, true)]
    [InlineData(CropType.Potato, FarmMode.BreakAndReplant, "potatoes", "farmland", "potato", 7, true)]
    [InlineData(CropType.Wheat, FarmMode.BreakAndReplant, "wheat", "farmland", "wheat_seeds", 7, true)]
    [InlineData(CropType.SugarCane, FarmMode.PreserveVerticalBase, "sugar_cane", null, null, 0, false)]
    [InlineData(CropType.Bamboo, FarmMode.PreserveVerticalBase, "bamboo", null, null, 0, false)]
    [InlineData(CropType.Cactus, FarmMode.PreserveVerticalBase, "cactus", null, null, 0, false)]
    [InlineData(CropType.Cocoa, FarmMode.CocoaPod, "cocoa", null, "cocoa_beans", 2, true)]
    public void EveryCrop_HasTheExpectedLifecycleProfile(
        CropType crop,
        FarmMode mode,
        string harvest,
        string? support,
        string? plantingItem,
        int maxAge,
        bool boneMeal)
    {
        CropProfile profile = FarmRules.Profile(crop);
        Assert.Equal(mode, profile.Mode);
        Assert.Equal(harvest, profile.HarvestBlock);
        Assert.Equal(support, profile.SupportBlock);
        Assert.Equal(plantingItem, profile.PlantItem);
        Assert.Equal(maxAge, profile.MaxAge);
        Assert.Equal(boneMeal, profile.CanBoneMeal);
    }

    [Theory]
    [InlineData(CropType.SugarCane, 1, false)]
    [InlineData(CropType.SugarCane, 2, true)]
    [InlineData(CropType.SugarCane, 3, true)]
    [InlineData(CropType.Bamboo, 1, false)]
    [InlineData(CropType.Bamboo, 2, true)]
    [InlineData(CropType.Bamboo, 3, true)]
    [InlineData(CropType.Cactus, 1, false)]
    [InlineData(CropType.Cactus, 2, true)]
    [InlineData(CropType.Cactus, 3, true)]
    public void VerticalCrops_OnlySelectTheSecondBlockAndPreserveTheBase(
        CropType crop,
        int stalkHeight,
        bool expected)
    {
        string block = FarmRules.HarvestBlock(crop);
        string? here = stalkHeight >= 2 ? block : null;
        string? below = stalkHeight >= 1 ? block : null;

        Assert.Equal(expected, FarmRules.IsVerticalHarvestLevel(crop, here, below, null));
        Assert.False(FarmRules.IsVerticalHarvestLevel(crop, block, block, block));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void Cocoa_OnlyMatureAgeIsHarvestable(int age, bool expected) =>
        Assert.Equal(expected, FarmRules.IsFullyGrown(CropType.Cocoa, age, isHarvestBlock: true));

    [Theory]
    [InlineData("north", Direction.North)]
    [InlineData("south", Direction.South)]
    [InlineData("west", Direction.West)]
    [InlineData("east", Direction.East)]
    public void Cocoa_FacingAndJungleSupportRulesAreExplicit(string input, Direction expected)
    {
        Assert.True(FarmRules.TryParseHorizontalDirection(input, out Direction actual));
        Assert.Equal(expected, actual);
        Assert.True(FarmRules.IsJungleWood("minecraft:jungle_log"));
        Assert.True(FarmRules.IsJungleWood("stripped_jungle_wood"));
        Assert.False(FarmRules.IsJungleWood("minecraft:oak_log"));
    }

    [Theory]
    [InlineData(CropType.Beetroot, "beetroot", true)]
    [InlineData(CropType.Beetroot, "beetroot_seeds", true)]
    [InlineData(CropType.Carrot, "carrot", true)]
    [InlineData(CropType.Melon, "melon_slice", true)]
    [InlineData(CropType.Melon, "melon", true)]
    [InlineData(CropType.NetherWart, "nether_wart", true)]
    [InlineData(CropType.Pumpkin, "pumpkin", true)]
    [InlineData(CropType.Potato, "poisonous_potato", true)]
    [InlineData(CropType.Wheat, "wheat_seeds", true)]
    [InlineData(CropType.SugarCane, "sugar_cane", true)]
    [InlineData(CropType.Bamboo, "bamboo", true)]
    [InlineData(CropType.Cactus, "cactus", true)]
    [InlineData(CropType.Cocoa, "cocoa_beans", true)]
    [InlineData(CropType.Wheat, "diamond", false)]
    [InlineData(CropType.Cocoa, "wheat_seeds", false)]
    public void CollectionFilter_UsesTheDecodedStackIdentity(CropType crop, string itemId, bool expected) =>
        Assert.Equal(expected, FarmRules.IsRelevantDrop(crop, Stack(itemId)));

    [Fact]
    public void RemovedStemSeedAliases_AreNotAcceptedAsCommands()
    {
        Assert.False(FarmRules.TryParseCrop("melon_seeds", out _));
        Assert.False(FarmRules.TryParseCrop("pumpkin_seeds", out _));
    }

    [Fact]
    public void MixedStemRules_RecognizeBothCropFamilies()
    {
        IReadOnlyList<StemVariant> variants = FarmRules.StemVariants(CropType.MelonsAndPumpkins);

        Assert.Equal([CropType.Melon, CropType.Pumpkin], variants.Select(variant => variant.Crop));
        Assert.True(FarmRules.IsStemFruitBlock(CropType.MelonsAndPumpkins, "pumpkin"));
        Assert.True(FarmRules.IsStemGrowthBlock(CropType.MelonsAndPumpkins, "melon_stem"));
        Assert.True(FarmRules.IsStemGrowthBlock(CropType.MelonsAndPumpkins, "attached_melon_stem"));
        Assert.True(FarmRules.IsAttachedStemBlock(CropType.MelonsAndPumpkins, "attached_pumpkin_stem"));
        Assert.False(FarmRules.IsAttachedStemBlock(CropType.MelonsAndPumpkins, "pumpkin_stem"));
        Assert.Equal(CropType.Melon, FarmRules.StemVariantFor(CropType.MelonsAndPumpkins, "attached_melon_stem")?.Crop);
        Assert.True(FarmRules.IsPlantingItem(CropType.MelonsAndPumpkins, Stack("pumpkin_seeds")));
        Assert.True(FarmRules.IsRelevantDrop(CropType.MelonsAndPumpkins, Stack("melon_slice")));
    }

    [Fact]
    public void LegacyCocoaBeans_AreRecognizedByDyeDamageOnly()
    {
        Assert.True(FarmRules.IsRelevantDrop(CropType.Cocoa, Stack("dye", damage: 3)));
        Assert.False(FarmRules.IsRelevantDrop(CropType.Cocoa, Stack("dye", damage: 2)));
    }

    [Theory]
    [InlineData("minecraft:item", true)]
    [InlineData("item", true)]
    [InlineData("minecraft:experience_orb", false)]
    public void CollectionRecognizesDroppedItemsWithoutDependingOnStackMetadata(string entityType, bool expected) =>
        Assert.Equal(expected, FarmRules.IsDroppedItemEntity(entityType));

    [Fact]
    public void NetheriteAxe_IsPreferredBeforeDiamondAndExistingFallbacks()
    {
        Assert.Equal(
            ["netherite_axe", "diamond_axe", "iron_axe", "golden_axe", "stone_axe"],
            FarmRules.HarvestAxes);
    }

    [Fact]
    public void Lifecycle_EnforcesEveryWholeFarmBarrierInOrder()
    {
        Assert.Equal(FarmState.CollectingAfterHarvest, FarmerLifecycle.Next(FarmState.Harvesting));
        Assert.Equal(FarmState.Planting, FarmerLifecycle.Next(FarmState.CollectingAfterHarvest));
        Assert.Equal(FarmState.BoneMealing, FarmerLifecycle.Next(FarmState.Planting));
        Assert.Equal(FarmState.Harvesting, FarmerLifecycle.Next(FarmState.BoneMealing));
    }

    [Theory]
    [InlineData(CropType.Carrot, 1, 4)]
    [InlineData(CropType.Beetroot, 1, 6)]
    [InlineData(CropType.Carrot, 133, 320)]
    [InlineData(CropType.NetherWart, 119, 0)]
    public void BoneMealRestockBudget_IsCropSpecificAndCappedAtFiveStacks(CropType crop, int targets, int expected) =>
        Assert.Equal(expected, FarmRules.BoneMealRestockTarget(crop, targets));

    [Fact]
    public void CarrotStorageBatch_DepositsEverySurplusItemAndKeepsExactFarmReserve()
    {
        const int reserve = 133;
        var stacks = new List<int> { 61, 64, 64, 64, 64, 64, 64, 64 };
        int carried = stacks.Sum();
        int deposited = 0;

        while (stacks.FindIndex(count => FarmRules.CanDepositWholeStack(carried, count, reserve)) is int index and >= 0)
        {
            int moved = stacks[index];
            stacks.RemoveAt(index);
            carried -= moved;
            deposited += moved;
        }

        int partial = FarmRules.PartialDepositAmount(carried, reserve);
        Assert.Contains(stacks, count => count >= partial);
        carried -= partial;
        deposited += partial;

        Assert.Equal(376, deposited);
        Assert.Equal(reserve, carried);
    }

    [Fact]
    public void Traversal_KeepsEachFarmRowContiguousInsteadOfExpandingInDistanceRings()
    {
        BlockPos[] targets =
        [
            new(1011, 64, 473), new(1017, 64, 473), new(1023, 64, 473),
            new(1011, 64, 475), new(1017, 64, 475), new(1023, 64, 475),
            new(1011, 64, 477), new(1017, 64, 477), new(1023, 64, 477),
        ];

        IReadOnlyList<BlockPos> route = FarmTraversal.Order(targets, new BlockPos(1012, 64, 475));
        Assert.Equal(9, route.Count);
        Assert.Equal(3, route.GroupBy(position => position.Z).Count());

        int rowTransitions = 0;
        var completedRows = new HashSet<int>();
        int currentRow = route[0].Z;
        foreach (BlockPos position in route.Skip(1))
        {
            if (position.Z == currentRow)
                continue;

            Assert.DoesNotContain(position.Z, completedRows);
            completedRows.Add(currentRow);
            currentRow = position.Z;
            rowTransitions++;
        }

        Assert.Equal(2, rowTransitions);
    }

    [Fact]
    public void Traversal_ChoosesTheNearestSerpentineEndpoint()
    {
        IReadOnlyList<BlockPos> route = FarmTraversal.Order(
            [new BlockPos(0, 64, 0), new BlockPos(1, 64, 0), new BlockPos(2, 64, 0)],
            new BlockPos(3, 64, 0));

        Assert.Equal(new BlockPos(2, 64, 0), route[0]);
        Assert.Equal(new BlockPos(0, 64, 0), route[^1]);
    }

    [Fact]
    public void DropTraversal_UsesOneWaypointForAllStacksInTheSamePickupBlock()
    {
        IReadOnlyList<Vec3d> route = FarmDropTraversal.Order(
            [
                new Vec3d(0.15, 64.1, 0.15),
                new Vec3d(0.85, 64.4, 0.85),
                new Vec3d(2.15, 64.1, 0.15),
            ],
            new Vec3d(0.5, 64, 0.5));

        Assert.Equal(2, route.Count);
        Assert.Contains(route, target => BlockPos.Containing(target) == new BlockPos(0, 64, 0));
        Assert.Contains(route, target => BlockPos.Containing(target) == new BlockPos(2, 64, 0));
    }

    [Fact]
    public void DropTraversal_PrioritizesDenseClustersBeforeNearbySingletons()
    {
        IReadOnlyList<Vec3d> route = FarmDropTraversal.Order(
            [
                new Vec3d(1.2, 64.1, 0.2),
                new Vec3d(10.2, 64.1, 0.2),
                new Vec3d(11.2, 64.1, 0.2),
                new Vec3d(12.2, 64.1, 0.2),
            ],
            new Vec3d(0.5, 64, 0.5));

        Assert.True(BlockPos.Containing(route[0]).X >= 10);
        Assert.Equal(new BlockPos(1, 64, 0), BlockPos.Containing(route[^1]));
    }

    [Fact]
    public void LiveDropTraversal_GroupsEntityIdsWithoutLosingTheirIdentity()
    {
        IReadOnlyList<FarmDropTraversal.Waypoint> route = FarmDropTraversal.Order(
            [
                new FarmDropTraversal.EntityTarget(12, new Vec3d(0.15, 119, 0.15)),
                new FarmDropTraversal.EntityTarget(13, new Vec3d(0.85, 119, 0.85)),
                new FarmDropTraversal.EntityTarget(20, new Vec3d(4.15, 119, 0.15)),
            ],
            new Vec3d(0.5, 119, 0.5));

        Assert.Equal(2, route.Count);
        Assert.Contains(route, waypoint => waypoint.EntityIds.SequenceEqual([12, 13]));
    }

    [Theory]
    [InlineData(118.94, 119)]
    [InlineData(119.00, 119)]
    public void DropStandingLevel_UsesTheActualRestingFeetBlock(double itemY, int expected) =>
        Assert.Equal(expected, FarmRules.DropFeetLevel(itemY));

    [Fact]
    public void CocoaStances_CoverThreeHeightsWithoutUsingThePodOrLog()
    {
        BlockPos pod = new(10, 70, 10);
        BlockPos support = pod.Offset(Direction.East);
        IReadOnlyList<BlockPos> stances = FarmerPlugin.CocoaStances(pod, Direction.East);

        Assert.Contains(stances, stance => stance.Y == 69);
        Assert.Contains(stances, stance => stance.Y == 68);
        Assert.Contains(stances, stance => stance.Y == 70);
        Assert.DoesNotContain(pod, stances);
        Assert.DoesNotContain(support, stances);
    }

    [Fact]
    public void InteractionReach_UsesEyeToBlockDistanceRatherThanGoalCompletion()
    {
        var target = new BlockPos(0, 64, 0);
        Assert.True(FarmRules.IsWithinInteractionReach(new Vec3d(4.4, 63, 0.5), target));
        Assert.False(FarmRules.IsWithinInteractionReach(new Vec3d(5.6, 63, 0.5), target));
        Assert.False(FarmRules.IsWithinInteractionReach(new Vec3d(0.5, 70, 0.5), target));
    }

    [Fact]
    public async Task TargetPass_PlantsMoreThanTwoTargetsAndRechecksStockEverySecondTarget()
    {
        int stockChecks = 0;
        var prepared = new List<int>();
        var acted = new List<int>();

        FarmerPassResult result = await FarmerTargetPass.RunAsync(
            [0, 1, 2, 3, 4],
            () => Task.FromResult(++stockChecks > 0),
            target =>
            {
                prepared.Add(target);
                return Task.FromResult(true);
            },
            target =>
            {
                acted.Add(target);
                return Task.FromResult(true);
            },
            () => true);

        Assert.Equal(5, result.Succeeded);
        Assert.Equal(3, stockChecks);
        Assert.Equal([0, 1, 2, 3, 4], prepared);
        Assert.Equal([0, 1, 2, 3, 4], acted);
    }

    [Fact]
    public async Task TargetPass_ContinuesAfterStaleUnreachableAndFailedPlacementTargets()
    {
        var acted = new List<int>();
        FarmerPassResult result = await FarmerTargetPass.RunAsync(
            [0, 1, 2, 3, 4],
            () => Task.FromResult(true),
            target => Task.FromResult(target is not (1 or 2)),
            target =>
            {
                acted.Add(target);
                return Task.FromResult(target != 3);
            },
            () => true);

        Assert.Equal(2, result.Succeeded);
        Assert.Equal(2, result.Skipped);
        Assert.Equal(1, result.Failed);
        Assert.Equal([0, 3, 4], acted);
    }

    [Fact]
    public async Task TargetPass_StopsCleanlyWhenPeriodicStockCheckObservesConsumption()
    {
        int stockChecks = 0;
        var acted = new List<int>();
        FarmerPassResult result = await FarmerTargetPass.RunAsync(
            [0, 1, 2, 3],
            () => Task.FromResult(++stockChecks == 1),
            _ => Task.FromResult(true),
            target =>
            {
                acted.Add(target);
                return Task.FromResult(true);
            },
            () => true);

        Assert.True(result.StockExhausted);
        Assert.Equal(2, result.Succeeded);
        Assert.Equal([0, 1], acted);
    }

    [Fact]
    public async Task TargetPass_WithNoTargetsDoesNotSpinOrProbeInventory()
    {
        int calls = 0;
        FarmerPassResult result = await FarmerTargetPass.RunAsync<int>(
            [],
            () =>
            {
                calls++;
                return Task.FromResult(true);
            },
            _ => Task.FromResult(true),
            _ => Task.FromResult(true),
            () => true);

        Assert.Equal(0, calls);
        Assert.Equal(0, result.Succeeded);
    }

    private static ItemStack Stack(string id, int damage = 0)
    {
        RegistryEntry<ItemDefinition> item = Registry.Direct(
            1,
            Identifier.Minecraft(id),
            new ItemDefinition(64));
        DataComponentMap components = damage == 0
            ? DataComponentMap.Empty
            : DataComponentMap.Empty.With(DataComponents.Damage, new DamageComponent(damage));
        return new ItemStack(item, 1, components);
    }
}
