namespace LlmCore.Api;

/// <summary>Failure kinds, so callers can tell retryable from fatal without parsing text.</summary>
public enum LLMErrorKind
{
    RateLimited,
    Auth,
    ModelNotFound,
    ContentFiltered,
    Network,
    Timeout,
    ToolFailed,
    Budget,
}

/// <summary>Typed failure carrying retry guidance and a correlation id for the logs.</summary>
public sealed class LLMException : Exception
{
    public LLMException(LLMErrorKind kind, string message, bool retryable = false)
        : base(message)
    {
        Kind = kind;
        Retryable = retryable;
    }

    public LLMException(LLMErrorKind kind, string message, Exception inner, bool retryable = false)
        : base(message, inner)
    {
        Kind = kind;
        Retryable = retryable;
    }

    public LLMErrorKind Kind { get; }
    public bool Retryable { get; }
    public TimeSpan? RetryAfter { get; init; }
    public string CorrelationId { get; init; } = Guid.NewGuid().ToString("N");
}
