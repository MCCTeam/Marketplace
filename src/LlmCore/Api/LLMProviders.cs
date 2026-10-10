namespace LlmCore.Api;

/// <summary>Wire formats the core speaks. Responses is the default everywhere.</summary>
public enum LLMWireFormat
{
    Responses,
    ChatCompletions,
}

/// <summary>
/// A model endpoint.
/// Keys are referenced by environment variable name and resolved inside the core at send time, so they never cross the plugin boundary.
/// </summary>
public sealed record LLMProviderDefinition(
    string Name,
    string Endpoint,
    LLMWireFormat Api = LLMWireFormat.Responses,
    bool Enabled = true,
    string? ApiKeyEnv = null);

/// <summary>Listing row with a plain reason when unreachable.</summary>
public sealed record LLMProviderStatus(
    string Name,
    bool Enabled,
    bool Reachable,
    string Reason);

/// <summary>One model id. Source is "discovered" or "manual".</summary>
public sealed record LLMModelInfo(
    string Id,
    string Provider,
    string Source);
