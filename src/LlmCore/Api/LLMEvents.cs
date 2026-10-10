namespace LlmCore.Api;

/// <summary>Broadcast per completed call. Per-plugin accounting stays core-private.</summary>
public sealed record LLMUsageEvent(
    string Model,
    int InTokens,
    int OutTokens);

/// <summary>Broadcast when provider availability changes.</summary>
public sealed record LLMStatusEvent(
    bool Available,
    string Endpoint,
    string Model,
    string Reason);

/// <summary>Broadcast when auto compaction rewrites a caller history.</summary>
public sealed record LLMCompactionEvent(
    string PluginId,
    string Model,
    int SavedTokens);
