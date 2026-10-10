using System.Text;
using LlmCore.Api;
using DMCBK.Core.Commands;
using DMCBK.Core.Commands.ArgumentTypes;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Umpk.Commands;

namespace DMCBK.OfficialPlugins.LlmTest;

/// <summary>
/// Drives llm-core through its public contract and writes every result to data/, so tests assert on files instead of timing.
/// Every command blocks on its async work; the core never captures a context.
/// </summary>
public sealed class LlmTestPlugin : IPlugin
{
    private PluginContext? _context;
    private IDisposable? _tools;

    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "llm-test";
        descriptor.Version = "0.2.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
    }

    public Task ActivateAsync(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        context.Commands.Register(new LlmTestCommand(this, context.Strings));
        if (context.Services.TryGet<ILLMService>(out var service) && service is not null)
            _tools = service.RegisterAttributed(this, context.Storage.GetPath("skills"));

        return Task.CompletedTask;
    }

    public Task DeactivateAsync(CancellationToken ct)
    {
        _tools?.Dispose();
        _tools = null;
        return Task.CompletedTask;
    }

    [LLMTool("test_echo", "Echoes its JSON arguments back as text.", NeedsApproval = false)]
    public Task<string> EchoAsync(string args, CancellationToken ct)
        => Task.FromResult("echo:" + args);

    internal bool TryService(out ILLMService? service)
    {
        service = null;
        return _context is not null && _context.Services.TryGet(out service) && service is not null;
    }

    internal void Write(string file, string content)
    {
        if (_context is null)
            return;

        try
        {
            File.WriteAllText(_context.Storage.GetPath(file), content);
        }
        catch (Exception)
        {
        }
    }

    internal string ReadSkillSeed()
        => "# probe\n\nA probe skill used by tests.\nSee [more](more.md) for details.\n";
}

/// <summary>The <c>llmtest</c> console command. One verb per capability, each writing a data file.</summary>
internal sealed class LlmTestCommand : CommandBase
{
    private readonly LlmTestPlugin _plugin;

    internal LlmTestCommand(LlmTestPlugin plugin, IPluginLocalization strings)
    {
        _plugin = plugin;
    }

    public override string CmdName => "llmtest";
    public override string CmdDesc => "Drives llm-core for tests.";
    public override string CmdUsage => "llmtest <verb>";
    public override CommandCategory Category => CommandCategory.Plugins;

    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .Executes(ctx => ShowUsage(ctx.Source))
            .ThenLiteral("use-stub", a => a
                .ThenArgument("endpoint", Arguments.GreedyString(), g => g.Executes(
                    ctx => Run(ctx.Source, "stub.log", (service, ct) =>
                    {
                        service.RegisterLLMProvider(new LLMProviderDefinition(
                            "stub", ctx.GetArgument<string>("endpoint"), LLMWireFormat.Responses, true, null));
                        return Task.FromResult("stub registered");
                    }))))
            .ThenLiteral("ask", a => a
                .ThenArgument("rest", Arguments.GreedyString(), g => g.Executes(
                    ctx => Run(ctx.Source, "ask.log", async (service, ct) =>
                    {
                        (string model, string prompt) = SplitModel(ctx.GetArgument<string>("rest"));
                        LLMResponse response = await service.CompleteAsync(
                            LLMRequest.Ask(prompt, model), ct);
                        return $"text={response.Text}\nin={response.InTokens} out={response.OutTokens}";
                    }))))
            .ThenLiteral("stream", a => a
                .ThenArgument("rest", Arguments.GreedyString(), g => g.Executes(
                    ctx => Run(ctx.Source, "stream.log", async (service, ct) =>
                    {
                        (string model, string prompt) = SplitModel(ctx.GetArgument<string>("rest"));
                        var text = new StringBuilder();
                        await foreach (LLMChunk chunk in service.StreamAsync(
                            LLMRequest.Ask(prompt, model), ct))
                            text.Append(chunk.Delta);

                        return "streamed=" + text;
                    }))))
            .ThenLiteral("structured", a => a
                .ThenArgument("rest", Arguments.GreedyString(), g => g.Executes(
                    ctx => Run(ctx.Source, "structured.log", async (service, ct) =>
                    {
                        (string model, string prompt) = SplitModel(ctx.GetArgument<string>("rest"));
                        LLMResponse response = await service.CompleteAsync(new LLMRequest
                        {
                            Messages = [new LLMMessage(LLMRoles.User, [new LLMTextPart(prompt)])],
                            Model = model,
                            JsonSchema = """{"type":"object","properties":{"answer":{"type":"string"}},"required":["answer"]}""",
                        }, ct);
                        return "json=" + (response.Json ?? "<none>");
                    }))))
            .ThenLiteral("tools", a => a
                .ThenArgument("rest", Arguments.GreedyString(), g => g.Executes(
                    ctx => Run(ctx.Source, "tools.log", async (service, ct) =>
                    {
                        (string model, string prompt) = SplitModel(ctx.GetArgument<string>("rest"));
                        LLMResponse response = await service.CompleteAsync(new LLMRequest
                        {
                            Messages = [new LLMMessage(LLMRoles.User, [new LLMTextPart(prompt)])],
                            Model = model,
                            AllowedTools = ["test_echo"],
                        }, ct);
                        return $"text={response.Text}\ncalls={response.ToolCalls.Count}";
                    }))))
            .ThenLiteral("skills", a => a.Executes(
                ctx => Run(ctx.Source, "skills.log", (service, ct) =>
                {
                    service.RegisterSkill(new LLMSkillDefinition(
                        "probe", "A probe skill.", null, _plugin.ReadSkillSeed(), "llm-test", "0.1.0", ["probe"]));
                    IReadOnlyList<LLMSkillSummary> listed = service.ListSkills();
                    return Task.FromResult("skills=" + string.Join(",", listed.Select(s => s.Name)));
                })))
            .ThenLiteral("models", a => a
                .ThenArgument("provider", Arguments.Word(), n => n.Executes(
                    ctx => Run(ctx.Source, "models.log", async (service, ct) =>
                    {
                        IReadOnlyList<LLMModelInfo> models =
                            await service.ListLLMsAsync(ctx.GetArgument<string>("provider"), true, ct);
                        return "models=" + string.Join(",", models.Select(m => m.Id));
                    }))))
            .ThenLiteral("live", a => a.Executes(
                ctx => Run(ctx.Source, "live.log", (service, ct) => LiveAsync(service, ct))))
            .ThenLiteral("reasoning", a => a
                .ThenArgument("rest", Arguments.GreedyString(), g => g.Executes(
                    ctx => Run(ctx.Source, "reasoning.log", (service, ct) =>
                        ReasoningAsync(service, ctx.GetArgument<string>("rest"), ct)))))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private static (string Model, string Prompt) SplitModel(string? rest)
    {
        string text = rest ?? string.Empty;
        int space = text.IndexOf(' ');
        return space <= 0
            ? (text.Trim(), string.Empty)
            : (text[..space].Trim(), text[(space + 1)..].Trim());
    }

    private int Run(
        CommandContext ctx, string file, Func<ILLMService, CancellationToken, Task<string>> work)
    {
        if (!_plugin.TryService(out ILLMService? service) || service is null)
            return ctx.Result.Fail("llm-core is not available.");

        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            string result = work(service, budget.Token).GetAwaiter().GetResult();
            _plugin.Write(file, result);
            return ctx.Result.Ok(file + " written");
        }
        catch (Exception ex)
        {
            _plugin.Write(file, "ERROR " + ex);
            return ctx.Result.Fail(ex.Message);
        }
    }

    private static async Task<string> LiveAsync(ILLMService service, CancellationToken ct)
    {
        string key = (Environment.GetEnvironmentVariable("LLM_LIVE_TEST_KEY") ?? string.Empty).Trim();
        if (key.Length == 0)
            return "no key in LLM_LIVE_TEST_KEY";

        Environment.SetEnvironmentVariable("LLM_LIVE_TEST_KEY", key);
        service.RegisterLLMProvider(new LLMProviderDefinition(
            "openrouter", "https://openrouter.ai/api/v1", LLMWireFormat.Responses, true, "LLM_LIVE_TEST_KEY"));
        const string model = "openrouter/deepseek/deepseek-v4.1-flash";

        try
        {
            LLMResponse response = await service.CompleteAsync(
                LLMRequest.Ask("Reply with exactly: alive", model), ct);
            if (response.Text.Trim().Length > 0)
                return $"responses text={response.Text.Trim()} in={response.InTokens} out={response.OutTokens}";
        }
        catch (LLMException)
        {
        }

        service.RegisterLLMProvider(new LLMProviderDefinition(
            "openrouter-chat", "https://openrouter.ai/api/v1", LLMWireFormat.ChatCompletions, true, "LLM_LIVE_TEST_KEY"));
        LLMResponse fallback = await service.CompleteAsync(
            LLMRequest.Ask("Reply with exactly: alive", "openrouter-chat/deepseek/deepseek-v4.1-flash"), ct);
        return $"chat text={fallback.Text.Trim()} in={fallback.InTokens} out={fallback.OutTokens}";
    }

    private static async Task<string> ReasoningAsync(ILLMService service, string rest, CancellationToken ct)
    {
        string[] parts = (rest ?? string.Empty).Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !Enum.TryParse<LLMReasoningEffort>(parts[1], true, out var level))
            return "use: llmtest reasoning <provider/model> <none|low|medium|high|xhigh|max> <prompt>";

        LLMResponse response = await service.CompleteAsync(new LLMRequest
        {
            Messages = [new LLMMessage(LLMRoles.User, [new LLMTextPart(parts[2])])],
            Model = parts[0],
            ReasoningEffort = level,
            MaxTokens = 200,
        }, ct);
        return $"level={level} finish={response.FinishReason} in={response.InTokens} out={response.OutTokens} text={response.Text.Trim()}";
    }
}
