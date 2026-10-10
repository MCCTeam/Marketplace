using System.Text.Json;
using LlmCore.Api;

namespace DMCBK.OfficialPlugins.LlmCore;

/// <summary>
/// The four read-only skill tools the core serves itself.
/// They run inside the bounded inner loop when named in AllowedTools, so callers never implement or approve them.
/// </summary>
public static class SkillBuiltins
{
    public const string List = "list_skills";
    public const string Read = "read_skill";
    public const string Search = "search_skills";
    public const string ReadFile = "read_skill_file";

    public static readonly IReadOnlyList<string> Names = [List, Read, Search, ReadFile];

    public static bool TryBuild(string name, SkillRegistry skills, out LlmBackendTool tool)
    {
        tool = name switch
        {
            List => new LlmBackendTool(List, "List registered skills as name plus description.", """{"type":"object","properties":{}}"""),
            Read => new LlmBackendTool(Read, "Read one skill body plus its reference list.", Schema(("name", "skill name"))),
            Search => new LlmBackendTool(Search, "Search skill names, descriptions, and bodies.", Schema(("query", "search text"))),
            ReadFile => new LlmBackendTool(ReadFile, "Read one skill reference file as text.", """{"type":"object","properties":{"skill":{"type":"string"},"path":{"type":"string"}},"required":["skill","path"]}"""),
            _ => null!,
        };
        return tool is not null;
    }

    public static async Task<string> ExecuteAsync(
        string name, string jsonArgs, SkillRegistry skills, CancellationToken ct)
    {
        using JsonDocument args = Parse(jsonArgs);
        JsonElement root = args.RootElement;
        try
        {
            return name switch
            {
                List => LLMJson.Encode(skills.List().Select(s => new
                {
                    s.Name,
                    s.Description,
                    s.Owner,
                    s.Version,
                    Keywords = s.Keywords ?? [],
                })),
                Read => ReadSkill(skills, Field(root, "name")),
                Search => LLMJson.Encode(skills.Search(Field(root, "query")).Select(s => new
                {
                    s.Name,
                    s.Description,
                    Snippet = Snippet(s.SkillFile),
                })),
                ReadFile => await skills.ReadFileAsync(Field(root, "skill"), Field(root, "path"), ct)
                    .ConfigureAwait(false),
                _ => LLMJson.Encode(new { error = $"unknown skill tool '{name}'" }),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return LLMJson.Encode(new { error = ex.Message });
        }
    }

    private static string ReadSkill(SkillRegistry skills, string name)
    {
        if (!skills.TryGet(name, out var record))
            return LLMJson.Encode(new { error = $"no skill named '{name}'" });

        return LLMJson.Encode(new
        {
            record.Name,
            record.Description,
            record.Owner,
            record.Version,
            Keywords = record.Keywords,
            References = record.References,
            Body = record.SkillFile,
        });
    }

    private static string Schema(params (string Name, string Description)[] fields)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();
        foreach ((string field, string description) in fields)
        {
            properties[field] = new { type = "string", description };
            required.Add(field);
        }

        return LLMJson.Encode(new { type = "object", properties, required });
    }

    private static JsonDocument Parse(string jsonArgs)
    {
        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(jsonArgs) ? "{}" : jsonArgs);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}");
        }
    }

    private static string Field(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string Snippet(string body)
    {
        string clean = (body ?? string.Empty).Replace('\n', ' ').Trim();
        return clean.Length > 200 ? clean[..200] + "..." : clean;
    }
}
