using DMCBK.OfficialPlugins.AutoCraft;
using Umpk.Geometry;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The headless-testable half of AutoCraft v2: the built-in recipe library and its tolerant name matching, order-count parsing, the ASCII renderer behind info/show, chest entry validation and candidate ordering, and settings validation including the legacy table migration.
/// </summary>
public sealed class AutoCraftV2Tests
{
    #region Library matching

    [Fact]
    public void BuiltIn_ContainsTorchesYieldingFour()
    {
        CraftRecipe torch = Assert.Single(CraftLibrary.BuiltIn, r => r.Name == "Torch");
        Assert.Equal(4, torch.Count);
        Assert.Equal("torch", torch.Result);
    }

    [Theory]
    [InlineData("torch", "Torch")]
    [InlineData("TORCHES", "Torch")]
    [InlineData("steel-gear", "Steel Gear")]
    [InlineData("steel_gear", "Steel Gear")]
    [InlineData("Crafting Table", "Crafting Table")]
    [InlineData("workbench", "Crafting Table")]
    [InlineData("advanced circuit", "Advanced Circuit")]
    public void Resolve_AcceptsNamesAliasesAndSeparators(string query, string expected)
    {
        (CraftRecipe? recipe, _) = CraftLibrary.Resolve(CraftLibrary.BuiltIn, query);
        Assert.NotNull(recipe);
        Assert.Equal(expected, recipe.Name);
    }

    [Fact]
    public void Resolve_Miss_ReturnsSubstringSuggestions()
    {
        (CraftRecipe? recipe, string[] suggestions) = CraftLibrary.Resolve(CraftLibrary.BuiltIn, "rai");
        Assert.Null(recipe);
        Assert.Contains("Rail", suggestions);
        Assert.Contains("Powered Rail", suggestions);
    }

    [Fact]
    public void Resolve_Typo_StillSuggests()
    {
        (CraftRecipe? recipe, string[] suggestions) = CraftLibrary.Resolve(CraftLibrary.BuiltIn, "toch");
        Assert.Null(recipe);
        Assert.Contains("Torch", suggestions);
    }

    [Fact]
    public void Resolve_BlankQuery_ReturnsNothing()
    {
        (CraftRecipe? recipe, string[] suggestions) = CraftLibrary.Resolve(CraftLibrary.BuiltIn, "   ");
        Assert.Null(recipe);
        Assert.Empty(suggestions);
    }

    [Theory]
    [InlineData("", "", 0)]
    [InlineData("toch", "torch", 1)]
    [InlineData("torch", "torch", 0)]
    public void Distance_MeasuresShortNames(string a, string b, int expected)
        => Assert.Equal(expected, CraftLibrary.Distance(a, b));

    [Fact]
    public void EffectiveRecipes_CustomWithSameName_WinsOverBuiltIn()
    {
        var customs = new List<CraftRecipe>
        {
            new() { Name = "torch", Station = CraftStation.Player, Result = "soul_torch", Count = 2, Slots = ["coal", "", "stick", ""] },
        };

        IReadOnlyList<CraftRecipe> merged = CraftLibrary.EffectiveRecipes(customs);
        (CraftRecipe? recipe, _) = CraftLibrary.Resolve(merged, "torch");
        Assert.NotNull(recipe);
        Assert.Equal("soul_torch", recipe.Result);
        Assert.Equal(2, recipe.Count);
        Assert.Single(merged, r => CraftLibrary.NormalizeName(r.Name) == "torch");
    }

    #endregion
    #region Order parsing

    [Theory]
    [InlineData("64 torches", 64, "torches")]
    [InlineData("2s stone bricks", 128, "stone bricks")]
    [InlineData("1s rails", 64, "rails")]
    [InlineData("x16 rails", 16, "rails")]
    [InlineData("torches", -1, "torches")]
    [InlineData("Steel Gear", -1, "Steel Gear")]
    [InlineData("xray", -1, "xray")]
    public void TryParseOrder_SplitsCountsFromNames(string text, int count, string name)
    {
        Assert.True(CraftLibrary.TryParseOrder(text, out int actualCount, out string actualName));
        Assert.Equal(count, actualCount);
        Assert.Equal(name, actualName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("64")]
    public void TryParseOrder_RejectsEmpties(string? text)
        => Assert.False(CraftLibrary.TryParseOrder(text, out _, out _));

    #endregion
    #region ASCII rendering

    [Fact]
    public void RenderAscii_Torch_PrintsTwoColumnsWithNeeds()
    {
        (CraftRecipe? recipe, _) = CraftLibrary.Resolve(CraftLibrary.BuiltIn, "torch");
        Assert.NotNull(recipe);
        Assert.Equal(
            "Torch x4  [any]\n+---+---+\n|coa|   |\n+---+---+\n|sti|   |\n+---+---+\nneeds: coal x1, stick x1",
            CraftLibrary.RenderAscii(recipe));
    }

    [Fact]
    public void RenderAscii_Table_PrintsThreeColumns()
    {
        (CraftRecipe? recipe, _) = CraftLibrary.Resolve(CraftLibrary.BuiltIn, "piston");
        Assert.NotNull(recipe);
        string ascii = CraftLibrary.RenderAscii(recipe);
        Assert.StartsWith("Piston x1  [table]\n+---+---+---+\n", ascii);
        Assert.Contains("needs: oak_planks x3", ascii);
    }

    [Theory]
    [InlineData("coal", "coa")]
    [InlineData("stick", "sti")]
    [InlineData("minecraft:iron_ingot", "iin")]
    [InlineData(null, "   ")]
    public void CodeFor_IsDeterministic(string? item, string expected)
        => Assert.Equal(expected, CraftLibrary.CodeFor(item));

    #endregion
    #region Chests

    [Theory]
    [InlineData("input", ChestDirection.Input)]
    [InlineData("OUTPUT", ChestDirection.Output)]
    [InlineData("nope", ChestDirection.Input)]
    public void ParseDirection_FallsBackToInput(string word, ChestDirection expected)
        => Assert.Equal(expected, ChestRules.ParseDirection(word));

    [Theory]
    [InlineData("chest", ChestKind.Chest)]
    [InlineData("Barrel", ChestKind.Barrel)]
    [InlineData("SHULKER", ChestKind.Shulker)]
    [InlineData("crate", ChestKind.Chest)]
    public void ParseKind_FallsBackToChest(string word, ChestKind expected)
        => Assert.Equal(expected, ChestRules.ParseKind(word));

    [Fact]
    public void ValidateEntry_NeedsALabelOrCoordinates()
    {
        Assert.Equal(ChestRules.NoLocator, ChestRules.ValidateEntry(null, "  "));
        Assert.Equal(ChestRules.BadPosition, ChestRules.ValidateEntry(null, "12 64"));
        Assert.Null(ChestRules.ValidateEntry("Main", null));
        Assert.Null(ChestRules.ValidateEntry(null, "12 64 -30"));
    }

    [Fact]
    public void Coords_RoundTrips()
    {
        Assert.True(Coords.TryParse("12 64 -30", out BlockPos pos));
        Assert.Equal(new BlockPos(12, 64, -30), pos);
        Assert.Equal("12 64 -30", Coords.Format(pos));
        Assert.False(Coords.TryParse("12 64", out _));
    }

    [Fact]
    public void OrderForJob_PutsFilteredFirstThenShared()
    {
        var entries = new List<ChestConfig>
        {
            new() { Direction = "input", Kind = "chest", Label = "Shared" },
            new() { Direction = "input", Kind = "barrel", Label = "Iron", Item = "iron_ingot" },
            new() { Direction = "output", Kind = "chest", Label = "Out" },
            new() { Direction = "input", Kind = "chest", Label = "Coal", Item = "coal" },
        };

        IReadOnlyList<ChestConfig> ordered = ChestRules.OrderForJob(
            entries, ChestDirection.Input, new HashSet<string>(StringComparer.Ordinal) { "iron_ingot" });

        Assert.Equal(2, ordered.Count);
        Assert.Equal("Iron", ordered[0].Label);
        Assert.Equal("Shared", ordered[1].Label);
    }

    [Theory]
    [InlineData("minecraft:chest", "chest", true)]
    [InlineData("minecraft:trapped_chest", "chest", true)]
    [InlineData("minecraft:barrel", "barrel", true)]
    [InlineData("minecraft:barrel", "chest", false)]
    [InlineData("minecraft:red_shulker_box", "shulker", true)]
    [InlineData("minecraft:shulker_box", "shulker", true)]
    [InlineData("minecraft:furnace", "shulker", false)]
    public void MatchesKind_AcceptsExpectedBlocks(string blockId, string kind, bool expected)
        => Assert.Equal(expected, ChestRules.MatchesKind(blockId, ChestRules.ParseKind(kind)));

    #endregion
    #region Settings

    [Fact]
    public void Validate_ClampsRadiusAndNormalizesBench()
    {
        var settings = new AutoCraftSettings { BenchRadius = 100, Bench = "nope" };
        settings.Validate();
        Assert.Equal(32, settings.BenchRadius);

        settings.BenchRadius = 0;
        settings.Bench = "12 64 -30";
        settings.Validate();
        Assert.Equal(2, settings.BenchRadius);
        Assert.Equal("12 64 -30", settings.Bench);
    }

    [Fact]
    public void Validate_DropsLocatorlessChestsAndPrefersCoordinates()
    {
        var settings = new AutoCraftSettings
        {
            Chests =
            [
                new ChestConfig { Direction = "INPUT", Kind = "BARREL", Label = "  ", Position = " x " },
                new ChestConfig { Direction = "output", Kind = "shulker", Label = "Both", Position = "1 2 3", Item = "Torch" },
            ],
        };
        settings.Validate();

        Assert.Single(settings.Chests);
        ChestConfig kept = settings.Chests[0];
        Assert.Equal("output", kept.Direction);
        Assert.Equal("shulker", kept.Kind);
        Assert.Equal(string.Empty, kept.Label);
        Assert.Equal("1 2 3", kept.Position);
        Assert.Equal("torch", kept.Item);
    }

    [Fact]
    public void PinnedBench_PrefersExplicitPinOverLegacyTable()
    {
        var settings = new AutoCraftSettings { Bench = "1 2 3" };
        settings.Validate();
        Assert.Equal(new BlockPos(1, 2, 3), settings.PinnedBench());
    }

    [Fact]
    public void PinnedBench_MigratesANonDefaultLegacyTable()
    {
        var settings = new AutoCraftSettings { CraftingTable = new TableLocation { X = 10, Y = 64, Z = -5 } };
        settings.Validate();
        Assert.Equal(new BlockPos(10, 64, -5), settings.PinnedBench());
    }

    [Fact]
    public void PinnedBench_DefaultsToAutomatic()
    {
        var settings = new AutoCraftSettings();
        settings.Validate();
        Assert.Null(settings.PinnedBench());
    }

    [Fact]
    public void EffectiveRecipes_LegacyAndCustomEntries_CraftThroughTheLibrary()
    {
        var settings = new AutoCraftSettings
        {
            Recipes = [new RecipeConfig { Name = "Old Favorite", Type = "player", Result = "Stone", Slots = ["Stone"] }],
            CustomRecipes = [new RecipeConfig { Name = "Torch", Type = "player", Result = "soul_torch", Slots = ["coal", "", "stick", ""] }],
        };
        settings.Validate();

        IReadOnlyList<CraftRecipe> recipes = settings.EffectiveRecipes();
        (CraftRecipe? legacy, _) = CraftLibrary.Resolve(recipes, "old favorite");
        Assert.NotNull(legacy);
        Assert.Equal(4, legacy.Slots.Length);

        (CraftRecipe? torch, _) = CraftLibrary.Resolve(recipes, "torch");
        Assert.NotNull(torch);
        Assert.Equal("soul_torch", torch.Result);
    }

    #endregion
}
