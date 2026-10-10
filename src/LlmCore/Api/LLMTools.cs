namespace LlmCore.Api;

/// <summary>
/// A locally implemented tool.
/// The core adapts attributed methods into this shape internally, so unload disposal still drops everything at once and no caller delegate outlives its plugin.
/// </summary>
public interface ILLMToolHandler
{
    Task<string> InvokeAsync(string args, CancellationToken ct);
}

/// <summary>Decides one approval-gated tool call. Scoped to the call; never retained past it.</summary>
public interface ILLMApprovalHandler
{
    Task<bool> DecideAsync(LLMToolCall call, CancellationToken ct);
}

/// <summary>A tool the core may execute inside its loop. Approval-gated unless opted out.</summary>
public sealed record LLMTool(
    string Name,
    string Description,
    string JsonSchema,
    ILLMToolHandler Handler,
    bool NeedsApproval = true);
