using System.Text.RegularExpressions;
using LlmCore.Api;

namespace DMCBK.OfficialPlugins.LlmCore;

/// <summary>One registered skill with its declared reference files.</summary>
public sealed record LlmSkillRecord(
    string Name,
    string Description,
    string? Directory,
    string SkillFile,
    string Owner,
    string? Version,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> References);

/// <summary>
/// Skill registry plus the file rules: reads canonicalize under the skill root, refuse escapes, and cap sizes.
/// Scanning and confinement are pure enough to unit test.
/// </summary>
public sealed partial class SkillRegistry
{
    public const int MaxSkillFileBytes = 256 * 1024;
    public const int MaxSkillsPerScan = 256;

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex NamePattern();

    private readonly object _gate = new();
    private readonly Dictionary<string, LlmSkillRecord> _skills = new(StringComparer.Ordinal);

    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    public IDisposable Register(string owner, LLMSkillDefinition skill)
    {
        if (!IsValidName(skill.Name))
            throw new ArgumentException($"Skill name '{skill.Name}' is not valid.", nameof(skill));

        string? directory = skill.Directory is null ? null : Path.GetFullPath(skill.Directory);
        var record = new LlmSkillRecord(
            skill.Name,
            (skill.Description ?? string.Empty).Trim(),
            directory,
            skill.SkillFile ?? string.Empty,
            owner,
            string.IsNullOrWhiteSpace(skill.Version) ? null : skill.Version.Trim(),
            (skill.Keywords ?? []).Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).ToList(),
            SkillScanner.FindReferences(skill.SkillFile ?? string.Empty));

        lock (_gate)
        {
            if (_skills.TryGetValue(record.Name, out LlmSkillRecord? existing))
            {
                throw new InvalidOperationException(
                    $"Skill '{record.Name}' is already registered by '{existing.Owner}'; '{owner}' collides.");
            }

            _skills[record.Name] = record;
            return new Handle(() =>
            {
                lock (_gate)
                {
                    if (_skills.TryGetValue(record.Name, out LlmSkillRecord? current)
                        && current.Owner == owner)
                        _skills.Remove(record.Name);
                }
            });
        }
    }

    /// <summary>Registers every nested skill folder under the path. Returns the registered names.</summary>
    public IReadOnlyList<string> RegisterDirectory(string owner, string path, out IDisposable handle)
    {
        var added = new List<string>();
        var handles = new List<IDisposable>();
        foreach (SkillScanner.FoundSkill found in SkillScanner.Scan(path))
        {
            try
            {
                handles.Add(Register(owner, new LLMSkillDefinition(
                    found.Name, found.Description, found.Directory, found.Body,
                    owner, found.Version, found.Keywords)));
                added.Add(found.Name);
            }
            catch (ArgumentException)
            {
                // A bad apple never spoils the scan.
            }
            catch (InvalidOperationException)
            {
            }
        }

        handle = new Composite(handles);
        return added;
    }

    public void EvictOwner(string owner)
    {
        lock (_gate)
        {
            foreach (string name in _skills.Where(p => p.Value.Owner == owner).Select(p => p.Key).ToList())
                _skills.Remove(name);
        }
    }

    public bool TryGet(string name, out LlmSkillRecord record)
    {
        lock (_gate)
            return _skills.TryGetValue(name, out record!);
    }

    public IReadOnlyList<LLMSkillSummary> List()
    {
        lock (_gate)
        {
            return _skills.Values
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .Select(s => new LLMSkillSummary(s.Name, s.Description, s.Owner, s.Version, s.Keywords))
                .ToList();
        }
    }

    public IReadOnlyList<LlmSkillRecord> Search(string query)
    {
        string needle = (query ?? string.Empty).Trim();
        lock (_gate)
        {
            return _skills.Values
                .Where(s => needle.Length == 0
                    || s.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || s.Description.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || s.Keywords.Any(k => k.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    || s.SkillFile.Contains(needle, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>Reads one reference file. Confined to the skill folder, fails closed outside it.</summary>
    public async Task<string> ReadFileAsync(string skill, string path, CancellationToken ct)
    {
        LlmSkillRecord record;
        lock (_gate)
        {
            if (!_skills.TryGetValue(skill, out record!))
                throw new ArgumentException($"No skill named '{skill}'.");
        }

        if (record.Directory is null)
            throw new InvalidOperationException($"Skill '{skill}' was registered as bare text and has no files.");

        string full = SkillScanner.Confine(record.Directory, path);
        var info = new FileInfo(full);
        if (info.Exists && info.Length > MaxSkillFileBytes)
            throw new InvalidOperationException($"Skill file '{path}' is over the size cap.");

        try
        {
            return await File.ReadAllTextAsync(full, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Skill file '{path}' cannot be read.", ex);
        }
    }

    private sealed class Handle(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

    private sealed class Composite(List<IDisposable> handles) : IDisposable
    {
        public void Dispose()
        {
            foreach (IDisposable handle in handles)
                handle.Dispose();
        }
    }
}
