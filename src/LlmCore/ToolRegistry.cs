using System.Text.RegularExpressions;
using LlmCore.Api;

namespace DMCBK.OfficialPlugins.LlmCore;

/// <summary>
/// Local tools from dependent plugins.
/// Handlers run behind unload-aware proxies: once the owner is evicted, calls fail fast instead of rooting a dead load context.
/// </summary>
public sealed partial class ToolRegistry
{
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex NamePattern();

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _tools = new(StringComparer.Ordinal);

    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    public IDisposable Register(string owner, LLMTool tool)
    {
        if (!IsValidName(tool.Name))
            throw new ArgumentException($"Tool name '{tool.Name}' is not valid.", nameof(tool));

        lock (_gate)
        {
            if (_tools.TryGetValue(tool.Name, out Entry? existing))
            {
                throw new InvalidOperationException(
                    $"Tool '{tool.Name}' is already registered by '{existing.Owner}'; '{owner}' collides.");
            }

            var entry = new Entry(owner, tool);
            _tools[tool.Name] = entry;
            return new Handle(() =>
            {
                lock (_gate)
                {
                    if (_tools.TryGetValue(tool.Name, out Entry? current) && ReferenceEquals(current, entry))
                    {
                        current.Dispose();
                        _tools.Remove(tool.Name);
                    }
                }
            });
        }
    }

    /// <summary>Withdraws everything one plugin registered, and poisons its handlers.</summary>
    public void EvictOwner(string owner)
    {
        lock (_gate)
        {
            foreach (var pair in _tools.Where(p => p.Value.Owner == owner).ToList())
            {
                pair.Value.Dispose();
                _tools.Remove(pair.Key);
            }
        }
    }

    public bool TryGet(string name, out Entry entry)
    {
        lock (_gate)
            return _tools.TryGetValue(name, out entry!);
    }

    public IReadOnlyList<string> Names()
    {
        lock (_gate)
            return _tools.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    public sealed class Entry(string owner, LLMTool tool) : IDisposable
    {
        private int _dead;

        public string Owner { get; } = owner;
        public LLMTool Tool { get; } = tool;

        public Task<string> InvokeAsync(string args, CancellationToken ct)
        {
            if (Volatile.Read(ref _dead) != 0)
                throw new OperationCanceledException($"Tool '{Tool.Name}' was unloaded with '{Owner}'.");

            return Tool.Handler.InvokeAsync(args, ct);
        }

        public void Dispose() => Interlocked.Exchange(ref _dead, 1);
    }

    private sealed class Handle(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
