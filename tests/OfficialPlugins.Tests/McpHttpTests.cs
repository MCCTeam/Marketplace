using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DMCBK.Testing;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The full HTTP path, in-process: the shipped plugin folder loads with the endpoint on, a real session runs against the fake server, and Streamable-HTTP JSON-RPC round-trips initialize, tools/list and two tool calls.
/// No live Minecraft server needed.
/// </summary>
public sealed class McpHttpTests
{
    private const int Port = 34567;

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src"))
                && File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root above " + AppContext.BaseDirectory);
    }

    private static async Task<JsonDocument> RpcAsync(
        HttpClient http, string url, object payload, CancellationToken ct, string? sessionId = null)
    {
        string body = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (sessionId is not null)
            request.Headers.Add("Mcp-Session-Id", sessionId);

        using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.Headers.TryGetValues("Mcp-Session-Id", out IEnumerable<string>? ids))
            LastSessionId = ids.FirstOrDefault() ?? LastSessionId;

        string text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"RPC failed: {(int)response.StatusCode} {text}");

        // Streamable HTTP may frame the JSON-RPC reply as an SSE data line; unwrap it.
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("data:", StringComparison.Ordinal))
            {
                string data = trimmed["data:".Length..].Trim();
                if (data.Length > 0 && !string.Equals(data, "[DONE]", StringComparison.Ordinal))
                    return JsonDocument.Parse(data);
            }
        }

        return JsonDocument.Parse(text);
    }

    private static string? LastSessionId;

    [Fact]
    public async Task Endpoint_ServesToolsAndSessionStatus()
    {
        await using PluginTestHost host = PluginTestHost.Create();
        string target = OfficialPluginFixture.Add(host, Path.Combine(RepositoryRoot(), "src", "McpServer"));
        File.WriteAllText(
            Path.Combine(target, "settings.toml"),
            $"Enabled = true\nPort = {Port}\nRequireAuthToken = false\n");

        Assert.True((await host.LoadAsync()).Success);
        Assert.True(host.Plugins.List().Single().Loaded);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        CancellationToken ct = cts.Token;
        await host.RunSessionAsync(async probe =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            string url = $"http://127.0.0.1:{Port}/mcp";

            using (JsonDocument init = await RpcAsync(http, url, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-03-26",
                    capabilities = new { },
                    clientInfo = new { name = "mcp-http-tests", version = "1.0.0" },
                },
            }, ct).ConfigureAwait(false))
                Assert.True(init.RootElement.TryGetProperty("result", out _));

            using (JsonDocument list = await RpcAsync(http, url, new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/list",
                @params = new { },
            }, ct, LastSessionId).ConfigureAwait(false))
            {
                JsonElement tools = list.RootElement.GetProperty("result").GetProperty("tools");
                var names = tools.EnumerateArray()
                    .Select(t => t.GetProperty("name").GetString())
                    .ToHashSet(StringComparer.Ordinal);
                Assert.Equal(63, names.Count);
                Assert.Contains("mcc_session_status", names);
                Assert.Contains("mcc_server_command", names);
                Assert.Contains("mcc_agent_guidance", names);
            }

            using (JsonDocument status = await RpcAsync(http, url, new
            {
                jsonrpc = "2.0",
                id = 3,
                method = "tools/call",
                @params = new { name = "mcc_session_status", arguments = new { } },
            }, ct, LastSessionId).ConfigureAwait(false))
            {
                string text = ReadText(status);
                using JsonDocument envelope = JsonDocument.Parse(text);
                Assert.True(envelope.RootElement.GetProperty("success").GetBoolean());
                Assert.True(envelope.RootElement.GetProperty("data").GetProperty("connected").GetBoolean());
            }

            using (JsonDocument guidance = await RpcAsync(http, url, new
            {
                jsonrpc = "2.0",
                id = 4,
                method = "tools/call",
                @params = new { name = "mcc_agent_guidance", arguments = new { } },
            }, ct, LastSessionId).ConfigureAwait(false))
            {
                string text = ReadText(guidance);
                using JsonDocument envelope = JsonDocument.Parse(text);
                Assert.True(envelope.RootElement.GetProperty("success").GetBoolean());
                Assert.Equal(
                    "mcc_operator_prompt",
                    envelope.RootElement.GetProperty("data").GetProperty("promptName").GetString());
            }
        });
    }

    private static string ReadText(JsonDocument reply)
    {
        JsonElement content = reply.RootElement.GetProperty("result").GetProperty("content");
        var texts = new List<string>();
        foreach (JsonElement block in content.EnumerateArray())
        {
            if (block.TryGetProperty("text", out JsonElement text)
                && text.GetString() is { } s)
                texts.Add(s);
        }

        Assert.NotEmpty(texts);
        return string.Join("\n", texts);
    }
}
