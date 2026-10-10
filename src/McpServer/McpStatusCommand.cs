using DMCBK.Core.Commands;
using Umpk.Commands;

namespace DMCBK.OfficialPlugins.McpServer;

/// <summary>The <c>mcp</c> command: reports the endpoint and listener state.</summary>
public sealed class McpStatusCommand : CommandBase
{
    private readonly McpServerPlugin _plugin;

    public McpStatusCommand(McpServerPlugin plugin)
    {
        _plugin = plugin;
    }

    /// <inheritdoc/>
    public override string CmdName => "mcp";

    /// <inheritdoc/>
    public override string CmdDesc => "Show the MCP server endpoint and listener state.";

    /// <inheritdoc/>
    public override string CmdUsage => "mcp";

    /// <inheritdoc/>
    public override IReadOnlyList<UsageLine> UsageLines =>
    [
        new(string.Empty, "show the MCP server endpoint and listener state"),
    ];

    /// <inheritdoc/>
    public override IReadOnlyList<string> Examples => ["mcp"];

    /// <inheritdoc/>
    public override string? ManTopic => "mcp-server";

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);
        builder.Literal(CmdName, l => l
            .Executes(ctx => Run(ctx.Source))
            .ThenLiteral("_help", h => h.Executes(ctx => ShowUsage(ctx.Source)).RedirectTo(help)));
    }

    private int Run(CommandContext context)
    {
        string state = _plugin.IsRunning ? "running" : "stopped";
        context.Output.WriteLine($"MCP server is {state} at {_plugin.Endpoint}.");
        return context.Result.Ok();
    }
}
