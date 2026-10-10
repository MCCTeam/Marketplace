// DiscordBridgeSettings - the persisted surface of DiscordBridge: token (optionally linked from the environment), the single-channel keys, and the route list that takes precedence over them.

using Tomlet.Attributes;
using DMCBK.PluginSdk;

namespace DMCBK.OfficialPlugins.DiscordBridge;

/// <summary>One bridged channel and what it may do.</summary>
public sealed class RouteConfig
{
    /// <summary>Local name used by the channel commands. Case-insensitive, must be unique.</summary>
    [TomlInlineComment("$settings.route_name$")]
    public string Name { get; set; } = string.Empty;

    /// <summary>The guild (server) the channel lives in.</summary>
    [TomlInlineComment("$settings.route_guild$")]
    public ulong GuildId { get; set; }

    /// <summary>The channel messages are relayed to and from.</summary>
    [TomlInlineComment("$settings.route_channel$")]
    public ulong ChannelId { get; set; }

    /// <summary>Relay game chat out to this channel.</summary>
    [TomlInlineComment("$settings.route_out$")]
    public bool RelayOut { get; set; } = true;

    /// <summary>Relay this channel's chat into the game.</summary>
    [TomlInlineComment("$settings.route_in$")]
    public bool RelayIn { get; set; } = true;

    /// <summary>Run MCC commands sent from this channel.</summary>
    [TomlInlineComment("$settings.route_cmd$")]
    public bool AllowCommands { get; set; }

    /// <summary>Who may relay and run commands here. Empty inherits the global OwnersIds.</summary>
    [TomlPrecedingComment("$settings.route_owners$")]
    public ulong[] OwnersIds { get; set; } = [];

    /// <summary>Command prefix for this channel. Empty inherits the global CommandPrefix.</summary>
    [TomlInlineComment("$settings.route_prefix$")]
    public string CommandPrefix { get; set; } = string.Empty;
}

/// <summary>The user-editable, persisted settings for DiscordBridge.</summary>
public sealed class DiscordBridgeSettings : IValidatablePluginSettings
{
    /// <summary>The shipped placeholder token, treated as "unset".</summary>
    public const string TokenPlaceholder = "your bot token here";

    /// <summary>Links a string setting to an environment variable: "env:NAME".</summary>
    public const string EnvPrefix = "env:";

    /// <summary>Watchers that DM the owners on a whisper.</summary>
    public const string WatchWhisper = "whisper";

    /// <summary>Watchers that DM the owners on a teleport request.</summary>
    public const string WatchTeleport = "teleport";

    /// <summary>Master switch for the plugin.</summary>
    [TomlInlineComment("$settings.enabled$")]
    public bool Enabled { get; set; } = true;

    /// <summary>The Discord bot token, or "env:NAME" to read it from the environment. Required.</summary>
    [TomlInlineComment("$settings.token$")]
    public string Token { get; set; } = string.Empty;

    /// <summary>The resolved token. Never serialized: Save writes the reference back, not the secret.</summary>
    [TomlNonSerialized]
    public string ResolvedToken { get; set; } = string.Empty;

    /// <summary>The name of the linked variable, for diagnostics. Never serialized.</summary>
    [TomlNonSerialized]
    public string? TokenEnvVar { get; set; }

    /// <summary>The guild (server) the bridged channel lives in. Used only when Routes is empty.</summary>
    [TomlInlineComment("$settings.guild_id$")]
    public ulong GuildId { get; set; }

    /// <summary>The channel messages are relayed to and from. Used only when Routes is empty.</summary>
    [TomlInlineComment("$settings.channel_id$")]
    public ulong ChannelId { get; set; }

    /// <summary>The Discord user ids allowed to relay into the game and run commands.</summary>
    [TomlPrecedingComment("$settings.owners_ids$")]
    public ulong[] OwnersIds { get; set; } = [];

    /// <summary>Accept DMs from owners as if they came from a channel.</summary>
    [TomlInlineComment("$settings.allow_direct_messages$")]
    public bool AllowDirectMessages { get; set; }

    /// <summary>Relay game chat out to owner DMs, the same text the channels get.</summary>
    [TomlInlineComment("$settings.dm_relay_out$")]
    public bool DmRelayOut { get; set; }

    /// <summary>Post a red embed with death coordinates when the client dies.</summary>
    [TomlInlineComment("$settings.death_embed$")]
    public bool DeathEmbed { get; set; } = true;

    /// <summary>Game events that DM the owners on their own: "whisper", "teleport".</summary>
    [TomlInlineComment("$settings.dm_owners_on$")]
    public string[] DmOwnersOn { get; set; } = [];

    /// <summary>The default command prefix for channels and DMs.</summary>
    [TomlInlineComment("$settings.command_prefix$")]
    public string CommandPrefix { get; set; } = ".";

    /// <summary>The bridged channels. Empty falls back to the GuildId/ChannelId pair.</summary>
    [TomlPrecedingComment("$settings.routes$")]
    public List<RouteConfig> Routes { get; set; } = [];

    /// <summary>How long, in seconds, a send may take before it is abandoned.</summary>
    [TomlInlineComment("$settings.message_send_timeout$")]
    public int Message_Send_Timeout { get; set; } = 3;

    /// <summary>Whether messages from other bots are relayed into the game.</summary>
    [TomlInlineComment("$settings.allow_other_bot_messages$")]
    public bool Allow_Other_Bot_Messages { get; set; }

    /// <summary>Whether lines the classifier could not categorize are relayed verbatim.</summary>
    [TomlInlineComment("$settings.relay_all_messages$")]
    public bool Relay_All_Messages { get; set; }

    /// <summary>How long, in seconds, lines are batched before being posted. 0 posts each line immediately.</summary>
    [TomlInlineComment("$settings.message_aggregation_interval$")]
    public double Message_Aggregation_Interval { get; set; } = 3.0;

    /// <summary>Template for a whisper directed at the client.</summary>
    [TomlPrecedingComment("$settings.formats$")]
    public string PrivateMessageFormat { get; set; } = "**[Private Message]** {username}: {message}";

    /// <summary>Template for a normal chat line.</summary>
    public string PublicMessageFormat { get; set; } = "{username}: {message}";

    /// <summary>Template for a teleport request.</summary>
    public string TeleportRequestMessageFormat { get; set; } = "A new Teleport Request from **{username}**!";

    /// <summary>Clamps the timings, resolves the token link, and self-disables when there is no usable token.</summary>
    public void Validate()
    {
        Token ??= string.Empty;
        OwnersIds ??= [];
        DmOwnersOn ??= [];
        Routes ??= [];
        PrivateMessageFormat ??= string.Empty;
        PublicMessageFormat ??= string.Empty;
        TeleportRequestMessageFormat ??= string.Empty;
        CommandPrefix ??= string.Empty;

        if (Message_Send_Timeout <= 0)
            Message_Send_Timeout = 3;

        if (Message_Aggregation_Interval < 0)
            Message_Aggregation_Interval = 0;

        CommandPrefix = BridgeRoutes.NormalizePrefix(CommandPrefix, ".");
        DmOwnersOn = BridgeRoutes.NormalizeWatchers(DmOwnersOn);

        foreach (RouteConfig route in Routes)
        {
            route.Name = (route.Name ?? string.Empty).Trim();
            route.OwnersIds ??= [];
            route.CommandPrefix = BridgeRoutes.NormalizePrefix(route.CommandPrefix ?? string.Empty, string.Empty);
        }

        // Keep the first route per name; the file stays hand-editable without silent merges.
        Routes = Routes
            .Where(r => r.Name.Length > 0 && r.ChannelId != 0)
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        TokenEnvVar = BridgeRoutes.EnvVarName(Token);
        ResolvedToken = BridgeRoutes.ResolveToken(Token);

        if (!HasToken(ResolvedToken))
            Enabled = false;
    }

    /// <summary>True when the configured token is a real one and not blank or the shipped placeholder.</summary>
    public static bool HasToken(string? token)
        => !string.IsNullOrWhiteSpace(token)
            && !string.Equals(token.Trim(), TokenPlaceholder, StringComparison.OrdinalIgnoreCase);
}
