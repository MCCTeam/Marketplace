using System.IO.Compression;
using DMCBK.Marketplace;
using DMCBK.PluginSdk;
using DMCBK.Testing;
using Xunit;

namespace DMCBK.Core.Tests;

public sealed class ReleaseArchiveTests
{
    [Fact]
    public async Task EveryOfficialAssetCanPrepareFromItsArchiveWithNugetContracts()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Marketplace.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        await using PluginTestHost host = PluginTestHost.Create();
        host.Client.Application = host.Client.Application with { Capabilities = new HashSet<string>(["aspnetcore"]) };
        var packages = new List<PluginInstallation>();
        foreach (string file in Directory.GetFiles(Path.Combine(root.FullName, "src"), "plugin.toml", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")))
        {
            Assert.True(PluginManifest.TryParse(File.ReadAllText(file), out PluginManifest manifest, out string? error), error);
            string source = Path.GetDirectoryName(file)!;
            string payload = manifest.Kind == "source" ? source : Directory.GetFiles(Path.Combine(source, "bin"), manifest.Entry, SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName).First()!;
            string stage = Path.Combine(host.PluginsRoot, "archive-tests", manifest.Id);
            PackedPlugin packed = PluginPackageBuilder.Pack(source, payload, stage, manifest.Kind, manifest.Target,
                new Uri("https://assets.test/"), manifest.Id == "llm-test" ? ["DMCBK.OfficialPlugins.LlmCore"] : []);
            string extracted = Path.Combine(stage, "package");
            ZipFile.ExtractToDirectory(packed.ArchivePath, extracted);
            Assert.True(File.Exists(Path.Combine(extracted, "LICENSE.md")));
            packages.Add(new(packed.Manifest, extracted, Path.Combine(host.PluginsRoot, "userdata", manifest.Id), false));
        }
        Assert.Equal(25, packages.Count);
        PluginDependencyGraph.Result graph = PluginDependencyGraph.Build(packages.Select(package => package.Manifest));
        await host.Plugins.PrepareAsync(graph.Order.Select(id => packages.Single(package => package.Manifest.Id == id)).ToArray(), default);
        Assert.Empty(host.Plugins.List());
    }
}
