namespace DMCBK.OfficialPlugins.McpServer;

/// <summary>
/// The tool-result envelope.
/// Every MCP tool returns this shape (serialized as JSON by the MCP SDK), kept matching the <c>MccMcpResult</c> shape so existing agents keep parsing: HTTP 200 always carries the envelope, and <c>success:false</c> plus <c>errorCode</c> is the failure signal the agent must check.
/// The SDK never sets protocol <c>isError</c>; the operator prompt says so explicitly.
/// </summary>
public sealed class McpResult
{
    /// <summary>Whether the tool did what was asked.</summary>
    public bool Success { get; init; }

    /// <summary>Machine-readable failure reason when <see cref="Success"/> is false.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Human-readable detail, safe to show the agent.</summary>
    public string? Message { get; init; }

    /// <summary>The tool payload on success, or structured failure context.</summary>
    public object? Data { get; init; }

    /// <summary>Builds a successful result.</summary>
    public static McpResult Ok(object? data = null, string? message = null)
        => new() { Success = true, Data = data, Message = message };

    /// <summary>Builds a failed result. The error code is required: agents switch on it.</summary>
    public static McpResult Fail(string errorCode, string? message = null, object? data = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(errorCode);
        return new McpResult { Success = false, ErrorCode = errorCode, Message = message, Data = data };
    }
}

/// <summary>Well-known error codes. Agents switch on these; do not rename them.</summary>
public static class McpErrorCodes
{
    /// <summary>No live session is attached.</summary>
    public const string Disconnected = "disconnected";

    /// <summary>The session is mid-reconnect or still configuring; retry shortly.</summary>
    public const string Busy = "busy";

    /// <summary>A capability toggle for this tool group is off.</summary>
    public const string CapabilityDisabled = "capability_disabled";

    /// <summary>A gameplay feature gate (terrain/inventory/entity/physics/pathfinding) is off.</summary>
    public const string FeatureDisabled = "feature_disabled";

    /// <summary>Caller arguments are missing or out of range.</summary>
    public const string InvalidArgs = "invalid_args";

    /// <summary>Arguments parse but the world/session state cannot honor them.</summary>
    public const string InvalidState = "invalid_state";

    /// <summary>The action was attempted and failed.</summary>
    public const string ActionFailed = "action_failed";

    /// <summary>The action started but did not verify (moved short, transfer partial, path partial).</summary>
    public const string ActionIncomplete = "action_incomplete";

    /// <summary>No bearer token is configured while auth is required.</summary>
    public const string MissingAuthToken = "missing_auth_token";

    /// <summary>The configured token is too weak to accept.</summary>
    public const string WeakAuthToken = "token_too_weak";

    /// <summary>The port cannot be bound or the bind address is invalid.</summary>
    public const string BindFailed = "bind_failed";

    /// <summary>The port number is out of range.</summary>
    public const string InvalidPort = "invalid_port";

    /// <summary>Stopping the listener failed; it may still be bound.</summary>
    public const string StopFailed = "stop_failed";

    /// <summary>Another plugin currently holds the exclusive movement lease.</summary>
    public const string MovementHeld = "movement_held";

    /// <summary>Chat text that would run a client or server command instead of chatting.</summary>
    public const string CommandTextBlocked = "internal_command_text_blocked";
}
