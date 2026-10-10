using DMCBK.Core.Commands;
using DMCBK.Testing;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// Live end-to-end against a real provider.
/// Opt-in only: runs when LLM_LIVE_TEST_KEY holds a key, and does nothing otherwise.
/// Spends a few cents at most on small test prompts.
/// </summary>
public sealed class LlmCoreLiveTests
{
    [Fact]
    public async Task Live_OpenRouter_Responds_Through_Stub_Consumer()
    {
        string key = (Environment.GetEnvironmentVariable("LLM_LIVE_TEST_KEY") ?? string.Empty).Trim();
        if (key.Length == 0)
            return;

        await using PluginTestHost host = PluginTestHost.Create();
        string root = RepositoryRoot();
        OfficialPluginFixture.Add(host, Path.Combine(root, "src", "LlmCore"));
        OfficialPluginFixture.Add(host, Path.Combine(root, "src", "LlmTest"));
        await host.LoadAsync();

        await host.RunSessionAsync(async _ =>
        {
            CmdResult result = await host.Client.Commands.DispatchAsync("llmtest live");
            Assert.Equal(CmdStatus.Done, result.Status);
            Assert.True(
                await host.WaitForAsync(() => ReadLive(host) is not null),
                "The live call never wrote its result file.");
            string live = ReadLive(host)!;
            Assert.Contains("alive", live);
            Assert.DoesNotContain("ERROR", live);

            CmdResult reasoned = await host.Client.Commands.DispatchAsync(
                "llmtest reasoning openrouter/deepseek/deepseek-v4.1-flash high Why is grass green? Answer in one short sentence.");
            Assert.Equal(CmdStatus.Done, reasoned.Status);
            Assert.True(
                await host.WaitForAsync(() => ReadLog(host, "reasoning.log") is not null),
                "The reasoning call never wrote its result file.");
            string reasoning = ReadLog(host, "reasoning.log")!;
            Assert.DoesNotContain("ERROR", reasoning);
            Assert.Contains("level=High", reasoning);
            Assert.True(reasoning.Length > "level=High".Length + 20, "No answer text came back: " + reasoning);
        });
    }

    private static string? ReadLive(PluginTestHost host) => ReadLog(host, "live.log");

    private static string? ReadLog(PluginTestHost host, string file)
    {
        string path = host.DataFile("llm-test", file);
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
}
