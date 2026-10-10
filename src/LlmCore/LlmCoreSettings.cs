using DMCBK.PluginSdk;
using Tomlet.Attributes;

namespace DMCBK.OfficialPlugins.LlmCore;

/// <summary>One provider row in settings. Lists serialize as [[Providers]] tables.</summary>
public sealed class LlmProviderRow
{
    [TomlInlineComment("$settings.provider_name$")]
    public string Name { get; set; } = string.Empty;

    [TomlInlineComment("$settings.provider_endpoint$")]
    public string Endpoint { get; set; } = string.Empty;

    [TomlInlineComment("$settings.provider_api$")]
    public string Api { get; set; } = "responses";

    [TomlInlineComment("$settings.provider_enabled$")]
    public bool Enabled { get; set; } = true;

    [TomlInlineComment("$settings.provider_key_env$")]
    public string ApiKeyEnv { get; set; } = string.Empty;
}

/// <summary>One shared MCP server row in settings. Lists serialize as [[McpServers]] tables.</summary>
public sealed class LlmMcpRow
{
    [TomlInlineComment("$settings.mcp_name$")]
    public string Name { get; set; } = string.Empty;

    [TomlInlineComment("$settings.mcp_transport$")]
    public string Transport { get; set; } = "http";

    [TomlInlineComment("$settings.mcp_url$")]
    public string Url { get; set; } = string.Empty;

    [TomlInlineComment("$settings.mcp_tools$")]
    public List<string> Tools { get; set; } = [];
}

/// <summary>One per-plugin daily token budget row.</summary>
public sealed class LlmPluginBudgetRow
{
    [TomlInlineComment("$settings.budget_plugin$")]
    public string Plugin { get; set; } = string.Empty;

    [TomlInlineComment("$settings.budget_tokens$")]
    public long TokensPerDay { get; set; }
}

/// <summary>The user-editable, persisted settings for llm-core.</summary>
public sealed class LlmCoreSettings : IValidatablePluginSettings
{
    [TomlInlineComment("$settings.enabled$")]
    public bool Enabled { get; set; } = true;

    [TomlInlineComment("$settings.default_model$")]
    public string DefaultModel { get; set; } = string.Empty;

    [TomlInlineComment("$settings.default_max_tokens$")]
    public int DefaultMaxTokens { get; set; }

    [TomlInlineComment("$settings.default_max_context$")]
    public int DefaultMaxContext { get; set; } = 32000;

    [TomlInlineComment("$settings.daily_token_budget$")]
    public long DailyTokenBudget { get; set; }

    [TomlPrecedingComment("$settings.plugin_budgets$")]
    public List<LlmPluginBudgetRow> PluginBudgets { get; set; } = [];

    [TomlPrecedingComment("$settings.providers$")]
    public List<LlmProviderRow> Providers { get; set; } = [];

    [TomlPrecedingComment("$settings.mcp_servers$")]
    public List<LlmMcpRow> McpServers { get; set; } = [];

    [TomlInlineComment("$settings.allow_local_endpoints$")]
    public bool AllowLocalEndpoints { get; set; }

    /// <summary>Normalizes on load or reload.</summary>
    public void Validate()
    {
        DefaultModel = (DefaultModel ?? string.Empty).Trim();
        Providers ??= [];
        McpServers ??= [];
        PluginBudgets ??= [];
        if (DefaultMaxTokens < 0)
            DefaultMaxTokens = 0;

        if (DefaultMaxContext < 0)
            DefaultMaxContext = 0;

        if (DailyTokenBudget < 0)
            DailyTokenBudget = 0;

        foreach (LlmProviderRow row in Providers)
        {
            row.Name = (row.Name ?? string.Empty).Trim();
            row.Endpoint = (row.Endpoint ?? string.Empty).Trim().TrimEnd('/');
            row.Api = (row.Api ?? string.Empty).Trim().ToLowerInvariant() == "chat" ? "chat" : "responses";
            row.ApiKeyEnv = (row.ApiKeyEnv ?? string.Empty).Trim();
        }

        foreach (LlmMcpRow row in McpServers)
        {
            row.Name = (row.Name ?? string.Empty).Trim();
            row.Transport = (row.Transport ?? string.Empty).Trim().ToLowerInvariant() == "sse" ? "sse" : "http";
            row.Url = (row.Url ?? string.Empty).Trim().TrimEnd('/');
            row.Tools = (row.Tools ?? []).Select(t => (t ?? string.Empty).Trim()).Where(t => t.Length > 0).ToList();
        }

        foreach (LlmPluginBudgetRow row in PluginBudgets)
        {
            row.Plugin = (row.Plugin ?? string.Empty).Trim();
            if (row.TokensPerDay < 0)
                row.TokensPerDay = 0;
        }
    }
}
