namespace LlmCore.Api;

/// <summary>
/// Dependency-free token estimate for pre-flight checks.
/// Character based, deliberately rough: it only decides whether to compact, split, or refuse before spending, never for billing.
/// </summary>
public static class LLMTokenEstimator
{
    private const int CharsPerToken = 4;

    public static int EstimateMessages(IEnumerable<LLMMessage> messages)
    {
        int chars = 0;
        foreach (LLMMessage message in messages)
        {
            chars += message.Role.Length + 8;
            foreach (LLMPart part in message.Parts)
            {
                chars += part switch
                {
                    LLMTextPart text => text.Text.Length,
                    LLMImagePart image => 1500 + image.Data.Length / 8,
                    _ => 0,
                };
            }
        }

        return Math.Max(1, chars / CharsPerToken);
    }

    public static int EstimateText(string? text)
        => text is null ? 0 : Math.Max(0, text.Length / CharsPerToken);
}
