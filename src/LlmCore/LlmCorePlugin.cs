using LlmCore.Api;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;

namespace DMCBK.OfficialPlugins.LlmCore;

/// <summary>
/// The shared AI plugin.
/// It owns providers, models, skills, and MCP servers, and registers the single ILLMService other plugins call.
/// On its own it answers nothing; it only serves.
/// </summary>
public sealed class LlmCorePlugin : IPlugin
{
    private PluginContext? _context;
    private LlmCoreSettings _settings = new();
    private string _epoch = Guid.NewGuid().ToString("N");

    private readonly ProviderRegistry _providers = new();
    private readonly ToolRegistry _tools = new();
    private readonly SkillRegistry _skills = new();
    private McpRegistry? _mcp;
    private ModelCatalog? _models;
    private LlmService? _service;
    private readonly LlmBudgetLedger _budgets = new();
    private IDisposable? _serviceHandle;

    /// <inheritdoc/>
    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "llm-core";
        descriptor.Version = "0.2.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
        descriptor.WithSettings<LlmCoreSettings>();
    }

    /// <inheritdoc/>
    public Task ActivateAsync(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _settings = context.Settings.Load<LlmCoreSettings>();
        ApplySettings();

        _models = new ModelCatalog(ModelCacheDirectory(context), NewHttpClient);
        _mcp = new McpRegistry(NewHttpClient);
        _mcp.SeedCentral(_settings.McpServers);
        _service = new LlmService(
            NewBackend, _providers, _models, _tools, _skills, _mcp, _budgets,
            () => _settings, () => _epoch, Publish, context.Logger);
        _serviceHandle = context.Services.Register<ILLMService>(_service);
        context.Commands.Register(new LlmCommand(this, context.Strings));

        context.SessionStarted += (_, _) => _epoch = Guid.NewGuid().ToString("N");
        context.SessionEnded += (_, _) => _epoch = Guid.NewGuid().ToString("N");
        context.Logger.LogInformation("{Message}", context.Strings.Format(
            "started", _providers.Snapshot().Count, _settings.DefaultModel));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DeactivateAsync(CancellationToken ct)
    {
        _serviceHandle?.Dispose();
        _serviceHandle = null;
        _mcp?.Dispose();
        _mcp = null;
        return Task.CompletedTask;
    }

    internal LlmCoreSettings Settings => _settings;
    internal ProviderRegistry Providers => _providers;
    internal ModelCatalog Models => _models ?? throw new InvalidOperationException("llm-core is not active.");
    internal McpRegistry Mcp => _mcp ?? throw new InvalidOperationException("llm-core is not active.");
    internal SkillRegistry Skills => _skills;
    internal LlmService Service => _service ?? throw new InvalidOperationException("llm-core is not active.");

    /// <summary>Reloads settings from disk and applies them. Used by commands and tests.</summary>
    public void Reload()
    {
        if (_context is { } context)
        {
            _settings = context.Settings.Load<LlmCoreSettings>();
            ApplySettings();
            _mcp?.SeedCentral(_settings.McpServers);
        }
    }

    internal void SaveSettings()
    {
        if (_context is { } context)
            context.Settings.Save(_settings);
    }

    internal string ResolveKey(ResolvedProvider provider) => EnvLinks.Secret(provider.ApiKeyEnv);

    internal long BudgetsSpentToday(string owner) => _budgets.SpentToday(owner);

    internal void SetPluginBudget(string plugin, long tokensPerDay)
    {
        _settings.PluginBudgets.RemoveAll(r => r.Plugin.Equals(plugin, StringComparison.OrdinalIgnoreCase));
        if (tokensPerDay > 0)
            _settings.PluginBudgets.Add(new LlmPluginBudgetRow { Plugin = plugin, TokensPerDay = tokensPerDay });

        SaveSettings();
        ApplySettings();
    }

    internal void UpsertCentralProvider(ResolvedProvider provider)
    {
        _settings.Providers.RemoveAll(r => r.Name.Equals(provider.Name, StringComparison.OrdinalIgnoreCase));
        _settings.Providers.Add(new LlmProviderRow
        {
            Name = provider.Name,
            Endpoint = provider.Endpoint,
            Api = provider.Api == LLMWireFormat.ChatCompletions ? "chat" : "responses",
            Enabled = provider.Enabled,
            ApiKeyEnv = provider.ApiKeyEnv ?? string.Empty,
        });
        SaveSettings();
        _providers.UpsertCentral(provider);
    }

    internal bool RemoveCentralProvider(string name)
    {
        int removed = _settings.Providers.RemoveAll(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
            SaveSettings();

        return _providers.RemoveCentral(name);
    }

    internal bool SetProviderEnabled(string name, bool enabled)
    {
        LlmProviderRow? row = _settings.Providers
            .FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (row is null)
            return _providers.SetEnabled(name, enabled);

        row.Enabled = enabled;
        SaveSettings();
        _providers.SetEnabled(name, enabled);
        return true;
    }

    internal bool ProviderIsApiOwned(string name, out string owner)
        => _providers.TryApiOwner(name, out owner);

    internal async Task ProbeEndpointAsync(string name, string url, string keyEnv = "", CancellationToken ct = default)
    {
        var probe = new ResolvedProvider(name, url, LLMWireFormat.Responses, true, keyEnv);
        if (_models is null)
            throw new InvalidOperationException("llm-core is not active.");

        await _models.DiscoverAsync(probe, ct).ConfigureAwait(false);
    }

    internal Task<IReadOnlyList<LlmMcpTool>> McpToolsAsync(string name, CancellationToken ct)
        => (_mcp ?? throw new InvalidOperationException("llm-core is not active.")).ToolsForServerAsync(name, ct);

    internal async Task AddCentralMcpAsync(
        string name, string url, IReadOnlyList<string> tools, CancellationToken ct)
    {
        if (_mcp is null)
            throw new InvalidOperationException("llm-core is not active.");

        using var client = new McpJsonRpcClient(NewHttpClient(), url);
        IReadOnlyList<LlmMcpTool> advertised = await client.ListToolsAsync(name, ct).ConfigureAwait(false);
        var allowed = new HashSet<string>(tools, StringComparer.Ordinal);
        if (allowed.Count > 0)
        {
            foreach (string wanted in allowed)
            {
                if (!advertised.Any(t => t.Name == wanted))
                    throw new ArgumentException($"MCP server '{name}' has no tool '{wanted}'.");
            }
        }

        _settings.McpServers.RemoveAll(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _settings.McpServers.Add(new LlmMcpRow
        {
            Name = name,
            Transport = "http",
            Url = url,
            Tools = allowed.Count > 0 ? allowed.ToList() : advertised.Select(t => t.Name).ToList(),
        });
        SaveSettings();
        _mcp.SeedCentral(_settings.McpServers);
    }

    internal bool RemoveCentralMcp(string name)
    {
        if (_mcp is null)
            return false;

        int removed = _settings.McpServers.RemoveAll(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
            SaveSettings();

        return _mcp.RemoveCentral(name);
    }

    internal bool McpIsApiOwned(string name, out string owner)
    {
        owner = string.Empty;
        return _mcp is not null && _mcp.TryOwnerOf(name, out owner) && !_mcp.IsCentral(name);
    }

    private void ApplySettings()
    {
        _providers.SeedCentral(_settings.Providers);
        var perPlugin = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (LlmPluginBudgetRow row in _settings.PluginBudgets)
        {
            if (row.Plugin.Length > 0 && row.TokensPerDay > 0)
                perPlugin[row.Plugin] = row.TokensPerDay;
        }

        _budgets.Configure(_settings.DailyTokenBudget, perPlugin);
    }

    private ILlmBackend NewBackend(ResolvedProvider provider, string modelId)
    {
        string key = ResolveKey(provider);
        if (key.Length == 0)
            key = "local";

        return provider.Api == LLMWireFormat.ChatCompletions
            ? new OpenAIChatBackend(provider.Endpoint, key, modelId)
            : new OpenAIResponsesBackend(provider.Endpoint, key);
    }

    private static HttpClient NewHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("mcc-llm-core/0.1.0");
        return client;
    }

    private static string ModelCacheDirectory(PluginContext context)
        => context.Storage.GetPath("models");

    private void Publish(object message)
    {
        if (_context is null)
            return;

        try
        {
            switch (message)
            {
                case LLMUsageEvent usage:
                    _context.Messenger.Publish(usage);
                    break;
                case LLMStatusEvent status:
                    _context.Messenger.Publish(status);
                    break;
                case LLMCompactionEvent compaction:
                    _context.Messenger.Publish(compaction);
                    break;
            }
        }
        catch (Exception)
        {
        }
    }
}
