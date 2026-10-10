using System.Text.RegularExpressions;

namespace DMCBK.OfficialPlugins.LlmCore;

/// <summary>Strips secrets from anything about to be logged or shown. Pure and unit tested.</summary>
public static partial class LlmRedactor
{
    [GeneratedRegex(@"sk-[A-Za-z0-9-_]{8,}", RegexOptions.Compiled)]
    private static partial Regex ApiKeyPattern();

    [GeneratedRegex(@"(?i)(bearer\s+)[A-Za-z0-9\-._~+/=]{8,}", RegexOptions.Compiled)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"([?&](?:key|api_key|apikey|token|access_token)=)[^&\s""]*", RegexOptions.Compiled)]
    private static partial Regex QuerySecretPattern();

    public static string Redact(string? text, IEnumerable<string?>? secrets = null)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        string result = QuerySecretPattern().Replace(text, "$1<redacted>");
        result = BearerPattern().Replace(result, "$1<redacted>");
        result = ApiKeyPattern().Replace(result, "<redacted>");

        if (secrets is not null)
        {
            foreach (string? secret in secrets)
            {
                if (!string.IsNullOrEmpty(secret) && secret.Length >= 8)
                    result = result.Replace(secret, "<redacted>", StringComparison.Ordinal);
            }
        }

        return result;
    }
}
