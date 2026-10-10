// Alerts commands: smart/batch/mode switches, rule and event listings, route editing, and the judge dry-run used for prompt tuning.

using DMCBK.Core.Commands;
using DMCBK.Core.Commands.ArgumentTypes;
using DMCBK.PluginSdk;
using Umpk.Commands;

namespace DMCBK.OfficialPlugins.Alerts;

/// <summary>The <c>alerts</c> console command.</summary>
internal sealed class AlertsCommand : CommandBase
{
    private readonly AlertsPlugin _plugin;
    private readonly IPluginLocalization _strings;

    internal AlertsCommand(AlertsPlugin plugin, IPluginLocalization strings)
    {
        _plugin = plugin;
        _strings = strings;
    }

    /// <inheritdoc/>
    public override string CmdName => "alerts";

    /// <inheritdoc/>
    public override string CmdDesc => _strings.Get("command_desc");

    /// <inheritdoc/>
    public override string CmdUsage => _strings.Get("command_usage");

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Plugins;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("smart <off|rules|full|status>", "Switches the judge posture or shows it."),
        new("batch <on|off|status>", "Flips batching or shows the queue."),
        new("rules", "Lists chat rules with hit counts."),
        new("events", "Lists event rules with cooldown state."),
        new("routes", "Shows the union of rule routes with delivery counts."),
        new("routes set <rule> <names...>", "Replaces one rule's routes."),
        new("routes mode <fixed|model>", "Everywhere, or let the judge pick a subset."),
        new("test \"<line>\"", "Dry-runs one raw line through the judge."),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples =>
    [
        "alerts smart status",
        "alerts routes set theft desktop telegram",
        "alerts test \"Steve whispered: meet me at spawn\"",
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["plugins", "man"];

    /// <inheritdoc/>
    public override string? ManTopic => "alerts";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .Executes(ctx => ShowUsage(ctx.Source))
            .ThenLiteral("smart", s => s
                .ThenLiteral("off", a => a.Executes(ctx => SmartSet(ctx.Source, "off")))
                .ThenLiteral("rules", a => a.Executes(ctx => SmartSet(ctx.Source, "rules")))
                .ThenLiteral("full", a => a.Executes(ctx => SmartSet(ctx.Source, "full")))
                .ThenLiteral("status", a => a.Executes(ctx => SmartStatus(ctx.Source))))
            .ThenLiteral("batch", b => b
                .ThenLiteral("on", a => a.Executes(ctx => BatchSet(ctx.Source, true)))
                .ThenLiteral("off", a => a.Executes(ctx => BatchSet(ctx.Source, false)))
                .ThenLiteral("status", a => a.Executes(ctx => BatchStatus(ctx.Source))))
            .ThenLiteral("rules", a => a.Executes(ctx => Rules(ctx.Source)))
            .ThenLiteral("events", a => a.Executes(ctx => Events(ctx.Source)))
            .ThenLiteral("routes", r => r
                .Executes(ctx => Routes(ctx.Source))
                .ThenLiteral("set", a => a
                    .ThenArgument("rule", Arguments.Word(), n => n
                        .ThenArgument("names", Arguments.GreedyString(), g => g.Executes(
                            ctx => RoutesSet(
                                ctx.Source,
                                ctx.GetArgument<string>("rule"),
                                ctx.GetArgument<string>("names"))))))
                .ThenLiteral("mode", a => a
                    .ThenLiteral("fixed", f => f.Executes(ctx => RoutesMode(ctx.Source, "fixed")))
                    .ThenLiteral("model", m => m.Executes(ctx => RoutesMode(ctx.Source, "model")))))
            .ThenLiteral("test", a => a
                .ThenArgument("line", Arguments.GreedyString(), g => g.Executes(
                    ctx => Test(ctx.Source, ctx.GetArgument<string>("line")))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private int SmartSet(CommandContext ctx, string mode)
    {
        _plugin.Settings.Smart.Mode = mode;
        _plugin.SaveSettings();
        return ctx.Result.Ok(_strings.Format("cmd_smart_set", mode));
    }

    private int SmartStatus(CommandContext ctx)
    {
        var judge = _plugin.JudgeStats();
        var lines = new List<string>
        {
            _strings.Format("cmd_smart_status", _plugin.Settings.Smart.Mode, _plugin.Settings.Smart.Model),
            _strings.Format("cmd_core", _plugin.CoreLinked() ? _strings.Get("cmd_loaded") : _strings.Get("cmd_missing")),
            _strings.Format("cmd_calls", judge.Started, judge.Calls, judge.ChatSeen, judge.Kept, judge.Dropped, judge.Errors),
        };
        if (judge.Errors > 0)
            lines.Add(_strings.Format("cmd_last_error", judge.LastErrorUtc.ToString("HH:mm:ss"), judge.LastError));

        return ctx.Result.Ok(string.Join("\n", lines));
    }

    private int BatchSet(CommandContext ctx, bool on)
    {
        _plugin.Settings.Smart.Batch = on;
        _plugin.SaveSettings();
        return ctx.Result.Ok(_strings.Format("cmd_batch_set", on ? _strings.Get("cmd_on") : _strings.Get("cmd_off")));
    }

    private int BatchStatus(CommandContext ctx)
    {
        var judge = _plugin.JudgeStats();
        return ctx.Result.Ok(_strings.Format(
            "cmd_batch_status",
            _plugin.Settings.Smart.Batch ? _strings.Get("cmd_on") : _strings.Get("cmd_off"),
            _plugin.Settings.Smart.BatchSize, _plugin.Settings.Smart.BatchSeconds,
            judge.QueueDepth, judge.BatchDrops));
    }

    private int Rules(CommandContext ctx)
    {
        var rules = _plugin.CompiledRules();
        if (rules.Count == 0)
            return ctx.Result.Fail(_strings.Get("cmd_no_rules"));

        return ctx.Result.Ok(string.Join("\n", rules.Select(r => _strings.Format(
            "cmd_rule_row", r.Config.Name, string.Join(", ", r.Config.Match),
            string.Join(", ", AlertsPlugin.EffectiveRoutes(r.Config.Routes)),
            r.Config.Smart ? _strings.Get("cmd_smart_tag") : _strings.Get("cmd_direct_tag"), r.Hits))));
    }

    private int Events(CommandContext ctx)
    {
        var rules = _plugin.CompiledEventRules();
        if (rules.Count == 0)
            return ctx.Result.Fail(_strings.Get("cmd_no_events"));

        return ctx.Result.Ok(string.Join("\n", rules.Select(r => _strings.Format(
            "cmd_event_row", r.Config.Name, string.Join(", ", r.Config.On),
            string.Join(", ", AlertsPlugin.EffectiveRoutes(r.Config.Routes)),
            r.Config.CooldownSeconds, r.Hits))));
    }

    private int Routes(CommandContext ctx)
    {
        var union = _plugin.CompiledRules()
            .SelectMany(r => AlertsPlugin.EffectiveRoutes(r.Config.Routes))
            .Concat(_plugin.CompiledEventRules()
                .SelectMany(r => AlertsPlugin.EffectiveRoutes(r.Config.Routes)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return ctx.Result.Ok(_strings.Format(
            "cmd_routes", _plugin.Settings.RouteMode,
            union.Count > 0 ? string.Join(", ", union) : _strings.Get("cmd_none")));
    }

    private int RoutesSet(CommandContext ctx, string ruleName, string names)
    {
        CompiledRule? rule = _plugin.CompiledRules().FirstOrDefault(r =>
            string.Equals(r.Config.Name, ruleName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (rule is null)
            return ctx.Result.Fail(_strings.Format("cmd_no_rule", ruleName.Trim()));

        var wanted = names.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var unknown = wanted.Where(w => !AlertRoutes.IsKnown(w)).ToList();
        if (unknown.Count > 0)
        {
            return ctx.Result.Fail(_strings.Format(
                "cmd_bad_routes", string.Join(", ", unknown), string.Join(", ", AlertRoutes.All)));
        }

        rule.Config.Routes = AlertRoutes.Normalize(wanted);
        _plugin.SaveSettings();
        return ctx.Result.Ok(_strings.Format(
            "cmd_routes_set", rule.Config.Name,
            rule.Config.Routes.Count > 0 ? string.Join(", ", rule.Config.Routes) : _strings.Get("cmd_all")));
    }

    private int RoutesMode(CommandContext ctx, string mode)
    {
        _plugin.Settings.RouteMode = mode;
        _plugin.SaveSettings();
        return ctx.Result.Ok(_strings.Format("cmd_route_mode", mode));
    }

    private int Test(CommandContext ctx, string line)
    {
        string raw = line.Trim().Trim('"');
        if (raw.Length == 0)
            return ctx.Result.Fail(_strings.Get("cmd_need_line"));

        if (!_plugin.SmartActive())
            return ctx.Result.Fail(_strings.Get("cmd_smart_off"));

        string result = _plugin.DryRun(raw);
        return result.StartsWith("ERROR ", StringComparison.Ordinal)
            ? ctx.Result.Fail(result["ERROR ".Length..])
            : ctx.Result.Ok(result);
    }
}
