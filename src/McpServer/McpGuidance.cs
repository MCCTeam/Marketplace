using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace DMCBK.OfficialPlugins.McpServer;

/// <summary>
/// Builds the agent-guidance bundle from the embedded operator prompt: the full markdown, a content version hash, the parsed best-practice bullets and example scenarios, and the live capability snapshot.
/// The prompt markdown is the same operating contract every agent gets.
/// </summary>
public sealed class McpGuidance
{
    private const string ResourceName = "MccMcpOperatorPrompt.md";

    private readonly Lazy<GuidanceDocument> _document;

    public McpGuidance()
    {
        _document = new Lazy<GuidanceDocument>(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The MCP prompt name served by <see cref="McpPromptSet"/>.</summary>
    public string PromptName => "mcc_operator_prompt";

    /// <summary>The skill name agents pin this bundle as.</summary>
    public string SkillName => "mcc-mcp-operator";

    /// <summary>The full system prompt: preamble plus operator prompt plus capability snapshot.</summary>
    public string GetSystemPrompt(IReadOnlyDictionary<string, bool> capabilityStatus)
    {
        GuidanceDocument document = _document.Value;
        var builder = new StringBuilder();
        builder.AppendLine("You are an external agent controlling Minecraft Console Client (MCC) through its MCP server plugin.");
        builder.AppendLine("Use the following MCP Operator Prompt as your system prompt. Treat the capability snapshot as authoritative and do not invent unsupported actions.");
        builder.AppendLine();
        builder.AppendLine(document.BodyMarkdown);
        builder.AppendLine();
        builder.AppendLine("Current capability snapshot");
        foreach (KeyValuePair<string, bool> entry in capabilityStatus)
            builder.AppendLine($"- {entry.Key}: {(entry.Value ? "enabled" : "disabled")}");

        return builder.ToString().Trim();
    }

    /// <summary>The <c>mcc_agent_guidance</c> tool payload.</summary>
    public object GetToolPayload(IReadOnlyDictionary<string, bool> capabilityStatus)
    {
        GuidanceDocument document = _document.Value;
        return new
        {
            promptName = PromptName,
            promptMarkdown = document.PromptMarkdown,
            skillName = SkillName,
            guidanceVersion = document.GuidanceVersion,
            skillMarkdown = document.SkillMarkdown,
            systemPrompt = GetSystemPrompt(capabilityStatus),
            bestPractices = document.BestPractices,
            exampleScenarios = document.ExampleScenarios,
            capabilityStatus,
        };
    }

    private static GuidanceDocument Load()
    {
        Assembly assembly = typeof(McpGuidance).Assembly;
        string? resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(ResourceName, StringComparison.Ordinal));
        if (resource is null)
            throw new InvalidOperationException($"Embedded MCP operator prompt resource '{ResourceName}' was not found.");

        using Stream? stream = assembly.GetManifestResourceStream(resource);
        if (stream is null)
            throw new InvalidOperationException($"Embedded MCP operator prompt resource '{resource}' could not be opened.");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        string promptMarkdown = reader.ReadToEnd();
        string body = StripFrontmatter(promptMarkdown);
        string version;
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(promptMarkdown));
            version = Convert.ToHexString(hash)[..8].ToLowerInvariant();
        }

        return new GuidanceDocument(
            promptMarkdown,
            body,
            version,
            ExtractBullets(body, "## Best Practices"),
            ExtractScenarios(body));
    }

    private static string StripFrontmatter(string markdown)
    {
        if (!markdown.StartsWith("---"))
            return markdown.Trim();

        int end = markdown.IndexOf("\n---", StringComparison.Ordinal);
        return (end < 0 ? markdown : markdown[(end + 4)..]).Trim();
    }

    private static IReadOnlyList<string> ExtractBullets(string body, string heading)
    {
        int start = body.IndexOf(heading, StringComparison.Ordinal);
        if (start < 0)
            return [];

        int next = body.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        string section = next < 0 ? body[start..] : body[start..next];
        return section.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("- "))
            .Select(line => line[2..].Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }

    private static IReadOnlyList<object> ExtractScenarios(string body)
    {
        const string heading = "## Example Scenarios";
        int start = body.IndexOf(heading, StringComparison.Ordinal);
        if (start < 0)
            return [];

        int next = body.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        string section = next < 0 ? body[start..] : body[start..next];
        var scenarios = new List<object>();
        string? title = null;
        var guidance = new StringBuilder();
        foreach (string raw in section.Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("### "))
            {
                if (title is not null)
                {
                    scenarios.Add(new { title, guidance = guidance.ToString().Trim() });
                    guidance.Clear();
                }

                title = line[4..].Trim();
            }
            else if (title is not null)
                guidance.AppendLine(line);
        }

        if (title is not null)
            scenarios.Add(new { title, guidance = guidance.ToString().Trim() });

        return scenarios;
    }

    private sealed record GuidanceDocument(
        string PromptMarkdown,
        string BodyMarkdown,
        string GuidanceVersion,
        IReadOnlyList<string> BestPractices,
        IReadOnlyList<object> ExampleScenarios)
    {
        public string SkillMarkdown => PromptMarkdown;
    }
}
