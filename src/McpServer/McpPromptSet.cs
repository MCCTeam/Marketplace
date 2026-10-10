using System.ComponentModel;
using ModelContextProtocol.Server;

namespace DMCBK.OfficialPlugins.McpServer;

/// <summary>The single MCP prompt: the canonical operator prompt for agents driving this server.</summary>
public sealed class McpPromptSet
{
    private readonly McpGuidance _guidance;
    private readonly McpCapabilities _capabilities;

    public McpPromptSet(McpGuidance guidance, McpCapabilities capabilities)
    {
        _guidance = guidance;
        _capabilities = capabilities;
    }

    [McpServerPrompt(Name = "mcc_operator_prompt"), Description("Get the canonical MCC MCP Operator Prompt for external agents using this MCP server.")]
    public string OperatorPrompt()
        => _guidance.GetSystemPrompt(_capabilities.BuildCapabilityStatus());
}
