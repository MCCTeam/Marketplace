using LlmCore.Api;
using DMCBK.OfficialPlugins.LlmCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DMCBK.Core.Tests;

/// <summary>
/// Headless coverage for llm-core: pure helpers, registries, the catalog, and the service loop against a scripted backend.
/// HTTP-level behavior against a stub server lives in LlmCoreIntegrationTests.
/// </summary>
public sealed class LlmCoreTests
{
    [Fact]
    public void EstimatorGrowsWithText_AndChargesMoreForImages()
    {
        int plain = LLMTokenEstimator.EstimateMessages(
            [new LLMMessage(LLMRoles.User, [new LLMTextPart("hello")])]);
        int longer = LLMTokenEstimator.EstimateMessages(
            [new LLMMessage(LLMRoles.User, [new LLMTextPart(new string('x', 4000))])]);
        int image = LLMTokenEstimator.EstimateMessages(
            [new LLMMessage(LLMRoles.User, [new LLMImagePart(new byte[8000], "image/png")])]);
        Assert.True(longer > plain);
        Assert.True(image > plain);
    }

    [Fact]
    public void RedactorStripsKeys_Bearers_QuerySecrets_AndConfiguredValues()
    {
        string dirty = "key=sk-abcdefgh12345678 here, Bearer abcdefgh12345678, "
            + "?api_key=hunter2 hi, custom hunter2-secret-value end";
        string clean = LlmRedactor.Redact(dirty, ["hunter2-secret-value"]);
        Assert.DoesNotContain("sk-abcdefgh12345678", clean);
        Assert.DoesNotContain("hunter2", clean);
        Assert.DoesNotContain("hunter2-secret-value", clean);
        Assert.Contains("here", clean);
    }

    [Fact]
    public void LedgerRefusesPastGlobal_AndPerPluginCaps()
    {
        var ledger = new LlmBudgetLedger();
        ledger.Configure(100, new Dictionary<string, long> { ["a"] = 40 });
        Assert.True(ledger.TryReserve("a", 30, out long first));
        ledger.Commit("a", first, 30);
        Assert.False(ledger.TryReserve("a", 20, out _));
        Assert.True(ledger.TryReserve("b", 60, out long second));
        ledger.Commit("b", second, 60);
        Assert.Equal(90, ledger.SpentToday("*"));
        Assert.Equal(30, ledger.SpentToday("a"));
    }

    [Fact]
    public void ToolRegistryRefusesDuplicates_AndEvictionPoisonsHandlers()
    {
        var registry = new ToolRegistry();
        var handler = new StubHandler("ok");
        using (registry.Register("one", new LLMTool("echo", "d", "{}", handler)))
        {
            InvalidOperationException collision = Assert.Throws<InvalidOperationException>(
                () => registry.Register("two", new LLMTool("echo", "d", "{}", handler)));
            Assert.Contains("one", collision.Message);
            Assert.Contains("two", collision.Message);
        }

        registry.Register("one", new LLMTool("echo", "d", "{}", handler));
        Assert.True(registry.TryGet("echo", out ToolRegistry.Entry entry));
        registry.EvictOwner("one");
        Assert.Throws<OperationCanceledException>(
            () => entry.InvokeAsync("{}", CancellationToken.None).GetAwaiter().GetResult());
    }

    [Fact]
    public void ProviderRegistryRefusesShadows()
    {
        var registry = new ProviderRegistry();
        registry.SeedCentral([new LlmProviderRow { Name = "main", Endpoint = "https://x/v1" }]);
        Assert.Throws<InvalidOperationException>(() => registry.Register(
            new ResolvedProvider("main", "https://y/v1", LLMWireFormat.Responses, true, null), "plugin"));
        using (registry.Register(
            new ResolvedProvider("extra", "https://y/v1", LLMWireFormat.Responses, true, null), "plugin"))
        {
            Assert.Throws<InvalidOperationException>(() => registry.Register(
                new ResolvedProvider("extra", "https://z/v1", LLMWireFormat.Responses, true, null), "other"));
        }
    }

    [Fact]
    public void ScannerFindsNestedSkills_SkipsHidden_AndConfineRefusesEscapes()
    {
        string root = Path.Combine(Path.GetTempPath(), "llm-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "a", "deep"));
        Directory.CreateDirectory(Path.Combine(root, ".hidden"));
        File.WriteAllText(Path.Combine(root, "a", "SKILL.md"), "---\nname: alpha\ndescription: first\n---\nBody here.");
        File.WriteAllText(Path.Combine(root, "a", "deep", "SKILL.md"), "# Beta\n\nSecond body.");
        File.WriteAllText(Path.Combine(root, ".hidden", "SKILL.md"), "# Nope");

        IReadOnlyList<SkillScanner.FoundSkill> found = SkillScanner.Scan(root);
        Assert.Equal(2, found.Count);
        Assert.Contains(found, f => f.Name == "alpha" && f.Description == "first");
        Assert.Contains(found, f => f.Name == "deep");

        IReadOnlyList<string> refs = SkillScanner.FindReferences("See [more](more.md) and (https://x/y).");
        Assert.Contains("more.md", refs);

        string confined = SkillScanner.Confine(Path.Combine(root, "a"), "more.md");
        Assert.StartsWith(Path.Combine(root, "a"), confined);
        Assert.Throws<UnauthorizedAccessException>(() => SkillScanner.Confine(Path.Combine(root, "a"), "../../x"));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task CatalogKeepsManual_AndForgetsStaleDiscovery()
    {
        string directory = Path.Combine(Path.GetTempPath(), "llm-cat-" + Guid.NewGuid().ToString("N"));
        var catalog = new ModelCatalog(directory, () => new HttpClient());
        var provider = new ResolvedProvider("p", "http://127.0.0.1:9/", LLMWireFormat.Responses, true, null);

        catalog.AddManual("p", "hand");
        IReadOnlyList<LLMModelInfo> listed = await catalog.ListAsync(provider, false, CancellationToken.None);
        Assert.Contains(listed, m => m.Id == "hand" && m.Source == "manual");
        Assert.True(catalog.RemoveManual("p", "hand"));
        Assert.False(catalog.RemoveManual("p", "hand"));

        catalog.AddManual("p", "hand");
        string discovered = Path.Combine(directory, "models", "p.discovered.json");
        Directory.CreateDirectory(Path.GetDirectoryName(discovered)!);
        File.WriteAllText(discovered, LLMJson.Encode(new
        {
            sha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData("old"u8.ToArray())),
            ids = new[] { "old" },
        }));
        File.SetLastWriteTimeUtc(discovered, DateTime.UtcNow.AddDays(-2));
        listed = await catalog.ListAsync(provider, false, CancellationToken.None);
        Assert.DoesNotContain(listed, m => m.Id == "old");
        Assert.Contains(listed, m => m.Id == "hand");

        File.WriteAllText(discovered, "not json{{{");
        listed = await catalog.ListAsync(provider, false, CancellationToken.None);
        Assert.Contains(listed, m => m.Id == "hand");
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task ServiceRunsLocalTools_InsideItsOwnLoop()
    {
        var backend = new FakeBackend();
        backend.Script.Enqueue(new LlmBackendResult(
            string.Empty, null,
            [new LLMToolCall("c1", "add", """{"a":2,"b":3}""")], 0, 0, null, null));
        backend.Script.Enqueue(new LlmBackendResult("five", null, [], 10, 4, null, "stop"));
        LlmService service = CreateService(backend);
        service.RegisterTool(new LLMTool("add", "adds", "{}", new StubHandler("five"), false));

        LLMResponse response = await service.CompleteAsync(LLMRequest.Ask("add it", "stub/m"), CancellationToken.None);
        Assert.Equal("five", response.Text);
        Assert.Single(response.ToolCalls);
        Assert.Equal(10, response.InTokens);
        Assert.Equal(4, response.OutTokens);
    }

    [Fact]
    public async Task ServiceRefusesApprovalGatedTools_WithoutAnApprover()
    {
        var backend = new FakeBackend();
        backend.Script.Enqueue(new LlmBackendResult(
            string.Empty, null, [new LLMToolCall("c1", "boom", "{}")], 0, 0, null, null));
        backend.Script.Enqueue(new LlmBackendResult("refused, so plain answer", null, [], 1, 1, null, "stop"));
        LlmService service = CreateService(backend);
        service.RegisterTool(new LLMTool("boom", "d", "{}", new StubHandler("never")));

        LLMResponse response = await service.CompleteAsync(
            LLMRequest.Ask("do it", "stub/m"), CancellationToken.None);
        Assert.Equal("refused, so plain answer", response.Text);
        string sentBack = backend.Seen[1].Messages.Last().Parts.OfType<LLMTextPart>().Single().Text;
        Assert.Contains("refused", sentBack);
    }

    [Fact]
    public async Task ServiceDryRun_ReturnsProposals_Unexecuted()
    {
        var backend = new FakeBackend();
        backend.Script.Enqueue(new LlmBackendResult(
            string.Empty, null, [new LLMToolCall("c1", "add", "{}")], 0, 0, null, null));
        var handler = new StubHandler("must not run");
        LlmService service = CreateService(backend);
        service.RegisterTool(new LLMTool("add", "d", "{}", handler, false));

        LLMResponse response = await service.CompleteAsync(new LLMRequest
        {
            Messages = [new LLMMessage(LLMRoles.User, [new LLMTextPart("hi")])],
            Model = "stub/m",
            AllowedTools = ["add"],
            DryRun = true,
        }, CancellationToken.None);
        Assert.Single(response.ToolCalls);
        Assert.False(handler.Ran);
    }

    [Fact]
    public async Task ServiceRejectsUnknownTools_AndSpentBudgets()
    {
        var backend = new FakeBackend();
        backend.Script.Enqueue(new LlmBackendResult("hi", null, [], 1, 1, null, "stop"));
        LlmService open = CreateService(backend);
        await Assert.ThrowsAsync<ArgumentException>(() => open.CompleteAsync(new LLMRequest
        {
            Messages = [new LLMMessage(LLMRoles.User, [new LLMTextPart("hi")])],
            Model = "stub/m",
            AllowedTools = ["nope"],
        }, CancellationToken.None));

        LlmService capped = CreateService(backend, globalBudget: 5);
        LLMResponse first = await capped.CompleteAsync(
            new LLMRequest { Messages = LLMRequest.Ask("hi", "stub/m").Messages, Model = "stub/m", MaxTokens = 1 },
            CancellationToken.None);
        Assert.Equal("hi", first.Text);
        await Assert.ThrowsAsync<LLMException>(() => capped.CompleteAsync(
            new LLMRequest { Messages = LLMRequest.Ask("hi", "stub/m").Messages, Model = "stub/m", MaxTokens = 1 },
            CancellationToken.None));
    }

    [Fact]
    public async Task ServiceExtractsStructuredJson()
    {
        var backend = new FakeBackend();
        backend.Script.Enqueue(new LlmBackendResult(
            string.Empty, """{"answer":"yes"}""", [], 3, 2, null, "stop"));
        LlmService service = CreateService(backend);
        LLMResponse response = await service.CompleteAsync(new LLMRequest
        {
            Messages = [new LLMMessage(LLMRoles.User, [new LLMTextPart("?")])],
            Model = "stub/m",
            JsonSchema = """{"type":"object"}""",
        }, CancellationToken.None);
        Assert.Equal("""{"answer":"yes"}""", response.Json);
        Assert.Equal(string.Empty, response.Text);
    }

    [Fact]
    public void ReasoningMapper_Covers_All_Levels_On_Both_Paths()
    {
        var cases = new (LLMReasoningEffort Level, string Wire)[]
        {
            (LLMReasoningEffort.None, "none"),
            (LLMReasoningEffort.Low, "low"),
            (LLMReasoningEffort.Medium, "medium"),
            (LLMReasoningEffort.High, "high"),
            (LLMReasoningEffort.XHigh, "xhigh"),
            (LLMReasoningEffort.Max, "max"),
        };
        foreach ((LLMReasoningEffort level, string wire) in cases)
        {
            Assert.Equal(wire, LlmReasoningMapper.ToResponses(level).ToString());
            Assert.Equal(wire, LlmReasoningMapper.ToChat(level).ToString());
        }
    }

    [Fact]
    public void AttributedRegistration_RejectsBadSignatures_ByName()
    {
        var backend = new FakeBackend();
        LlmService service = CreateService(backend);
        LlmToolSignatureException ex = Assert.Throws<LlmToolSignatureException>(
            () => service.RegisterAttributed(new BadTools(), null));
        Assert.Contains("Broken", ex.Message);
        Assert.Contains("llm-test-double", ex.Message);
    }

    private sealed class StubHandler(string reply) : ILLMToolHandler
    {
        public bool Ran { get; private set; }
        public Task<string> InvokeAsync(string args, CancellationToken ct)
        {
            Ran = true;
            return Task.FromResult(reply + ":" + args);
        }
    }

    private sealed class FakeBackend : ILlmBackend
    {
        public readonly Queue<LlmBackendResult> Script = new();
        public readonly List<LlmBackendCall> Seen = new();
        public Task<LlmBackendResult> CompleteAsync(LlmBackendCall call, CancellationToken ct)
        {
            Seen.Add(call);
            return Task.FromResult(Script.Dequeue());
        }
    }

    private sealed class BadTools
    {
        [LLMTool("ok", "fine")]
        public Task<string> Fine(string args, CancellationToken ct) => Task.FromResult(args);

        [LLMTool("llm-test-double", "broken")]
        public string Broken(string args) => args;
    }

    private static LlmService CreateService(FakeBackend backend, long globalBudget = 0)
    {
        var providers = new ProviderRegistry();
        providers.Register(
            new ResolvedProvider("stub", "http://127.0.0.1:9/", LLMWireFormat.Responses, true, null), "test");
        string directory = Path.Combine(Path.GetTempPath(), "llm-svc-" + Guid.NewGuid().ToString("N"));
        var models = new ModelCatalog(directory, () => new HttpClient());
        var ledger = new LlmBudgetLedger();
        ledger.Configure(globalBudget, null);
        var settings = new LlmCoreSettings { AllowLocalEndpoints = true };
        return new LlmService(
            (_, _) => backend, providers, models, new ToolRegistry(), new SkillRegistry(),
            new McpRegistry(() => new HttpClient()), ledger, () => settings, null,
            _ => { }, NullLogger.Instance);
    }
}
