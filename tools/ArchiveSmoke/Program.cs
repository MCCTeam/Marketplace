using System.IO.Compression;
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
var result = await host.LoadAsync();
if (!result.Success) throw new InvalidOperationException("Archive plugin loading failed.");
var plugins = host.Plugins.List();
foreach (var plugin in plugins)
{
    Console.WriteLine($"{plugin.Id}: loaded={plugin.Loaded}");
    if (!plugin.Loaded) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(plugin));
}
if (plugins.Count != 25) throw new InvalidOperationException($"Expected 25 loaded plugins, found {plugins.Count}.");
if (plugins.Any(plugin => !plugin.Loaded)) throw new InvalidOperationException("Some plugins failed to load.");
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
