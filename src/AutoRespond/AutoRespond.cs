// AutoRespond: matches incoming chat against rules from a matches file and runs the matching rule's action as an MCC command.
// The action picked depends on the kind of line (public chat, private message or anything else), and each rule has its own cooldown and owners-only flag.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DMCBK.Core.Commands;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Tomlet.Attributes;
using Umpk.Client.Events;
using Umpk.Text;

namespace DMCBK.OfficialPlugins.AutoRespond;

/// <summary>
/// Renders a chat component back to section-sign formatting codes, which is what <c>Match_Colors</c> matches against.
/// Pure logic, unit tested.
/// </summary>
public static class LegacyText
{
    private const char Section = '\u00a7';

    private static readonly Dictionary<string, char> ColorCodes = new(StringComparer.Ordinal)
    {
        ["black"] = '0',
        ["dark_blue"] = '1',
        ["dark_green"] = '2',
        ["dark_aqua"] = '3',
        ["dark_red"] = '4',
        ["dark_purple"] = '5',
        ["gold"] = '6',
        ["gray"] = '7',
        ["dark_gray"] = '8',
        ["blue"] = '9',
        ["green"] = 'a',
        ["aqua"] = 'b',
        ["red"] = 'c',
        ["light_purple"] = 'd',
        ["yellow"] = 'e',
        ["white"] = 'f',
    };

    /// <summary>Flattens a component tree to text carrying section-sign color and format codes.</summary>
    public static string Render(Component? component, ITranslationSource? translations)
    {
        if (component is null)
            return string.Empty;

        var builder = new StringBuilder();
        var state = new RenderState();
        Append(component, Style.Empty, builder, state, translations);
        return builder.ToString();
    }

    private static void Append(
        Component node, Style inherited, StringBuilder builder, RenderState state, ITranslationSource? translations)
    {
        Style effective = node.Style.ApplyTo(inherited);
        string own = new Component(node.Content, Style.Empty).ToPlainText(translations);
        if (own.Length > 0)
        {
            AppendCodes(builder, effective, state);
            builder.Append(own);
        }

        foreach (Component child in node.Children)
            Append(child, effective, builder, state, translations);
    }

    private static void AppendCodes(StringBuilder builder, Style style, RenderState state)
    {
        char? color = style.Color is { IsNamed: true, Name: { } name } && ColorCodes.TryGetValue(name, out char code)
            ? code
            : null;
        bool bold = style.Bold ?? false;
        bool italic = style.Italic ?? false;
        bool underlined = style.Underlined ?? false;
        bool strikethrough = style.Strikethrough ?? false;
        bool obfuscated = style.Obfuscated ?? false;

        if (state.Initialized
            && color == state.Color
            && bold == state.Bold
            && italic == state.Italic
            && underlined == state.Underlined
            && strikethrough == state.Strikethrough
            && obfuscated == state.Obfuscated)
            return;

        // A style change is expressed the way vanilla does it: reset, then the color, then the formats.
        if (state.Initialized)
            builder.Append(Section).Append('r');

        if (color is { } value)
            builder.Append(Section).Append(value);

        if (obfuscated)
            builder.Append(Section).Append('k');

        if (bold)
            builder.Append(Section).Append('l');

        if (strikethrough)
            builder.Append(Section).Append('m');

        if (underlined)
            builder.Append(Section).Append('n');

        if (italic)
            builder.Append(Section).Append('o');

        state.Initialized = true;
        state.Color = color;
        state.Bold = bold;
        state.Italic = italic;
        state.Underlined = underlined;
        state.Strikethrough = strikethrough;
        state.Obfuscated = obfuscated;
    }

    private sealed class RenderState
    {
        public bool Initialized { get; set; }

        public char? Color { get; set; }

        public bool Bold { get; set; }

        public bool Italic { get; set; }

        public bool Underlined { get; set; }

        public bool Strikethrough { get; set; }

        public bool Obfuscated { get; set; }
    }
}

/// <summary>The kind of line a rule action is chosen for.</summary>
public enum RespondMessageType
{
    /// <summary>A public chat message.</summary>
    Public,

    /// <summary>A private message (whisper) addressed to the client.</summary>
    Private,

    /// <summary>Anything else (server/system lines).</summary>
    Other,
}

/// <summary>
/// One respond rule: a trigger (regex or substring), the per-kind actions, the owners-only flag and the cooldown.
/// </summary>
public sealed class RespondRule
{
    private DateTime _cooldownExpiration = DateTime.MinValue;

    /// <summary>Creates a rule. Exactly one of <paramref name="regex"/>/<paramref name="match"/> is used.</summary>
    public RespondRule(
        Regex? regex,
        string? match,
        string? actionPublic,
        string? actionPrivate,
        string? actionOther,
        bool ownersOnly,
        TimeSpan cooldown)
    {
        Regex = regex;
        Match = match;
        ActionPublic = actionPublic;
        ActionPrivate = actionPrivate;
        ActionOther = actionOther;
        OwnersOnly = ownersOnly;
        Cooldown = cooldown;
    }

    /// <summary>The trigger regular expression, or null when this is a substring rule.</summary>
    public Regex? Regex { get; }

    /// <summary>The trigger substring (case-insensitive), or null when this is a regex rule.</summary>
    public string? Match { get; }

    /// <summary>The action run for public chat, or null when the rule ignores public chat.</summary>
    public string? ActionPublic { get; }

    /// <summary>The action run for private messages, or null when the rule ignores them.</summary>
    public string? ActionPrivate { get; }

    /// <summary>The action run for any other line, or null when the rule ignores them.</summary>
    public string? ActionOther { get; }

    /// <summary>Whether only messages from configured bot owners may fire this rule.</summary>
    public bool OwnersOnly { get; }

    /// <summary>The minimum time between two firings of this rule.</summary>
    public TimeSpan Cooldown { get; }

    /// <summary>True when the rule has at least one trigger.</summary>
    public bool HasTrigger => Regex is not null || Match is not null;

    /// <summary>True when the rule has at least one action.</summary>
    public bool HasAction => ActionPublic is not null || ActionPrivate is not null || ActionOther is not null;

    /// <summary>
    /// Tests the rule against one message and returns the command to run, or null when it does not fire.
    /// The gates run in order: cooldown, owners-only, the per-kind action lookup, then the trigger.
    /// On a regex hit the captures go into the action and into <paramref name="captures"/>, so the caller can publish them as variables.
    /// </summary>
    public string? TryMatch(
        string username,
        string message,
        RespondMessageType messageType,
        IReadOnlyCollection<string> botOwners,
        IDictionary<string, string> captures,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(captures);

        if (now < _cooldownExpiration)
            return null;

        if (OwnersOnly && !IsOwner(username, botOwners))
            return null;

        string? toSend = messageType switch
        {
            RespondMessageType.Public => ActionPublic,
            RespondMessageType.Private => ActionPrivate,
            _ => ActionOther,
        };

        if (string.IsNullOrEmpty(toSend))
            return null;

        if (Regex is not null)
        {
            if (!Regex.IsMatch(message))
                return null;

            _cooldownExpiration = now + Cooldown;
            Match regexMatch = Regex.Match(message);
            captures["match_0"] = regexMatch.Groups[0].Value;
            for (int i = regexMatch.Groups.Count - 1; i >= 1; i--)
            {
                toSend = toSend.Replace(
                    string.Create(CultureInfo.InvariantCulture, $"${i}"),
                    regexMatch.Groups[i].Value,
                    StringComparison.Ordinal);
                captures[string.Create(CultureInfo.InvariantCulture, $"match_{i}")] = regexMatch.Groups[i].Value;
            }

            captures["match_u"] = username;
            return toSend.Replace("$u", username, StringComparison.Ordinal);
        }

        if (!string.IsNullOrEmpty(Match)
            && message.Contains(Match, StringComparison.OrdinalIgnoreCase))
        {
            _cooldownExpiration = now + Cooldown;
            captures["match_0"] = message;
            captures["match_u"] = username;
            return toSend.Replace("$u", username, StringComparison.Ordinal);
        }

        return null;
    }

    /// <summary>Trimmed, lowercased membership in the configured owner list.</summary>
    public static bool IsOwner(string? username, IReadOnlyCollection<string> botOwners)
    {
        if (string.IsNullOrEmpty(username) || botOwners.Count == 0)
            return false;

        string normalized = username.Trim().ToLowerInvariant();
        foreach (string owner in botOwners)
        {
            if (string.Equals(owner.Trim().ToLowerInvariant(), normalized, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The key=value dump of the rule that the load-time diagnostics quote.
    /// Field names, not prose, so it is not translated.
    /// </summary>
    public override string ToString() => string.Format(
        CultureInfo.InvariantCulture,
        "match='{0}' regex='{1}' action='{2}' actionprivate='{3}' actionother='{4}' ownersonly={5} cooldown={6}",
        Match,
        Regex?.ToString(),
        ActionPublic,
        ActionPrivate,
        ActionOther,
        OwnersOnly,
        (int)Cooldown.TotalSeconds);
}

/// <summary>Why the parser rejected something.</summary>
public enum RuleProblemKind
{
    /// <summary>The rule has a trigger but nothing to run.</summary>
    NoAction,

    /// <summary>The rule has an action but nothing to fire it.</summary>
    NoTrigger,

    /// <summary>The regex would not compile.</summary>
    BadRegex,
}

/// <summary>One thing the parser rejected, with the offending text.</summary>
/// <param name="Kind">Why it was rejected.</param>
/// <param name="Detail">The rule's dump, or the pattern that would not compile.</param>
public sealed record RuleProblem(RuleProblemKind Kind, string Detail);

/// <summary>The outcome of parsing a matches file: the rules plus what was rejected.</summary>
/// <param name="Rules">The accepted rules, in file order.</param>
/// <param name="Problems">One entry per rejected rule or malformed value.</param>
public sealed record MatchesFileParseResult(IReadOnlyList<RespondRule> Rules, IReadOnlyList<RuleProblem> Problems)
{
    /// <summary>The offending text of each problem, in order.</summary>
    public IReadOnlyList<string> Diagnostics => [.. Problems.Select(p => p.Detail)];
}

/// <summary>The matches-file grammar, kept pure so it can be unit tested headlessly.</summary>
public static class MatchesFile
{
    /// <summary>Parses the matches-file grammar from its lines.</summary>
    public static MatchesFileParseResult Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var rules = new List<RespondRule>();
        var problems = new List<RuleProblem>();

        Regex? matchRegex = null;
        string? matchString = null;
        string? matchAction = null;
        string? matchActionPrivate = null;
        string? matchActionOther = null;
        bool ownersOnly = false;
        TimeSpan cooldown = TimeSpan.Zero;

        void Flush()
        {
            if (matchRegex is null
                && matchString is null
                && matchAction is null
                && matchActionPrivate is null
                && matchActionOther is null
                && !ownersOnly
                && cooldown == TimeSpan.Zero)
                return;

            var rule = new RespondRule(
                matchRegex, matchString, matchAction, matchActionPrivate, matchActionOther, ownersOnly, cooldown);

            if (!rule.HasAction)
                problems.Add(new RuleProblem(RuleProblemKind.NoAction, rule.ToString()));
            else if (!rule.HasTrigger)
                problems.Add(new RuleProblem(RuleProblemKind.NoTrigger, rule.ToString()));
            else
                rules.Add(rule);
        }

        void Reset()
        {
            matchRegex = null;
            matchString = null;
            matchAction = null;
            matchActionPrivate = null;
            matchActionOther = null;
            ownersOnly = false;
            cooldown = TimeSpan.Zero;
        }

        foreach (string rawLine in lines)
        {
            string line = (rawLine ?? string.Empty).Split('#')[0].Trim();
            if (line.Length == 0)
                continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                if (string.Equals(line[1..^1].Trim(), "match", StringComparison.OrdinalIgnoreCase))
                {
                    Flush();
                    Reset();
                }

                continue;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1)
                continue;

            string argName = line[..separator];
            string argValue = line[(separator + 1)..];

            switch (argName.Trim().ToLowerInvariant())
            {
                case "regex":
                    try
                    {
                        matchRegex = new Regex(argValue, RegexOptions.None, TimeSpan.FromSeconds(1));
                    }
                    catch (ArgumentException)
                    {
                        problems.Add(new RuleProblem(RuleProblemKind.BadRegex, argValue));
                    }

                    break;
                case "match":
                    matchString = argValue;
                    break;
                case "action":
                    matchAction = argValue;
                    break;
                case "actionprivate":
                    matchActionPrivate = argValue;
                    break;
                case "actionother":
                    matchActionOther = argValue;
                    break;
                case "ownersonly":
                    ownersOnly = bool.TryParse(argValue.Trim(), out bool owners) && owners;
                    break;
                case "cooldown":
                    cooldown = int.TryParse(argValue.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out int seconds)
                        ? TimeSpan.FromSeconds(seconds)
                        : TimeSpan.Zero;
                    break;
                default:
                    break;
            }
        }

        Flush();
        return new MatchesFileParseResult(rules, problems);
    }

    /// <summary>Maps a classified chat kind onto the message type used to pick the action.</summary>
    public static RespondMessageType ToMessageType(ChatKind kind) => kind switch
    {
        ChatKind.Chat => RespondMessageType.Public,
        ChatKind.PrivateMessage => RespondMessageType.Private,
        _ => RespondMessageType.Other,
    };

    /// <summary>
    /// The text a rule is tested against: the message body for public and private chat, not the '&lt;sender&gt; body' envelope, and the whole line for anything else.
    /// </summary>
    public static string MatchTarget(RespondMessageType messageType, string body, string raw)
        => messageType == RespondMessageType.Other ? raw : body;

    /// <summary>The default matches file written on first run, documenting the grammar.</summary>
    public static string DefaultFileContent =>
        "# AutoRespond rules. One [Match] section per rule.\n"
        + "#   match=<substring>          case-insensitive substring trigger\n"
        + "#   regex=<expression>         regular-expression trigger ($1..$n are substituted into the action)\n"
        + "#   action=<command>           run for public chat\n"
        + "#   actionprivate=<command>    run for private messages\n"
        + "#   actionother=<command>      run for any other line\n"
        + "#   ownersonly=true|false      only fire for the configured bot owners\n"
        + "#   cooldown=<seconds>         minimum delay between two firings of this rule\n"
        + "# $u is replaced by the sender name in every action.\n"
        + "\n"
        + "[Match]\n"
        + "match=ping\n"
        + "action=send pong\n"
        + "cooldown=5\n";
}


/// <summary>The user-editable, persisted settings for AutoRespond (serialized to settings.toml).</summary>
public sealed class AutoRespondSettings : IValidatablePluginSettings
{
    /// <summary>Master switch.</summary>
    [TomlInlineComment("$settings.enabled$")]
    public bool Enabled { get; set; }

    /// <summary>The rules file, resolved inside this plugin's data/ folder.</summary>
    [TomlInlineComment("$settings.matches_file$")]
    public string Matches_File { get; set; } = "matches.ini";

    /// <summary>Whether the color codes stay in the text the rules are tested against.</summary>
    [TomlInlineComment("$settings.match_colors$")]
    public bool Match_Colors { get; set; }

    /// <summary>Normalizes on load or reload.</summary>
    public void Validate()
    {
        Matches_File ??= string.Empty;
        if (Matches_File.Length == 0)
            Matches_File = "matches.ini";
    }
}

/// <summary>The AutoRespond plugin entry point.</summary>
public sealed class AutoRespondPlugin : IPlugin
{
    private readonly object _gate = new();
    private AutoRespondSettings _settings = new();
    private PluginContext? _context;
    private IReadOnlyList<RespondRule> _rules = [];

    /// <inheritdoc/>
    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "auto-respond";
        descriptor.Version = "2.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
        descriptor.WithSettings<AutoRespondSettings>();
    }

    /// <inheritdoc/>
    public Task ActivateAsync(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _settings = context.Settings.Load<AutoRespondSettings>();
        LoadRules(context);

        context.SessionStarted += (_, e) => e.Session.Events.Subscribe<ChatMessageReceived>(
            message => OnChat(context, message));
        return Task.CompletedTask;
    }

    /// <summary>The rules currently loaded (for diagnostics and tests).</summary>
    public IReadOnlyList<RespondRule> Rules
    {
        get
        {
            lock (_gate)
                return _rules;
        }
    }

    private void LoadRules(PluginContext context)
    {
        string path;
        try
        {
            path = context.Storage.GetPath(_settings.Matches_File);
        }
        catch (ArgumentException ex)
        {
            context.Logger.LogWarning(ex, "{Message}", context.Strings.Format("file_error", _settings.Matches_File));
            return;
        }

        try
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path, MatchesFile.DefaultFileContent);
                context.Logger.LogInformation("{Message}", context.Strings.Format("file_created", path));
            }

            context.Logger.LogDebug("{Message}", context.Strings.Format("loading", path));
            MatchesFileParseResult parsed = MatchesFile.Parse(File.ReadAllLines(path));
            foreach (RuleProblem problem in parsed.Problems)
                context.Logger.LogDebug("{Message}", context.Strings.Format(KeyOf(problem.Kind), problem.Detail));

            lock (_gate)
                _rules = parsed.Rules;

            if (parsed.Rules.Count == 0)
                context.Logger.LogWarning("{Message}", context.Strings.Get("no_rules"));
            else
                context.Logger.LogInformation("{Message}", context.Strings.Format("loaded", parsed.Rules.Count));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Logger.LogWarning(ex, "{Message}", context.Strings.Format("file_error", path));
        }
    }

    private static string KeyOf(RuleProblemKind kind) => kind switch
    {
        RuleProblemKind.NoAction => "no_action",
        RuleProblemKind.NoTrigger => "no_trigger",
        _ => "bad_regex",
    };

    private void OnChat(PluginContext context, ChatMessageReceived message)
    {
        if (!_settings.Enabled)
            return;

        IReadOnlyList<RespondRule> rules = Rules;
        if (rules.Count == 0)
            return;

        ClassifiedChat classified = context.Chat.Classify(message);
        RespondMessageType messageType = MatchesFile.ToMessageType(classified.Kind);
        string sender = classified.Sender ?? string.Empty;

        // Never react to our own messages.
        if (messageType != RespondMessageType.Other
            && context.CurrentSession is { } session
            && string.Equals(sender, session.State.Self.Username, StringComparison.OrdinalIgnoreCase))
            return;

        // The chat pipeline hands plugins plain text, so Match_Colors has to re-render the formatting codes from the component tree.
        string target = _settings.Match_Colors
            ? LegacyText.Render(message.Message, context.Translations)
            : MatchesFile.MatchTarget(messageType, classified.Body, classified.Raw);
        IReadOnlyCollection<string> owners = context.Client.Configuration?.Permissions.BotOwners ?? [];

        foreach (RespondRule rule in rules)
        {
            var captures = new Dictionary<string, string>(StringComparer.Ordinal);
            string? action = rule.TryMatch(sender, target, messageType, owners, captures, DateTime.Now);
            if (string.IsNullOrEmpty(action))
                continue;

            foreach (KeyValuePair<string, string> capture in captures)
                context.Variables.Set(capture.Key, capture.Value);

            // Dispatch off the session loop.
            // CommandService runs Brigadier synchronously and its commands block on the session loop, so dispatching from this handler, which runs on the loop, would deadlock the session.
            string command = action;
            _ = Task.Run(() => RunAsync(context, command));
        }
    }

    private static async Task RunAsync(PluginContext context, string action)
    {
        string command = context.Variables.Expand(action);
        context.Logger.LogInformation("{Message}", context.Strings.Format("running", command));
        try
        {
            CmdResult result = await context.Client.Commands.DispatchAsync(command).ConfigureAwait(false);
            if (result.Status != CmdStatus.Done || !string.IsNullOrWhiteSpace(result.Message))
                context.Logger.LogInformation("{Message}", context.Strings.Format("result", result.Message ?? string.Empty));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.LogWarning(ex, "{Message}", context.Strings.Format("running", command));
        }
    }

    /// <summary>Reloads the rules from disk (used by the settings reload path and by tests).</summary>
    public void Reload()
    {
        if (_context is { } context)
        {
            _settings = context.Settings.Load<AutoRespondSettings>();
            LoadRules(context);
        }
    }
}
