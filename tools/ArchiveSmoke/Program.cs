using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DMCBK.Testing;
using Microsoft.Extensions.Logging;

using ILoggerFactory loggers = LoggerFactory.Create(builder => builder.AddProvider(new DiagnosticLoggerProvider()));
await using PluginTestHost host = PluginTestHost.Create(new PluginTestHostOptions { LoggerFactory = loggers });
host.Client.Application = host.Client.Application with { Capabilities = new HashSet<string>(["aspnetcore"]) };
string[] archives = Directory.GetFiles(args[0], "*.zip", SearchOption.AllDirectories);
if (archives.Length != 25) throw new InvalidDataException($"Expected 25 archives, found {archives.Length}.");
foreach (string path in archives)
{
    string package = Path.Combine(host.PluginsRoot, "archive-check", Path.GetFileNameWithoutExtension(path));
    ZipFile.ExtractToDirectory(path, package);
    host.AddPluginFolder(package);
}

int port;
using (var listener = new TcpListener(IPAddress.Loopback, 0))
{
    listener.Start();
    port = ((IPEndPoint)listener.LocalEndpoint).Port;
}
string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
Environment.SetEnvironmentVariable("DMCBK_ARCHIVE_SMOKE_TOKEN", token);
string settings = Path.Combine(host.PluginsRoot, "userdata", "mcp-server", "settings.toml");
Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
File.WriteAllText(settings, $"Enabled = true\nBindHost = \"127.0.0.1\"\nPort = {port}\nRequireAuthToken = true\nAuthTokenEnvVar = \"DMCBK_ARCHIVE_SMOKE_TOKEN\"\n");

var result = await host.LoadAsync();
if (!result.Success) throw new InvalidOperationException("Archive plugin loading failed.");
var plugins = host.Plugins.List();
foreach (var plugin in plugins)
{
    Console.WriteLine($"{plugin.Id}: loaded={plugin.Loaded}");
    if (!plugin.Loaded) Console.WriteLine(JsonSerializer.Serialize(plugin));
}
if (plugins.Count != 25) throw new InvalidOperationException($"Expected 25 loaded plugins, found {plugins.Count}.");
if (plugins.Any(plugin => !plugin.Loaded)) throw new InvalidOperationException("Some plugins failed to load.");

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
string endpoint = $"http://127.0.0.1:{port}/mcp";
using (HttpResponseMessage denied = await http.GetAsync(endpoint))
    if (denied.StatusCode != HttpStatusCode.Unauthorized)
        throw new InvalidOperationException($"Unauthenticated MCP request returned {denied.StatusCode}.");
using var initialize = new HttpRequestMessage(HttpMethod.Post, endpoint);
initialize.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
initialize.Headers.Accept.ParseAdd("application/json, text/event-stream");
initialize.Content = new StringContent("""
    {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"archive-smoke","version":"1.0.0"}}}
    """, Encoding.UTF8, "application/json");
using HttpResponseMessage response = await http.SendAsync(initialize);
response.EnsureSuccessStatusCode();
string body = await response.Content.ReadAsStringAsync();
if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
    body = body.Split('\n').First(line => line.StartsWith("data:", StringComparison.Ordinal))[5..].Trim();
using JsonDocument reply = JsonDocument.Parse(body);
if (!reply.RootElement.TryGetProperty("result", out JsonElement initialized)
    || !initialized.TryGetProperty("serverInfo", out _))
    throw new InvalidOperationException("MCP initialization did not return server information.");
Console.WriteLine("PASS: MCP listener rejected unauthenticated access and completed authenticated initialization.");

await host.RunSessionAsync(_ => Task.CompletedTask);
Console.WriteLine("PASS: all 25 generated archives loaded, activated and handled a scripted session.");

sealed class DiagnosticLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new DiagnosticLogger(categoryName);
    public void Dispose() { }
}

sealed class DiagnosticLogger(string category) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(level)) Console.WriteLine($"{category}: {formatter(state, exception)} {exception}");
    }
}
