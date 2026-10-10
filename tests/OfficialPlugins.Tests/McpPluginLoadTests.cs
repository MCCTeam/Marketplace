using DMCBK.Testing;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// The shipped plugin folder loads through the real host: manifest, entry dll, deps.json and the MCP payloads resolve in the collectible context, the <c>mcp</c> command registers, and with the endpoint disabled nothing binds.
/// </summary>
public sealed class McpPluginLoadTests
{
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

    [Fact]
    public async Task ShippedFolder_LoadsCommandWithoutBinding()
    {
        await using PluginTestHost host = PluginTestHost.Create();
        string target = OfficialPluginFixture.Add(host, Path.Combine(RepositoryRoot(), "src", "McpServer"));
        File.WriteAllText(Path.Combine(target, "settings.toml"), "Enabled = false\n");

        Assert.True((await host.LoadAsync()).Success);
        Assert.True(host.Plugins.List().Single().Loaded);
        Assert.Contains(host.Client.Commands.ListCommands(), c => c.Name == "mcp");
    }
}
