#pragma warning disable OPENAI001 // Responses surface is still marked experimental upstream.
using System.ClientModel;
using System.ClientModel.Primitives;
using LlmCore.Api;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

namespace DMCBK.OfficialPlugins.LlmCore;

/// <summary>One tool offered to a backend round, in plain JSON.</summary>
public sealed record LlmBackendTool(string Name, string Description, string JsonSchema);

/// <summary>One backend round: text, optional structured JSON, proposed calls, usage.</summary>
public sealed record LlmBackendResult(
    string Text,
    string? Json,
    IReadOnlyList<LLMToolCall> ToolCalls,
    int InTokens,
    int OutTokens,
    string? ResponseId,
    string? FinishReason);

/// <summary>Backend input. Messages stay in Api types; each backend maps them to its wire shape.</summary>
public sealed record LlmBackendCall(
    string Model,
    IReadOnlyList<LLMMessage> Messages,
    string? JsonSchema,
    IReadOnlyList<LlmBackendTool> Tools,
    double? Temperature,
    double? TopP,
    LLMReasoningEffort? Reasoning,
    int? MaxTokens,
    bool Store);

/// <summary>One provider round trip. Implementations wrap the official SDK; tests fake this.</summary>
public interface ILlmBackend
{
    Task<LlmBackendResult> CompleteAsync(LlmBackendCall call, CancellationToken ct);
}

/// <summary>
/// Maps core reasoning levels onto SDK effort values.
/// None through High have first-class SDK members; XHigh and Max ride the SDK's implicit string conversion so compatible servers receive them verbatim.
/// </summary>
public static class LlmReasoningMapper
{
    public static ResponseReasoningEffortLevel ToResponses(LLMReasoningEffort effort) => effort switch
    {
        LLMReasoningEffort.None => ResponseReasoningEffortLevel.None,
        LLMReasoningEffort.Low => ResponseReasoningEffortLevel.Low,
        LLMReasoningEffort.High => ResponseReasoningEffortLevel.High,
        LLMReasoningEffort.XHigh => (ResponseReasoningEffortLevel)"xhigh",
        LLMReasoningEffort.Max => (ResponseReasoningEffortLevel)"max",
        _ => ResponseReasoningEffortLevel.Medium,
    };

    public static ChatReasoningEffortLevel ToChat(LLMReasoningEffort effort) => effort switch
    {
        LLMReasoningEffort.None => ChatReasoningEffortLevel.None,
        LLMReasoningEffort.Low => ChatReasoningEffortLevel.Low,
        LLMReasoningEffort.High => ChatReasoningEffortLevel.High,
        LLMReasoningEffort.XHigh => (ChatReasoningEffortLevel)"xhigh",
        LLMReasoningEffort.Max => (ChatReasoningEffortLevel)"max",
        _ => ChatReasoningEffortLevel.Medium,
    };
}

/// <summary>Maps HTTP statuses onto the typed error contract.</summary>
public static class LlmBackendErrors
{
    public static LLMException FromStatus(int status, string message, Exception inner)
        => status switch
        {
            401 or 403 => new LLMException(LLMErrorKind.Auth, message, inner),
            404 => new LLMException(LLMErrorKind.ModelNotFound, message, inner),
            429 => new LLMException(LLMErrorKind.RateLimited, message, inner, true)
            {
                RetryAfter = RetryAfterOf(inner),
            },
            >= 500 or 408 => new LLMException(LLMErrorKind.Network, message, inner, true),
            _ => new LLMException(LLMErrorKind.Network, message, inner),
        };

    private static TimeSpan? RetryAfterOf(Exception ex)
    {
        try
        {
            if (ex is ClientResultException result
                && result.GetRawResponse() is { } response
                && response.Headers.TryGetValue("retry-after", out string? value)
                && int.TryParse(value, out int seconds))
                return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 600));
        }
        catch (Exception)
        {
            // Header shape differs across SDK builds; absence just means no hint.
        }

        return null;
    }
}

/// <summary>Responses API backend over the official SDK. Custom endpoints reach compatible servers.</summary>
public sealed class OpenAIResponsesBackend : ILlmBackend
{
    private readonly ResponsesClient _client;

    public OpenAIResponsesBackend(string endpoint, string apiKey)
    {
        var options = new ResponsesClientOptions { Endpoint = new Uri(endpoint) };
        _client = new ResponsesClient(new ApiKeyCredential(apiKey), options);
    }

    public async Task<LlmBackendResult> CompleteAsync(LlmBackendCall call, CancellationToken ct)
    {
        var items = call.Messages.SelectMany(MapMessages).ToList();
        var options = new CreateResponseOptions(call.Model, items)
        {
            Temperature = call.Temperature.HasValue ? (float)call.Temperature.Value : null,
            TopP = call.TopP.HasValue ? (float)call.TopP.Value : null,
            MaxOutputTokenCount = call.MaxTokens,
            StoredOutputEnabled = call.Store,
        };
        if (call.Reasoning.HasValue)
        {
            options.ReasoningOptions = new ResponseReasoningOptions
            {
                ReasoningEffortLevel = LlmReasoningMapper.ToResponses(call.Reasoning.Value),
            };
        }

        if (!string.IsNullOrWhiteSpace(call.JsonSchema))
        {
            options.TextOptions = new ResponseTextOptions
            {
                TextFormat = ResponseTextFormat.CreateJsonSchemaFormat(
                    "answer", BinaryData.FromString(call.JsonSchema), null, false),
            };
        }

        foreach (LlmBackendTool tool in call.Tools)
        {
            options.Tools.Add(new FunctionTool(tool.Name, BinaryData.FromString(tool.JsonSchema), false)
            {
                FunctionDescription = tool.Description,
            });
        }

        try
        {
            ClientResult<ResponseResult> raw =
                await _client.CreateResponseAsync(options, ct).ConfigureAwait(false);
            return MapResult(raw.Value, call.JsonSchema);
        }
        catch (ClientResultException ex)
        {
            throw LlmBackendErrors.FromStatus(ex.Status, $"Responses call failed ({ex.Status}).", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new LLMException(LLMErrorKind.Network, "Responses call failed: network error.", ex, true);
        }
    }

    private static IEnumerable<ResponseItem> MapMessages(LLMMessage message)
    {
        if (message.Role == LLMRoles.Tool)
        {
            yield return ResponseItem.CreateFunctionCallOutputItem(
                message.CallId ?? throw new ArgumentException("Tool message without a call id."),
                JoinText(message));
            yield break;
        }

        if (message.Calls is { Count: > 0 })
        {
            // Chatter next to calls is dropped: the wire takes one item per call and the model already said its piece when it proposed them.
            foreach (LLMToolCall call in message.Calls)
            {
                yield return ResponseItem.CreateFunctionCallItem(
                    call.Id, call.Name, BinaryData.FromString(call.JsonArgs));
            }

            yield break;
        }

        if (message.Role is LLMRoles.System)
        {
            yield return ResponseItem.CreateSystemMessageItem(JoinText(message));
            yield break;
        }

        var parts = new List<ResponseContentPart>();
        foreach (LLMPart part in message.Parts)
        {
            switch (part)
            {
                case LLMTextPart text:
                    parts.Add(ResponseContentPart.CreateInputTextPart(text.Text));
                    break;
                case LLMImagePart image:
                    parts.Add(ResponseContentPart.CreateInputImagePart(BinaryData.FromBytes(image.Data), null));
                    break;
            }
        }

        yield return ResponseItem.CreateUserMessageItem(parts);
    }

    private static string JoinText(LLMMessage message)
        => string.Join("\n", message.Parts.OfType<LLMTextPart>().Select(p => p.Text));

    private static LlmBackendResult MapResult(ResponseResult result, string? schema)
    {
        var calls = new List<LLMToolCall>();
        foreach (ResponseItem item in result.OutputItems)
        {
            if (item is FunctionCallResponseItem function)
                calls.Add(new LLMToolCall(function.CallId, function.FunctionName, function.FunctionArguments.ToString()));
        }

        string text;
        try
        {
            text = result.GetOutputText() ?? string.Empty;
        }
        catch (Exception)
        {
            // A calls-only response carries no text; the loop feeds the calls back.
            text = string.Empty;
        }

        string? json = null;
        if (!string.IsNullOrWhiteSpace(schema))
        {
            string candidate = text.Trim();
            if (candidate.StartsWith('{'))
            {
                json = candidate;
                text = string.Empty;
            }
        }

        return new LlmBackendResult(
            text,
            json,
            calls,
            result.Usage?.InputTokenCount ?? 0,
            result.Usage?.OutputTokenCount ?? 0,
            result.Id,
            result.Status?.ToString());
    }
}

/// <summary>Chat Completions backend over the official SDK, for servers without Responses.</summary>
public sealed class OpenAIChatBackend : ILlmBackend
{
    private readonly ChatClient _client;

    public OpenAIChatBackend(string endpoint, string apiKey, string model)
    {
        var options = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
        _client = new ChatClient(model, new ApiKeyCredential(apiKey), options);
    }

    public async Task<LlmBackendResult> CompleteAsync(LlmBackendCall call, CancellationToken ct)
    {
        var messages = call.Messages.Select(MapMessage).ToList();
        var options = new ChatCompletionOptions
        {
            Temperature = call.Temperature.HasValue ? (float)call.Temperature.Value : null,
            TopP = call.TopP.HasValue ? (float)call.TopP.Value : null,
            MaxOutputTokenCount = call.MaxTokens,
            ReasoningEffortLevel = call.Reasoning.HasValue
                ? LlmReasoningMapper.ToChat(call.Reasoning.Value)
                : null,
        };
        if (!string.IsNullOrWhiteSpace(call.JsonSchema))
        {
            options.ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                "answer", BinaryData.FromString(call.JsonSchema), null, false);
        }

        foreach (LlmBackendTool tool in call.Tools)
        {
            options.Tools.Add(ChatTool.CreateFunctionTool(
                tool.Name, tool.Description, BinaryData.FromString(tool.JsonSchema), false));
        }

        try
        {
            ClientResult<ChatCompletion> raw =
                await _client.CompleteChatAsync(messages, options, ct).ConfigureAwait(false);
            return MapResult(raw.Value, call.JsonSchema);
        }
        catch (ClientResultException ex)
        {
            throw LlmBackendErrors.FromStatus(ex.Status, $"Chat call failed ({ex.Status}).", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new LLMException(LLMErrorKind.Network, "Chat call failed: network error.", ex, true);
        }
    }

    private static ChatMessage MapMessage(LLMMessage message)
    {
        switch (message.Role)
        {
            case LLMRoles.System:
                return new SystemChatMessage(JoinText(message));
            case LLMRoles.Tool:
                return new ToolChatMessage(
                    message.CallId ?? throw new ArgumentException("Tool message without a call id."),
                    JoinText(message));
            case LLMRoles.Assistant when message.Calls is { Count: > 0 }:
                var assistant = new AssistantChatMessage(JoinText(message));
                foreach (LLMToolCall call in message.Calls)
                {
                    assistant.ToolCalls.Add(
                        ChatToolCall.CreateFunctionToolCall(call.Id, call.Name, BinaryData.FromString(call.JsonArgs)));
                }

                return assistant;
            case LLMRoles.Assistant:
                return new AssistantChatMessage(JoinText(message));
            default:
                var parts = new List<ChatMessageContentPart>();
                foreach (LLMPart part in message.Parts)
                {
                    switch (part)
                    {
                        case LLMTextPart text:
                            parts.Add(ChatMessageContentPart.CreateTextPart(text.Text));
                            break;
                        case LLMImagePart image:
                            parts.Add(ChatMessageContentPart.CreateImagePart(
                                BinaryData.FromBytes(image.Data), image.MimeType, null));
                            break;
                    }
                }

                return new UserChatMessage(parts);
        }
    }

    private static string JoinText(LLMMessage message)
        => string.Join("\n", message.Parts.OfType<LLMTextPart>().Select(p => p.Text));

    private static LlmBackendResult MapResult(ChatCompletion completion, string? schema)
    {
        var calls = completion.ToolCalls
            .Select(c => new LLMToolCall(c.Id, c.FunctionName, c.FunctionArguments.ToString()))
            .ToList();
        string text = completion.Content.Count > 0 ? completion.Content[0].Text ?? string.Empty : string.Empty;
        string? json = null;
        if (!string.IsNullOrWhiteSpace(schema))
        {
            string candidate = text.Trim();
            if (candidate.StartsWith('{'))
            {
                json = candidate;
                text = string.Empty;
            }
        }

        return new LlmBackendResult(
            text,
            json,
            calls,
            completion.Usage?.InputTokenCount ?? 0,
            completion.Usage?.OutputTokenCount ?? 0,
            completion.Id,
            completion.FinishReason.ToString());
    }
}
