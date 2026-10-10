using LlmCore.Api;

namespace DMCBK.OfficialPlugins.LlmCore;

/// <summary>A provider in force: settings row plus optional API override.</summary>
public sealed record ResolvedProvider(
    string Name,
    string Endpoint,
    LLMWireFormat Api,
    bool Enabled,
    string? ApiKeyEnv);

/// <summary>
/// Central settings providers plus API registrations.
/// One name exists once across both doors; collisions fail loudly naming both owners instead of shadowing silently.
/// </summary>
public sealed class ProviderRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ResolvedProvider> _central = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (ResolvedProvider Provider, string Owner)> _api = new(StringComparer.OrdinalIgnoreCase);

    public void SeedCentral(IEnumerable<LlmProviderRow> rows)
    {
        lock (_gate)
        {
            _central.Clear();
            foreach (LlmProviderRow row in rows)
            {
                if (row.Name.Length == 0 || row.Endpoint.Length == 0)
                    continue;

                _central[row.Name] = new ResolvedProvider(
                    row.Name,
                    row.Endpoint,
                    row.Api == "chat" ? LLMWireFormat.ChatCompletions : LLMWireFormat.Responses,
                    row.Enabled,
                    row.ApiKeyEnv);
            }
        }
    }

    public IDisposable Register(ResolvedProvider provider, string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider.Name);
        lock (_gate)
        {
            if (_central.ContainsKey(provider.Name))
            {
                throw new InvalidOperationException(
                    $"Provider '{provider.Name}' is already configured centrally; '{owner}' cannot shadow it.");
            }

            if (_api.TryGetValue(provider.Name, out var existing))
            {
                throw new InvalidOperationException(
                    $"Provider '{provider.Name}' is already registered by '{existing.Owner}'; '{owner}' collides.");
            }

            _api[provider.Name] = (provider, owner);
            return new Handle(() =>
            {
                lock (_gate)
                {
                    if (_api.TryGetValue(provider.Name, out var current) && current.Owner == owner)
                        _api.Remove(provider.Name);
                }
            });
        }
    }

    /// <summary>Reachability is probed on demand by commands, never during listing.</summary>
    public IReadOnlyList<LLMProviderStatus> List()
    {
        lock (_gate)
        {
            return AllLocked().Select(p => new LLMProviderStatus(
                p.Name, p.Enabled, false, p.Enabled ? "not probed" : "disabled")).ToList();
        }
    }

    public bool TryResolve(string name, out ResolvedProvider provider)
    {
        lock (_gate)
        {
            if (_api.TryGetValue(name, out var api))
            {
                provider = api.Provider;
                return true;
            }

            return _central.TryGetValue(name, out provider!);
        }
    }

    public bool TryApiOwner(string name, out string owner)
    {
        lock (_gate)
        {
            if (_api.TryGetValue(name, out var api))
            {
                owner = api.Owner;
                return true;
            }

            owner = string.Empty;
            return false;
        }
    }

    public bool SetEnabled(string name, bool enabled)
    {
        lock (_gate)
        {
            if (_central.TryGetValue(name, out ResolvedProvider? central))
            {
                _central[name] = central with { Enabled = enabled };
                return true;
            }

            return false;
        }
    }

    public bool RemoveCentral(string name)
    {
        lock (_gate)
            return _central.Remove(name);
    }

    public void UpsertCentral(ResolvedProvider provider)
    {
        lock (_gate)
            _central[provider.Name] = provider;
    }

    public IReadOnlyList<ResolvedProvider> Snapshot()
    {
        lock (_gate)
            return AllLocked().ToList();
    }

    private IEnumerable<ResolvedProvider> AllLocked()
    {
        foreach (var pair in _central.Values)
            yield return pair;

        foreach (var pair in _api.Values)
            yield return pair.Provider;
    }

    private sealed class Handle(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
