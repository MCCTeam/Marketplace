using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using LlmCore.Api;
using Microsoft.Extensions.Logging;

namespace DMCBK.OfficialPlugins.LlmCore;

/// <summary>Last call summary for the why command.</summary>
public sealed record LlmCallSummary(
    string Model,
    IReadOnlyList<string> ToolsUsed,
    int InTokens,
    int OutTokens,
    string? FinishReason,
    DateTimeOffset WhenUtc);

/// <summary>
/// The shared implementation behind ILLMService.
/// Runs the tool loop itself, enforces budgets, injects skills, compacts history on request, and publishes usage events.
/// One instance serves every plugin.
/// </summary>
public sealed class LlmService : ILLMService
{
    private const int MaxRounds = 25;

    private readonly Func<ResolvedProvider, string, ILlmBackend> _backends;
    private readonly ProviderRegistry _providers;
    private readonly ModelCatalog _models;
    private readonly ToolRegistry _tools;
    private readonly SkillRegistry _skills;
    private readonly McpRegistry _mcp;
    private readonly LlmBudgetLedger _budgets;
    private readonly Func<LlmCoreSettings> _settings;
    private readonly Func<string?>? _epochNow;
    private readonly Action<object> _publish;
    private readonly ILogger _logger;

    public LlmService(
        Func<ResolvedProvider, string, ILlmBackend> backends,
        ProviderRegistry providers,
        ModelCatalog models,
        ToolRegistry tools,
        SkillRegistry skills,
        McpRegistry mcp,
        LlmBudgetLedger budgets,
        Func<LlmCoreSettings> settings,
        Func<string?>? epochNow,
        Action<object> publish,
        ILogger logger)
    {
        _backends = backends;
        _providers = providers;
        _models = models;
        _tools = tools;
        _skills = skills;
        _mcp = mcp;
        _budgets = budgets;
        _settings = settings;
        _epochNow = epochNow;
        _publish = publish;
        _logger = logger;
    }

    /// <summary>Last completed call, for diagnostics. Null before the first call.</summary>
    public LlmCallSummary? LastCall { get; private set; }

    public async Task<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Messages);
        if (request.Messages.Count == 0)
            throw new ArgumentException("A call needs at least one message.", nameof(request));

        if (request.MaxIterations < 1)
            throw new ArgumentException("MaxIterations is at least 1.", nameof(request));

        ct.ThrowIfCancellationRequested();
        string correlation = Guid.NewGuid().ToString("N");
        LlmCoreSettings settings = _settings();
        (ResolvedProvider provider, string modelId) = ResolveModel(request.Model, settings);
        string key = EnvLinks.Secret(provider.ApiKeyEnv);
        if (key.Length == 0 && provider.Endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new LLMException(LLMErrorKind.Auth,
                $"Provider '{provider.Name}' needs a key: set {provider.ApiKeyEnv} and retry.");
        }

        string owner = BudgetOwner(request);
        int estimate = LLMTokenEstimator.EstimateMessages(request.Messages) + (request.MaxTokens ?? 1000);
        if (!_budgets.TryReserve(owner, estimate, out long reservation))
        {
            throw new LLMException(LLMErrorKind.Budget,
                $"Daily token budget is spent for '{owner}'.", false);
        }

        ILlmBackend backend;
        try
        {
            backend = _backends(provider, modelId);
        }
        catch (Exception ex)
        {
            _budgets.Commit(owner, reservation, 0);
            throw new LLMException(LLMErrorKind.Network, $"Cannot reach provider '{provider.Name}'.", ex, true);
        }

        List<LLMMessage> history = BuildMessages(request);
        history = await MaybeCompactAsync(request, history, provider, modelId, owner, ct).ConfigureAwait(false);
        CheckImages(request);
        Dictionary<string, LlmMcpTool> mcpTools = await ResolveMcpToolsAsync(request, ct).ConfigureAwait(false);

        int inTokens = 0;
        int outTokens = 0;
        var executed = new List<LLMToolCall>();
        string? responseId = null;
        string? finish = null;
        int rounds = Math.Min(request.MaxIterations, MaxRounds);

        try
        {
            for (int round = 0; round < rounds; round++)
            {
                ct.ThrowIfCancellationRequested();
                CheckEpoch(request);
                LlmBackendCall call = new(
                    modelId, history, request.JsonSchema, OfferedTools(request, mcpTools),
                    request.Temperature, request.TopP, request.ReasoningEffort,
                    request.MaxTokens ?? (settings.DefaultMaxTokens > 0 ? settings.DefaultMaxTokens : null),
                    request.Store);
                LlmBackendResult result;
                try
                {
                    result = await backend.CompleteAsync(call, ct).ConfigureAwait(false);
                }
                catch (LLMException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new LLMException(
                        LLMErrorKind.Network, $"Provider call failed: {ex.GetType().Name}: {ex.Message}.", ex, true);
                }

                inTokens += result.InTokens;
                outTokens += result.OutTokens;
                responseId ??= result.ResponseId;
                finish = result.FinishReason;

                if (request.DryRun || result.ToolCalls.Count == 0)
                {
                    executed.AddRange(result.ToolCalls);
                    _budgets.Commit(owner, reservation, inTokens + outTokens);
                    PublishUsage(modelId, inTokens, outTokens);
                    var done = new LLMResponse(result.Text, result.Json, executed, inTokens, outTokens, responseId, finish);
                    Remember(request, done);
                    return done;
                }

                // Custom backends may return null text; only the loop below may see it.
                string assistantText = result.Text ?? string.Empty;
                IReadOnlyList<LLMPart> assistantParts = assistantText.Length > 0
                    ? [new LLMTextPart(assistantText)]
                    : [];
                history.Add(new LLMMessage(LLMRoles.Assistant, assistantParts, Calls: result.ToolCalls));
                foreach (LLMToolCall toolCall in result.ToolCalls)
                {
                    ct.ThrowIfCancellationRequested();
                    CheckEpoch(request);
                    string output = await ExecuteToolAsync(request, toolCall, ct).ConfigureAwait(false);
                    executed.Add(toolCall);
                    history.Add(new LLMMessage(
                        LLMRoles.Tool, [new LLMTextPart(output)], CallId: toolCall.Id));
                }
            }

            throw new LLMException(LLMErrorKind.ToolFailed,
                $"Tool loop gave up after {rounds} rounds without a final answer.");
        }
        catch (Exception)
        {
            _budgets.Commit(owner, reservation, inTokens + outTokens);
            throw;
        }
    }

    public async IAsyncEnumerable<LLMChunk> StreamAsync(
        LLMRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        // The loop runs whole rounds; deltas stream as the final text is produced.
        LLMResponse response = await CompleteAsync(request, ct).ConfigureAwait(false);
        const int size = 200;
        for (int at = 0; at < response.Text.Length; at += size)
        {
            ct.ThrowIfCancellationRequested();
            yield return new LLMChunk(response.Text.Substring(at, Math.Min(size, response.Text.Length - at)), false);
            await Task.Yield();
        }

        yield return new LLMChunk(string.Empty, true);
    }

    public IDisposable RegisterTool(LLMTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return _tools.Register(OwnerOf(tool.Handler), tool);
    }

    public IDisposable RegisterAttributed(object target, string? basePath = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        Type type = target is Type t ? t : target.GetType();
        string owner = OwnerOfType(type);
        var handles = new List<IDisposable>();

        foreach (var method in type.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        {
            var attribute = method.GetCustomAttributes(typeof(LLMToolAttribute), false)
                .OfType<LLMToolAttribute>().FirstOrDefault();
            if (attribute is null)
                continue;

            ValidateToolMethod(owner, type, method, attribute.Name);
            object? instance = method.IsStatic ? null : target is Type ? null : target;
            if (!method.IsStatic && instance is null)
                throw new ArgumentException($"Tool method '{method.Name}' needs an instance target.");

            var handler = new MethodToolHandler(method, instance);
            handles.Add(_tools.Register(owner, new LLMTool(
                attribute.Name, attribute.Description, attribute.JsonSchema, handler, attribute.NeedsApproval)));
        }

        foreach (var attribute in type.GetCustomAttributes(typeof(LLMSkillAttribute), false)
            .OfType<LLMSkillAttribute>())
        {
            string directory = ResolveSkillPath(basePath, attribute.Path);
            string body = File.Exists(Path.Combine(directory, "SKILL.md"))
                ? File.ReadAllText(Path.Combine(directory, "SKILL.md"))
                : string.Empty;
            handles.Add(_skills.Register(owner, new LLMSkillDefinition(
                attribute.Name,
                attribute.Description ?? string.Empty,
                directory,
                body,
                attribute.Owner ?? owner,
                attribute.Version,
                attribute.Keywords)));
        }

        foreach (var attribute in type.GetCustomAttributes(typeof(LLMSkillsAttribute), false)
            .OfType<LLMSkillsAttribute>())
        {
            _skills.RegisterDirectory(owner, ResolveSkillPath(basePath, attribute.Path), out IDisposable dirHandle);
            handles.Add(dirHandle);
        }

        return new Composite(handles);
    }

    public IDisposable RegisterSkill(LLMSkillDefinition skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        return _skills.Register("api", skill);
    }

    public IDisposable RegisterSkillsDirectory(string path)
    {
        _skills.RegisterDirectory("api", path, out IDisposable handle);
        return handle;
    }

    public IReadOnlyList<LLMSkillSummary> ListSkills() => _skills.List();

    public Task<string> ReadSkillFileAsync(string skill, string path, CancellationToken ct)
        => _skills.ReadFileAsync(skill, path, ct);

    public IDisposable RegisterLLMProvider(LLMProviderDefinition provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return _providers.Register(new ResolvedProvider(
            provider.Name.Trim(), provider.Endpoint.Trim().TrimEnd('/'), provider.Api,
            provider.Enabled, (provider.ApiKeyEnv ?? string.Empty).Trim()), "api");
    }

    public IReadOnlyList<LLMProviderStatus> ListLLMProviders() => _providers.List();

    public Task<IReadOnlyList<LLMModelInfo>> ListLLMsAsync(string provider, bool refresh, CancellationToken ct)
    {
        if (!_providers.TryResolve(provider, out ResolvedProvider? resolved) || resolved is null)
            throw new ArgumentException($"No provider named '{provider}'.");

        return _models.ListAsync(resolved, refresh, ct);
    }

    public void AddLLM(string provider, string modelId)
    {
        if (!_providers.TryResolve(provider, out _))
            throw new ArgumentException($"No provider named '{provider}'.");

        _models.AddManual(provider, modelId.Trim());
    }

    public bool RemoveLLM(string provider, string modelId) => _models.RemoveManual(provider, modelId);

    public void ClearLLM(string provider) => _models.ClearDiscovered(provider);

    public IDisposable RegisterMcpServer(LLMMcpServerDefinition server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return _mcp.Register(server, "api");
    }

    public IReadOnlyList<LLMMcpServerStatus> ListMcpServers() => _mcp.List();

    private static string OwnerOf(ILLMToolHandler handler)
    {
        if (handler is MethodToolHandler method)
            return OwnerOfType(method.Method.DeclaringType);

        return OwnerOfType(handler.GetType());
    }

    private static string OwnerOfType(Type? type)
    {
        AssemblyLoadContext? context = type is null ? null : AssemblyLoadContext.GetLoadContext(type.Assembly);
        string name = context?.Name ?? string.Empty;
        return name.StartsWith("plugin:", StringComparison.Ordinal) ? name["plugin:".Length..] : "api";
    }

    private static string ResolveSkillPath(string? basePath, string path)
    {
        string clean = (path ?? string.Empty).Trim();
        if (Path.IsPathRooted(clean))
            return clean;

        if (string.IsNullOrEmpty(basePath))
            throw new ArgumentException($"Skill path '{path}' is relative but no base path was given.");

        return Path.Combine(basePath, clean);
    }

    private (ResolvedProvider Provider, string ModelId) ResolveModel(string? model, LlmCoreSettings settings)
    {
        string wanted = string.IsNullOrWhiteSpace(model) ? settings.DefaultModel : model.Trim();
        if (string.IsNullOrWhiteSpace(wanted))
            throw new ArgumentException("No model named and no default model configured. Run 'llm use'.");

        int slash = wanted.IndexOf('/');
        if (slash <= 0)
            throw new ArgumentException($"Model '{wanted}' needs the provider/model-id form.");

        string provider = wanted[..slash];
        string modelId = wanted[(slash + 1)..];
        if (!_providers.TryResolve(provider, out ResolvedProvider? resolved) || resolved is null)
            throw new LLMException(LLMErrorKind.ModelNotFound, $"No provider named '{provider}'.");

        if (!resolved.Enabled)
            throw new LLMException(LLMErrorKind.ModelNotFound, $"Provider '{provider}' is disabled.");

        if (modelId.Length == 0)
            throw new ArgumentException($"Model '{wanted}' names no model id.");

        if (resolved.Endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !_settings().AllowLocalEndpoints
            && IsLoopback(resolved.Endpoint))
        {
            throw new LLMException(LLMErrorKind.Auth,
                $"Provider '{provider}' points at localhost, which settings forbid. Enable allow-local-endpoints first.");
        }

        return (resolved, modelId);
    }

    private static bool IsLoopback(string endpoint)
    {
        try
        {
            string host = new Uri(endpoint).Host;
            return host is "localhost" or "127.0.0.1" or "::1" || host.EndsWith(".localhost", StringComparison.Ordinal);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static string BudgetOwner(LLMRequest request)
        => string.IsNullOrWhiteSpace(request.ActivityId) ? "shared" : request.ActivityId.Split(':')[0];

    private void CheckEpoch(LLMRequest request)
    {
        if (request.SessionEpoch is null || _epochNow is null)
            return;

        if (_epochNow() != request.SessionEpoch)
            throw new OperationCanceledException("The session changed under a running tool loop.");
    }

    private void CheckImages(LLMRequest request)
    {
        int images = request.Messages.SelectMany(m => m.Parts).OfType<LLMImagePart>().Count();
        if (images > request.MaxImagesPerRequest)
        {
            throw new ArgumentException(
                $"The call carries {images} images but allows {request.MaxImagesPerRequest}.");
        }
    }

    private List<LLMMessage> BuildMessages(LLMRequest request)
    {
        var messages = new List<LLMMessage>();
        var skills = new List<string>();
        if (request.Skills is not null)
        {
            foreach (string name in request.Skills)
            {
                if (_skills.TryGet(name, out var record) && record.SkillFile.Length > 0)
                    skills.Add($"# Skill: {record.Name}\n{record.Description}\n{record.SkillFile}");
            }
        }

        if (skills.Count > 0)
        {
            messages.Add(new LLMMessage(LLMRoles.System,
                [new LLMTextPart("Use these skills when relevant.\n" + string.Join("\n\n", skills))]));
        }

        if (request.AutoSkills)
        {
            var names = _skills.List();
            if (names.Count > 0)
            {
                messages.Add(new LLMMessage(LLMRoles.System, [new LLMTextPart(
                    "Registered skills: " + string.Join(", ", names.Select(s => s.Name))
                    + ". Pull what you need with list_skills, read_skill, search_skills, read_skill_file.")]));
            }
        }

        messages.AddRange(request.Messages);
        return messages;
    }

    private List<LlmBackendTool> OfferedTools(LLMRequest request, Dictionary<string, LlmMcpTool> mcpTools)
    {
        var offered = new List<LlmBackendTool>();
        if (request.AllowedTools is null && !request.AutoSkills)
            return offered;

        var wanted = new HashSet<string>(request.AllowedTools ?? [], StringComparer.Ordinal);
        if (request.AutoSkills)
        {
            foreach (string builtin in SkillBuiltins.Names)
                wanted.Add(builtin);
        }

        foreach (string name in wanted)
            OfferOne(request, mcpTools, offered, name);

        return offered;
    }

    private void OfferOne(
        LLMRequest request, Dictionary<string, LlmMcpTool> mcpTools, List<LlmBackendTool> offered, string name)
    {
        if (SkillBuiltins.TryBuild(name, _skills, out LlmBackendTool? builtin))
        {
            offered.Add(builtin);
            return;
        }

        if (_tools.TryGet(name, out var local))
        {
            offered.Add(new LlmBackendTool(name, local.Tool.Description, local.Tool.JsonSchema));
            return;
        }

        if (mcpTools.TryGetValue(name, out LlmMcpTool? remote))
        {
            offered.Add(new LlmBackendTool(
                name, $"Remote tool {remote.Name} on MCP server {remote.Server}.",
                remote.JsonSchema));
            return;
        }

        throw new ArgumentException($"Unknown tool '{name}'.");
    }

    /// <summary>Lists each named MCP server once per call, so rounds never re-list.</summary>
    private async Task<Dictionary<string, LlmMcpTool>> ResolveMcpToolsAsync(LLMRequest request, CancellationToken ct)
    {
        var resolved = new Dictionary<string, LlmMcpTool>(StringComparer.Ordinal);
        if (request.AllowedTools is null)
            return resolved;

        var servers = new Dictionary<string, IReadOnlyList<LlmMcpTool>>(StringComparer.Ordinal);
        foreach (string name in request.AllowedTools)
        {
            if (SkillBuiltins.TryBuild(name, _skills, out _) || _tools.TryGet(name, out _))
                continue;

            if (!TrySplitMcp(name, out string server, out string tool))
                throw new ArgumentException($"Unknown tool '{name}'.");

            if (!servers.TryGetValue(server, out var tools))
            {
                try
                {
                    tools = await _mcp.ToolsForServerAsync(server, ct).ConfigureAwait(false);
                }
                catch (ArgumentException)
                {
                    throw new ArgumentException($"Unknown tool '{name}'.");
                }

                servers[server] = tools;
            }

            LlmMcpTool? match = tools.FirstOrDefault(t => t.Name == tool);
            if (match is null)
                throw new ArgumentException($"MCP server '{server}' has no tool '{tool}'.");

            resolved[name] = match;
        }

        return resolved;
    }

    private bool TrySplitMcp(string name, out string server, out string tool)
    {
        server = string.Empty;
        tool = string.Empty;
        foreach (LLMMcpServerStatus status in _mcp.List())
        {
            if (name.StartsWith(status.Name + "_", StringComparison.Ordinal))
            {
                server = status.Name;
                tool = name[(status.Name.Length + 1)..];
                return tool.Length > 0;
            }
        }

        return false;
    }

    private bool McpHasTool(string server, string tool)
    {
        try
        {
            IReadOnlyList<LlmMcpTool> tools = _mcp.ToolsForServerAsync(server, CancellationToken.None)
                .GetAwaiter().GetResult();
            return tools.Any(t => t.Name == tool);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<string> ExecuteToolAsync(LLMRequest request, LLMToolCall call, CancellationToken ct)
    {
        if (SkillBuiltins.TryBuild(call.Name, _skills, out _))
            return await SkillBuiltins.ExecuteAsync(call.Name, call.JsonArgs, _skills, ct).ConfigureAwait(false);

        if (_tools.TryGet(call.Name, out var local))
        {
            if (local.Tool.NeedsApproval)
            {
                bool approved = request.Approve is not null
                    && await request.Approve.DecideAsync(call, ct).ConfigureAwait(false);
                if (!approved)
                    return LLMJson.Encode(new { error = $"tool '{call.Name}' was refused" });
            }

            try
            {
                string output = await local.InvokeAsync(call.JsonArgs, ct).ConfigureAwait(false);
                return Truncate(output, request.MaxToolResultBytes);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return LLMJson.Encode(new { error = $"tool '{call.Name}' failed: {ex.Message}" });
            }
        }

        if (TrySplitMcp(call.Name, out string server, out string tool))
        {
            try
            {
                string output = await _mcp.CallToolAsync(server, tool, call.JsonArgs, ct).ConfigureAwait(false);
                return Truncate(output, request.MaxToolResultBytes);
            }
            catch (LLMException ex)
            {
                return LLMJson.Encode(new { error = $"MCP tool '{call.Name}' failed: {ex.Message}" });
            }
        }

        return LLMJson.Encode(new { error = $"unknown tool '{call.Name}'" });
    }

    private static string Truncate(string output, int maxBytes)
    {
        if (output.Length <= maxBytes)
            return output;

        int keep = Math.Max(0, maxBytes - 64);
        return output[..Math.Min(keep, output.Length)] + $"\n[truncated to {maxBytes} bytes]";
    }

    private async Task<List<LLMMessage>> MaybeCompactAsync(
        LLMRequest request, List<LLMMessage> history,
        ResolvedProvider provider, string modelId, string owner, CancellationToken ct)
    {
        LlmCoreSettings settings = _settings();
        if (request.Compaction != LLMCompactionMode.Auto)
            return history;

        int maxContext = request.MaxContext ?? (settings.DefaultMaxContext > 0 ? settings.DefaultMaxContext : 32000);
        if (LLMTokenEstimator.EstimateMessages(history) <= maxContext)
            return history;

        int firstUser = history.FindIndex(m => m.Role != LLMRoles.System);
        if (firstUser < 0 || history.Count - firstUser < 6)
            return history;

        int cut = firstUser;
        int end = history.Count - 2;
        while (cut < end && !IsPlain(history[cut]))
            cut++;

        int tail = end;
        while (tail > cut && !IsPlain(history[tail]))
            tail--;

        if (tail - cut < 2)
            return history;

        List<LLMMessage> block = history.GetRange(cut, tail - cut + 1);
        string transcript = string.Join("\n", block.SelectMany(m => m.Parts.OfType<LLMTextPart>()
            .Select(p => $"{m.Role}: {p.Text}")));
        ILlmBackend backend = _backends(provider, modelId);
        var summary = new LLMRequest
        {
            Messages = [new LLMMessage(LLMRoles.User, [new LLMTextPart(
                "Summarize for continued work. Keep names, decisions, promises, and open loops. Be brief:\n" + transcript)])],
            Model = request.Model,
            MaxTokens = 500,
        };
        LlmBackendResult condensed;
        try
        {
            condensed = await backend.CompleteAsync(ToBackendCall(summary, modelId), ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return history;
        }

        var compacted = history[..cut].ToList();
        compacted.Add(new LLMMessage(LLMRoles.Assistant, [new LLMTextPart("Earlier summary: " + condensed.Text.Trim())]));
        compacted.AddRange(history[(tail + 1)..]);
        int saved = LLMTokenEstimator.EstimateMessages(history) - LLMTokenEstimator.EstimateMessages(compacted);
        _publish(new LLMCompactionEvent(owner, $"{provider.Name}/{modelId}", Math.Max(0, saved)));
        return compacted;
    }

    private LlmBackendCall ToBackendCall(LLMRequest request, string modelId)
    {
        LlmCoreSettings settings = _settings();
        return new LlmBackendCall(
            modelId, BuildMessages(request), request.JsonSchema, [],
            request.Temperature, request.TopP, request.ReasoningEffort,
            request.MaxTokens ?? (settings.DefaultMaxTokens > 0 ? settings.DefaultMaxTokens : null),
            request.Store);
    }

    private static bool IsPlain(LLMMessage message)
        => (message.Role is LLMRoles.User or LLMRoles.Assistant)
            && message is { Calls: null }
            && message.Parts.All(p => p is LLMTextPart);

    private void PublishUsage(string model, int inTokens, int outTokens)
    {
        try
        {
            _publish(new LLMUsageEvent(model, inTokens, outTokens));
        }
        catch (Exception)
        {
        }
    }

    private void Remember(LLMRequest request, LLMResponse response)
    {
        try
        {
            LastCall = new LlmCallSummary(
                request.Model ?? string.Empty,
                response.ToolCalls.Select(c => c.Name).Distinct(StringComparer.Ordinal).ToList(),
                response.InTokens,
                response.OutTokens,
                response.FinishReason,
                DateTimeOffset.UtcNow);
        }
        catch (Exception)
        {
        }
    }

    private void ValidateToolMethod(string owner, Type type, MethodInfo method, string tool)
    {
        ParameterInfo[] parameters = method.GetParameters();
        bool shape = method.ReturnType == typeof(Task<string>)
            && parameters.Length == 2
            && parameters[0].ParameterType == typeof(string)
            && parameters[1].ParameterType == typeof(CancellationToken);
        if (!shape)
            throw new LlmToolSignatureException(owner, type.FullName ?? type.Name, method.Name, tool);
    }

    private sealed class MethodToolHandler(MethodInfo method, object? instance) : ILLMToolHandler
    {
        public MethodInfo Method { get; } = method;

        public Task<string> InvokeAsync(string args, CancellationToken ct)
            => (Task<string>)Method.Invoke(instance, [args, ct])!;
    }

    private sealed class Composite(List<IDisposable> handles) : IDisposable
    {
        public void Dispose()
        {
            foreach (IDisposable handle in handles)
                handle.Dispose();
        }
    }
}

/// <summary>Names a tool method that breaks the fixed shape, so the fix is obvious.</summary>
public sealed class LlmToolSignatureException(string owner, string type, string method, string tool)
    : InvalidOperationException(
        $"Plugin '{owner}' method '{type}.{method}' for tool '{tool}' cannot be an LLM tool: " +
        "use Task<string> Name(string args, CancellationToken ct).");
