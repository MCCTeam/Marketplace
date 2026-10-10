using DMCBK.Core.Tests.Fakes;
using DMCBK.OfficialPlugins.AntiAfk;
using DMCBK.OfficialPlugins.AutoRelog;
using DMCBK.OfficialPlugins.FollowPlayer;
using Umpk.Client.Snapshots;
using Umpk.Game.Blocks;
using Umpk.Geometry;
using Umpk.Protocol.Java;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The headless half of the bot ports: every decision the legacy bots made that does not need a live session lives in a pure static helper on its plugin, so the tick/delay arithmetic, the kick-message matching, the retry budget, the name rules, the stand-off test and the chunk-grid reachability lookup are all covered here rather than only on a server.
/// </summary>
public sealed class PortedBotLogicTests
{
    // --------------------------------------------------------------------------------------------------- AntiAFK ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(5.0, 100)]
    [InlineData(60.0, 1200)]
    [InlineData(1.5, 30)]
    [InlineData(0.0, 0)]
    public void AntiAfk_ToTicks_MatchesLegacyDoubleToTick(double seconds, int expected)
        => Assert.Equal(expected, AntiAfkTiming.ToTicks(seconds));

    [Fact]
    public void AntiAfk_ToTicks_ClampsAtTheLegacyCeiling()
        => Assert.Equal(int.MaxValue, AntiAfkTiming.ToTicks(double.MaxValue));

    [Fact]
    public void AntiAfk_NextDelayTicks_WithEqualBounds_IsExact()
        => Assert.Equal(1200, AntiAfkTiming.NextDelayTicks(60.0, 60.0, new Random(1)));

    [Fact]
    public void AntiAfk_NextDelayTicks_StaysInsideTheRange()
    {
        var random = new Random(7);
        for (int i = 0; i < 200; i++)
        {
            int ticks = AntiAfkTiming.NextDelayTicks(10.0, 30.0, random);
            Assert.InRange(ticks, 200, 600);
        }
    }

    /// <summary>A 3x3 surface sample centered on (0, 0) at ground level 63 (so a player stands at Y 64).</summary>
    private static SurfaceRegionSnapshot FlatRegion(int originX = -1, int originZ = -1, int size = 3)
    {
        BlockState grass = FakeBlockDataSource.Instance.StateFor(FakeBlockDataSource.GrassBlock);
        var columns = new List<SurfaceColumn>(size * size);
        for (int i = 0; i < size * size; i++)
            columns.Add(new SurfaceColumn(true, true, 63, grass));

        return new SurfaceRegionSnapshot(originX, originZ, size, size, columns);
    }

    [Fact]
    public void AntiAfk_GoalPick_StandsOnTheSurfaceAndNeverOnTheCurrentColumn()
    {
        var random = new Random(3);
        var origin = new Vec3d(0.5, 64.0, 0.5);
        for (int i = 0; i < 100; i++)
        {
            Assert.True(AntiAfkTiming.TryPickGoal(FlatRegion(), origin, random, out Vec3d goal));
            Assert.Equal(64.0, goal.Y);
            Assert.InRange(goal.X, -0.5, 1.5);
            Assert.InRange(goal.Z, -0.5, 1.5);
            Assert.False(goal.X == 0.5 && goal.Z == 0.5, "the current column must never be chosen");
        }
    }

    [Fact]
    public void AntiAfk_GoalPick_SkipsUnloadedFluidAndEmptyColumns()
    {
        BlockState water = FakeBlockDataSource.Instance.StateFor(FakeBlockDataSource.Water);
        BlockState lava = FakeBlockDataSource.Instance.StateFor(FakeBlockDataSource.Lava);
        BlockState grass = FakeBlockDataSource.Instance.StateFor(FakeBlockDataSource.GrassBlock);

        // Every neighbour is unusable: unloaded, fluid, or no surface found.
        var columns = new List<SurfaceColumn>
        {
            SurfaceColumn.Unloaded,
            new(true, true, 63, water),
            new(true, false, 0, default),
            SurfaceColumn.Unloaded,
            new(true, true, 63, grass), // the player's own column
            SurfaceColumn.Unloaded,
            new(true, true, 63, lava),
            new(true, false, 0, default),
            SurfaceColumn.Unloaded,
        };

        var region = new SurfaceRegionSnapshot(-1, -1, 3, 3, columns);
        Assert.False(AntiAfkTiming.TryPickGoal(region, new Vec3d(0.5, 64.0, 0.5), new Random(1), out _));
    }

    [Fact]
    public void AntiAfk_GoalPick_RejectsColumnsBeyondTheHeightDelta()
    {
        BlockState stone = FakeBlockDataSource.Instance.StateFor(FakeBlockDataSource.Stone);
        var columns = new List<SurfaceColumn>(9);
        for (int i = 0; i < 9; i++)
            // A cliff far above the player's feet: standable, but not somewhere to wander to.
            columns.Add(new SurfaceColumn(true, true, 120, stone));

        var region = new SurfaceRegionSnapshot(-1, -1, 3, 3, columns);
        Assert.False(AntiAfkTiming.TryPickGoal(region, new Vec3d(0.5, 64.0, 0.5), new Random(1), out _));
    }

    [Fact]
    public void AntiAfk_GoalPick_AcceptsASmallStepUp()
    {
        BlockState stone = FakeBlockDataSource.Instance.StateFor(FakeBlockDataSource.Stone);
        var columns = new List<SurfaceColumn>(9);
        for (int i = 0; i < 9; i++)
            columns.Add(new SurfaceColumn(true, true, 65, stone));

        var region = new SurfaceRegionSnapshot(-1, -1, 3, 3, columns);
        Assert.True(AntiAfkTiming.TryPickGoal(region, new Vec3d(0.5, 64.0, 0.5), new Random(1), out Vec3d goal));
        Assert.Equal(66.0, goal.Y);
    }

    [Fact]
    public void AntiAfk_Validate_SwapsAnInvertedDelayRange()
    {
        var settings = new AntiAfkSettings { Delay = new DMCBK.OfficialPlugins.AntiAfk.DelayRange { Min = 90, Max = 30 } };
        settings.Validate();
        Assert.Equal(30, settings.Delay.Min);
        Assert.Equal(90, settings.Delay.Max);
    }

    [Fact]
    public void AntiAfk_Validate_AppliesTheLegacyFloorsAndFallbacks()
    {
        var settings = new AntiAfkSettings
        {
            Delay = new DMCBK.OfficialPlugins.AntiAfk.DelayRange { Min = 0.0, Max = 0.0 },
            Walk_Range = 0,
            Walk_Retries = 0,
            Command = null!,
        };

        settings.Validate();

        Assert.Equal(1.0, settings.Delay.Min);
        Assert.Equal(1.0, settings.Delay.Max);
        Assert.Equal(5, settings.Walk_Range);
        Assert.Equal(1, settings.Walk_Retries);
        Assert.Equal(string.Empty, settings.Command);
    }

    // --------------------------------------------------------------------------------------------------- AutoRelog ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(-1, true)]
    [InlineData(int.MaxValue, true)]
    [InlineData(0, false)]
    [InlineData(3, false)]
    public void AutoRelog_UnlimitedRetries_MatchesTheLegacyRule(int retries, bool expected)
        => Assert.Equal(expected, AutoRelogPolicy.HasUnlimitedRetries(retries));

    [Theory]
    [InlineData(3, 0, true)]
    [InlineData(3, 2, true)]
    [InlineData(3, 3, false)]
    [InlineData(3, 9, false)]
    [InlineData(-1, 1000, true)]
    public void AutoRelog_RetryBudget_IsSpentOncePerAttempt(int retries, int used, bool expected)
        => Assert.Equal(expected, AutoRelogPolicy.CanReconnect(retries, used));

    [Fact]
    public void AutoRelog_KickMatch_IsCaseInsensitiveSubstring()
    {
        string[] triggers = ["Server is restarting", "Server is full"];
        Assert.True(AutoRelogPolicy.ShouldReconnect(
            "The SERVER IS RESTARTING, back in a minute", triggers, ignoreKickMessage: false, out string? matched));
        Assert.Equal("Server is restarting", matched);
    }

    [Fact]
    public void AutoRelog_KickMatch_RejectsAnUnrelatedMessage()
    {
        string[] triggers = ["Server is restarting"];
        Assert.False(AutoRelogPolicy.ShouldReconnect(
            "You have been banned", triggers, ignoreKickMessage: false, out string? matched));
        Assert.Null(matched);
    }

    [Fact]
    public void AutoRelog_IgnoreKickMessage_MatchesAnythingWithNoTrigger()
    {
        Assert.True(AutoRelogPolicy.ShouldReconnect("anything at all", [], ignoreKickMessage: true, out string? matched));
        Assert.Null(matched);
    }

    [Fact]
    public void AutoRelog_KickMatch_SkipsEmptyTriggers()
        => Assert.False(AutoRelogPolicy.ShouldReconnect("kicked", [string.Empty], ignoreKickMessage: false, out _));

    [Fact]
    public void AutoRelog_Delay_WithEqualBounds_IsExact()
        => Assert.Equal(3.0, AutoRelogPolicy.NextDelaySeconds(3.0, 3.0, new Random(1)));

    [Fact]
    public void AutoRelog_Delay_StaysInsideTheRange()
    {
        var random = new Random(11);
        for (int i = 0; i < 200; i++)
            Assert.InRange(AutoRelogPolicy.NextDelaySeconds(2.0, 8.0, random), 2.0, 8.0);
    }

    [Theory]
    [InlineData(CloseReason.SocketEof)]
    [InlineData(CloseReason.IdleTimeout)]
    [InlineData(CloseReason.ProtocolViolation)]
    public void AutoRelog_TransportClose_ReadsAsTheLegacyConnectionLostPhrase(CloseReason reason)
    {
        // The shipped default trigger list contains "Connection has been lost"; a transport close carries no server message, so it has to render as that phrase for the defaults to keep working.
        string described = AutoRelogPolicy.DescribeTransportClose(reason);
        Assert.True(AutoRelogPolicy.ShouldReconnect(
            described, ["Connection has been lost"], ignoreKickMessage: false, out _));
    }

    [Fact]
    public void AutoRelog_TransportClose_LeavesOtherReasonsAlone()
        => Assert.Equal("Transferred", AutoRelogPolicy.DescribeTransportClose(CloseReason.Transferred));

    [Fact]
    public void AutoRelog_Validate_SwapsAndFloorsTheDelayRange()
    {
        var settings = new AutoRelogSettings { Delay = new DMCBK.OfficialPlugins.AutoRelog.DelayRange { Min = 9, Max = 0 } };
        settings.Validate();
        Assert.Equal(0.1, settings.Delay.Min);
        Assert.Equal(9.0, settings.Delay.Max);
    }

    [Fact]
    public void AutoRelog_Validate_SelfDisablesWhenNothingCouldEverMatch()
    {
        var settings = new AutoRelogSettings { Kick_Messages = [], Ignore_Kick_Message = false };
        settings.Validate();
        Assert.False(settings.Enabled);
    }

    [Fact]
    public void AutoRelog_Validate_KeepsEnabledWhenTheFilterIsOff()
    {
        var settings = new AutoRelogSettings { Kick_Messages = [], Ignore_Kick_Message = true };
        settings.Validate();
        Assert.True(settings.Enabled);
    }

    // --------------------------------------------------------------------------------------------------- FollowPlayer ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1.5, 30)]
    [InlineData(0.0, 0)]
    [InlineData(-5.0, 0)]
    public void Follow_ToTicks_MatchesTheLegacyUpdateThrottle(double seconds, int expected)
        => Assert.Equal(expected, FollowPolicy.ToTicks(seconds));

    [Theory]
    [InlineData("Notch", true)]
    [InlineData("player_1", true)]
    [InlineData("aaaaaaaaaaaaaaaa", true)]
    [InlineData("aaaaaaaaaaaaaaaaa", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("bad name", false)]
    [InlineData("bad-name", false)]
    [InlineData("bad.name", false)]
    public void Follow_NameRule_MatchesTheLegacyValidator(string? name, bool expected)
        => Assert.Equal(expected, FollowPolicy.IsValidName(name));

    [Theory]
    [InlineData(10.0, 3.0, true)]
    [InlineData(3.0, 3.0, true)]
    [InlineData(2.99, 3.0, false)]
    [InlineData(0.0, 0.0, true)]
    public void Follow_StandOff_StopsInsideTheConfiguredDistance(double distance, double stopAt, bool expected)
        => Assert.Equal(expected, FollowPolicy.ShouldStep(distance, stopAt));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 3)]
    [InlineData(16, 33)]
    [InlineData(17, 0)]
    public void Follow_ReachabilityProbe_IsBoundedByTheMaxRange(int chunkDelta, int expected)
        => Assert.Equal(expected, FollowPolicy.GridSizeFor(chunkDelta));

    [Fact]
    public void Follow_ChunkLookup_ReadsTheGridRowMajorAroundTheCenter()
    {
        bool[] loaded = [true, true, true, true, true, true, true, true, false];
        var grid = new ChunkStatusGrid(-4, 7, 3, 3, loaded);

        Assert.True(FollowPolicy.IsChunkLoaded(grid, -4, 7));
        Assert.False(FollowPolicy.IsChunkLoaded(grid, -3, 8));
        Assert.False(FollowPolicy.IsChunkLoaded(grid, 40, 40));
    }

    [Fact]
    public void Follow_Validate_ClampsNegativeSettings()
    {
        var settings = new FollowPlayerSettings { Update_Limit = -1, Stop_At_Distance = -2 };
        settings.Validate();
        Assert.Equal(0, settings.Update_Limit);
        Assert.Equal(0, settings.Stop_At_Distance);
    }

    [Fact]
    public void Follow_Validate_LeavesValidSettingsAlone()
    {
        var settings = new FollowPlayerSettings();
        settings.Validate();
        Assert.Equal(1.5, settings.Update_Limit);
        Assert.Equal(3.0, settings.Stop_At_Distance);
    }

    #region Target resolution goes name -> tab-list uuid -> tracked entity
    // The entity model declares a player-profile field that nothing ever populates, so resolving a name off the entity found nothing and every /follow start refused with "that player is not in range".
    // The uuid is the only join that actually exists between a name and a tracked player entity.

    private static readonly Guid TargetUuid = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherUuid = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Follow_Resolve_FindsTheTrackedEntityBehindATabListName()
    {
        bool found = FollowPolicy.TryResolveTarget(
            "Notch",
            [("Notch", TargetUuid), ("Someone", OtherUuid)],
            id => id == TargetUuid ? 42 : null,
            out int entityId,
            out Guid uuid,
            out string resolvedName);

        Assert.True(found);
        Assert.Equal(42, entityId);
        Assert.Equal(TargetUuid, uuid);
        Assert.Equal("Notch", resolvedName);
    }

    [Fact]
    public void Follow_Resolve_IsCaseInsensitiveAndReturnsTheTabListSpelling()
    {
        Assert.True(FollowPolicy.TryResolveTarget(
            "nOtCh", [("Notch", TargetUuid)], _ => 7, out _, out _, out string resolvedName));

        Assert.Equal("Notch", resolvedName);
    }

    [Fact]
    public void Follow_Resolve_OnServerButUntracked_IsTheOutOfRangeRefusal()
    {
        // In the tab list with no tracked entity means "on the server but out of tracking range", which is the legacy refusal rather than an unknown-player error.
        Assert.False(FollowPolicy.TryResolveTarget(
            "Notch", [("Notch", TargetUuid)], _ => null, out int entityId, out Guid uuid, out _));

        Assert.Equal(0, entityId);
        Assert.Equal(Guid.Empty, uuid);
    }

    [Fact]
    public void Follow_Resolve_UnknownName_Fails()
        => Assert.False(FollowPolicy.TryResolveTarget(
            "Nobody", [("Notch", TargetUuid)], _ => 7, out _, out _, out _));

    [Fact]
    public void Follow_Resolve_EmptyTabList_Fails()
        => Assert.False(FollowPolicy.TryResolveTarget("Notch", [], _ => 7, out _, out _, out _));

    [Fact]
    public void Follow_Resolve_NeverMatchesADifferentPlayersEntity()
    {
        // The uuid must gate the lookup: a name match on one entry must not hand back another player's id.
        Assert.True(FollowPolicy.TryResolveTarget(
            "Someone",
            [("Notch", TargetUuid), ("Someone", OtherUuid)],
            id => id == OtherUuid ? 99 : 1,
            out int entityId,
            out Guid uuid,
            out _));

        Assert.Equal(99, entityId);
        Assert.Equal(OtherUuid, uuid);
    }
    #endregion
}
