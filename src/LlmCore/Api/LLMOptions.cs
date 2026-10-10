namespace LlmCore.Api;

/// <summary>
/// Reasoning effort levels.
/// Null on a request means the core default.
/// None disables reasoning.
/// XHigh and Max exist for models that offer them; compatible servers receive them verbatim, and servers that do not know them answer with a plain error.
/// </summary>
public enum LLMReasoningEffort
{
    None,
    Low,
    Medium,
    High,
    XHigh,
    Max,
}

/// <summary>Context compression policy. Off unless the caller opts in.</summary>
public enum LLMCompactionMode
{
    Off,
    Auto,
}
