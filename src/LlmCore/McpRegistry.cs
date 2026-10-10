using System.Text;
using System.Text.Json;
using LlmCore.Api;

namespace DMCBK.OfficialPlugins.LlmCore;

/// <summary>One remote tool advertised by an MCP server.</summary>
public sealed record LlmMcpTool(string Server, string Name, string Description, string JsonSchema);

/// <summary>
/// Minimal JSON-RPC client over HTTP POST.
/// Responses that arrive as server-sent events are parsed from the data lines, which covers streamable HTTP servers without a separate transport.
/// </summary>
public sealed class McpJsonRpcClient : IDisposable
{
    private readonly HttpClient _client;
    private readonly string _url;
    private bool _disposed;

    public McpJsonRpcClient(HttpClient client, string url)
    {
        _client = client;
        _url = url.TrimEnd('/');
    }

    public async Task<IReadOnlyList<LlmMcpTool>> ListToolsAsync(string server, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        using JsonDocument answer = await CallAsync("tools/list", new { }, ct).ConfigureAwait(false);
        var tools = new List<LlmMcpTool>();
        if (answer.RootElement.TryGetProperty("result", out JsonElement result)
            && result.TryGetProperty("tools", out JsonElement list))
        {
            foreach (JsonElement entry in list.EnumerateArray())
            {
                string name = entry.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty;
                if (name.Length == 0)
                    continue;

                string description = entry.TryGetProperty("description", out JsonElement d) ? d.GetString() ?? string.Empty : string.Empty;
                string schema = entry.TryGetProperty("inputSchema", out JsonElement s)
                    ? s.GetRawText()
                    : """{"type":"object","properties":{}}""";
                tools.Add(new LlmMcpTool(server, name, description, schema));
            }
        }

        return tools;
    }

    public async Task<string> CallToolAsync(string name, string jsonArgs, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        JsonDocument args;
        try
        {
            args = JsonDocument.Parse(string.IsNullOrWhiteSpace(jsonArgs) ? "{}" : jsonArgs);
        }
        catch (JsonException)
        {
            args = JsonDocument.Parse("{}");
        }

        using (args)
        {
            using JsonDocument answer = await CallAsync(
                "tools/call", new { name, arguments = args.RootElement.Clone() }, ct).ConfigureAwait(false);
            if (!answer.RootElement.TryGetProperty("result", out JsonElement result))
            {
                string error = answer.RootElement.TryGetProperty("error", out JsonElement e) ? e.GetRawText() : "unknown";
                return LLMJson.Encode(new { error });
            }

            if (result.TryGetProperty("content", out JsonElement content))
            {
                var texts = new List<string>();
                foreach (JsonElement block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("text", out JsonElement text) && text.GetString() is { } s)
                        texts.Add(s);
                }

                if (texts.Count > 0)
                    return string.Join("\n", texts);
            }

            return result.GetRawText();
        }
    }

    private bool _initialized;

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
            return;

        using JsonDocument _ = await CallAsync("initialize", new
        {
            protocolVersion = "2025-03-26",
            capabilities = new { },
            clientInfo = new { name = "llm-core", version = "0.1.0" },
        }, ct).ConfigureAwait(false);
        _initialized = true;
    }

    private async Task<JsonDocument> CallAsync(string method, object parameters, CancellationToken ct)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(McpJsonRpcClient));

        string payload = LLMJson.Encode(new
        {
            jsonrpc = "2.0",
            id = Guid.NewGuid().ToString("N"),
            method,
            @params = parameters,
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new LLMException(LLMErrorKind.Network, $"MCP call '{method}' failed: network error.", ex, true);
        }

        if (!response.IsSuccessStatusCode)
            throw new LLMException(LLMErrorKind.Network, $"MCP call '{method}' failed ({(int)response.StatusCode}).", false);

        try
        {
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            string? media = response.Content.Headers.ContentType?.MediaType;
            if (media is not null && media.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
                body = UnwrapEvents(body);

            return JsonDocument.Parse(body);
        }
        catch (LLMException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new LLMException(LLMErrorKind.Network, $"MCP call '{method}' answered garbage.", ex);
        }
    }

    private static string UnwrapEvents(string body)
    {
        var data = new StringBuilder();
        foreach (string line in body.Split('\n'))
        {
            string clean = line.Trim();
            if (clean.StartsWith("data:", StringComparison.Ordinal))
            {
                string chunk = clean["data:".Length..].Trim();
                if (chunk is not ("[DONE]" or ""))
                    data.Append(chunk);
            }
        }

        return data.Length > 0 ? data.ToString() : body;
    }

    public void Dispose() => _disposed = true;
}

/// <summary>
/// MCP servers from settings plus API registrations.
/// Connections are lazy: definitions register at activate, the JSON-RPC handshake runs on first tool use or an explicit check.
/// </summary>
public sealed class McpRegistry : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _servers = new(StringComparer.Ordinal);
    private readonly Func<HttpClient> _clients;
    private bool _disposed;

    public McpRegistry(Func<HttpClient> clients)
    {
        _clients = clients;
    }

    public void SeedCentral(IEnumerable<LlmMcpRow> rows)
    {
        lock (_gate)
        {
            foreach (string name in _servers.Where(p => p.Value.Central).Select(p => p.Key).ToList())
            {
                _servers[name].Dispose();
                _servers.Remove(name);
            }

            foreach (LlmMcpRow row in rows)
            {
                if (row.Name.Length == 0 || row.Url.Length == 0)
                    continue;

                _servers[row.Name] = new Entry(
                    new LLMMcpServerDefinition(
                        row.Name,
                        row.Transport == "sse" ? LLMMcpTransport.Sse : LLMMcpTransport.Http,
                        row.Url,
                        row.Tools),
                    "central",
                    true,
                    _clients);
            }
        }
    }

    public IDisposable Register(LLMMcpServerDefinition server, string owner)
    {
        if (!ToolRegistry.IsValidName(server.Name))
            throw new ArgumentException($"MCP server name '{server.Name}' is not valid.", nameof(server));

        lock (_gate)
        {
            if (_servers.TryGetValue(server.Name, out Entry? existing))
            {
                throw new InvalidOperationException(
                    $"MCP server '{server.Name}' is already registered by '{existing.Owner}'.");
            }

            var entry = new Entry(server, owner, false, _clients);
            _servers[server.Name] = entry;
            return new Handle(() =>
            {
                lock (_gate)
                {
                    if (_servers.TryGetValue(server.Name, out Entry? current) && ReferenceEquals(current, entry))
                    {
                        current.Dispose();
                        _servers.Remove(server.Name);
                    }
                }
            });
        }
    }

    public bool RemoveCentral(string name)
    {
        lock (_gate)
        {
            if (_servers.TryGetValue(name, out Entry? entry) && entry.Central)
            {
                entry.Dispose();
                _servers.Remove(name);
                return true;
            }

            return false;
        }
    }

    public bool IsCentral(string name)
    {
        lock (_gate)
        {
            return _servers.TryGetValue(name, out Entry? entry) && entry.Central;
        }
    }

    public bool TryOwnerOf(string name, out string owner)
    {
        lock (_gate)
        {
            if (_servers.TryGetValue(name, out Entry? entry))
            {
                owner = entry.Owner;
                return true;
            }

            owner = string.Empty;
            return false;
        }
    }

    public IReadOnlyList<LLMMcpServerStatus> List()
    {
        lock (_gate)
        {
            return _servers.Values
                .OrderBy(e => e.Definition.Name, StringComparer.Ordinal)
                .Select(e => new LLMMcpServerStatus(e.Definition.Name, e.Connected, e.LastReason))
                .ToList();
        }
    }

    public async Task<IReadOnlyList<LlmMcpTool>> ToolsForServerAsync(string name, CancellationToken ct)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_servers.TryGetValue(name, out entry!))
                throw new ArgumentException($"No MCP server named '{name}'.");
        }

        return await entry.ToolsAsync(ct).ConfigureAwait(false);
    }

    public async Task<string> CallToolAsync(string server, string tool, string jsonArgs, CancellationToken ct)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_servers.TryGetValue(server, out entry!))
                throw new ArgumentException($"No MCP server named '{server}'.");
        }

        return await entry.CallAsync(tool, jsonArgs, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            foreach (Entry entry in _servers.Values)
                entry.Dispose();

            _servers.Clear();
        }
    }

    private sealed class Entry : IDisposable
    {
        private readonly Func<HttpClient> _clients;
        private McpJsonRpcClient? _client;
        private bool _disposed;

        public Entry(LLMMcpServerDefinition definition, string owner, bool central, Func<HttpClient> clients)
        {
            Definition = definition;
            Owner = owner;
            Central = central;
            _clients = clients;
            LastReason = central ? "central, not checked yet" : $"registered by {owner}, not checked yet";
        }

        public LLMMcpServerDefinition Definition { get; }
        public string Owner { get; }
        public bool Central { get; }
        public bool Connected { get; private set; }
        public string LastReason { get; private set; }

        public async Task<IReadOnlyList<LlmMcpTool>> ToolsAsync(CancellationToken ct)
        {
            McpJsonRpcClient client = Client();
            try
            {
                IReadOnlyList<LlmMcpTool> all = await client.ListToolsAsync(Definition.Name, ct).ConfigureAwait(false);
                var allowed = new HashSet<string>(Definition.Tools, StringComparer.Ordinal);
                var filtered = all.Where(t => allowed.Contains(t.Name)).ToList();
                Connected = true;
                LastReason = $"{filtered.Count} tools";
                return filtered;
            }
            catch (LLMException ex)
            {
                Connected = false;
                LastReason = ex.Message;
                throw;
            }
        }

        public async Task<string> CallAsync(string tool, string jsonArgs, CancellationToken ct)
        {
            McpJsonRpcClient client = Client();
            try
            {
                string result = await client.CallToolAsync(tool, jsonArgs, ct).ConfigureAwait(false);
                Connected = true;
                LastReason = "ok";
                return result;
            }
            catch (LLMException ex)
            {
                Connected = false;
                LastReason = ex.Message;
                throw;
            }
        }

        private McpJsonRpcClient Client()
        {
            lock (this)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(McpJsonRpcClient));

                _client ??= new McpJsonRpcClient(_clients(), Definition.Url);
                return _client;
            }
        }

        public void Dispose()
        {
            lock (this)
            {
                _disposed = true;
                _client?.Dispose();
                _client = null;
            }
        }
    }

    private sealed class Handle(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
