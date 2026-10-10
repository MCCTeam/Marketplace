using System.Text.RegularExpressions;
using DMCBK.PluginSdk;
using DMCBK.OfficialPlugins.AutoRespond;
using DMCBK.OfficialPlugins.ChatLog;
using DMCBK.OfficialPlugins.PlayerListLogger;
using DMCBK.OfficialPlugins.RemoteControl;
using DMCBK.OfficialPlugins.ScriptScheduler;
using Umpk.Text;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The headless half of the bot ports (AutoRespond / ChatLog / PlayerListLogger / RemoteControl / ScriptScheduler): every decision the legacy bots made that does not need a live session lives in a pure helper on its plugin, so the rule grammar, the regex substitution, the cooldown, the filter table, the record format, the authorisation checks and the schedule parsing are covered here rather than only on a server.
/// RemoteControl's checks matter most: it executes commands, so a widened check is a security bug.
/// </summary>
public sealed class ChatBotLogicTests
{
    // --------------------------------------------------------------------------------------------------- AutoRespond - the matches-file grammar ---------------------------------------------------------------------------------------------------

    [Fact]
    public void AutoRespond_Parse_ReadsASubstringRule()
    {
        MatchesFileParseResult parsed = MatchesFile.Parse(
        [
            "[Match]",
            "match=hello",
            "action=send hi",
        ]);

        RespondRule rule = Assert.Single(parsed.Rules);
        Assert.Equal("hello", rule.Match);
        Assert.Null(rule.Regex);
        Assert.Equal("send hi", rule.ActionPublic);
        Assert.False(rule.OwnersOnly);
        Assert.Equal(TimeSpan.Zero, rule.Cooldown);
    }

    [Fact]
    public void AutoRespond_Parse_ReadsEveryLegacyKeyOfARule()
    {
        MatchesFileParseResult parsed = MatchesFile.Parse(
        [
            "[Match]",
            "regex=^hi (.+)$",
            "action=send public $1",
            "actionprivate=send private $1",
            "actionother=send other $1",
            "ownersonly=true",
            "cooldown=30",
        ]);

        RespondRule rule = Assert.Single(parsed.Rules);
        Assert.NotNull(rule.Regex);
        Assert.Equal("send public $1", rule.ActionPublic);
        Assert.Equal("send private $1", rule.ActionPrivate);
        Assert.Equal("send other $1", rule.ActionOther);
        Assert.True(rule.OwnersOnly);
        Assert.Equal(TimeSpan.FromSeconds(30), rule.Cooldown);
    }

    [Fact]
    public void AutoRespond_Parse_StripsCommentsAndBlankLines()
    {
        MatchesFileParseResult parsed = MatchesFile.Parse(
        [
            "# a comment",
            string.Empty,
            "[Match]      # trailing comment on the header",
            "match=ping   # trailing comment on a value",
            "action=send pong",
        ]);

        RespondRule rule = Assert.Single(parsed.Rules);
        Assert.Equal("ping", rule.Match?.Trim());
    }

    [Fact]
    public void AutoRespond_Parse_ReadsSeveralRules()
    {
        MatchesFileParseResult parsed = MatchesFile.Parse(
        [
            "[Match]",
            "match=one",
            "action=send 1",
            "[Match]",
            "match=two",
            "action=send 2",
        ]);

        Assert.Equal(2, parsed.Rules.Count);
        Assert.Equal("one", parsed.Rules[0].Match);
        Assert.Equal("two", parsed.Rules[1].Match);
    }

    [Fact]
    public void AutoRespond_Parse_DropsARuleWithNoAction()
    {
        MatchesFileParseResult parsed = MatchesFile.Parse(["[Match]", "match=hello"]);

        Assert.Empty(parsed.Rules);
        Assert.Single(parsed.Diagnostics);
    }

    [Fact]
    public void AutoRespond_Parse_DropsARuleWithNoTrigger()
    {
        MatchesFileParseResult parsed = MatchesFile.Parse(["[Match]", "action=send hi"]);

        Assert.Empty(parsed.Rules);
        Assert.Single(parsed.Diagnostics);
    }

    [Fact]
    public void AutoRespond_Parse_ReportsAnInvalidRegexAndKeepsGoing()
    {
        MatchesFileParseResult parsed = MatchesFile.Parse(
        [
            "[Match]",
            "regex=([unclosed",
            "action=send hi",
            "[Match]",
            "match=ok",
            "action=send ok",
        ]);

        Assert.Single(parsed.Rules);
        Assert.Equal("ok", parsed.Rules[0].Match);
        Assert.Contains(parsed.Diagnostics, d => d.Contains("([unclosed", StringComparison.Ordinal));
    }

    [Fact]
    public void AutoRespond_Parse_OnAnEmptyFileYieldsNothing()
    {
        MatchesFileParseResult parsed = MatchesFile.Parse([]);

        Assert.Empty(parsed.Rules);
        Assert.Empty(parsed.Diagnostics);
    }

    [Fact]
    public void AutoRespond_DefaultFileContent_ParsesIntoAUsableRule()
    {
        MatchesFileParseResult parsed = MatchesFile.Parse(
            MatchesFile.DefaultFileContent.Split('\n'));

        Assert.Single(parsed.Rules);
    }

    // --------------------------------------------------------------------------------------------------- AutoRespond - rule matching ---------------------------------------------------------------------------------------------------

    private static RespondRule SubstringRule(
        string match, string? actionPublic = "send hit", string? actionPrivate = null, string? actionOther = null,
        bool ownersOnly = false, int cooldownSeconds = 0)
        => new(null, match, actionPublic, actionPrivate, actionOther, ownersOnly, TimeSpan.FromSeconds(cooldownSeconds));

    [Fact]
    public void AutoRespond_Match_IsCaseInsensitiveOnSubstrings()
    {
        RespondRule rule = SubstringRule("HELLO");
        var captures = new Dictionary<string, string>(StringComparer.Ordinal);

        Assert.Equal(
            "send hit",
            rule.TryMatch("bob", "well hello there", RespondMessageType.Public, [], captures, DateTime.Now));
    }

    [Fact]
    public void AutoRespond_Match_ReturnsNullWhenTheSubstringIsAbsent()
    {
        RespondRule rule = SubstringRule("hello");
        var captures = new Dictionary<string, string>(StringComparer.Ordinal);

        Assert.Null(rule.TryMatch("bob", "goodbye", RespondMessageType.Public, [], captures, DateTime.Now));
    }

    [Fact]
    public void AutoRespond_Match_PicksTheActionForTheMessageKind()
    {
        RespondRule rule = SubstringRule("hi", actionPublic: "pub", actionPrivate: "priv", actionOther: "other");
        var captures = new Dictionary<string, string>(StringComparer.Ordinal);

        Assert.Equal("pub", rule.TryMatch("bob", "hi", RespondMessageType.Public, [], captures, DateTime.Now));
        Assert.Equal("priv", rule.TryMatch("bob", "hi", RespondMessageType.Private, [], captures, DateTime.Now));
        Assert.Equal("other", rule.TryMatch("bob", "hi", RespondMessageType.Other, [], captures, DateTime.Now));
    }

    [Fact]
    public void AutoRespond_Match_SkipsAKindWithNoConfiguredAction()
    {
        RespondRule rule = SubstringRule("hi", actionPublic: "pub", actionPrivate: null);
        var captures = new Dictionary<string, string>(StringComparer.Ordinal);

        Assert.Null(rule.TryMatch("bob", "hi", RespondMessageType.Private, [], captures, DateTime.Now));
    }

    [Fact]
    public void AutoRespond_Match_SubstitutesTheSenderForDollarU()
    {
        RespondRule rule = SubstringRule("hi", actionPublic: "send hello $u");
        var captures = new Dictionary<string, string>(StringComparer.Ordinal);

        Assert.Equal(
            "send hello bob",
            rule.TryMatch("bob", "hi", RespondMessageType.Public, [], captures, DateTime.Now));
        Assert.Equal("bob", captures["match_u"]);
        Assert.Equal("hi", captures["match_0"]);
    }

    [Fact]
    public void AutoRespond_Match_SubstitutesRegexCaptures()
    {
        var rule = new RespondRule(
            new Regex(@"^give me (\d+) (\w+)$"), null, "send giving $1 $2 to $u", null, null, false, TimeSpan.Zero);
        var captures = new Dictionary<string, string>(StringComparer.Ordinal);

        string? action = rule.TryMatch(
            "bob", "give me 12 apples", RespondMessageType.Public, [], captures, DateTime.Now);

        Assert.Equal("send giving 12 apples to bob", action);
        Assert.Equal("give me 12 apples", captures["match_0"]);
        Assert.Equal("12", captures["match_1"]);
        Assert.Equal("apples", captures["match_2"]);
        Assert.Equal("bob", captures["match_u"]);
    }

    [Fact]
    public void AutoRespond_Match_SubstitutesDoubleDigitCapturesBeforeSingleDigitOnes()
    {
        // The legacy loop walks the groups downward so $10 is replaced before $1 could eat its first char.
        var pattern = new Regex(@"^(a) (b) (c) (d) (e) (f) (g) (h) (i) (j)$");
        var rule = new RespondRule(pattern, null, "send $10-$1", null, null, false, TimeSpan.Zero);
        var captures = new Dictionary<string, string>(StringComparer.Ordinal);

        Assert.Equal(
            "send j-a",
            rule.TryMatch("bob", "a b c d e f g h i j", RespondMessageType.Public, [], captures, DateTime.Now));
    }

    [Fact]
    public void AutoRespond_Match_HonorsTheCooldown()
    {
        RespondRule rule = SubstringRule("hi", cooldownSeconds: 60);
        var captures = new Dictionary<string, string>(StringComparer.Ordinal);
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);

        Assert.NotNull(rule.TryMatch("bob", "hi", RespondMessageType.Public, [], captures, start));
        Assert.Null(rule.TryMatch("bob", "hi", RespondMessageType.Public, [], captures, start.AddSeconds(30)));
        Assert.NotNull(rule.TryMatch("bob", "hi", RespondMessageType.Public, [], captures, start.AddSeconds(61)));
    }

    [Fact]
    public void AutoRespond_Match_CooldownIsNotArmedByANonMatch()
    {
        RespondRule rule = SubstringRule("hi", cooldownSeconds: 60);
        var captures = new Dictionary<string, string>(StringComparer.Ordinal);
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);

        Assert.Null(rule.TryMatch("bob", "nope", RespondMessageType.Public, [], captures, start));
        Assert.NotNull(rule.TryMatch("bob", "hi", RespondMessageType.Public, [], captures, start.AddSeconds(1)));
    }

    [Fact]
    public void AutoRespond_Match_OwnersOnlyRejectsANonOwner()
    {
        RespondRule rule = SubstringRule("hi", ownersOnly: true);
        var captures = new Dictionary<string, string>(StringComparer.Ordinal);

        Assert.Null(rule.TryMatch("mallory", "hi", RespondMessageType.Public, ["bob"], captures, DateTime.Now));
        Assert.NotNull(rule.TryMatch("Bob", "hi", RespondMessageType.Public, ["bob"], captures, DateTime.Now));
    }

    [Fact]
    public void AutoRespond_Match_OwnersOnlyRejectsAnEmptySender()
    {
        RespondRule rule = SubstringRule("hi", ownersOnly: true);
        var captures = new Dictionary<string, string>(StringComparer.Ordinal);

        Assert.Null(rule.TryMatch(string.Empty, "hi", RespondMessageType.Public, ["bob"], captures, DateTime.Now));
    }

    [Theory]
    [InlineData(ChatKind.Chat, RespondMessageType.Public)]
    [InlineData(ChatKind.PrivateMessage, RespondMessageType.Private)]
    [InlineData(ChatKind.System, RespondMessageType.Other)]
    [InlineData(ChatKind.TeleportRequest, RespondMessageType.Other)]
    [InlineData(ChatKind.Unknown, RespondMessageType.Other)]
    public void AutoRespond_ToMessageType_MapsTheClassifiedKind(ChatKind kind, RespondMessageType expected)
        => Assert.Equal(expected, MatchesFile.ToMessageType(kind));

    [Fact]
    public void AutoRespond_MatchTarget_UsesTheBodyForChatAndTheWholeLineOtherwise()
    {
        Assert.Equal("body", MatchesFile.MatchTarget(RespondMessageType.Public, "body", "<bob> body"));
        Assert.Equal("body", MatchesFile.MatchTarget(RespondMessageType.Private, "body", "bob whispers: body"));
        Assert.Equal("raw line", MatchesFile.MatchTarget(RespondMessageType.Other, "body", "raw line"));
    }

    // --------------------------------------------------------------------------------------------------- AutoRespond - the legacy formatting-code renderer behind Match_Colors ---------------------------------------------------------------------------------------------------

    [Fact]
    public void LegacyText_Render_OnPlainTextEmitsNoCodes()
        => Assert.Equal("hello", LegacyText.Render(Component.Text("hello"), null));

    [Fact]
    public void LegacyText_Render_OnANullComponentIsEmpty()
        => Assert.Equal(string.Empty, LegacyText.Render(null, null));

    [Fact]
    public void LegacyText_Render_EmitsTheNamedColorCode()
    {
        var red = new Component(new TextContent("danger"), new Style { Color = TextColor.Red });

        Assert.Equal("\u00a7cdanger", LegacyText.Render(red, null));
    }

    [Fact]
    public void LegacyText_Render_EmitsFormatCodes()
    {
        var bold = new Component(new TextContent("loud"), new Style { Bold = true, Italic = true });

        Assert.Equal("\u00a7l\u00a7oloud", LegacyText.Render(bold, null));
    }

    [Fact]
    public void LegacyText_Render_InheritsTheParentStyleIntoChildren()
    {
        var tree = new Component(
            new TextContent("a"),
            new Style { Color = TextColor.Gold },
            [new Component(new TextContent("b"))]);

        // The child inherits gold, so no second code sequence is emitted.
        Assert.Equal("\u00a76ab", LegacyText.Render(tree, null));
    }

    [Fact]
    public void LegacyText_Render_ResetsBetweenDifferentStyles()
    {
        var tree = new Component(
            TextContent.Empty,
            Style.Empty,
            [
                new Component(new TextContent("a"), new Style { Color = TextColor.Red }),
                new Component(new TextContent("b"), new Style { Color = TextColor.Blue }),
            ]);

        Assert.Equal("\u00a7ca\u00a7r\u00a79b", LegacyText.Render(tree, null));
    }

    [Fact]
    public void LegacyText_Render_KeepsThePlainTextReadable()
    {
        var tree = new Component(
            TextContent.Empty,
            Style.Empty,
            [
                new Component(new TextContent("<bob> "), new Style { Color = TextColor.Gray }),
                new Component(new TextContent("hello"), new Style { Color = TextColor.White }),
            ]);

        string rendered = LegacyText.Render(tree, null);

        Assert.Contains("<bob> ", rendered, StringComparison.Ordinal);
        Assert.Contains("hello", rendered, StringComparison.Ordinal);
    }

    // --------------------------------------------------------------------------------------------------- ChatLog ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("all", MessageFilter.All)]
    [InlineData("messages", MessageFilter.Messages)]
    [InlineData("chat", MessageFilter.Chat)]
    [InlineData("private_chat", MessageFilter.PrivateChat)]
    [InlineData("internal_msg", MessageFilter.InternalMsg)]
    [InlineData("ALL", MessageFilter.All)]
    [InlineData("  chat  ", MessageFilter.Chat)]
    public void ChatLog_TryParseFilter_AcceptsTheLegacySpellings(string value, MessageFilter expected)
    {
        Assert.True(ChatLogFormat.TryParseFilter(value, out MessageFilter filter));
        Assert.Equal(expected, filter);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("nonsense")]
    public void ChatLog_TryParseFilter_RejectsUnknownValuesAndFallsBackToMessages(string? value)
    {
        Assert.False(ChatLogFormat.TryParseFilter(value, out MessageFilter filter));
        Assert.Equal(MessageFilter.Messages, filter);
    }

    [Theory]
    [InlineData(MessageFilter.All, "all")]
    [InlineData(MessageFilter.Messages, "messages")]
    [InlineData(MessageFilter.Chat, "chat")]
    [InlineData(MessageFilter.PrivateChat, "private_chat")]
    [InlineData(MessageFilter.InternalMsg, "internal_msg")]
    public void ChatLog_FilterName_RoundTrips(MessageFilter filter, string expected)
    {
        Assert.Equal(expected, ChatLogFormat.FilterName(filter));
        Assert.True(ChatLogFormat.TryParseFilter(expected, out MessageFilter parsed));
        Assert.Equal(filter, parsed);
    }

    [Fact]
    public void ChatLog_Select_MatchesTheLegacyFilterTable()
    {
        Assert.Equal(new FilterSelection(true, true, true, true), ChatLogFormat.Select(MessageFilter.All));
        Assert.Equal(new FilterSelection(true, true, false, true), ChatLogFormat.Select(MessageFilter.Messages));
        Assert.Equal(new FilterSelection(true, false, false, true), ChatLogFormat.Select(MessageFilter.Chat));
        Assert.Equal(new FilterSelection(false, true, false, true), ChatLogFormat.Select(MessageFilter.PrivateChat));
        Assert.Equal(new FilterSelection(false, false, false, true), ChatLogFormat.Select(MessageFilter.InternalMsg));
    }

    [Fact]
    public void ChatLog_BuildLine_UsesTheLegacyPrefixes()
    {
        FilterSelection all = ChatLogFormat.Select(MessageFilter.All);

        Assert.Equal("Chat bob: hello", ChatLogFormat.BuildLine(all, ChatKind.Chat, "bob", "hello", "<bob> hello"));
        Assert.Equal(
            "Private bob: hello",
            ChatLogFormat.BuildLine(all, ChatKind.PrivateMessage, "bob", "hello", "bob whispers: hello"));
        Assert.Equal(
            "Other: the server restarts",
            ChatLogFormat.BuildLine(all, ChatKind.System, null, "the server restarts", "the server restarts"));
    }

    [Fact]
    public void ChatLog_BuildLine_DropsWhatTheFilterExcludes()
    {
        FilterSelection chatOnly = ChatLogFormat.Select(MessageFilter.Chat);

        Assert.Null(ChatLogFormat.BuildLine(chatOnly, ChatKind.PrivateMessage, "bob", "hi", "bob whispers: hi"));
        Assert.Null(ChatLogFormat.BuildLine(chatOnly, ChatKind.System, null, "sys", "sys"));
        Assert.NotNull(ChatLogFormat.BuildLine(chatOnly, ChatKind.Chat, "bob", "hi", "<bob> hi"));
    }

    [Fact]
    public void ChatLog_BuildLine_MessagesModeDropsSystemLinesOnly()
    {
        FilterSelection messages = ChatLogFormat.Select(MessageFilter.Messages);

        Assert.NotNull(ChatLogFormat.BuildLine(messages, ChatKind.Chat, "bob", "hi", "<bob> hi"));
        Assert.NotNull(ChatLogFormat.BuildLine(messages, ChatKind.PrivateMessage, "bob", "hi", "w"));
        Assert.Null(ChatLogFormat.BuildLine(messages, ChatKind.System, null, "sys", "sys"));
    }

    [Fact]
    public void ChatLog_BuildLine_InternalMsgModeLogsNothingFromChat()
    {
        FilterSelection internalOnly = ChatLogFormat.Select(MessageFilter.InternalMsg);

        Assert.Null(ChatLogFormat.BuildLine(internalOnly, ChatKind.Chat, "bob", "hi", "<bob> hi"));
        Assert.Null(ChatLogFormat.BuildLine(internalOnly, ChatKind.PrivateMessage, "bob", "hi", "w"));
        Assert.Null(ChatLogFormat.BuildLine(internalOnly, ChatKind.System, null, "sys", "sys"));
    }

    [Fact]
    public void ChatLog_ApplyTimestamp_UsesTheLegacyFormat()
    {
        var now = new DateTime(2026, 7, 25, 9, 5, 3, DateTimeKind.Local);

        Assert.Equal("2026-07-25 09:05:03 Chat bob: hi", ChatLogFormat.ApplyTimestamp("Chat bob: hi", true, now));
        Assert.Equal("Chat bob: hi", ChatLogFormat.ApplyTimestamp("Chat bob: hi", false, now));
    }

    [Fact]
    public void ChatLog_ExpandFileName_ReplacesTheLegacyPlaceholders()
        => Assert.Equal(
            "chatlog-bob-localhost-25565.txt",
            ChatLogFormat.ExpandFileName("chatlog-%username%-%serverip%.txt", "bob", "localhost-25565"));

    [Fact]
    public void ChatLog_ExpandFileName_SanitizesCharactersAFileSystemRejects()
        => Assert.Equal("log-localhost_25565.txt", ChatLogFormat.ExpandFileName("log-%serverip%.txt", "x", "localhost:25565"));

    [Fact]
    public void ChatLog_ExpandFileName_LeavesAPlainNameAlone()
        => Assert.Equal("chatlog.txt", ChatLogFormat.ExpandFileName("chatlog.txt", "bob", "localhost"));

    [Fact]
    public void ChatLog_Settings_ValidateNormalizesTheFilterAndFileName()
    {
        var settings = new ChatLogSettings { Filter = "NONSENSE", Log_File = "   " };
        settings.Validate();

        Assert.Equal("messages", settings.Filter);
        Assert.Equal("chatlog-%username%-%serverip%.txt", settings.Log_File);
    }

    // --------------------------------------------------------------------------------------------------- PlayerListLogger ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(60.0, 60.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(0.5, 1.0)]
    [InlineData(0.0, 1.0)]
    [InlineData(-5.0, 1.0)]
    public void PlayerListLogger_ClampDelay_MatchesTheLegacyFloor(double input, double expected)
        => Assert.Equal(expected, PlayerListRecord.ClampDelay(input));

    [Fact]
    public void PlayerListLogger_Build_UsesTheLegacyUnpaddedHeader()
    {
        var now = new DateTime(2026, 7, 5, 9, 4, 0, DateTimeKind.Local);

        string record = PlayerListRecord.Build(now, ["bob", "alice"]);
        string[] lines = record.Split(Environment.NewLine);

        Assert.Equal("[2026/7/5 9:4]", lines[0]);
        Assert.Equal("bob, alice", lines[1]);
        Assert.Equal(string.Empty, lines[2]);
    }

    [Fact]
    public void PlayerListLogger_Build_WithNoPlayersStillWritesTheRecord()
    {
        var now = new DateTime(2026, 12, 31, 23, 59, 0, DateTimeKind.Local);

        string[] lines = PlayerListRecord.Build(now, []).Split(Environment.NewLine);

        Assert.Equal("[2026/12/31 23:59]", lines[0]);
        Assert.Equal(string.Empty, lines[1]);
    }

    [Fact]
    public void PlayerListLogger_Settings_ValidateClampsTheDelayAndFileName()
    {
        var settings = new PlayerListLoggerSettings { Delay = 0.2, File = "  " };
        settings.Validate();

        Assert.Equal(1.0, settings.Delay);
        Assert.Equal("playerlog.txt", settings.File);
    }

    // --------------------------------------------------------------------------------------------------- RemoteControl - the authorisation checks (a widened check here is a security regression) ---------------------------------------------------------------------------------------------------

    private static readonly string[] Owners = ["bob", "alice"];

    [Theory]
    [InlineData("bob", true)]
    [InlineData("BOB", true)]
    [InlineData("  bob  ", true)]
    [InlineData("Alice", true)]
    [InlineData("mallory", false)]
    [InlineData("bo", false)]
    [InlineData("bobby", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void RemoteControl_IsOwner_MatchesTheLegacyCheck(string? sender, bool expected)
        => Assert.Equal(expected, RemoteControlAuth.IsOwner(sender, Owners));

    [Fact]
    public void RemoteControl_IsOwner_WithNoConfiguredOwnersAuthorisesNobody()
    {
        Assert.False(RemoteControlAuth.IsOwner("bob", []));
        Assert.False(RemoteControlAuth.IsOwner("bob", null));
    }

    [Fact]
    public void RemoteControl_Decide_RunsACommandOnlyForAnOwnerWhisper()
        => Assert.Equal(
            RemoteAction.RunCommand,
            RemoteControlAuth.Decide(ChatKind.PrivateMessage, "bob", "health", Owners, true, false));

    [Fact]
    public void RemoteControl_Decide_IgnoresAWhisperFromANonOwner()
        => Assert.Equal(
            RemoteAction.Ignore,
            RemoteControlAuth.Decide(ChatKind.PrivateMessage, "mallory", "health", Owners, true, false));

    [Fact]
    public void RemoteControl_Decide_IgnoresAnEmptyWhisperBody()
        => Assert.Equal(
            RemoteAction.Ignore,
            RemoteControlAuth.Decide(ChatKind.PrivateMessage, "bob", "   ", Owners, true, false));

    [Theory]
    [InlineData(ChatKind.Chat)]
    [InlineData(ChatKind.System)]
    [InlineData(ChatKind.Unknown)]
    public void RemoteControl_Decide_NeverRunsACommandFromPublicOrSystemLines(ChatKind kind)
        => Assert.Equal(
            RemoteAction.Ignore,
            RemoteControlAuth.Decide(kind, "bob", "health", Owners, true, false));

    [Fact]
    public void RemoteControl_Decide_AcceptsAnOwnerTeleportRequest()
        => Assert.Equal(
            RemoteAction.AcceptTeleport,
            RemoteControlAuth.Decide(ChatKind.TeleportRequest, "bob", "x", Owners, true, false));

    [Fact]
    public void RemoteControl_Decide_RejectsANonOwnerTeleportRequestUnlessEveryoneIsAllowed()
    {
        Assert.Equal(
            RemoteAction.Ignore,
            RemoteControlAuth.Decide(ChatKind.TeleportRequest, "mallory", "x", Owners, true, false));
        Assert.Equal(
            RemoteAction.AcceptTeleport,
            RemoteControlAuth.Decide(ChatKind.TeleportRequest, "mallory", "x", Owners, true, true));
    }

    [Fact]
    public void RemoteControl_Decide_IgnoresTeleportRequestsWhenAutoTpacceptIsOff()
    {
        Assert.Equal(
            RemoteAction.Ignore,
            RemoteControlAuth.Decide(ChatKind.TeleportRequest, "bob", "x", Owners, false, false));
        Assert.Equal(
            RemoteAction.Ignore,
            RemoteControlAuth.Decide(ChatKind.TeleportRequest, "bob", "x", Owners, false, true));
    }

    [Fact]
    public void RemoteControl_Decide_WithNoOwnersRunsNothingButStillHonorsTpacceptEveryone()
    {
        Assert.Equal(
            RemoteAction.Ignore,
            RemoteControlAuth.Decide(ChatKind.PrivateMessage, "bob", "health", [], true, true));
        Assert.Equal(
            RemoteAction.AcceptTeleport,
            RemoteControlAuth.Decide(ChatKind.TeleportRequest, "bob", "x", [], true, true));
    }

    [Fact]
    public void RemoteControl_BuildReply_UsesTheConfiguredPrivateMessageCommand()
    {
        Assert.Equal("/tell bob Done.", RemoteControlAuth.BuildReply("tell", "bob", "Done."));
        Assert.Equal("/msg bob Done.", RemoteControlAuth.BuildReply("msg", "bob", "Done."));
        Assert.Equal("/tell bob Done.", RemoteControlAuth.BuildReply("   ", "bob", "Done."));
    }

    // --------------------------------------------------------------------------------------------------- ScriptScheduler ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1.0, 10.0, 1.0, 10.0)]
    [InlineData(0.0, 10.0, 0.1, 10.0)]
    [InlineData(-5.0, -1.0, 0.1, 0.1)]
    [InlineData(10.0, 1.0, 1.0, 10.0)]
    [InlineData(5.0, 5.0, 5.0, 5.0)]
    public void ScriptScheduler_ClampInterval_MatchesTheLegacyClamping(
        double min, double max, double expectedMin, double expectedMax)
    {
        (double low, double high) = ScheduleRules.ClampInterval(min, max);

        Assert.Equal(expectedMin, low, 3);
        Assert.Equal(expectedMax, high, 3);
    }

    [Theory]
    [InlineData("14:00:00", 14, 0, 0)]
    [InlineData("14:00", 14, 0, 0)]
    [InlineData("  23:59:59  ", 23, 59, 59)]
    [InlineData("00:00:00", 0, 0, 0)]
    public void ScriptScheduler_TryParseTime_AcceptsTheSupportedShapes(
        string value, int hour, int minute, int second)
    {
        Assert.True(ScheduleRules.TryParseTime(value, out TimeOnly time));
        Assert.Equal(new TimeOnly(hour, minute, second), time);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("nonsense")]
    [InlineData("25:00:00")]
    [InlineData("1.02:00:00")]
    public void ScriptScheduler_TryParseTime_RejectsTheRest(string? value)
        => Assert.False(ScheduleRules.TryParseTime(value, out _));

    [Fact]
    public void ScriptScheduler_ValidTimes_KeepsOnlyTheParseableEntries()
    {
        IReadOnlyList<TimeOnly> times = ScheduleRules.ValidTimes(["14:00:00", "nope", "06:30"]);

        Assert.Equal(2, times.Count);
        Assert.Equal(new TimeOnly(14, 0, 0), times[0]);
        Assert.Equal(new TimeOnly(6, 30, 0), times[1]);
    }

    [Fact]
    public void ScriptScheduler_HasAction_RequiresANonBlankAction()
    {
        Assert.True(ScheduleRules.HasAction(new TaskConfig { Action = "send hi" }));
        Assert.False(ScheduleRules.HasAction(new TaskConfig { Action = "  " }));
        Assert.False(ScheduleRules.HasAction(new TaskConfig { Action = string.Empty }));
    }

    [Fact]
    public void ScriptScheduler_HasTrigger_AcceptsEachLegacyTriggerKind()
    {
        Assert.True(ScheduleRules.HasTrigger(new TaskConfig { Trigger_On_Login = true }));
        Assert.True(ScheduleRules.HasTrigger(new TaskConfig { Trigger_On_First_Login = true }));
        Assert.True(ScheduleRules.HasTrigger(new TaskConfig
        {
            Trigger_On_Times = new TriggerOnTimeConfig { Enable = true, Times = ["14:00:00"] },
        }));
        Assert.True(ScheduleRules.HasTrigger(new TaskConfig
        {
            Trigger_On_Interval = new TriggerOnIntervalConfig { Enable = true, MinTime = 1, MaxTime = 2 },
        }));
    }

    [Fact]
    public void ScriptScheduler_HasTrigger_RejectsATaskWithNoUsableTrigger()
    {
        Assert.False(ScheduleRules.HasTrigger(new TaskConfig()));
        Assert.False(ScheduleRules.HasTrigger(new TaskConfig
        {
            // Enabled but with no parseable time.
            Trigger_On_Times = new TriggerOnTimeConfig { Enable = true, Times = ["nonsense"] },
        }));
        Assert.False(ScheduleRules.HasTrigger(new TaskConfig
        {
            // Times present but the trigger is off.
            Trigger_On_Times = new TriggerOnTimeConfig { Enable = false, Times = ["14:00:00"] },
        }));
        Assert.False(ScheduleRules.HasTrigger(new TaskConfig
        {
            Trigger_On_Interval = new TriggerOnIntervalConfig { Enable = false, MinTime = 1, MaxTime = 2 },
        }));
    }

    [Fact]
    public void ScriptScheduler_ShouldRunOnLogin_MatchesTheLegacyRule()
    {
        var onLogin = new TaskConfig { Trigger_On_Login = true };
        var onFirstLogin = new TaskConfig { Trigger_On_First_Login = true };
        var never = new TaskConfig();

        Assert.True(ScheduleRules.ShouldRunOnLogin(onLogin, isFirstLogin: true));
        Assert.True(ScheduleRules.ShouldRunOnLogin(onLogin, isFirstLogin: false));
        Assert.True(ScheduleRules.ShouldRunOnLogin(onFirstLogin, isFirstLogin: true));
        Assert.False(ScheduleRules.ShouldRunOnLogin(onFirstLogin, isFirstLogin: false));
        Assert.False(ScheduleRules.ShouldRunOnLogin(never, isFirstLogin: true));
    }

    [Fact]
    public void ScriptScheduler_Settings_ValidateClampsEveryTaskInterval()
    {
        var settings = new ScriptSchedulerSettings
        {
            TaskList =
            [
                new TaskConfig
                {
                    Trigger_On_Interval = new TriggerOnIntervalConfig { Enable = true, MinTime = 9, MaxTime = 2 },
                },
            ],
        };

        settings.Validate();

        Assert.Equal(2.0, settings.TaskList[0].Trigger_On_Interval.MinTime, 3);
        Assert.Equal(9.0, settings.TaskList[0].Trigger_On_Interval.MaxTime, 3);
    }

    [Fact]
    public void ScriptScheduler_Describe_MentionsTheTaskNameAndAction()
    {
        string described = ScheduleRules.Describe(new TaskConfig { Task_Name = "nightly", Action = "send hi" });

        Assert.Contains("nightly", described, StringComparison.Ordinal);
        Assert.Contains("send hi", described, StringComparison.Ordinal);
    }
}
