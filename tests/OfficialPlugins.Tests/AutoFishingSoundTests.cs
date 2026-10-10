using DMCBK.OfficialPlugins.AutoFishing;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The splash matcher behind AutoFishing's <c>Enable_Sound_Detection</c>.
/// Both wire spellings have to match: the id-addressed packets resolve through the sound registry to the namespaced name, while the pre-1.19.3 name-addressed packets carry the bare path.
/// </summary>
public sealed class AutoFishingSoundTests
{
    [Theory]
    [InlineData("minecraft:entity.fishing_bobber.splash")]
    [InlineData("entity.fishing_bobber.splash")]
    [InlineData("MINECRAFT:ENTITY.FISHING_BOBBER.SPLASH")]
    public void IsSplashSound_MatchesTheBobberSplash(string name)
        => Assert.True(FishingRules.IsSplashSound(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("minecraft:entity.fishing_bobber.throw")]
    [InlineData("minecraft:entity.fishing_bobber.retrieve")]
    [InlineData("minecraft:entity.generic.splash")]
    [InlineData("minecraft:block.water.ambient")]
    public void IsSplashSound_RejectsEverythingElse(string? name)
        => Assert.False(FishingRules.IsSplashSound(name));
}
