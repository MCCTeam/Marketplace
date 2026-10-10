using DMCBK.PluginSdk;
using Tomlet.Attributes;

namespace DMCBK.OfficialPlugins.McpServer;

/// <summary>
/// The user-editable, persisted settings for the MCP server plugin (serialized to settings.toml).
/// Uses the same section shape as <c>ChatBot.McpServer</c>, with its transport and capability tables.
/// </summary>
public sealed class McpServerSettings : IValidatablePluginSettings
{
    /// <summary>Default loopback bind host. Never default to a wildcard: that would expose the full toolset to the LAN.</summary>
    public const string DefaultBindHost = "127.0.0.1";

    /// <summary>Default port, kept stable so existing agent wiring keeps working.</summary>
    public const int DefaultPort = 33333;

    /// <summary>Default route, kept stable for existing agent wiring.</summary>
    public const string DefaultRoute = "/mcp";

    /// <summary>Default auth-token environment variable, kept stable for existing agent wiring.</summary>
    public const string DefaultAuthTokenEnvVar = "MCC_MCP_AUTH_TOKEN";

    /// <summary>Master switch for the HTTP listener. The plugin can stay loaded while the endpoint is off.</summary>
    [TomlInlineComment("$settings.enabled$")]
    public bool Enabled { get; set; } = true;

    /// <summary>Interface to bind. Loopback only unless the operator knows why a wider bind is needed.</summary>
    [TomlInlineComment("$settings.bind_host$")]
    public string BindHost { get; set; } = DefaultBindHost;

    /// <summary>TCP port to listen on.</summary>
    [TomlInlineComment("$settings.port$")]
    public int Port { get; set; } = DefaultPort;

    /// <summary>HTTP route the MCP endpoint is served from.</summary>
    [TomlInlineComment("$settings.route$")]
    public string Route { get; set; } = DefaultRoute;

    /// <summary>
    /// Whether callers must present <c>Authorization: Bearer</c>.
    /// On by default: loopback is not a security boundary (same-box software and DNS-rebinding both reach it), and the toolset can move, dig, chat and run commands as the player.
    /// </summary>
    [TomlInlineComment("$settings.require_auth_token$")]
    public bool RequireAuthToken { get; set; } = true;

    /// <summary>Name of the environment variable the bearer token is read from. The secret itself never lives in this file.</summary>
    [TomlInlineComment("$settings.auth_token_env_var$")]
    public string AuthTokenEnvVar { get; set; } = DefaultAuthTokenEnvVar;

    /// <summary>Whether the listener may bind a non-loopback address. Refused unless auth is also on.</summary>
    [TomlInlineComment("$settings.allow_non_loopback$")]
    public bool AllowNonLoopback { get; set; }

    /// <summary>Session/server/reference reads.</summary>
    [TomlInlineComment("$settings.cap_session_status$")]
    public bool CapSessionStatus { get; set; } = true;

    /// <summary>Chat sends, internal commands, respawn, disconnect.</summary>
    [TomlInlineComment("$settings.cap_chat_and_commands$")]
    public bool CapChatAndCommands { get; set; } = true;

    /// <summary>Movement, look, dig, place, use, entity interaction.</summary>
    [TomlInlineComment("$settings.cap_movement$")]
    public bool CapMovement { get; set; } = true;

    /// <summary>Inventory snapshots, search, containers, transfers.</summary>
    [TomlInlineComment("$settings.cap_inventory$")]
    public bool CapInventory { get; set; } = true;

    /// <summary>Entity, block, item and sign reads.</summary>
    [TomlInlineComment("$settings.cap_entity_world$")]
    public bool CapEntityWorld { get; set; } = true;

    /// <summary>Normalize on load and reload (the settings pipeline validate hook).</summary>
    public void Validate()
    {
        if (Port is < 1 or > 65535)
            Port = DefaultPort;

        if (string.IsNullOrWhiteSpace(BindHost))
            BindHost = DefaultBindHost;
        else
            BindHost = BindHost.Trim();

        if (string.IsNullOrWhiteSpace(Route))
            Route = DefaultRoute;
        else
        {
            Route = Route.Trim();
            if (!Route.StartsWith('/'))
                Route = "/" + Route;

            if (Route.Length > 1)
                Route = Route.TrimEnd('/');
        }

        if (string.IsNullOrWhiteSpace(AuthTokenEnvVar))
            AuthTokenEnvVar = DefaultAuthTokenEnvVar;
        else
            AuthTokenEnvVar = AuthTokenEnvVar.Trim();

        // A wide bind without auth is never what the operator meant: fall back to loopback rather than exposing an unauthenticated player-control endpoint to the network.
        if (!IsLoopback(BindHost) && (!RequireAuthToken || !AllowNonLoopback))
            BindHost = DefaultBindHost;
    }

    /// <summary>True for the addresses that never leave the machine.</summary>
    public static bool IsLoopback(string host)
        => string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(host, "::1", StringComparison.Ordinal)
            || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
}
