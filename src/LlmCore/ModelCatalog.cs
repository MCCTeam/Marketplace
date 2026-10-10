using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LlmCore.Api;

namespace DMCBK.OfficialPlugins.LlmCore;

/// <summary>
/// Model ids per provider: discovered through GET models, cached one day, manual ids overlaid.
/// Discovered and manual lists live in separate files with a checksum, so a truncated cache never poisons routing.
/// </summary>
public sealed class ModelCatalog
{
    public const string DiscoveredSource = "discovered";
    public const string ManualSource = "manual";

    private static readonly TimeSpan TimeToLive = TimeSpan.FromDays(1);

    private readonly string _directory;
    private readonly Func<HttpClient> _clients;
    private readonly Dictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public ModelCatalog(string directory, Func<HttpClient> clients)
    {
        _directory = directory;
        _clients = clients;
    }

    public async Task<IReadOnlyList<LLMModelInfo>> ListAsync(
        ResolvedProvider provider, bool refresh, CancellationToken ct)
    {
        var seen = new Dictionary<string, LLMModelInfo>(StringComparer.Ordinal);
        foreach (string id in ReadManual(provider.Name))
            seen[id] = new LLMModelInfo(id, provider.Name, ManualSource);

        if (!refresh)
        {
            foreach (string id in ReadDiscovered(provider.Name))
                seen.TryAdd(id, new LLMModelInfo(id, provider.Name, DiscoveredSource));

            return seen.Values.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
        }

        SemaphoreSlim gate = GateFor(provider.Name);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IReadOnlyList<string> fresh = await DiscoverAsync(provider, ct).ConfigureAwait(false);
            WriteDiscovered(provider.Name, fresh);
            foreach (string id in fresh)
                seen.TryAdd(id, new LLMModelInfo(id, provider.Name, DiscoveredSource));
        }
        finally
        {
            gate.Release();
        }

        return seen.Values.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
    }

    public DateTime? CacheAge(string provider)
    {
        try
        {
            string path = DiscoveredPath(provider);
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void AddManual(string provider, string modelId)
    {
        var ids = new SortedSet<string>(ReadManual(provider), StringComparer.Ordinal) { modelId };
        WriteManual(provider, ids);
    }

    public bool RemoveManual(string provider, string modelId)
    {
        var ids = new SortedSet<string>(ReadManual(provider), StringComparer.Ordinal);
        bool removed = ids.Remove(modelId);
        if (removed)
            WriteManual(provider, ids);

        return removed;
    }

    public void ClearDiscovered(string provider)
    {
        try
        {
            File.Delete(DiscoveredPath(provider));
        }
        catch (Exception)
        {
        }
    }

    public async Task<IReadOnlyList<string>> DiscoverAsync(ResolvedProvider provider, CancellationToken ct)
    {
        string key = EnvLinks.Secret(provider.ApiKeyEnv);
        using HttpClient client = _clients();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, provider.Endpoint.TrimEnd('/') + "/models");
        if (key.Length > 0)
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new LLMException(LLMErrorKind.Network, $"Model discovery failed for '{provider.Name}'.", ex, true);
        }

        if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
            throw new LLMException(LLMErrorKind.Auth, $"Model discovery refused for '{provider.Name}'.", false);

        if (!response.IsSuccessStatusCode)
        {
            throw LlmBackendErrors.FromStatus(
                (int)response.StatusCode, $"Model discovery failed for '{provider.Name}' ({(int)response.StatusCode}).",
                new HttpRequestException(response.ReasonPhrase));
        }

        try
        {
            using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
            var ids = new List<string>();
            if (document.RootElement.TryGetProperty("data", out JsonElement data))
            {
                foreach (JsonElement entry in data.EnumerateArray())
                {
                    if (entry.TryGetProperty("id", out JsonElement id)
                        && id.GetString() is { Length: > 0 } text)
                        ids.Add(text);
                }
            }

            return ids;
        }
        catch (LLMException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LLMException(LLMErrorKind.Network, $"Model discovery answered garbage for '{provider.Name}'.", ex);
        }
    }

    private IReadOnlyList<string> ReadDiscovered(string provider)
    {
        IReadOnlyList<string> ids = ReadStore(DiscoveredPath(provider));
        if (ids.Count == 0)
            return ids;

        try
        {
            DateTime written = File.GetLastWriteTimeUtc(DiscoveredPath(provider));
            return DateTime.UtcNow - written > TimeToLive ? [] : ids;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private IReadOnlyList<string> ReadManual(string provider) => ReadStore(ManualPath(provider));

    private static IReadOnlyList<string> ReadStore(string path)
    {
        try
        {
            if (!File.Exists(path))
                return [];

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("ids", out JsonElement ids))
                return [];

            string payload = string.Join("\n", ids.EnumerateArray()
                .Select(e => e.GetString() ?? string.Empty)
                .Where(s => s.Length > 0));
            string? want = document.RootElement.TryGetProperty("sha256", out JsonElement sum)
                ? sum.GetString()
                : null;
            string got = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            if (want is null || !want.Equals(got, StringComparison.OrdinalIgnoreCase))
                return [];

            return payload.Split('\n').Where(s => s.Length > 0).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private void WriteDiscovered(string provider, IEnumerable<string> ids) => WriteStore(DiscoveredPath(provider), ids);

    private void WriteManual(string provider, IEnumerable<string> ids) => WriteStore(ManualPath(provider), ids);

    private void WriteStore(string path, IEnumerable<string> ids)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _directory);
            string payload = string.Join("\n", ids.Where(s => s.Length > 0));
            string store = LLMJson.Encode(new
            {
                sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
                ids = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            });
            string temp = path + ".tmp";
            File.WriteAllText(temp, store);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception)
        {
        }
    }

    private string DiscoveredPath(string provider) => Path.Combine(_directory, "models", provider + ".discovered.json");
    private string ManualPath(string provider) => Path.Combine(_directory, "models", provider + ".manual.json");

    private SemaphoreSlim GateFor(string provider)
    {
        lock (_gate)
        {
            if (!_gates.TryGetValue(provider, out SemaphoreSlim? gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _gates[provider] = gate;
            }

            return gate;
        }
    }
}
