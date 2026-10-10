namespace LlmCore.Api;

/// <summary>One content part inside a message. Sealed hierarchy so the core never meets an unknown CLR type.</summary>
public abstract record LLMPart;

/// <summary>Plain text content.</summary>
public sealed record LLMTextPart(string Text) : LLMPart;

/// <summary>Image bytes plus mime type, for example a map snapshot PNG.</summary>
public sealed record LLMImagePart(byte[] Data, string MimeType) : LLMPart;

/// <summary>Role names used in message lists.</summary>
public static class LLMRoles
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
}

/// <summary>
/// A role plus its content parts.
/// Tool messages carry the call id they answer; assistant messages may carry the calls they proposed, so a tool loop replays full history on every round.
/// </summary>
public sealed record LLMMessage(
    string Role,
    IReadOnlyList<LLMPart> Parts,
    string? CallId = null,
    IReadOnlyList<LLMToolCall>? Calls = null);
