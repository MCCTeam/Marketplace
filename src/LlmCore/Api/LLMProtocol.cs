namespace LlmCore.Api;

/// <summary>A function call the model proposed. Results travel back as tool-role messages.</summary>
public sealed record LLMToolCall(string Id, string Name, string JsonArgs);

/// <summary>
/// One inference call.
/// A sealed class with init properties so later versions can add knobs without breaking callers compiled against an older shape.
/// </summary>
public sealed class LLMRequest
{
    public required IReadOnlyList<LLMMessage> Messages { get; init; }

    /// <summary>Model as "provider/model-id". Null or blank means the core default.</summary>
    public string? Model { get; init; }

    /// <summary>JSON schema string for structured output. The answer JSON arrives on the response.</summary>
    public string? JsonSchema { get; init; }

    /// <summary>Tool names the model may call this round. Null means text only.</summary>
    public IReadOnlyList<string>? AllowedTools { get; init; }

    /// <summary>Skill names injected into the system message.</summary>
    public IReadOnlyList<string>? Skills { get; init; }

    /// <summary>When true the model may discover and pull registered skills itself.</summary>
    public bool AutoSkills { get; init; }

    public int? MaxTokens { get; init; }
    public bool Store { get; init; }

    /// <summary>Tool rounds per call.</summary>
    public int MaxIterations { get; init; } = 5;

    /// <summary>Plan the calls and return them unexecuted.</summary>
    public bool DryRun { get; init; }

    public ILLMApprovalHandler? Approve { get; init; }
    public LLMReasoningEffort? ReasoningEffort { get; init; }
    public double? Temperature { get; init; }
    public double? TopP { get; init; }
    public int? TopK { get; init; }

    /// <summary>Cap on context tokens sent. Null means the core default.</summary>
    public int? MaxContext { get; init; }

    public LLMCompactionMode Compaction { get; init; } = LLMCompactionMode.Off;
    public int MaxToolResultBytes { get; init; } = 32_000;
    public int MaxImagesPerRequest { get; init; } = 4;

    /// <summary>Correlates logs, events, and traces for one task.</summary>
    public string? ActivityId { get; init; }

    /// <summary>Tool loops abort when the session epoch changes under them.</summary>
    public string? SessionEpoch { get; init; }

    /// <summary>The 80 percent case in one call.</summary>
    public static LLMRequest Ask(string text, string? model = null) => new()
    {
        Messages = [new LLMMessage(LLMRoles.User, [new LLMTextPart(text)])],
        Model = model,
    };
}

/// <summary>The final answer after the core ran the tool loop itself.</summary>
public sealed record LLMResponse(
    string Text,
    string? Json,
    IReadOnlyList<LLMToolCall> ToolCalls,
    int InTokens,
    int OutTokens,
    string? ResponseId,
    string? FinishReason);

/// <summary>One streamed text delta.</summary>
public sealed record LLMChunk(string Delta, bool Done);

/// <summary>Shared contract for inference. Async throughout; the messenger stays broadcast only.</summary>
public interface ILLMService
{
    Task<LLMResponse> CompleteAsync(LLMRequest request, CancellationToken ct);

    /// <summary>
    /// Streams the final text in deltas.
    /// Enumerate with <c>WithCancellation(ct)</c>; the implementation honors cancellation between chunks and tool rounds.
    /// </summary>
    IAsyncEnumerable<LLMChunk> StreamAsync(LLMRequest request, CancellationToken ct);

    IDisposable RegisterTool(LLMTool tool);

    /// <summary>
    /// Registers every attributed tool and skill on the target.
    /// Relative skill paths resolve against <paramref name="basePath"/>.
    /// One handle withdraws everything.
    /// </summary>
    IDisposable RegisterAttributed(object target, string? basePath = null);

    IDisposable RegisterSkill(LLMSkillDefinition skill);
    IDisposable RegisterSkillsDirectory(string path);
    IReadOnlyList<LLMSkillSummary> ListSkills();
    Task<string> ReadSkillFileAsync(string skill, string path, CancellationToken ct);

    IDisposable RegisterLLMProvider(LLMProviderDefinition provider);
    IReadOnlyList<LLMProviderStatus> ListLLMProviders();
    Task<IReadOnlyList<LLMModelInfo>> ListLLMsAsync(string provider, bool refresh, CancellationToken ct);
    void AddLLM(string provider, string modelId);
    bool RemoveLLM(string provider, string modelId);
    void ClearLLM(string provider);

    IDisposable RegisterMcpServer(LLMMcpServerDefinition server);
    IReadOnlyList<LLMMcpServerStatus> ListMcpServers();
}
