using System.IO.Compression;
using System.Text.Json;
using DMCBK.OfficialPlugins.DiscordBridge;
using DMCBK.OfficialPlugins.DiscordRpc;
using DMCBK.OfficialPlugins.Map;
using DMCBK.OfficialPlugins.ReplayCapture;
using DMCBK.OfficialPlugins.TelegramBridge;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using DiscordFormats = DMCBK.OfficialPlugins.DiscordBridge.BridgeFormats;
using TelegramFormats = DMCBK.OfficialPlugins.TelegramBridge.BridgeFormats;

namespace DMCBK.Core.Tests;

/// <summary>
/// The headless half of the bot ports (DiscordBridge, TelegramBridge, DiscordRpc, ReplayCapture, Map).
/// Every decision that does not need a live session or a network account lives in a pure helper on its plugin, so the message projection, the section-code to Markdown conversion, the aggregation packing, the route resolution, env links, id parsing and prefix splitting, the presence placeholder expansion and palette/patch/BMP/PNG pipeline and the .mcpr container are all covered here rather than only against a real Discord, Telegram or Minecraft server.
/// </summary>
public sealed class IntegrationBotLogicTests
{
    private static readonly DateTime Stamp = new(2026, 7, 25, 13, 45, 30, DateTimeKind.Local);

    // --------------------------------------------------------------------------------------------------- DiscordBridge ---------------------------------------------------------------------------------------------------

    [Fact]
    public void DiscordBridge_Settings_SelfDisableWithoutAToken()
    {
        var settings = new DiscordBridgeSettings();
        settings.Validate();
        Assert.False(settings.Enabled);
    }

    [Fact]
    public void DiscordBridge_Settings_TreatTheShippedPlaceholderAsUnset()
    {
        var settings = new DiscordBridgeSettings { Token = DiscordBridgeSettings.TokenPlaceholder };
        settings.Validate();
        Assert.False(settings.Enabled);
    }

    [Fact]
    public void DiscordBridge_Settings_StayEnabledWithARealToken()
    {
        var settings = new DiscordBridgeSettings { Token = "a-token-shaped-string" };
        settings.Validate();
        Assert.True(settings.Enabled);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(-5, 3)]
    [InlineData(7, 7)]
    public void DiscordBridge_Settings_ClampTheSendTimeoutLikeTheLegacy(int configured, int expected)
    {
        var settings = new DiscordBridgeSettings { Token = "t", Message_Send_Timeout = configured };
        settings.Validate();
        Assert.Equal(expected, settings.Message_Send_Timeout);
    }

    [Fact]
    public void DiscordBridge_Settings_ClampANegativeAggregationInterval()
    {
        var settings = new DiscordBridgeSettings { Token = "t", Message_Aggregation_Interval = -1.0 };
        settings.Validate();
        Assert.Equal(0.0, settings.Message_Aggregation_Interval);
    }

    [Fact]
    public void DiscordBridge_Render_UsesThePublicFormatForChat()
    {
        var chat = new ClassifiedChat(ChatKind.Chat, "Steve", "hello", "<Steve> hello");
        RelayedMessage? relayed = DiscordFormatting.Render(chat, Formats(), relayAllMessages: false, Stamp);

        Assert.NotNull(relayed);
        Assert.Equal("Steve: hello", relayed.Text);
        Assert.False(relayed.IsTeleportRequest);
    }

    [Fact]
    public void DiscordBridge_Render_UsesThePrivateFormatForWhispers()
    {
        var chat = new ClassifiedChat(ChatKind.PrivateMessage, "Alex", "psst", "Alex whispers to you: psst");
        RelayedMessage? relayed = DiscordFormatting.Render(chat, Formats(), relayAllMessages: false, Stamp);

        Assert.NotNull(relayed);
        Assert.Equal("**[PM]** Alex: psst", relayed.Text);
    }

    [Fact]
    public void DiscordBridge_Render_FlagsTeleportRequestsForTheButtonEmbed()
    {
        var chat = new ClassifiedChat(ChatKind.TeleportRequest, "Alex", "tpa", "Alex has requested to teleport");
        RelayedMessage? relayed = DiscordFormatting.Render(chat, Formats(), relayAllMessages: false, Stamp);

        Assert.NotNull(relayed);
        Assert.True(relayed.IsTeleportRequest);
        Assert.Equal("TPA from Alex", relayed.Text);
    }

    [Fact]
    public void DiscordBridge_Render_DropsUnclassifiedLinesUnlessRelayAllIsOn()
    {
        var chat = new ClassifiedChat(ChatKind.System, null, "Server restarting", "Server restarting");

        Assert.Null(DiscordFormatting.Render(chat, Formats(), relayAllMessages: false, Stamp));

        RelayedMessage? relayed = DiscordFormatting.Render(chat, Formats(), relayAllMessages: true, Stamp);
        Assert.NotNull(relayed);
        Assert.Equal("Server restarting", relayed.Text);
    }

    [Fact]
    public void DiscordBridge_Render_DropsBlankLines()
    {
        var chat = new ClassifiedChat(ChatKind.Chat, "Steve", "   ", "   ");
        Assert.Null(DiscordFormatting.Render(chat, Formats(), relayAllMessages: true, Stamp));
    }

    [Fact]
    public void DiscordBridge_Expand_FillsTheTimestampPlaceholder()
    {
        string text = DiscordFormatting.Expand("[{timestamp}] {username}: {message}", "Steve", "hi", Stamp);
        Assert.Equal("[2026-07-25 13:45:30] Steve: hi", text);
    }

    [Theory]
    // Closed runs become the matching Markdown.
    [InlineData("\u00a7lbold\u00a7r tail", "**bold** tail")]
    [InlineData("\u00a7mgone\u00a7r", "~~gone~~")]
    [InlineData("\u00a7nunder\u00a7r", "__under__")]
    [InlineData("\u00a7oitalic\u00a7r", "*italic*")]
    // A run left open to the end of the line is still closed by the converter.
    [InlineData("\u00a7lbold to the end", "**bold to the end**")]
    // Colour codes and anything else are stripped.
    [InlineData("\u00a7cred \u00a7atext", "red text")]
    [InlineData("plain", "plain")]
    public void DiscordBridge_ToDiscordMarkdown_MatchesTheLegacyConversion(string input, string expected)
        => Assert.Equal(expected, DiscordFormatting.ToDiscordMarkdown(input));

    [Theory]
    [InlineData("\u00a7aHP\u00a7r 20\u00a77/20", "HP 20/20")]
    [InlineData("\u00a7lbold\u00a7r plain", "bold plain")]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    public void DiscordBridge_StripCodes_RemovesEverySectionCode(string input, string expected)
        => Assert.Equal(expected, DiscordFormatting.StripCodes(input));

    [Theory]
    [InlineData(ChatKind.PrivateMessage, true)]
    [InlineData(ChatKind.TeleportRequest, false)]
    [InlineData(ChatKind.Chat, false)]
    public void DiscordBridge_DmExclusive_KeepsWatchedEventsOutOfChannels(ChatKind kind, bool exclusive)
        => Assert.Equal(exclusive, BridgeRoutes.IsDmExclusive(kind, ["whisper"]));

    [Fact]
    public void DiscordBridge_Settings_RoundTripsRoutesAsRoutesTables()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "settings.toml");
            var settings = new DiscordBridgeSettings
            {
                Token = "t",
                DmRelayOut = true,
                Routes =
                [
                    new RouteConfig
                    {
                        Name = "general", GuildId = 1, ChannelId = 2,
                        RelayOut = true, RelayIn = false, AllowCommands = true,
                        OwnersIds = [999], CommandPrefix = "\\",
                    },
                ],
            };

            new PluginSettings(path, NullLogger.Instance).Save(settings);
            string toml = File.ReadAllText(path);

            // The on-disk shape is [[Routes]] (plural): the file the live bot writes must load back.
            Assert.Contains("[[Routes]]", toml);
            Assert.Contains("DmRelayOut = true", toml);

            DiscordBridgeSettings loaded = new PluginSettings(path, NullLogger.Instance)
                .Load<DiscordBridgeSettings>();

            RouteConfig route = Assert.Single(loaded.Routes);
            Assert.Equal("general", route.Name);
            Assert.Equal(2UL, route.ChannelId);
            Assert.False(route.RelayIn);
            Assert.True(route.AllowCommands);
            Assert.Equal([999], route.OwnersIds);
            Assert.Equal("\\", route.CommandPrefix);
            Assert.True(loaded.DmRelayOut);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DiscordBridge_PackAggregated_JoinsLinesUntilTheBudget()
    {
        IReadOnlyList<string> packed = DiscordFormatting.PackAggregated(["one", "two", "three"], budget: 100);

        Assert.Single(packed);
        Assert.Equal("one\ntwo\nthree", packed[0]);
    }

    [Fact]
    public void DiscordBridge_PackAggregated_StartsANewMessageOnOverflow()
    {
        string a = new('a', 6);
        string b = new('b', 6);
        IReadOnlyList<string> packed = DiscordFormatting.PackAggregated([a, b], budget: 10);

        Assert.Equal(2, packed.Count);
        Assert.Equal(a, packed[0]);
        Assert.Equal(b, packed[1]);
    }

    [Fact]
    public void DiscordBridge_PackAggregated_KeepsAnOversizedLineRatherThanDroppingIt()
    {
        string huge = new('x', 50);
        IReadOnlyList<string> packed = DiscordFormatting.PackAggregated([huge], budget: 10);

        Assert.Single(packed);
        Assert.Equal(huge, packed[0]);
    }

    [Fact]
    public void DiscordBridge_Chunk_PacksShortTextIntoOneMessage()
    {
        IReadOnlyList<string> chunks = DiscordFormatting.Chunk("one\ntwo", budget: 100);

        Assert.Single(chunks);
        Assert.Equal("one\ntwo", chunks[0]);
    }

    [Fact]
    public void DiscordBridge_Chunk_HardCutsAnOversizedLineWithoutDroppingText()
    {
        string huge = new('x', 25);
        IReadOnlyList<string> chunks = DiscordFormatting.Chunk(huge, budget: 10);

        Assert.Equal(huge, string.Concat(chunks));
        Assert.All(chunks, c => Assert.True(c.Length <= 10));
    }

    [Fact]
    public void DiscordBridge_StripCodes_RemovesBackgroundEscapesToo()
        => Assert.Equal("hi", DiscordFormatting.StripCodes("\u00a7\u00a77hi\u00a7\u00a7r"));

    [Theory]
    [InlineData("printed body", "Done", "printed body")]
    [InlineData("", "Done", "Done")]
    [InlineData("  ", "\u00a7aHP\u00a7r 20", "HP 20")]
    public void DiscordBridge_ReplyBody_PrefersThePrintedBody(string captured, string message, string expected)
        => Assert.Equal(expected, DiscordFormatting.ReplyBody(captured, message));

    [Theory]
    [InlineData(1.0, "██████████")]
    [InlineData(0.0, "░░░░░░░░░░")]
    [InlineData(0.5, "█████░░░░░")]
    [InlineData(2.0, "██████████")]
    [InlineData(-1.0, "░░░░░░░░░░")]
    public void DiscordBridge_Bar_FillsProportionally(double fraction, string expected)
        => Assert.Equal(expected, DiscordVitals.Bar(fraction));

    [Fact]
    public void DiscordBridge_Bar_UsesAPartialBlockForRemainders()
        => Assert.Equal("█▌░░░░░░░░", DiscordVitals.Bar(0.15));

    [Theory]
    [InlineData(1, "I")]
    [InlineData(4, "IV")]
    [InlineData(9, "IX")]
    [InlineData(14, "XIV")]
    public void DiscordBridge_ToRoman_MatchesGameLevels(int number, string expected)
        => Assert.Equal(expected, DiscordVitals.ToRoman(number));

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(185, "3m 05s")]
    [InlineData(120, "2m")]
    [InlineData(7260, "2h 01m")]
    [InlineData(-1, "∞")]
    [InlineData(86399, "23h 59m")]
    [InlineData(86400, "∞")]
    [InlineData(100000, "∞")]
    public void DiscordBridge_FormatDuration_StaysCompact(int seconds, string expected)
        => Assert.Equal(expected, DiscordVitals.FormatDuration(seconds));

    [Fact]
    public void DiscordBridge_Effects_UsesEmojiAndLevels()
    {
        var card = DiscordVitals.Effects(
        [
            new ListedEffect("minecraft:speed", "Speed", 2, 185),
            new ListedEffect("minecraft:night_vision", "Night Vision", 0, -1),
        ],
        new EffectsWords("Effects", "None"));

        Assert.Contains("💨 Speed II — 3m 05s", card.Body);
        Assert.Contains("🌙 Night Vision — ∞", card.Body);
    }

    [Fact]
    public void DiscordBridge_Effects_SaysSoWhenClear()
    {
        var card = DiscordVitals.Effects([], new EffectsWords("Effects", "None"));

        Assert.Equal("None", card.Body);
    }

    [Fact]
    public void DiscordBridge_Vitals_BandsAndFloors()
    {
        var words = new VitalsWords("Health", "HP", "Food", "sat", "Level", "xp");

        DiscordCard full = DiscordVitals.Vitals(20f, 20f, 20, 20, 20f, 5, 128, 0.5f, words);
        DiscordCard critical = DiscordVitals.Vitals(4f, 20f, 20, 20, 20f, 5, 128, 0.5f, words);

        Assert.Equal(0x55FF55, full.Color);
        Assert.Equal(0xFF5555, critical.Color);
        Assert.Contains("14/20", DiscordVitals.Vitals(14.000002f, 20f, 20, 20, 20f, 0, 0, 0f, words).Body);
    }

    [Fact]
    public void DiscordBridge_Death_FloorsNegativeCoordinates()
    {
        DiscordCard card = DiscordVitals.Death("Died", 123.7, 64.0, -45.2);

        Assert.Equal(0xFF0000, card.Color);
        Assert.Equal("`123, 64, -46`", card.Body);
    }

    [Fact]
    public void TelegramBridge_Settings_ResolvesAnEnvLinkedToken()
    {
        Environment.SetEnvironmentVariable("MCC_TEST_TELEGRAM_TOKEN", "live-token");
        try
        {
            var settings = new TelegramBridgeSettings { Token = "env:MCC_TEST_TELEGRAM_TOKEN" };
            settings.Validate();
            Assert.True(settings.Enabled);
            Assert.Equal("live-token", settings.ResolvedToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCC_TEST_TELEGRAM_TOKEN", null);
        }
    }

    [Fact]
    public void DiscordBridge_DiffEffects_AnnouncesGainsAndLosses()
    {
        var known = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["minecraft:speed"] = 1,
            ["minecraft:poison"] = 0,
        };
        var current = new List<DiscordVitals.TrackedEffect>
        {
            new("minecraft:speed", 1),
            new("minecraft:regeneration", 0),
        };

        (IReadOnlyList<DiscordVitals.TrackedEffect> gained, IReadOnlyList<DiscordVitals.TrackedEffect> lost) =
            DiscordVitals.DiffEffects(known, current);

        Assert.Equal("minecraft:regeneration", Assert.Single(gained).Id);
        Assert.Equal("minecraft:poison", Assert.Single(lost).Id);
    }

    [Fact]
    public void DiscordBridge_DiffEffects_RelevelCountsAsGained()
    {
        var known = new Dictionary<string, int>(StringComparer.Ordinal) { ["minecraft:speed"] = 1 };
        var current = new List<DiscordVitals.TrackedEffect> { new("minecraft:speed", 2) };

        (IReadOnlyList<DiscordVitals.TrackedEffect> gained, IReadOnlyList<DiscordVitals.TrackedEffect> lost) =
            DiscordVitals.DiffEffects(known, current);

        Assert.Equal(2, Assert.Single(gained).Level);
        Assert.Empty(lost);
    }

    [Fact]
    public void DiscordBridge_DiffEffects_SilentWhenNothingChanged()
    {
        var known = new Dictionary<string, int>(StringComparer.Ordinal) { ["minecraft:speed"] = 1 };
        var current = new List<DiscordVitals.TrackedEffect> { new("minecraft:speed", 1) };

        (IReadOnlyList<DiscordVitals.TrackedEffect> gained, IReadOnlyList<DiscordVitals.TrackedEffect> lost) =
            DiscordVitals.DiffEffects(known, current);

        Assert.Empty(gained);
        Assert.Empty(lost);
    }

    [Fact]
    public void DiscordBridge_Settings_ResolvesAnEnvLinkedToken()
    {
        Environment.SetEnvironmentVariable("MCC_TEST_BRIDGE_TOKEN", "live-token");
        try
        {
            var settings = new DiscordBridgeSettings { Token = "env:MCC_TEST_BRIDGE_TOKEN" };
            settings.Validate();
            Assert.True(settings.Enabled);
            Assert.Equal("live-token", settings.ResolvedToken);
            Assert.Equal("MCC_TEST_BRIDGE_TOKEN", settings.TokenEnvVar);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCC_TEST_BRIDGE_TOKEN", null);
        }
    }

    [Fact]
    public void DiscordBridge_Settings_DisablesOnAnUnsetEnvLink()
    {
        Environment.SetEnvironmentVariable("MCC_TEST_BRIDGE_MISSING", null);
        var settings = new DiscordBridgeSettings { Token = "env:MCC_TEST_BRIDGE_MISSING" };
        settings.Validate();
        Assert.False(settings.Enabled);
        Assert.Equal("MCC_TEST_BRIDGE_MISSING", settings.TokenEnvVar);
    }

    [Fact]
    public void DiscordBridge_Settings_NormalizesWatchersAndPrefix()
    {
        var settings = new DiscordBridgeSettings
        {
            Token = "t",
            DmOwnersOn = ["Whisper", "junk", "TELEPORT", "whisper"],
            CommandPrefix = "   ",
        };
        settings.Validate();
        Assert.Equal(["whisper", "teleport"], settings.DmOwnersOn);
        Assert.Equal(".", settings.CommandPrefix);
    }

    [Fact]
    public void DiscordBridge_Routes_MigratesTheLegacyPairToAFullRoute()
    {
        IReadOnlyList<EffectiveRoute> routes = BridgeRoutes.Resolve([], 123, 456, [111], ".");

        EffectiveRoute route = Assert.Single(routes);
        Assert.Equal("default", route.Name);
        Assert.True(route.RelayOut && route.RelayIn && route.AllowCommands);
        Assert.Equal([111], route.Owners);
        Assert.Equal(".", route.Prefix);
    }

    [Fact]
    public void DiscordBridge_Routes_PrefersConfiguredRoutesOverLegacy()
    {
        var configured = new List<RouteConfig>
        {
            new() { Name = "general", GuildId = 1, ChannelId = 2, AllowCommands = true },
        };

        IReadOnlyList<EffectiveRoute> routes = BridgeRoutes.Resolve(configured, 123, 456, [], ".");

        EffectiveRoute route = Assert.Single(routes);
        Assert.Equal("general", route.Name);
        Assert.Equal(2UL, route.ChannelId);
    }

    [Fact]
    public void DiscordBridge_Routes_InheritsOwnersAndPrefixUnlessOverridden()
    {
        var configured = new List<RouteConfig>
        {
            new() { Name = "a", GuildId = 1, ChannelId = 2 },
            new()
            {
                Name = "b", GuildId = 1, ChannelId = 3,
                OwnersIds = [999], CommandPrefix = "\\",
            },
        };

        IReadOnlyList<EffectiveRoute> routes = BridgeRoutes.Resolve(configured, 0, 0, [111], ".");

        Assert.Equal([111], routes[0].Owners);
        Assert.Equal(".", routes[0].Prefix);
        Assert.Equal([999], routes[1].Owners);
        Assert.Equal("\\", routes[1].Prefix);
    }

    [Fact]
    public void DiscordBridge_Routes_FindsByNameAndByChannel()
    {
        var configured = new List<RouteConfig>
        {
            new() { Name = "General", GuildId = 1, ChannelId = 2 },
        };
        IReadOnlyList<EffectiveRoute> routes = BridgeRoutes.Resolve(configured, 0, 0, [], ".");

        Assert.NotNull(BridgeRoutes.FindByName(routes, "general"));
        Assert.NotNull(BridgeRoutes.FindByChannel(routes, 1, 2));
        Assert.Null(BridgeRoutes.FindByName(routes, "nope"));
        Assert.Null(BridgeRoutes.FindByChannel(routes, 1, 9));
    }

    [Theory]
    [InlineData("123", true)]
    [InlineData("  456  ", true)]
    [InlineData("0", false)]
    [InlineData("-5", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    public void DiscordBridge_Routes_ParsesIdsStrictly(string text, bool ok)
        => Assert.Equal(ok, BridgeRoutes.TryParseId(text, out _));

    [Fact]
    public void DiscordBridge_Routes_ParsesAnIdListAndSkipsJunk()
        => Assert.Equal([111UL, 222UL], BridgeRoutes.ParseIds("111 junk 222 111"));

    [Theory]
    [InlineData(".cmd", ".", "cmd", true)]
    [InlineData("\\cmd", "\\", "cmd", true)]
    [InlineData("cmd", ".", "", false)]
    [InlineData(".dscbridge status", ".", "dscbridge status", true)]
    public void DiscordBridge_Routes_StripsOnlyALeadingPrefix(
        string message, string prefix, string command, bool stripped)
    {
        Assert.Equal(stripped, BridgeRoutes.TryStripPrefix(message, prefix, out string actual));
        Assert.Equal(command, actual);
    }

    // --------------------------------------------------------------------------------------------------- TelegramBridge ---------------------------------------------------------------------------------------------------

    [Fact]
    public void TelegramBridge_Settings_SelfDisableWithoutAToken()
    {
        var settings = new TelegramBridgeSettings();
        settings.Validate();
        Assert.False(settings.Enabled);
    }

    [Fact]
    public void TelegramBridge_Settings_ClampTheSendTimeoutAndKeepTheToken()
    {
        var settings = new TelegramBridgeSettings { Token = "t", Message_Send_Timeout = 0 };
        settings.Validate();

        Assert.True(settings.Enabled);
        Assert.Equal(3, settings.Message_Send_Timeout);
    }

    [Fact]
    public void TelegramBridge_Render_RelaysUnclassifiedLinesVerbatim()
    {
        // The legacy Telegram bridge, unlike the Discord one, had no Relay_All_Messages switch: anything it could not classify went out as-is.
        var chat = new ClassifiedChat(ChatKind.System, null, "Server restarting", "Server restarting");
        Assert.Equal("Server restarting", BridgeFormatting.Render(chat, TelegramFormatsValue(), Stamp));
    }

    [Fact]
    public void TelegramBridge_Render_UsesTheConfiguredTemplates()
    {
        var chat = new ClassifiedChat(ChatKind.PrivateMessage, "Alex", "psst", "raw");
        Assert.Equal("*(PM)* Alex: psst", BridgeFormatting.Render(chat, TelegramFormatsValue(), Stamp));
    }

    [Fact]
    public void TelegramBridge_Render_DropsBlankLines()
    {
        var chat = new ClassifiedChat(ChatKind.Chat, "Steve", "", "  ");
        Assert.Null(BridgeFormatting.Render(chat, TelegramFormatsValue(), Stamp));
    }

    [Fact]
    public void TelegramBridge_IsAuthorized_RejectsEveryoneWhenTheListIsEmpty()
    {
        Assert.False(BridgeFormatting.IsAuthorized(42L, []));
        Assert.True(BridgeFormatting.IsAuthorized(42L, [7L, 42L]));
        Assert.False(BridgeFormatting.IsAuthorized(43L, [7L, 42L]));
    }

    [Theory]
    [InlineData("", TelegramAction.Ignore)]
    [InlineData("   ", TelegramAction.Ignore)]
    [InlineData("/start", TelegramAction.Ignore)]
    [InlineData(".chatid", TelegramAction.ReportChatId)]
    [InlineData(".quit", TelegramAction.RefuseQuit)]
    [InlineData(".exit now", TelegramAction.RefuseQuit)]
    [InlineData(".health", TelegramAction.RunCommand)]
    [InlineData("hello world", TelegramAction.SendChat)]
    public void TelegramBridge_ClassifyInbound_MatchesTheLegacyLadder(string text, TelegramAction expected)
        => Assert.Equal(expected, BridgeFormatting.ClassifyInbound(
            text, DMCBK.OfficialPlugins.TelegramBridge.BridgeDirection.Both));

    [Fact]
    public void TelegramBridge_ClassifyInbound_InTelegramOnlyDirection_StillRunsBridgeCommands()
    {
        // Plain chat is not relayed into the game, but a "." command from an authorized chat still works.
        Assert.Equal(
            TelegramAction.Ignore,
            BridgeFormatting.ClassifyInbound("hello", DMCBK.OfficialPlugins.TelegramBridge.BridgeDirection.Telegram));
        Assert.Equal(
            TelegramAction.RunCommand,
            BridgeFormatting.ClassifyInbound(".health", DMCBK.OfficialPlugins.TelegramBridge.BridgeDirection.Telegram));
    }

    [Theory]
    [InlineData("\u00a7aHP\u00a7r 20\u00a77/20", "HP 20/20")]
    [InlineData("\u00a7lbold\u00a7r plain", "bold plain")]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    public void TelegramBridge_StripCodes_RemovesEverySectionCode(string input, string expected)
        => Assert.Equal(expected, BridgeFormatting.StripCodes(input));

    [Fact]
    public void TelegramBridge_StripCodes_RemovesBackgroundEscapesToo()
        => Assert.Equal("hi", BridgeFormatting.StripCodes("\u00a7\u00a77hi\u00a7\u00a7r"));

    [Theory]
    [InlineData("printed body", "Done", "printed body")]
    [InlineData("", "Done", "Done")]
    [InlineData("  ", "\u00a7aHP\u00a7r 20", "HP 20")]
    public void TelegramBridge_ReplyBody_PrefersThePrintedBody(string captured, string message, string expected)
        => Assert.Equal(expected, BridgeFormatting.ReplyBody(captured, message));

    // --------------------------------------------------------------------------------------------------- DiscordRpc ---------------------------------------------------------------------------------------------------

    [Fact]
    public void DiscordRpc_Settings_SelfDisableWithoutAnApplicationId()
    {
        var settings = new DiscordRpcSettings();
        settings.Validate();
        Assert.False(settings.Enabled);
    }

    [Fact]
    public void DiscordRpc_Settings_ClampAnUnusableUpdateInterval()
    {
        var settings = new DiscordRpcSettings { ApplicationId = "1234", UpdateIntervalSeconds = 0 };
        settings.Validate();

        Assert.True(settings.Enabled);
        Assert.Equal(10, settings.UpdateIntervalSeconds);
    }

    [Fact]
    public void DiscordRpc_Format_ExpandsEveryPlaceholder()
    {
        var settings = new DiscordRpcSettings { ApplicationId = "1234" };
        settings.Validate();

        string text = PresenceFormatter.Format(
            "{server_host}:{server_port} {username} {health}/{max_health} {food} {dimension} {gamemode} "
            + "{x},{y},{z} {player_count} {protocol}",
            Facts(),
            settings);

        Assert.Equal("play.example.com:25565 Steve 15/20 17 The Nether Creative 10,64,-30 4 772", text);
    }

    [Fact]
    public void DiscordRpc_Format_MasksEveryFactTheUserOptedOutOf()
    {
        var settings = new DiscordRpcSettings
        {
            ApplicationId = "1234",
            ShowServerAddress = false,
            ShowHealth = false,
            ShowCoordinates = false,
            ShowDimension = false,
            ShowGamemode = false,
        };
        settings.Validate();

        string text = PresenceFormatter.Format(
            "{server_host}:{server_port} {health}/{max_health} {food} {x},{y},{z} {dimension} {gamemode}",
            Facts(),
            settings);

        Assert.Equal("Hidden:**** ?/? ? ?,?,? Hidden Hidden", text);
    }

    [Fact]
    public void DiscordRpc_Format_RoundsHealthUpLikeTheLegacy()
    {
        var settings = new DiscordRpcSettings { ApplicationId = "1234" };
        settings.Validate();

        Assert.Equal("15", PresenceFormatter.Format("{health}", Facts() with { Health = 14.2f }, settings));
    }

    [Theory]
    [InlineData("minecraft:overworld", "Overworld")]
    [InlineData("minecraft:the_nether", "The Nether")]
    [InlineData("minecraft:the_end", "The End")]
    [InlineData("someplugin:void", "someplugin:void")]
    [InlineData("", "Unknown")]
    public void DiscordRpc_DimensionDisplayName_PrettifiesTheVanillaDimensions(string raw, string expected)
        => Assert.Equal(expected, PresenceFormatter.DimensionDisplayName(raw));

    [Theory]
    [InlineData(0, "Survival")]
    [InlineData(1, "Creative")]
    [InlineData(2, "Adventure")]
    [InlineData(3, "Spectator")]
    [InlineData(9, "Unknown")]
    public void DiscordRpc_GameModeDisplayName_CoversTheVanillaModes(int mode, string expected)
        => Assert.Equal(expected, PresenceFormatter.GameModeDisplayName(mode));

    [Fact]
    public void DiscordRpc_Truncate_KeepsShortValuesAndEllipsizesLongOnes()
    {
        Assert.Equal("short", PresenceFormatter.Truncate("short"));
        Assert.Equal(string.Empty, PresenceFormatter.Truncate(null));

        string truncated = PresenceFormatter.Truncate(new string('x', 200));
        Assert.Equal(PresenceFormatter.DiscordFieldLimit, truncated.Length);
        Assert.EndsWith("...", truncated, StringComparison.Ordinal);
    }

    // --------------------------------------------------------------------------------------------------- ReplayCapture ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ReplayCapture_Settings_ClampTheBackupIntervalLikeTheLegacy()
    {
        var settings = new ReplayCaptureSettings { Backup_Interval = -50 };
        settings.Validate();
        Assert.Equal(-1, settings.Backup_Interval);
    }

    [Theory]
    [InlineData(300.0, true)]
    [InlineData(0.0, false)]
    [InlineData(-1.0, false)]
    public void ReplayCapture_BackupsEnabled_MatchesTheLegacyRule(double interval, bool expected)
        => Assert.Equal(expected, ReplayAvailability.BackupsEnabled(interval));

    [Fact]
    public void ReplayCapture_Evaluate_StillReportsAMissingRawPacketFeed()
    {
        // The self-disable path is preserved, not weakened: a host with no frame stream must still refuse to record and say why, rather than produce an empty .mcpr. It is simply unreachable on this host.
        Assert.Equal(
            ReplayUnavailableReason.NoRawPacketFeed,
            ReplayAvailability.Evaluate(enabled: true, inSession: true, rawFeedAvailable: false));
    }

    [Fact]
    public void ReplayCapture_Evaluate_PrefersTheDisabledReasonOverTheMissingFeed()
        => Assert.Equal(
            ReplayUnavailableReason.Disabled,
            ReplayAvailability.Evaluate(enabled: false, inSession: true, rawFeedAvailable: false));

    [Theory]
    [InlineData(true, ReplayUnavailableReason.None)]
    [InlineData(false, ReplayUnavailableReason.NoSession)]
    public void ReplayCapture_Evaluate_WithAFeed_OnlyNeedsASession(
        bool inSession, ReplayUnavailableReason expected)
        => Assert.Equal(expected, ReplayAvailability.Evaluate(enabled: true, inSession, rawFeedAvailable: true));

    [Fact]
    public void ReplayCapture_ThisBuild_HasARawPacketFeed_SoTheRecorderActuallyRuns()
    {
        // The plugin shipped a complete .mcpr writer that self-disabled because no raw feed existed.
        // The host exposes one (ISessionScope.ObservePackets), so the recorder is live rather than a stub.
        Assert.True(ReplayAvailability.RawPacketFeedAvailable);
        Assert.Equal(
            ReplayUnavailableReason.None,
            ReplayAvailability.Evaluate(
                enabled: true, inSession: true, ReplayAvailability.RawPacketFeedAvailable));
    }

    [Theory]
    [InlineData(0, new byte[] { 0x00 })]
    [InlineData(1, new byte[] { 0x01 })]
    [InlineData(127, new byte[] { 0x7F })]
    [InlineData(128, new byte[] { 0x80, 0x01 })]
    [InlineData(300, new byte[] { 0xAC, 0x02 })]
    public void ReplayCapture_WriteVarInt_MatchesTheProtocolEncoding(int value, byte[] expected)
    {
        Span<byte> buffer = stackalloc byte[5];
        int written = ReplayWriter.WriteVarInt(buffer, value);
        Assert.Equal(expected, buffer[..written].ToArray());
    }

    [Fact]
    public void ReplayCapture_Writer_ProducesAReadableMcprArchive()
    {
        string root = NewTempDirectory();
        try
        {
            string path;
            using (var writer = new ReplayWriter(
                Path.Combine(root, "work"), Path.Combine(root, "out"), 772, "1.21.5", "localhost:25565"))
            {
                writer.SetClientEntityId(42);
                writer.AddPlayerUuid(Guid.Parse("11111111-2222-3333-4444-555555555555"));
                writer.AddPacket(0x26, [1, 2, 3]);
                writer.AddPacket(0x1A, [9]);

                // Serverbound frames are dropped: a replay is what the client saw.
                writer.AddPacket(0x05, [7], isInbound: false);
                Assert.Equal(2, writer.FrameCount);

                path = writer.Stop();
                Assert.False(writer.RecordRunning);
            }

            Assert.True(File.Exists(path));
            Assert.EndsWith(".mcpr", path, StringComparison.Ordinal);

            using ZipArchive archive = ZipFile.OpenRead(path);
            Assert.NotNull(archive.GetEntry(ReplayWriter.RecordingEntryName));
            Assert.NotNull(archive.GetEntry(ReplayMetadata.FileName));

            using (Stream metadata = archive.GetEntry(ReplayMetadata.FileName)!.Open())
            {
                using JsonDocument document = JsonDocument.Parse(metadata);
                JsonElement root2 = document.RootElement;
                Assert.Equal("MCPR", root2.GetProperty("fileFormat").GetString());
                Assert.Equal(14, root2.GetProperty("fileFormatVersion").GetInt32());
                Assert.Equal(772, root2.GetProperty("protocol").GetInt32());
                Assert.Equal("1.21.5", root2.GetProperty("mcversion").GetString());
                Assert.Equal("localhost:25565", root2.GetProperty("serverName").GetString());
                Assert.Equal("MCC", root2.GetProperty("generator").GetString());
                Assert.Equal(42, root2.GetProperty("selfId").GetInt32());
                Assert.Single(root2.GetProperty("players").EnumerateArray());
            }

            // The frame stream: 8-byte big-endian header (millis, length) then the VarInt id and the body.
            using Stream recording = archive.GetEntry(ReplayWriter.RecordingEntryName)!.Open();
            using var buffer = new MemoryStream();
            recording.CopyTo(buffer);
            byte[] frames = buffer.ToArray();

            Assert.Equal(0x26, frames[8]);
            Assert.Equal(4, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(frames.AsSpan(4)));
            Assert.Equal(new byte[] { 1, 2, 3 }, frames[9..12]);

            // Second frame starts right after the first: 8 header + 1 id + 3 body = 12.
            Assert.Equal(2, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(frames.AsSpan(16)));
            Assert.Equal(0x1A, frames[20]);
            Assert.Equal(9, frames[21]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReplayCapture_Writer_CanSnapshotWhileStillRecording()
    {
        string root = NewTempDirectory();
        try
        {
            using var writer = new ReplayWriter(
                Path.Combine(root, "work"), Path.Combine(root, "out"), 772, "1.21.5", "localhost:25565");
            writer.AddPacket(0x26, [1, 2, 3]);

            string backup = writer.CreateBackup("SNAPSHOT.mcpr");

            Assert.True(File.Exists(backup));
            Assert.True(writer.RecordRunning);

            // The recording keeps going after a snapshot.
            writer.AddPacket(0x27, [4]);
            Assert.Equal(2, writer.FrameCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // --------------------------------------------------------------------------------------------------- Map ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Map_Settings_ClampANonPositiveResizeTarget()
    {
        var settings = new MapSettings { Resize_To = 0 };
        settings.Validate();
        Assert.Equal(128, settings.Resize_To);
    }

    [Fact]
    public void Map_ColorByteToRgb_AppliesTheShadeMultiplier()
    {
        // Base colour 1 is grass (0x7F, 0xB2, 0x38); the low two bits pick 180/220/255/135 out of 255.
        Assert.Equal(new MapColor(0x59, 0x7D, 0x27), MapColors.ColorByteToRgb(0b000001_00));
        Assert.Equal(new MapColor(0x7F, 0xB2, 0x38), MapColors.ColorByteToRgb(0b000001_10));
    }

    [Fact]
    public void Map_ColorByteToRgb_FallsBackToMagentaForAnUnknownPaletteId()
    {
        byte unknown = (byte)(MapColors.BaseColorCount << 2);
        Assert.Equal(new MapColor(248, 0, 248), MapColors.ColorByteToRgb(unknown));
    }

    // Patch merging moved into the core (ClientState.Maps applies each rectangular update into the real 128x128 grid), so the plugin no longer owns an ApplyPatch of its own.
    // What it owns is the snapshot it takes on the session loop for the command thread to render from, which is what these cover.

    [Fact]
    public void Map_CopyFrom_TakesTheCoresMergedGridVerbatim()
    {
        var map = new CachedMap { MapId = 1 };
        byte[] grid = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];

        map.CopyFrom(grid, 4, 4);

        Assert.Equal(4, map.Width);
        Assert.Equal(4, map.Height);
        Assert.Equal(grid, map.SnapshotColors());
    }

    [Fact]
    public void Map_CopyFrom_SnapshotIsDetachedFromTheSourceBuffer()
    {
        // The session's grid keeps being written by the loop; a render must never see it change mid-way.
        var map = new CachedMap { MapId = 1 };
        byte[] source = [1, 2, 3, 4];
        map.CopyFrom(source, 2, 2);

        source[0] = 99;

        Assert.Equal(1, map.SnapshotColors()[0]);
    }

    [Fact]
    public void Map_CopyFrom_RejectsAGridWhoseLengthDisagreesWithItsDimensions()
    {
        var map = new CachedMap { MapId = 1 };
        Assert.Throws<ArgumentOutOfRangeException>(() => map.CopyFrom(new byte[15], 4, 4));
    }

    [Fact]
    public void Map_PatchMergingBelongsToTheCore_NotToThePlugin()
    {
        // The plugin merged rectangular pixel patches itself only because the applier wrote nothing but the map id into ClientState.
        // It writes scale, lock, icons and the merged 128x128 grid now, so a second merge implementation here would be a duplicate that can silently disagree with the core's.
        Assert.Null(typeof(CachedMap).GetMethod("ApplyPatch"));
        Assert.NotNull(typeof(CachedMap).GetMethod("CopyFrom"));
    }

    [Fact]
    public void Map_BmpEncoder_WritesAValid24BitHeader()
    {
        RgbImage image = RgbImage.FromMapColors(new byte[4], 2, 2);
        byte[] bmp = BmpEncoder.Encode(image);

        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);
        Assert.Equal((uint)bmp.Length, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(2)));
        Assert.Equal(54u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(10)));
        Assert.Equal(2u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(18)));
        Assert.Equal(2u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bmp.AsSpan(22)));
        Assert.Equal(24, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bmp.AsSpan(28)));
    }

    [Fact]
    public void Map_BmpEncoder_WritesRowsBottomUpAsBgr()
    {
        // A 1x2 image: top pixel is palette id 4 (pure red), bottom pixel is id 8 (white).
        RgbImage image = RgbImage.FromMapColors([0b000100_10, 0b001000_10], 1, 2);
        byte[] bmp = BmpEncoder.Encode(image);

        // BMP rows run bottom-up, so the first pixel row written is the image's LAST row (white).
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF }, bmp[54..57]);
        // Then the top row (red), as BGR.
        Assert.Equal(new byte[] { 0x00, 0x00, 0xFF }, bmp[(54 + 4)..(54 + 7)]);
    }

    [Fact]
    public void Map_PngEncoder_WritesASignedTruecolourPng()
    {
        RgbImage image = RgbImage.FromMapColors(new byte[16], 4, 4);
        byte[] png = PngEncoder.Encode(image);

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        Assert.Equal("IHDR"u8.ToArray(), png[12..16]);
        Assert.Equal(4, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(4, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        Assert.Equal(8, png[24]);  // bit depth
        Assert.Equal(2, png[25]);  // truecolour
        Assert.Equal("IEND"u8.ToArray(), png[^8..^4]);
    }

    [Fact]
    public void Map_ResizeNearest_ScalesToTheExactRequestedSize()
    {
        RgbImage image = RgbImage.FromMapColors([0b000100_10, 0b001000_10, 0b001000_10, 0b000100_10], 2, 2);
        RgbImage resized = image.ResizeNearest(512, 512);

        Assert.Equal(512, resized.Width);
        Assert.Equal(512, resized.Height);
        // Nearest-neighbour keeps the map's hard pixel edges: the top-left quadrant is still pure red.
        Assert.Equal(new MapColor(0xFF, 0x00, 0x00), resized.GetPixel(0, 0));
        Assert.Equal(new MapColor(0xFF, 0x00, 0x00), resized.GetPixel(255, 255));
        Assert.Equal(new MapColor(0xFF, 0xFF, 0xFF), resized.GetPixel(256, 0));
    }

    [Fact]
    public void Map_BridgeImageRequest_RoundTripsForTheAddressedBridgeOnly()
    {
        string[] message = BridgeImageRequest.Create(
            BridgeImageRequest.TargetDiscord, "/tmp/map.png", "a caption");

        Assert.True(BridgeImageRequest.TryRead(
            message, BridgeImageRequest.TargetDiscord, out string path, out string caption));
        Assert.Equal("/tmp/map.png", path);
        Assert.Equal("a caption", caption);

        // The Telegram bridge must not pick up a Discord-addressed request.
        Assert.False(BridgeImageRequest.TryRead(message, BridgeImageRequest.TargetTelegram, out _, out _));
    }

    [Fact]
    public void Map_BridgeImageRequest_IgnoresForeignMessagesOnTheSharedStringArrayChannel()
    {
        // Cross-plugin messages travel as string[] because plugin-defined types do not unify across the per-plugin collectible load contexts, so every reader must tolerate other plugins' traffic.
        Assert.False(BridgeImageRequest.TryRead(
            ["some.other.plugin", "x"], BridgeImageRequest.TargetDiscord, out _, out _));
        Assert.False(BridgeImageRequest.TryRead(null, BridgeImageRequest.TargetDiscord, out _, out _));
    }

    [Fact]
    public void Map_BridgeImageRequest_IsReadIdenticallyByBothBridgePlugins()
    {
        // The three plugins live in separate assemblies and cannot share a contract type, so the wire shape is asserted from BOTH ends here: a drift between them would silently stop the handoff.
        string[] toDiscord = BridgeImageRequest.Create(
            BridgeImageRequest.TargetDiscord, "/tmp/a.png", "discord caption");
        string[] toTelegram = BridgeImageRequest.Create(
            BridgeImageRequest.TargetTelegram, "/tmp/b.png", "telegram caption");

        Assert.True(DiscordBridgePlugin.TryReadImageRequest(toDiscord, out string discordPath, out string discordCaption));
        Assert.Equal("/tmp/a.png", discordPath);
        Assert.Equal("discord caption", discordCaption);
        Assert.False(DiscordBridgePlugin.TryReadImageRequest(toTelegram, out _, out _));

        Assert.True(TelegramBridgePlugin.TryReadImageRequest(toTelegram, out string telegramPath, out string telegramCaption));
        Assert.Equal("/tmp/b.png", telegramPath);
        Assert.Equal("telegram caption", telegramCaption);
        Assert.False(TelegramBridgePlugin.TryReadImageRequest(toDiscord, out _, out _));
    }

    [Fact]
    public void Map_BridgeImageRequest_CarriesAnOptionalDestination()
    {
        string[] message = BridgeImageRequest.Create(
            BridgeImageRequest.TargetDiscord, "/tmp/a.png", "caption", "route:general,dm:123");

        Assert.True(BridgeImageRequest.TryRead(
            message, BridgeImageRequest.TargetDiscord,
            out _, out _, out string destination));
        Assert.Equal("route:general,dm:123", destination);

        Assert.True(DiscordBridgePlugin.TryReadImageRequest(
            message, out _, out _, out string bridgeDestination));
        Assert.Equal("route:general,dm:123", bridgeDestination);

        // The Telegram bridge ignores the fifth element but still reads the request.
        string[] telegram = BridgeImageRequest.Create(
            BridgeImageRequest.TargetTelegram, "/tmp/b.png", "caption", "route:x");
        Assert.True(TelegramBridgePlugin.TryReadImageRequest(telegram, out _, out _));

        // Four-element messages predate destinations and read back empty.
        string[] legacy = ["mcc.plugins.bridge.image.v1", "discord", "/tmp/c.png", "caption"];
        Assert.True(DiscordBridgePlugin.TryReadImageRequest(
            legacy, out _, out _, out string legacyDestination));
        Assert.Equal(string.Empty, legacyDestination);
    }

    [Theory]
    [InlineData("", 0, 0)]
    [InlineData("route:general", 1, 0)]
    [InlineData("route:general,route:alerts", 2, 0)]
    [InlineData("dm:123", 0, 1)]
    [InlineData("route:general,dm:123", 1, 1)]
    [InlineData("route:general, dm:123 ", 1, 1)]
    [InlineData("route:,dm:abc,route:general", 1, 0)]
    public void DiscordBridge_ParseImageTargets_SplitsRoutesAndDms(
        string destination, int routes, int dms)
    {
        BridgeRoutes.ImageTargets targets = BridgeRoutes.ParseImageTargets(destination);

        Assert.Equal(routes, targets.Routes.Count);
        Assert.Equal(dms, targets.Dms.Count);
    }

    [Fact]
    public void DiscordBridge_ParseImageTargets_ReadsTheIds()
    {
        BridgeRoutes.ImageTargets targets =
            BridgeRoutes.ParseImageTargets("route:General,dm:123,dm:123");

        Assert.Equal("General", Assert.Single(targets.Routes));
        Assert.Equal(123UL, Assert.Single(targets.Dms));
    }

    [Fact]
    public void DiscordBridge_TextRequest_RoundTrips()
    {
        string[] message = ["mcc.plugins.bridge.text.v1", "discord", "[Alert] hi", "route:general,dm:123"];

        Assert.True(DiscordBridgePlugin.TryReadTextRequest(message, out string text, out string destination));
        Assert.Equal("[Alert] hi", text);
        Assert.Equal("route:general,dm:123", destination);

        Assert.False(DiscordBridgePlugin.TryReadTextRequest(
            ["mcc.plugins.bridge.text.v1", "telegram", "hi", ""], out _, out _));
        Assert.False(DiscordBridgePlugin.TryReadTextRequest(
            ["mcc.plugins.bridge.image.v1", "discord", "/tmp/a.png", "c"], out _, out _));
        Assert.False(DiscordBridgePlugin.TryReadTextRequest(null, out _, out _));
    }

    [Theory]
    [InlineData("Steve: hello all", true)]
    [InlineData("STEVE: hello", true)]
    [InlineData("Alex: hello", false)]
    public void AlertMatcher_PlainEntriesMatchSubstrings(string line, bool expected)
    {
        var matcher = AlertMatcher.Build(["steve"], useRegex: false);

        Assert.Equal(expected, matcher.IsMatch(line));
        Assert.Empty(matcher.Errors);
    }

    [Theory]
    [InlineData("<Steve> hello", true)]
    [InlineData("<SteveX> hello", false)]
    [InlineData("Steve whispers: meet at 100 64 200", true)]
    [InlineData("Steve whispers: meet atabyss", false)]
    public void AlertMatcher_RegexEntriesMatchPatterns(string line, bool expected)
    {
        var matcher = AlertMatcher.Build(["^<Steve>", @"\b\d{3} \d{2,3} \d{3}\b"], useRegex: true);

        Assert.Equal(expected, matcher.IsMatch(line));
        Assert.Empty(matcher.Errors);
    }

    [Fact]
    public void AlertMatcher_BadPatternsAreSkippedAndReported()
    {
        var matcher = AlertMatcher.Build(["(unclosed", "good"], useRegex: true);

        Assert.Equal("(unclosed", Assert.Single(matcher.Errors));
        Assert.True(matcher.IsMatch("a good line"));
        Assert.False(matcher.IsMatch("nothing here"));
    }

    [Fact]
    public void AlertMatcher_FirstMatchNamesTheTrigger()
    {
        var matcher = AlertMatcher.Build(["admin", "help"], useRegex: false);

        Assert.Equal("admin", matcher.FirstMatch("admin: help me"));
        Assert.Null(matcher.FirstMatch("nothing here"));
    }

    // --------------------------------------------------------------------------------------------------- Helpers ---------------------------------------------------------------------------------------------------

    private static DiscordFormats Formats()
        => new("**[PM]** {username}: {message}", "{username}: {message}", "TPA from {username}");

    private static TelegramFormats TelegramFormatsValue()
        => new("*(PM)* {username}: {message}", "{username}: {message}", "TPA from {username}");

    private static PresenceFacts Facts() => new(
        "play.example.com", 25565, "Steve", 15f, 17, 10, 64, -30, "minecraft:the_nether", 1, 4, 772);

    private static string NewTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "mcc-p12d-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
