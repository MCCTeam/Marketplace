// Alerts - chat rules and game-event rules with per-rule routes, plus optional LLM smart matching through llm-core.
// Classic matching always works.
// Smart confirmation only judges chat, never events.

using System.Text.RegularExpressions;
using DMCBK.Core.Commands;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Tomlet.Attributes;
using Umpk.Client.Events;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DMCBK.OfficialPlugins.Tests")]

namespace DMCBK.OfficialPlugins.Alerts;

/// <summary>Every destination an alert can go to.</summary>
public static class AlertRoutes
{
    public const string Desktop = "desktop";
    public const string Discord = "discord";
    public const string Telegram = "telegram";
    public const string File = "file";
    public const string Beep = "beep";

    /// <summary>All routes, in the order they are listed and delivered.</summary>
    public static readonly IReadOnlyList<string> All = [Desktop, Discord, Telegram, File, Beep];

    /// <summary>True for a known route name (case-insensitive).</summary>
    public static bool IsKnown(string? name)
        => All.Any(r => string.Equals(r, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Normalizes a route list: trims, drops blanks and duplicates, keeps known order.</summary>
    public static List<string> Normalize(IEnumerable<string>? routes)
    {
        var wanted = new HashSet<string>(
            (routes ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()),
            StringComparer.OrdinalIgnoreCase);
        return All.Where(r => wanted.Contains(r)).ToList();
    }
}

/// <summary>One chat rule: patterns, where hits go, and whether the judge confirms first.</summary>
public sealed class AlertRuleConfig
{
    [TomlInlineComment("$settings.rule_name$")]
    public string Name { get; set; } = string.Empty;

    [TomlPrecedingComment("$settings.rule_match$")]
    public List<string> Match { get; set; } = [];

    [TomlInlineComment("$settings.rule_routes$")]
    public List<string> Routes { get; set; } = [];

    [TomlInlineComment("$settings.rule_beep$")]
    public bool Beep { get; set; } = true;

    [TomlInlineComment("$settings.rule_smart$")]
    public bool Smart { get; set; }

    [TomlInlineComment("$settings.rule_regex$")]
    public bool Regex { get; set; }

    [TomlInlineComment("$settings.rule_prompt$")]
    public string Prompt { get; set; } = string.Empty;

    [TomlInlineComment("$settings.rule_prompt_file$")]
    public string PromptFile { get; set; } = string.Empty;
}

/// <summary>One game-event rule. Events always fire directly; the judge never sees them.</summary>
public sealed class AlertEventRuleConfig
{
    [TomlInlineComment("$settings.event_name$")]
    public string Name { get; set; } = string.Empty;

    [TomlPrecedingComment("$settings.event_on$")]
    public List<string> On { get; set; } = [];

    [TomlInlineComment("$settings.event_routes$")]
    public List<string> Routes { get; set; } = [];

    [TomlInlineComment("$settings.event_beep$")]
    public bool Beep { get; set; } = true;

    [TomlInlineComment("$settings.event_cooldown$")]
    public int CooldownSeconds { get; set; }
}

/// <summary>The [Smart] table: everything the judge needs, in one place.</summary>
public sealed class AlertsSmartConfig
{
    [TomlInlineComment("$settings.smart_mode$")]
    public string Mode { get; set; } = "off";

    [TomlInlineComment("$settings.smart_model$")]
    public string Model { get; set; } = "local/qwen3-8b";

    [TomlInlineComment("$settings.smart_confidence$")]
    public double Confidence { get; set; } = 0.7;

    [TomlInlineComment("$settings.smart_prompt$")]
    public string Prompt { get; set; } = string.Empty;

    [TomlInlineComment("$settings.smart_prompt_file$")]
    public string PromptFile { get; set; } = string.Empty;

    [TomlInlineComment("$settings.smart_catchall_routes$")]
    public List<string> CatchAllRoutes { get; set; } = ["desktop"];

    [TomlInlineComment("$settings.smart_batch$")]
    public bool Batch { get; set; }

    [TomlInlineComment("$settings.smart_batch_size$")]
    public int BatchSize { get; set; } = 10;

    [TomlInlineComment("$settings.smart_batch_seconds$")]
    public int BatchSeconds { get; set; } = 15;

    [TomlInlineComment("$settings.smart_context$")]
    public List<string> Context { get; set; } = ["nick", "position", "server"];

    [TomlInlineComment("$settings.smart_owner$")]
    public string Owner { get; set; } = string.Empty;

    [TomlInlineComment("$settings.smart_history_lines$")]
    public int HistoryLines { get; set; } = 5;
}

/// <summary>The user-editable, persisted settings for Alerts (serialized to settings.toml).</summary>
public sealed class AlertsSettings : IValidatablePluginSettings
{
    [TomlInlineComment("$settings.enabled$")]
    public bool Enabled { get; set; } = true;

    [TomlInlineComment("$settings.use_regex$")]
    public bool UseRegex { get; set; }

    [TomlPrecedingComment("$settings.excludes$")]
    public List<string> Excludes { get; set; } = ["joined the game", "left the game"];

    [TomlPrecedingComment("$settings.rules$")]
    public List<AlertRuleConfig> Rule { get; set; } = [];

    [TomlPrecedingComment("$settings.event_rules$")]
    public List<AlertEventRuleConfig> EventRule { get; set; } = [];

    [TomlInlineComment("$settings.route_mode$")]
    public string RouteMode { get; set; } = "fixed";

    [TomlPrecedingComment("$settings.priority_patterns$")]
    public List<string> PriorityPatterns { get; set; } = ["whispers:"];

    [TomlInlineComment("$settings.log_to_file$")]
    public bool LogToFile { get; set; }

    [TomlInlineComment("$settings.log_file$")]
    public string LogFile { get; set; } = "alerts-log.txt";

    [TomlInlineComment("$settings.discord_target$")]
    public string Discord_Target { get; set; } = string.Empty;

    [TomlInlineComment("$settings.smart$")]
    public AlertsSmartConfig Smart { get; set; } = new();

    // Older flat keys, folded into Rule on load so existing configs keep working.
    public List<string>? Matches { get; set; }
    public bool Beep { get; set; } = true;
    public bool Send_To_Discord { get; set; }

    /// <summary>Normalizes on load or reload.</summary>
    public void Validate()
    {
        LogFile ??= string.Empty;
        if (LogFile.Trim().Length == 0)
            LogFile = "alerts-log.txt";

        Excludes ??= [];
        PriorityPatterns ??= [];
        Rule ??= [];
        EventRule ??= [];
        Smart ??= new();
        Smart.Context ??= [];
        Smart.CatchAllRoutes ??= [];
        Discord_Target = (Discord_Target ?? string.Empty).Trim();
        RouteMode = string.Equals(RouteMode?.Trim(), "model", StringComparison.OrdinalIgnoreCase)
            ? "model" : "fixed";
        Smart.Mode = Smart.Mode?.Trim().ToLowerInvariant() switch
        {
            "rules" => "rules",
            "full" => "full",
            _ => "off",
        };
        Smart.Confidence = Math.Clamp(Smart.Confidence <= 0 ? 0.7 : Smart.Confidence, 0.01, 1.0);
        Smart.BatchSize = Math.Clamp(Smart.BatchSize <= 0 ? 10 : Smart.BatchSize, 1, 100);
        Smart.BatchSeconds = Math.Clamp(Smart.BatchSeconds <= 0 ? 15 : Smart.BatchSeconds, 1, 300);
        Smart.HistoryLines = Math.Clamp(Smart.HistoryLines < 0 ? 5 : Smart.HistoryLines, 0, 50);

        foreach (AlertRuleConfig rule in Rule)
        {
            rule.Name = (rule.Name ?? string.Empty).Trim();
            rule.Match ??= [];
            rule.Prompt = (rule.Prompt ?? string.Empty).Trim();
            rule.PromptFile = (rule.PromptFile ?? string.Empty).Trim();
        }

        foreach (AlertEventRuleConfig rule in EventRule)
        {
            rule.Name = (rule.Name ?? string.Empty).Trim();
            rule.On ??= [];
            if (rule.CooldownSeconds < 0)
                rule.CooldownSeconds = 0;
        }

        MigrateLegacy();
    }

    private void MigrateLegacy()
    {
        if (Rule.Count > 0 || Matches is not { Count: > 0 })
            return;

        Rule.Add(new AlertRuleConfig
        {
            Name = "legacy",
            Match = [.. Matches],
            Routes = [.. AlertRoutes.All],
            Beep = Beep,
            Regex = UseRegex,
        });
    }
}

/// <summary>
/// The cross-plugin text handoff contract.
/// Same rules as the Map plugin's image contract: plugins load into separate collectible load contexts, so the message is a tagged <see cref="string"/> array, whose element type lives in the shared default context.
/// </summary>
public static class BridgeTextRequest
{
    /// <summary>Element 0 of the message: the versioned tag that identifies this contract.</summary>
    public const string Tag = "mcc.plugins.bridge.text.v1";

    /// <summary>The <c>Target</c> value naming the Discord bridge.</summary>
    public const string TargetDiscord = "discord";

    /// <summary>The <c>Target</c> value naming the Telegram bridge.</summary>
    public const string TargetTelegram = "telegram";

    /// <summary>Builds a request message: [tag, target, text, destination].</summary>
    public static string[] Create(string target, string text, string destination)
        => [Tag, target, text, destination ?? string.Empty];
}

/// <summary>One compiled chat rule: the config plus its precompiled matcher.</summary>
public sealed class CompiledRule
{
    public required AlertRuleConfig Config { get; init; }
    public required AlertMatcher Matcher { get; init; }
    public long Hits { get; set; }
}

/// <summary>One compiled event rule: the config plus the last firing time per event key.</summary>
public sealed class CompiledEventRule
{
    public required AlertEventRuleConfig Config { get; init; }
    public long Hits { get; set; }
    public Dictionary<string, DateTime> LastFired { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The Alerts plugin entry point.</summary>
public sealed class AlertsPlugin : IPlugin
{
    private readonly object _gate = new();
    private AlertsSettings _settings = new();
    private PluginContext? _context;
    private AlertMatcher _suppressors = AlertMatcher.Build([], useRegex: false);
    private List<CompiledRule> _rules = [];
    private List<CompiledEventRule> _eventRules = [];
    private CancellationTokenSource? _sessionCts;
    private LlmJudge? _judge;
    private LlmCoreLink? _cachedLink;
    private bool _coreWarningLogged;
    private DateTime _sessionStartUtc = DateTime.UtcNow;

    internal AlertsSettings Settings => _settings;

    /// <inheritdoc/>
    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "alerts";
        descriptor.Version = "3.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
        descriptor.WithSettings<AlertsSettings>();
    }

    /// <inheritdoc/>
    public Task ActivateAsync(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _settings = context.Settings.Load<AlertsSettings>();
        RebuildMatchers(context);
        context.Commands.Register(new AlertsCommand(this, context.Strings));
        context.SessionStarted += (_, e) => OnSessionStarted(context, e.Session);
        context.SessionEnded += (_, _) => OnSessionEnded();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DeactivateAsync(CancellationToken ct)
    {
        OnSessionEnded();
        return Task.CompletedTask;
    }

    /// <summary>Compiles the trigger lists, warning once per pattern that does not compile.</summary>
    internal void RebuildMatchers(PluginContext context)
    {
        _suppressors = AlertMatcher.Build(_settings.Excludes, _settings.UseRegex);
        var rules = new List<CompiledRule>();
        foreach (AlertRuleConfig config in _settings.Rule.Where(r => r.Name.Length > 0 && r.Match.Count > 0))
        {
            var matcher = AlertMatcher.Build(config.Match, config.Regex || _settings.UseRegex);
            rules.Add(new CompiledRule { Config = config, Matcher = matcher });
            foreach (string bad in matcher.Errors)
                context.Logger.LogWarning("{Message}", context.Strings.Format("bad_pattern", bad));
        }

        _rules = rules;
        _eventRules = _settings.EventRule
            .Where(r => r.Name.Length > 0 && r.On.Count > 0)
            .Select(r => new CompiledEventRule { Config = r })
            .ToList();
        foreach (string bad in _suppressors.Errors)
            context.Logger.LogWarning("{Message}", context.Strings.Format("bad_pattern", bad));
    }

    private void OnSessionStarted(PluginContext context, ISessionScope session)
    {
        lock (_gate)
        {
            _judge?.Dispose();
            _judge = new LlmJudge(this, context);
            _sessionCts?.Cancel();
            _sessionCts?.Dispose();
            _sessionCts = new CancellationTokenSource();
            _sessionStartUtc = DateTime.UtcNow;
        }

        MaybeWarnCoreMissing(context);
        session.Events.Subscribe<ChatMessageReceived>(message => OnChat(context, session, message));
        session.Events.Subscribe<Died>(_ => OnEvent(context, session, "died", "You died."));
        session.Events.Subscribe<Respawned>(_ => OnEvent(context, session, "respawned", "You respawned."));
        session.Events.Subscribe<ExplosionOccurred>(e => OnEvent(context, session, "explosion", $"Explosion at {Fmt(e.Center)}."));
        session.Events.Subscribe<EntityHurt>(e => OnSelfHurt(context, session, e));
        session.Events.Subscribe<HealthChanged>(e => OnHealth(context, session, e));
        session.Events.Subscribe<Disconnected>(_ => OnEvent(context, session, "disconnected", "Disconnected from the server."));
        session.Events.Subscribe<DifficultyChanged>(_ => OnEvent(context, session, "difficulty", "Server difficulty changed."));
    }

    private void OnSessionEnded()
    {
        LlmJudge? judge;
        lock (_gate)
        {
            _sessionCts?.Cancel();
            _sessionCts?.Dispose();
            _sessionCts = null;
            judge = _judge;
            _judge = null;
        }

        judge?.Flush();
        judge?.Dispose();
    }

    private static string Fmt(Umpk.Geometry.Vec3d v) => $"{v.X:0.#}, {v.Y:0.#}, {v.Z:0.#}";

    private void OnSelfHurt(PluginContext context, ISessionScope session, EntityHurt hurt)
    {
        if (hurt.EntityId == session.State.Self.EntityId)
            OnEvent(context, session, "hurt", "You took damage.");
    }

    private void OnHealth(PluginContext context, ISessionScope session, HealthChanged changed)
    {
        foreach (CompiledEventRule rule in _eventRules)
        {
            foreach (string on in rule.Config.On)
            {
                if (on.StartsWith("health-below-", StringComparison.OrdinalIgnoreCase)
                    && float.TryParse(on["health-below-".Length..], out float limit)
                    && changed.Health <= limit)
                    FireEventRule(context, rule, $"health-below-{limit}", $"Health is low: {changed.Health:0.#}.");
            }
        }
    }

    private void OnChat(PluginContext context, ISessionScope session, ChatMessageReceived message)
    {
        if (!_settings.Enabled)
            return;

        // Matching always sees the full original line, sender name included, never the stripped body.
        ClassifiedChat classified = context.Chat.Classify(message);
        string haystack = classified.Raw;
        LlmJudge? judge;
        lock (_gate)
            judge = _judge;

        judge?.NoteChat(classified);

        if (_suppressors.IsMatch(haystack))
            return;

        foreach (CompiledRule rule in _rules)
        {
            string? hit = rule.Matcher.FirstMatch(haystack);
            if (hit is null)
                continue;

            rule.Hits++;
            if (SmartActive() && rule.Config.Smart && judge is not null)
                judge.JudgeRule(context, session, rule, haystack, CurrentToken());
            else
                Notify(context, session, haystack, EffectiveRoutes(rule.Config.Routes), rule.Config.Beep, hit);

            return;
        }

        if (SmartActive() && _settings.Smart.Mode == "full" && judge is not null)
            judge.JudgeCatchAll(context, session, haystack, CurrentToken());
    }

    private void OnEvent(PluginContext context, ISessionScope session, string key, string text)
    {
        if (!_settings.Enabled)
            return;

        foreach (CompiledEventRule rule in _eventRules)
        {
            if (rule.Config.On.Any(o => string.Equals(o.Trim(), key, StringComparison.OrdinalIgnoreCase)))
                FireEventRule(context, rule, key, text);
        }
    }

    private void FireEventRule(PluginContext context, CompiledEventRule rule, string key, string text)
    {
        lock (_gate)
        {
            if (rule.Config.CooldownSeconds > 0
                && rule.LastFired.TryGetValue(key, out DateTime last)
                && DateTime.UtcNow - last < TimeSpan.FromSeconds(rule.Config.CooldownSeconds))
                return;

            rule.LastFired[key] = DateTime.UtcNow;
            rule.Hits++;
        }

        // Events always fire directly.
        // The judge never sees them.
        ISessionScope? session = context.CurrentSession;
        Notify(context, session, text, EffectiveRoutes(rule.Config.Routes), rule.Config.Beep, rule.Config.Name);
    }

    /// <summary>Routes with fallback to every channel when the rule names none.</summary>
    internal static List<string> EffectiveRoutes(List<string>? routes)
    {
        List<string> normalized = AlertRoutes.Normalize(routes);
        return normalized.Count > 0 ? normalized : [.. AlertRoutes.All];
    }

    internal bool SmartActive() => _settings.Smart.Mode is "rules" or "full";

    internal bool SmartWanted() => SmartActive() && (_rules.Any(r => r.Config.Smart) || _settings.Smart.Mode == "full");

    internal DateTime SessionStartUtc
    {
        get { lock (_gate) return _sessionStartUtc; }
    }

    internal IReadOnlyList<CompiledRule> CompiledRules() => _rules;

    internal IReadOnlyList<CompiledEventRule> CompiledEventRules() => _eventRules;

    internal void SaveSettings()
    {
        _context?.Settings.Save(_settings);
    }

    internal LlmJudgeStats JudgeStats()
    {
        lock (_gate)
        {
            return _judge is null
                ? new LlmJudgeStats(0, 0, 0, 0, 0, 0, 0, 0, string.Empty, DateTime.MinValue)
                : new LlmJudgeStats(
                    _judge.Started, _judge.Calls, _judge.ChatSeen, _judge.Kept, _judge.Dropped, _judge.Errors,
                    _judge.QueueDepth, _judge.BatchDrops, _judge.LastError, _judge.LastErrorUtc);
        }
    }

    /// <summary>Runs one raw line through the judge synchronously, for the test command.</summary>
    internal string DryRun(string raw)
    {
        if (_context is null)
            return "ERROR not active.";

        if (!TryLink(_context, out _, out _))
            return "ERROR llm-core is not loaded. Load it first, then retry.";

        try
        {
            var judge = new LlmJudge(this, _context);
            try
            {
                LlmRawAnswer answer = judge.JudgeOnceAsync(
                    _context, null, null, raw, CurrentToken()).GetAwaiter().GetResult();
                LlmVerdict? verdict = VerdictParser.TryParse(answer.Json, answer.Text);
                if (verdict is null)
                    return $"ERROR the judge answered garbage: {Trim(answer.Json ?? answer.Text, 160)}";

                string routes = verdict.Routes.Count > 0 ? string.Join(", ", verdict.Routes) : "-";
                return $"alert={verdict.Alert} confidence={verdict.Confidence:0.00} routes=[{routes}] reason={verdict.Reason}";
            }
            finally
            {
                judge.Dispose();
            }
        }
        catch (Exception ex)
        {
            return $"ERROR {Trim(ex.Message, 200)}";
        }
    }

    private static string Trim(string text, int max)
        => text.Length > max ? text[..max] + "..." : text;

    private CancellationToken CurrentToken()
    {
        lock (_gate)
            return _sessionCts?.Token ?? CancellationToken.None;
    }

    /// <summary>Warns once when smart matching is switched on but no usable llm-core answers.</summary>
    internal void MaybeWarnCoreMissing(PluginContext context)
    {
        if (!SmartWanted() || _coreWarningLogged)
            return;

        if (!TryLink(context, out _, out _))
        {
            _coreWarningLogged = true;
            context.Logger.LogWarning("{Message}", context.Strings.Get("core_missing"));
        }
    }

    internal bool CoreLinked() => _context is not null && TryLink(_context, out _, out _);

    /// <summary>
    /// Finds the llm-core copy that unifies with this host: every candidate context is tried, and only the one whose service hands over without a contract mismatch is kept.
    /// Stale contexts from dead hosts or reloads fail the handover and are skipped, never mistaken for absent.
    /// The proven link is cached so status polls and judge calls do one hub lookup instead of a full context scan; a failed handover drops the cache and rescans.
    /// </summary>
    internal bool TryLink(PluginContext context, out LlmCoreLink? link, out object? service)
    {
        lock (_gate)
        {
            if (_cachedLink is not null)
            {
                object? instance = _cachedLink.TryGetService(context.Services);
                if (instance is not null)
                {
                    link = _cachedLink;
                    service = instance;
                    return true;
                }

                _cachedLink = null;
            }
        }

        foreach (LlmCoreLink candidate in LlmCoreLink.TryResolveAll())
        {
            try
            {
                object? instance = candidate.TryGetService(context.Services);
                if (instance is not null)
                {
                    lock (_gate)
                        _cachedLink = candidate;

                    link = candidate;
                    service = instance;
                    return true;
                }
            }
            catch (Exception)
            {
            }
        }

        link = null;
        service = null;
        return false;
    }

    internal void Notify(
        PluginContext context, ISessionScope? session, string raw, List<string> routes, bool beep, string highlight)
    {
        string line = context.Strings.Format("alert", raw);
        bool shown = false;
        try
        {
            shown = context.Client.HostUi?.TryNotify(new HostNotification(line, routes.Contains(AlertRoutes.Beep) && beep, highlight)) ?? false;
        }
        catch (Exception ex)
        {
            context.Logger.LogDebug(ex, "{Message}", context.Strings.Get("notify_failed"));
        }

        if (!shown)
            context.Logger.LogInformation("{Message}", line);

        if (routes.Contains(AlertRoutes.Desktop))
            DesktopNotifier.Toast("MCC Alerts", raw);

        if (routes.Contains(AlertRoutes.File))
            AppendLog(context, raw);

        if (routes.Contains(AlertRoutes.Discord))
        {
            context.Messenger.Publish(BridgeTextRequest.Create(
                BridgeTextRequest.TargetDiscord, line, _settings.Discord_Target));
        }

        if (routes.Contains(AlertRoutes.Telegram))
        {
            context.Messenger.Publish(BridgeTextRequest.Create(
                BridgeTextRequest.TargetTelegram, line, string.Empty));
        }
    }

    private void AppendLog(PluginContext context, string raw)
    {
        if (!_settings.LogToFile || _settings.LogFile.Length == 0)
            return;

        try
        {
            string path = context.Storage.GetPath(_settings.LogFile);
            lock (_gate)
                File.AppendAllText(path, $"[{DateTime.Now:yyyy/MM/dd HH:mm:ss}] {raw}{Environment.NewLine}");
        }
        catch (ArgumentException ex)
        {
            context.Logger.LogWarning(ex, "{Message}", context.Strings.Format("invalid_file", _settings.LogFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            context.Logger.LogWarning(ex, "{Message}", context.Strings.Get("log_failed"));
        }
    }
}
