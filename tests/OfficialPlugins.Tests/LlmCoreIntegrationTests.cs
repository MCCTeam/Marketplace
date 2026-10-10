using System.Net;
using System.Net.Sockets;
using System.Text;
using DMCBK.Core.Commands;
using DMCBK.Testing;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// llm-core plus its LlmTest consumer, loaded for real through PluginTestHost, talking to a loopback stub that speaks just enough of the OpenAI wire format.
/// No key, no network beyond localhost.
/// </summary>
public sealed class LlmCoreIntegrationTests : IDisposable
{
    private readonly StubServer _stub = new();

    public void Dispose() => _stub.Dispose();

    [Fact]
    public async Task Ask_Streams_Structured_And_Tools_EndToEnd()
    {
        await using PluginTestHost host = PluginTestHost.Create();
        AddPlugins(host);
        await host.LoadAsync();

        await host.RunSessionAsync(async _ =>
        {
            await Dispatch(host, $"llmtest use-stub {_stub.Url}");

            await Dispatch(host, "llmtest ask stub/stub-model say hi");
            Assert.True(await host.WaitForAsync(() => Read(host, "llm-test", "ask.log") is not null));
            Assert.Contains("hello from stub", Read(host, "llm-test", "ask.log"));

            await Dispatch(host, "llmtest stream stub/stub-model say hi");
            Assert.True(await host.WaitForAsync(() => Read(host, "llm-test", "stream.log") is not null));
            Assert.Contains("hello from stub", Read(host, "llm-test", "stream.log"));

            await Dispatch(host, "llmtest structured stub/stub-model pick one");
            Assert.True(await host.WaitForAsync(() => Read(host, "llm-test", "structured.log") is not null));
            Assert.Contains("""{"answer":"yes"}""", Read(host, "llm-test", "structured.log"));

            await Dispatch(host, "llmtest tools stub/stub-model call the echo tool");
            Assert.True(await host.WaitForAsync(() => Read(host, "llm-test", "tools.log") is not null));
            Assert.Contains("tools done", Read(host, "llm-test", "tools.log"));
        });
    }

    [Fact]
    public async Task Skills_Models_And_LlmCommands_EndToEnd()
    {
        await using PluginTestHost host = PluginTestHost.Create();
        AddPlugins(host);
        await host.LoadAsync();

        await host.RunSessionAsync(async _ =>
        {
            await Dispatch(host, $"llmtest use-stub {_stub.Url}");

            await Dispatch(host, "llmtest skills");
            Assert.True(await host.WaitForAsync(() => Read(host, "llm-test", "skills.log") is not null));
            Assert.Contains("probe", Read(host, "llm-test", "skills.log"));

            await Dispatch(host, "llmtest models stub");
            Assert.True(await host.WaitForAsync(() => Read(host, "llm-test", "models.log") is not null));
            Assert.Contains("stub-model", Read(host, "llm-test", "models.log"));

            CmdResult listed = await Dispatch(host, "llm provider list");
            Assert.Contains("stub", listed.Message);

            CmdResult models = await Dispatch(host, "llm model list stub");
            Assert.Contains("stub-model", models.Message);

            await Dispatch(host, "llm use stub/stub-model");

            CmdResult status = await Dispatch(host, "llm status");
            Assert.Contains("stub/stub-model", status.Message);

            await Dispatch(host, "llm budget");

            CmdResult skills = await Dispatch(host, "llm skill list");
            Assert.Contains("probe", skills.Message);

            CmdResult mcp = await host.Client.Commands.DispatchAsync("llm mcp list");
            Assert.Equal(CmdStatus.Fail, mcp.Status);

            await Dispatch(host, "llmtest ask stub/stub-model say hi");
            Assert.True(await host.WaitForAsync(() => Read(host, "llm-test", "ask.log") is not null));

            await Dispatch(host, "llm why");
        });
    }

    private static async Task<CmdResult> Dispatch(PluginTestHost host, string command)
    {
        CmdResult result = await host.Client.Commands.DispatchAsync(command);
        Assert.True(result.Status == CmdStatus.Done, $"Command '{command}' failed: {result.Message}");
        return result;
    }

    private void AddPlugins(PluginTestHost host)
    {
        string root = RepositoryRoot();
        string core = OfficialPluginFixture.Add(host, Path.Combine(root, "src", "LlmCore"));
        File.WriteAllText(Path.Combine(core, "settings.toml"), "AllowLocalEndpoints = true\n");
        OfficialPluginFixture.Add(host, Path.Combine(root, "src", "LlmTest"));
    }

    private static string? Read(PluginTestHost host, string plugin, string file)
    {
        string path = host.DataFile(plugin, file);
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

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

    /// <summary>Just enough of the OpenAI wire format for the SDK to parse: models plus responses.</summary>
    private sealed class StubServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _loop;

        public StubServer()
        {
            int port;
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }

            Url = $"http://127.0.0.1:{port}/v1";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _loop = Task.Run(AcceptLoop);
        }

        public string Url { get; }

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Stop();
            _listener.Close();
        }

        private async Task AcceptLoop()
        {
            while (!_stopping.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(() => Answer(context));
            }
        }

        private static void Answer(HttpListenerContext context)
        {
            try
            {
                string body;
                using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                    body = reader.ReadToEnd();

                string payload = context.Request is { HttpMethod: "GET", Url: { AbsolutePath: "/v1/models" } }
                    ? """{"object":"list","data":[{"id":"stub-model"}]}"""
                    : ResponsesPayload(body);
                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch (Exception)
            {
            }
            finally
            {
                try
                {
                    context.Response.Close();
                }
                catch (Exception)
                {
                }
            }
        }

        private static string ResponsesPayload(string body)
        {
            if (body.Contains("function_call_output", StringComparison.Ordinal))
                return MessagePayload("tools done");

            if (body.Contains("test_echo", StringComparison.Ordinal))
            {
                return """
                    {"id":"resp_1","object":"response","created_at":1,"model":"stub-model","status":"completed",
                     "output":[{"type":"function_call","id":"fc_1","call_id":"call_1","status":"completed",
                     "name":"test_echo","arguments":"{\"x\":1}"}],
                     "usage":{"input_tokens":5,"output_tokens":3,"total_tokens":8}}
                    """;
            }

            if (body.Contains("\"answer\"", StringComparison.Ordinal))
                return MessagePayload("{\"answer\":\"yes\"}");

            return MessagePayload("hello from stub");
        }

        private static string MessagePayload(string text) =>
            "{\"id\":\"resp_1\",\"object\":\"response\",\"created_at\":1,\"model\":\"stub-model\",\"status\":\"completed\","
            + "\"output\":[{\"type\":\"message\",\"id\":\"m_1\",\"status\":\"completed\",\"role\":\"assistant\","
            + "\"content\":[{\"type\":\"output_text\",\"text\":" + JsonText(text) + ",\"annotations\":[]}]}],"
            + "\"usage\":{\"input_tokens\":5,\"output_tokens\":3,\"total_tokens\":8}}";

        private static string JsonText(string text)
            => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
