using System.Text.RegularExpressions;

namespace DMCBK.OfficialPlugins.LlmCore;

/// <summary>Scanning SKILL.md trees and confining reads. Pure and unit tested.</summary>
public static partial class SkillScanner
{
    [GeneratedRegex(@"\[.*?\]\(([^)#?]+)\)")]
    private static partial Regex LinkPattern();

    /// <summary>One skill folder found on disk.</summary>
    public sealed record FoundSkill(
        string Name,
        string Description,
        string Directory,
        string Body,
        string? Version,
        IReadOnlyList<string> Keywords);

    /// <summary>Walks nested directories for SKILL.md files, skipping hidden folders.</summary>
    public static IReadOnlyList<FoundSkill> Scan(string root)
    {
        var found = new List<FoundSkill>();
        string basePath;
        try
        {
            basePath = Path.GetFullPath(root);
            if (!Directory.Exists(basePath))
                return found;
        }
        catch (Exception)
        {
            return found;
        }

        var queue = new Queue<string>();
        queue.Enqueue(basePath);
        while (queue.Count > 0 && found.Count < SkillRegistry.MaxSkillsPerScan)
        {
            string directory;
            try
            {
                directory = queue.Dequeue();
            }
            catch (Exception)
            {
                break;
            }

            string candidate = Path.Combine(directory, "SKILL.md");
            if (File.Exists(candidate))
            {
                try
                {
                    string body = File.ReadAllText(candidate);
                    if (body.Length <= SkillRegistry.MaxSkillFileBytes)
                    {
                        var (name, description, version, keywords) = ParseFrontmatter(body, Path.GetFileName(directory));
                        found.Add(new FoundSkill(name, description, directory, body, version, keywords));
                    }
                }
                catch (Exception)
                {
                }
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (string child in children)
            {
                string leaf = Path.GetFileName(child);
                if (!leaf.StartsWith('.') && !leaf.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                    queue.Enqueue(child);
            }
        }

        return found;
    }

    /// <summary>Links plus an optional frontmatter references list, as declared (not yet confined).</summary>
    public static IReadOnlyList<string> FindReferences(string? body)
    {
        string text = body ?? string.Empty;
        var refs = new List<string>();
        foreach (Match match in LinkPattern().Matches(text))
        {
            string target = match.Groups[1].Value.Trim();
            if (target.Length > 0 && !target.Contains("://") && !target.StartsWith('#'))
                refs.Add(target);
        }

        foreach (string line in FrontmatterList(text, "references"))
        {
            if (!refs.Contains(line, StringComparer.Ordinal))
                refs.Add(line);
        }

        return refs;
    }

    /// <summary>Resolves a skill-relative path under its root. Throws on any escape attempt.</summary>
    public static string Confine(string root, string relative)
    {
        string basePath = Path.GetFullPath(root);
        string full = Path.GetFullPath(Path.Combine(basePath, (relative ?? string.Empty).Trim()));
        if (!full.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !full.Equals(basePath, StringComparison.Ordinal))
            throw new UnauthorizedAccessException($"Skill path '{relative}' escapes its folder.");

        string target = ResolveLinks(full);
        if (!target.StartsWith(basePath + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !target.Equals(basePath, StringComparison.Ordinal))
            throw new UnauthorizedAccessException($"Skill path '{relative}' escapes its folder through a link.");

        return target;
    }

    private static string ResolveLinks(string path)
    {
        try
        {
            var current = new FileInfo(path);
            for (int depth = 0; depth < 8 && current.LinkTarget is { } link; depth++)
            {
                string next = Path.IsPathRooted(link)
                    ? link
                    : Path.Combine(current.DirectoryName ?? string.Empty, link);
                current = new FileInfo(Path.GetFullPath(next));
            }

            return current.FullName;
        }
        catch (Exception)
        {
            return Path.GetFullPath(path);
        }
    }

    private static (string Name, string Description, string? Version, IReadOnlyList<string> Keywords) ParseFrontmatter(
        string body, string fallbackName)
    {
        string name = fallbackName;
        string description = string.Empty;
        string? version = null;
        var keywords = new List<string>();

        string text = body ?? string.Empty;
        if (!text.StartsWith("---"))
        {
            description = FirstLine(text);
            return (Slug(name), description, version, keywords);
        }

        int end = text.IndexOf("\n---", StringComparison.Ordinal);
        if (end < 0)
            return (Slug(name), string.Empty, version, keywords);

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lists = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        foreach (string raw in text[3..end].Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("- ", StringComparison.Ordinal) && current is not null)
            {
                string item = line[2..].Trim().Trim('"', '\'');
                if (item.Length > 0)
                {
                    if (!lists.TryGetValue(current, out List<string>? items))
                    {
                        items = [];
                        lists[current] = items;
                    }

                    items.Add(item);
                }

                continue;
            }

            current = null;
            int colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim().Trim('"', '\'');
            if (value.Length == 0)
                current = key;
            else
                fields[key] = value;
        }

        if (fields.TryGetValue("name", out string? n) && n.Length > 0)
            name = n;

        if (fields.TryGetValue("description", out string? d))
            description = d;

        if (fields.TryGetValue("version", out string? v) && v.Length > 0)
            version = v;

        if (lists.TryGetValue("keywords", out List<string>? words))
            keywords.AddRange(words);

        if (description.Length == 0)
            description = FirstLine(text[(end + 4)..]);

        return (Slug(name), description, version, keywords);
    }

    private static IEnumerable<string> FrontmatterList(string body, string key)
    {
        string text = body ?? string.Empty;
        if (!text.StartsWith("---"))
            yield break;

        int end = text.IndexOf("\n---", StringComparison.Ordinal);
        if (end < 0)
            yield break;

        bool inside = false;
        foreach (string raw in text[3..end].Split('\n'))
        {
            string line = raw.Trim();
            if (inside)
            {
                if (line.StartsWith("- ", StringComparison.Ordinal))
                {
                    string item = line[2..].Trim().Trim('"', '\'');
                    if (item.Length > 0)
                        yield return item;

                    continue;
                }

                inside = false;
            }

            if (line.Equals(key + ":", StringComparison.OrdinalIgnoreCase))
                inside = true;
        }
    }

    private static string FirstLine(string text)
    {
        foreach (string line in (text ?? string.Empty).Split('\n'))
        {
            string clean = line.Trim().TrimStart('#').Trim();
            if (clean.Length > 0)
                return clean.Length > 160 ? clean[..160] : clean;
        }

        return string.Empty;
    }

    private static string Slug(string name)
    {
        string slug = new string((name ?? string.Empty).ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);

        return slug.Length == 0 ? "skill" : slug;
    }
}
