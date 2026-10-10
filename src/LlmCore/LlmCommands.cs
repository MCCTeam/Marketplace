using LlmCore.Api;
using DMCBK.Core.Commands;
using DMCBK.Core.Commands.ArgumentTypes;
using DMCBK.PluginSdk;
using Umpk.Commands;

namespace DMCBK.OfficialPlugins.LlmCore;

/// <summary>The <c>llm</c> console command: providers, models, budgets, skills, and MCP servers.</summary>
internal sealed class LlmCommand : CommandBase
{
    private readonly LlmCorePlugin _plugin;
    private readonly IPluginLocalization _strings;

    internal LlmCommand(LlmCorePlugin plugin, IPluginLocalization strings)
    {
        _plugin = plugin;
        _strings = strings;
    }

    /// <inheritdoc/>
    public override string CmdName => "llm";

    /// <inheritdoc/>
    public override string CmdDesc => _strings.Get("command_desc");

    /// <inheritdoc/>
    public override string CmdUsage => _strings.Get("command_usage");

    /// <inheritdoc/>
    public override CommandCategory Category => CommandCategory.Plugins;

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new("status", _strings.Get("usage_status")),
        new("provider list", _strings.Get("usage_provider_list")),
        new("provider add <name> <url>", _strings.Get("usage_provider_add")),
        new("provider remove <name> <name>", _strings.Get("usage_provider_remove")),
        new("provider edit <name> <key=value...>", _strings.Get("usage_provider_edit")),
        new("provider enable|disable <name>", _strings.Get("usage_provider_toggle")),
        new("provider status <name>", _strings.Get("usage_provider_status")),
        new("model list <provider>", _strings.Get("usage_model_list")),
        new("model refresh <provider>", _strings.Get("usage_model_refresh")),
        new("model add <provider> <id>", _strings.Get("usage_model_add")),
        new("model remove <provider> <id>", _strings.Get("usage_model_remove")),
        new("cache clear <provider>", _strings.Get("usage_cache_clear")),
        new("use <provider/model>", _strings.Get("usage_use")),
        new("budget [plugin] [tokens]", _strings.Get("usage_budget")),
        new("setup <provider> <url> <keyenv> <model>", _strings.Get("usage_setup")),
        new("doctor", _strings.Get("usage_doctor")),
        new("why", _strings.Get("usage_why")),
        new("skill list", _strings.Get("usage_skill_list")),
        new("mcp list", _strings.Get("usage_mcp_list")),
        new("mcp check <name>", _strings.Get("usage_mcp_check")),
        new("mcp add <name> <url> [tools...]", _strings.Get("usage_mcp_add")),
        new("mcp remove <name> <name>", _strings.Get("usage_mcp_remove")),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples =>
    [
        "llm status",
        "llm setup local http://localhost:11434/v1/ - local/qwen3-8b",
        "llm model list local",
        "llm use local/qwen3-8b",
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> SeeAlso => ["plugins", "man"];

    /// <inheritdoc/>
    public override string? ManTopic => "llm-core";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .Executes(ctx => ShowUsage(ctx.Source))
            .ThenLiteral("status", a => a.Executes(ctx => Status(ctx.Source)))
            .ThenLiteral("provider", p => p
                .ThenLiteral("list", a => a.Executes(ctx => ProviderList(ctx.Source)))
                .ThenLiteral("add", a => a
                    .ThenArgument("name", Arguments.Word(), n => n
                        .ThenArgument("url", Arguments.GreedyString(), u => u.Executes(
                            ctx => ProviderAdd(ctx.Source, ctx.GetArgument<string>("name"), ctx.GetArgument<string>("url"))))))
                .ThenLiteral("remove", a => a
                    .ThenArgument("name", Arguments.Word(), n => n
                        .ThenArgument("confirm", Arguments.Word(), c => c.Executes(
                            ctx => ProviderRemove(ctx.Source, ctx.GetArgument<string>("name"), ctx.GetArgument<string>("confirm"))))))
                .ThenLiteral("edit", a => a
                    .ThenArgument("name", Arguments.Word(), n => n
                        .ThenArgument("pairs", Arguments.GreedyString(), g => g.Executes(
                            ctx => ProviderEdit(ctx.Source, ctx.GetArgument<string>("name"), ctx.GetArgument<string>("pairs"))))))
                .ThenLiteral("enable", a => a
                    .ThenArgument("name", Arguments.Word(), n => n.Executes(
                        ctx => ProviderToggle(ctx.Source, ctx.GetArgument<string>("name"), true))))
                .ThenLiteral("disable", a => a
                    .ThenArgument("name", Arguments.Word(), n => n.Executes(
                        ctx => ProviderToggle(ctx.Source, ctx.GetArgument<string>("name"), false))))
                .ThenLiteral("status", a => a
                    .ThenArgument("name", Arguments.Word(), n => n.Executes(
                        ctx => ProviderStatus(ctx.Source, ctx.GetArgument<string>("name"))))))
            .ThenLiteral("model", m => m
                .ThenLiteral("list", a => a
                    .ThenArgument("provider", Arguments.Word(), n => n.Executes(
                        ctx => ModelList(ctx.Source, ctx.GetArgument<string>("provider"), false))))
                .ThenLiteral("refresh", a => a
                    .ThenArgument("provider", Arguments.Word(), n => n.Executes(
                        ctx => ModelList(ctx.Source, ctx.GetArgument<string>("provider"), true))))
                .ThenLiteral("add", a => a
                    .ThenArgument("provider", Arguments.Word(), n => n
                        .ThenArgument("id", Arguments.GreedyString(), g => g.Executes(
                            ctx => ModelAdd(ctx.Source, ctx.GetArgument<string>("provider"), ctx.GetArgument<string>("id"))))))
                .ThenLiteral("remove", a => a
                    .ThenArgument("provider", Arguments.Word(), n => n
                        .ThenArgument("id", Arguments.GreedyString(), g => g.Executes(
                            ctx => ModelRemove(ctx.Source, ctx.GetArgument<string>("provider"), ctx.GetArgument<string>("id")))))))
            .ThenLiteral("cache", c => c
                .ThenLiteral("clear", a => a
                    .ThenArgument("provider", Arguments.Word(), n => n.Executes(
                        ctx => CacheClear(ctx.Source, ctx.GetArgument<string>("provider"))))))
            .ThenLiteral("use", a => a
                .ThenArgument("model", Arguments.GreedyString(), g => g.Executes(
                    ctx => Use(ctx.Source, ctx.GetArgument<string>("model")))))
            .ThenLiteral("budget", a => a.Executes(ctx => BudgetShow(ctx.Source))
                .ThenArgument("rest", Arguments.GreedyString(), g => g.Executes(
                    ctx => BudgetSet(ctx.Source, ctx.GetArgument<string>("rest")))))
            .ThenLiteral("setup", a => a
                .ThenArgument("provider", Arguments.Word(), p => p
                    .ThenArgument("url", Arguments.Word(), u => u
                        .ThenArgument("keyenv", Arguments.Word(), k => k
                            .ThenArgument("model", Arguments.GreedyString(), m => m.Executes(
                                ctx => Setup(
                                    ctx.Source,
                                    ctx.GetArgument<string>("provider"),
                                    ctx.GetArgument<string>("url"),
                                    ctx.GetArgument<string>("keyenv"),
                                    ctx.GetArgument<string>("model"))))))))
            .ThenLiteral("doctor", a => a.Executes(ctx => Doctor(ctx.Source)))
            .ThenLiteral("why", a => a.Executes(ctx => Why(ctx.Source)))
            .ThenLiteral("skill", s => s
                .ThenLiteral("list", a => a.Executes(ctx => SkillList(ctx.Source))))
            .ThenLiteral("mcp", m => m
                .ThenLiteral("list", a => a.Executes(ctx => McpList(ctx.Source)))
                .ThenLiteral("check", a => a
                    .ThenArgument("name", Arguments.Word(), n => n.Executes(
                        ctx => McpCheck(ctx.Source, ctx.GetArgument<string>("name")))))
                .ThenLiteral("add", a => a
                    .ThenArgument("name", Arguments.Word(), n => n
                        .ThenArgument("rest", Arguments.GreedyString(), g => g.Executes(
                            ctx => McpAdd(
                                ctx.Source,
                                ctx.GetArgument<string>("name"),
                                ctx.GetArgument<string>("rest"))))))
                .ThenLiteral("remove", a => a
                    .ThenArgument("name", Arguments.Word(), n => n
                        .ThenArgument("confirm", Arguments.Word(), c => c.Executes(
                            ctx => McpRemove(ctx.Source, ctx.GetArgument<string>("name"), ctx.GetArgument<string>("confirm")))))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private static T Await<T>(Task<T> work) => work.GetAwaiter().GetResult();

    private static void Await(Task work) => work.GetAwaiter().GetResult();

    private int Status(CommandContext ctx)
    {
        var lines = new List<string>
        {
            _strings.Format("status_default", _plugin.Settings.DefaultModel.Length > 0 ? _plugin.Settings.DefaultModel : "-"),
        };
        foreach (LLMProviderStatus provider in _plugin.Providers.List())
        {
            lines.Add(_strings.Format(
                "status_provider", provider.Name, provider.Enabled ? "+" : "-", provider.Reason));
        }

        lines.Add(_strings.Format("status_spent", _plugin.BudgetsSpentToday("*"), _plugin.Settings.DailyTokenBudget));
        return ctx.Result.Ok(string.Join("\n", lines));
    }

    private int ProviderList(CommandContext ctx)
    {
        IReadOnlyList<LLMProviderStatus> providers = _plugin.Providers.List();
        return providers.Count == 0
            ? ctx.Result.Fail(_strings.Get("no_providers"))
            : ctx.Result.Ok(string.Join("\n", providers.Select(p => _strings.Format(
                "provider_row", p.Name, p.Enabled ? "+" : "-", p.Reason))));
    }

    private int ProviderAdd(CommandContext ctx, string name, string url)
    {
        name = name.Trim();
        url = url.Trim().TrimEnd('/');
        if (name.Length == 0 || url.Length == 0 || !Uri.IsWellFormedUriString(url, UriKind.Absolute))
            return ctx.Result.Fail(_strings.Get("bad_provider"));

        if (_plugin.Providers.TryResolve(name, out _))
            return ctx.Result.Fail(_strings.Format("provider_exists", name));

        try
        {
            Await(_plugin.ProbeEndpointAsync(name, url));
        }
        catch (LLMException ex)
        {
            return ctx.Result.Fail(_strings.Format("provider_unreachable", LlmRedactor.Redact(ex.Message)));
        }

        _plugin.UpsertCentralProvider(new ResolvedProvider(name, url, LLMWireFormat.Responses, true, string.Empty));
        return ctx.Result.Ok(_strings.Format("provider_added", name));
    }

    private int ProviderRemove(CommandContext ctx, string name, string confirm)
    {
        if (!string.Equals(name.Trim(), confirm.Trim(), StringComparison.Ordinal))
            return ctx.Result.Fail(_strings.Get("confirm_mismatch"));

        if (_plugin.RemoveCentralProvider(name.Trim()))
            return ctx.Result.Ok(_strings.Format("provider_removed", name.Trim()));

        return _plugin.ProviderIsApiOwned(name.Trim(), out string owner)
            ? ctx.Result.Fail(_strings.Format("provider_owned", name.Trim(), owner))
            : ctx.Result.Fail(_strings.Format("provider_missing", name.Trim()));
    }

    private int ProviderEdit(CommandContext ctx, string name, string pairs)
    {
        if (!_plugin.Providers.TryResolve(name.Trim(), out ResolvedProvider? current) || current is null)
            return ctx.Result.Fail(_strings.Format("provider_missing", name.Trim()));

        string endpoint = current.Endpoint;
        LLMWireFormat api = current.Api;
        string keyEnv = current.ApiKeyEnv ?? string.Empty;
        foreach (string pair in pairs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            if (equals <= 0)
                return ctx.Result.Fail(_strings.Format("bad_pair", pair));

            string key = pair[..equals].Trim().ToLowerInvariant();
            string value = pair[(equals + 1)..].Trim();
            switch (key)
            {
                case "endpoint":
                    endpoint = value.TrimEnd('/');
                    break;
                case "api":
                    api = value == "chat" ? LLMWireFormat.ChatCompletions : LLMWireFormat.Responses;
                    break;
                case "keyenv":
                    keyEnv = value;
                    break;
                default:
                    return ctx.Result.Fail(_strings.Format("bad_pair", pair));
            }
        }

        if (!Uri.IsWellFormedUriString(endpoint, UriKind.Absolute))
            return ctx.Result.Fail(_strings.Get("bad_provider"));

        _plugin.UpsertCentralProvider(current with { Endpoint = endpoint, Api = api, ApiKeyEnv = keyEnv });
        return ctx.Result.Ok(_strings.Format("provider_edited", current.Name));
    }

    private int ProviderToggle(CommandContext ctx, string name, bool enabled)
    {
        return _plugin.SetProviderEnabled(name.Trim(), enabled)
            ? ctx.Result.Ok(_strings.Format(enabled ? "provider_enabled" : "provider_disabled", name.Trim()))
            : ctx.Result.Fail(_strings.Format("provider_missing", name.Trim()));
    }

    private int ProviderStatus(CommandContext ctx, string name)
    {
        if (!_plugin.Providers.TryResolve(name.Trim(), out ResolvedProvider? provider) || provider is null)
            return ctx.Result.Fail(_strings.Format("provider_missing", name.Trim()));

        if (!provider.Enabled)
            return ctx.Result.Ok(_strings.Format("provider_disabled", provider.Name));

        try
        {
            IReadOnlyList<LLMModelInfo> models = Await(_plugin.Models.ListAsync(provider, true, CancellationToken.None));
            DateTime? cached = _plugin.Models.CacheAge(provider.Name);
            return ctx.Result.Ok(_strings.Format(
                "provider_status", provider.Name, models.Count, cached?.ToString("o") ?? "-"));
        }
        catch (LLMException ex)
        {
            return ctx.Result.Fail(_strings.Format("provider_unreachable", LlmRedactor.Redact(ex.Message)));
        }
    }

    private int ModelList(CommandContext ctx, string provider, bool refresh)
    {
        if (!_plugin.Providers.TryResolve(provider.Trim(), out ResolvedProvider? resolved) || resolved is null)
            return ctx.Result.Fail(_strings.Format("provider_missing", provider.Trim()));

        try
        {
            IReadOnlyList<LLMModelInfo> models = Await(_plugin.Models.ListAsync(resolved, refresh, CancellationToken.None));
            return models.Count == 0
                ? ctx.Result.Fail(_strings.Format("no_models", resolved.Name))
                : ctx.Result.Ok(string.Join("\n", models.Select(m => _strings.Format(
                    "model_row", m.Id, m.Source))));
        }
        catch (LLMException ex)
        {
            return ctx.Result.Fail(_strings.Format("provider_unreachable", LlmRedactor.Redact(ex.Message)));
        }
    }

    private int ModelAdd(CommandContext ctx, string provider, string id)
    {
        id = id.Trim();
        if (id.Length == 0)
            return ctx.Result.Fail(_strings.Get("bad_model"));

        try
        {
            _plugin.Service.AddLLM(provider.Trim(), id);
        }
        catch (ArgumentException ex)
        {
            return ctx.Result.Fail(LlmRedactor.Redact(ex.Message));
        }

        return ctx.Result.Ok(_strings.Format("model_added", id, provider.Trim()));
    }

    private int ModelRemove(CommandContext ctx, string provider, string id)
    {
        return _plugin.Service.RemoveLLM(provider.Trim(), id.Trim())
            ? ctx.Result.Ok(_strings.Format("model_removed", id.Trim()))
            : ctx.Result.Fail(_strings.Format("model_missing", id.Trim()));
    }

    private int CacheClear(CommandContext ctx, string provider)
    {
        _plugin.Service.ClearLLM(provider.Trim());
        return ctx.Result.Ok(_strings.Format("cache_cleared", provider.Trim()));
    }

    private int Use(CommandContext ctx, string model)
    {
        model = model.Trim();
        int slash = model.IndexOf('/');
        if (slash <= 0 || slash == model.Length - 1)
            return ctx.Result.Fail(_strings.Get("bad_model"));

        string provider = model[..slash];
        if (!_plugin.Providers.TryResolve(provider, out _))
            return ctx.Result.Fail(_strings.Format("provider_missing", provider));

        _plugin.Settings.DefaultModel = model;
        _plugin.SaveSettings();
        return ctx.Result.Ok(_strings.Format("use_set", model));
    }

    private int BudgetShow(CommandContext ctx)
    {
        return ctx.Result.Ok(_strings.Format(
            "budget_show", _plugin.BudgetsSpentToday("*"), _plugin.Settings.DailyTokenBudget));
    }

    private int BudgetSet(CommandContext ctx, string rest)
    {
        string[] parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && long.TryParse(parts[0], out long global) && global >= 0)
        {
            _plugin.Settings.DailyTokenBudget = global;
            _plugin.SaveSettings();
            _plugin.Reload();
            return ctx.Result.Ok(_strings.Format("budget_set", global));
        }

        if (parts.Length == 2 && long.TryParse(parts[1], out long perPlugin) && perPlugin >= 0)
        {
            _plugin.SetPluginBudget(parts[0], perPlugin);
            return ctx.Result.Ok(_strings.Format("budget_plugin_set", parts[0], perPlugin));
        }

        return ctx.Result.Fail(_strings.Get("bad_budget"));
    }

    private int Setup(CommandContext ctx, string provider, string url, string keyEnv, string model)
    {
        provider = provider.Trim();
        url = url.Trim().TrimEnd('/');
        keyEnv = keyEnv.Trim() == "-" ? string.Empty : keyEnv.Trim();
        model = model.Trim();
        if (provider.Length == 0 || !Uri.IsWellFormedUriString(url, UriKind.Absolute) || model.IndexOf('/') <= 0)
            return ctx.Result.Fail(_strings.Get("bad_setup"));

        try
        {
            Await(_plugin.ProbeEndpointAsync(provider, url, keyEnv));
        }
        catch (LLMException ex)
        {
            return ctx.Result.Fail(_strings.Format("provider_unreachable", LlmRedactor.Redact(ex.Message)));
        }

        _plugin.UpsertCentralProvider(new ResolvedProvider(provider, url, LLMWireFormat.Responses, true, keyEnv));
        _plugin.Settings.DefaultModel = model;
        _plugin.SaveSettings();
        return ctx.Result.Ok(_strings.Format("setup_done", provider, model));
    }

    private int Doctor(CommandContext ctx)
    {
        var lines = new List<string>();
        IReadOnlyList<ResolvedProvider> providers = _plugin.Providers.Snapshot();
        if (providers.Count == 0)
            lines.Add(_strings.Get("doctor_no_providers"));

        foreach (ResolvedProvider provider in providers)
        {
            if (!provider.Enabled)
            {
                lines.Add(_strings.Format("doctor_disabled", provider.Name));
                continue;
            }

            string key = _plugin.ResolveKey(provider);
            if (key.Length == 0 && provider.Endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                lines.Add(_strings.Format("doctor_no_key", provider.Name, provider.ApiKeyEnv));
                continue;
            }

            try
            {
                IReadOnlyList<LLMModelInfo> models =
                    Await(_plugin.Models.ListAsync(provider, true, CancellationToken.None));
                lines.Add(_strings.Format("doctor_ok", provider.Name, models.Count));
            }
            catch (LLMException ex)
            {
                lines.Add(_strings.Format("doctor_fail", provider.Name, LlmRedactor.Redact(ex.Message)));
            }
        }

        lines.Add(_strings.Format(
            "budget_show", _plugin.BudgetsSpentToday("*"), _plugin.Settings.DailyTokenBudget));
        return ctx.Result.Ok(string.Join("\n", lines));
    }

    private int Why(CommandContext ctx)
    {
        LlmCallSummary? last = _plugin.Service.LastCall;
        return last is null
            ? ctx.Result.Fail(_strings.Get("why_empty"))
            : ctx.Result.Ok(_strings.Format(
                "why_row", last.Model, string.Join(",", last.ToolsUsed),
                last.InTokens, last.OutTokens, last.FinishReason ?? "-"));
    }

    private int SkillList(CommandContext ctx)
    {
        IReadOnlyList<LLMSkillSummary> skills = _plugin.Service.ListSkills();
        return skills.Count == 0
            ? ctx.Result.Fail(_strings.Get("no_skills"))
            : ctx.Result.Ok(string.Join("\n", skills.Select(s => _strings.Format(
                "skill_row", s.Name, s.Owner ?? "-", s.Version ?? "-"))));
    }

    private int McpList(CommandContext ctx)
    {
        IReadOnlyList<LLMMcpServerStatus> servers = _plugin.Service.ListMcpServers();
        return servers.Count == 0
            ? ctx.Result.Fail(_strings.Get("no_mcp"))
            : ctx.Result.Ok(string.Join("\n", servers.Select(s => _strings.Format(
                "mcp_row", s.Name, s.Connected ? "+" : "-", LlmRedactor.Redact(s.Reason)))));
    }

    private int McpCheck(CommandContext ctx, string name)
    {
        try
        {
            IReadOnlyList<LlmMcpTool> tools =
                Await(_plugin.McpToolsAsync(name.Trim(), CancellationToken.None));
            return ctx.Result.Ok(_strings.Format(
                "mcp_tools", name.Trim(), string.Join(",", tools.Select(t => t.Name))));
        }
        catch (Exception ex) when (ex is ArgumentException or LLMException or InvalidOperationException)
        {
            return ctx.Result.Fail(LlmRedactor.Redact(ex.Message));
        }
    }

    private int McpAdd(CommandContext ctx, string name, string rest)
    {
        name = name.Trim();
        string[] parts = (rest ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string url = parts.Length > 0 ? parts[0].Trim().TrimEnd('/') : string.Empty;
        var wanted = parts.Skip(1).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        if (name.Length == 0 || !Uri.IsWellFormedUriString(url, UriKind.Absolute))
            return ctx.Result.Fail(_strings.Get("bad_mcp"));

        try
        {
            Await(_plugin.AddCentralMcpAsync(name, url, wanted, CancellationToken.None));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or LLMException)
        {
            return ctx.Result.Fail(LlmRedactor.Redact(ex.Message));
        }

        return ctx.Result.Ok(_strings.Format("mcp_added", name));
    }

    private int McpRemove(CommandContext ctx, string name, string confirm)
    {
        if (!string.Equals(name.Trim(), confirm.Trim(), StringComparison.Ordinal))
            return ctx.Result.Fail(_strings.Get("confirm_mismatch"));

        if (_plugin.RemoveCentralMcp(name.Trim()))
            return ctx.Result.Ok(_strings.Format("mcp_removed", name.Trim()));

        return _plugin.McpIsApiOwned(name.Trim(), out string owner)
            ? ctx.Result.Fail(_strings.Format("mcp_owned", name.Trim(), owner))
            : ctx.Result.Fail(_strings.Format("mcp_missing", name.Trim()));
    }
}
