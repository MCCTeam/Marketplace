using System.IO.Compression;
using DMCBK.Core;
using DMCBK.Marketplace;
using DMCBK.PluginSdk;
using DMCBK.Testing;

namespace DMCBK.Core.Tests;

internal static class OfficialPluginFixture
{
    internal static string Add(PluginTestHost host, string source)
    {
        if (!PluginManifest.TryParse(File.ReadAllText(Path.Combine(source, "plugin.toml")), out PluginManifest manifest, out string? error))
            throw new InvalidDataException(error);
        string stage = Path.Combine(host.PluginsRoot, "fixture-packages", manifest.Id);
        string payload = manifest.Kind == "source" ? source : Directory.EnumerateFiles(Path.Combine(source, "bin"), manifest.Entry, SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName).First()!;
        PackedPlugin package = PluginPackageBuilder.Pack(source, payload, stage, manifest.Kind, manifest.Target,
            new Uri("https://example.invalid/"), manifest.Id == "llm-test" ? ["DMCBK.OfficialPlugins.LlmCore"] : []);
        string extracted = Path.Combine(stage, manifest.Id);
        ZipFile.ExtractToDirectory(package.ArchivePath, extracted);
        host.AddPluginFolder(extracted);
        host.Client.Application = host.Client.Application with { Capabilities = new HashSet<string>(["aspnetcore"]) };
        string user = Path.Combine(host.PluginsRoot, "userdata", manifest.Id);
        Directory.CreateDirectory(user);
        return user;
    }
}
