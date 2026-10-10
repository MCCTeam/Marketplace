namespace LlmCore.Api;

/// <summary>MCP transports the core speaks. Both ride over HTTP POST in this version.</summary>
public enum LLMMcpTransport
{
    Http,
    Sse,
}

/// <summary>
/// A remote tool server.
/// Tools lists the remote tool names the core may expose, surfaced as "server_tool".
/// One name exists once across API and central registrations.
/// </summary>
public sealed record LLMMcpServerDefinition(
    string Name,
    LLMMcpTransport Transport,
    string Url,
    IReadOnlyList<string> Tools);

/// <summary>Listing row with a plain reason when disconnected.</summary>
public sealed record LLMMcpServerStatus(
    string Name,
    bool Connected,
    string Reason);
