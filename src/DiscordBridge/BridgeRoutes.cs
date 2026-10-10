// BridgeRoutes - the pure half of the multi-channel bridge: env links, single-channel fallback, effective owners and prefixes, id parsing, and command-prefix splitting.
// No Discord client, no network, so the test suite pins all of it headlessly.

using DMCBK.PluginSdk;

namespace DMCBK.OfficialPlugins.DiscordBridge;

/// <summary>A route with its inherited values filled in.</summary>
/// <param name="Name">The local route name.</param>
/// <param name="GuildId">The guild the channel lives in.</param>
/// <param name="ChannelId">The channel itself.</param>
/// <param name="RelayOut">Whether game chat goes out to this channel.</param>
/// <param name="RelayIn">Whether this channel's chat comes into the game.</param>
/// <param name="AllowCommands">Whether this channel may run MCC commands.</param>
/// <param name="Owners">The effective owner ids (route override or global).</param>
/// <param name="Prefix">The effective command prefix (route override or global).</param>
public sealed record EffectiveRoute(
    string Name,
    ulong GuildId,
    ulong ChannelId,
    bool RelayOut,
    bool RelayIn,
    bool AllowCommands,
    ulong[] Owners,
    string Prefix);

/// <summary>Pure helpers for routes, prefixes, and env-linked secrets.</summary>
public static class BridgeRoutes
{
    /// <summary>The name of the linked variable, or null when the value is a literal token.</summary>
    public static string? EnvVarName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string text = value.Trim();
        if (!text.StartsWith(DiscordBridgeSettings.EnvPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        string name = text[DiscordBridgeSettings.EnvPrefix.Length..].Trim();
        return name.Length == 0 ? null : name;
    }

    /// <summary>Resolves a token that may be an "env:NAME" link. Unset or empty reads as blank.</summary>
    public static string ResolveToken(string? value)
    {
        string? name = EnvVarName(value);
        if (name is null)
            return (value ?? string.Empty).Trim();

        return (Environment.GetEnvironmentVariable(name) ?? string.Empty).Trim();
    }

    /// <summary>Trims a prefix; falls back when blank or longer than two characters.</summary>
    public static string NormalizePrefix(string? prefix, string fallback)
    {
        string text = (prefix ?? string.Empty).Trim();
        return text.Length is >= 1 and <= 2 ? text : fallback;
    }

    /// <summary>Lowercases the watcher list and drops anything but "whisper" and "teleport".</summary>
    public static string[] NormalizeWatchers(string[]? watchers)
    {
        if (watchers is null || watchers.Length == 0)
            return [];

        return watchers
            .Select(w => (w ?? string.Empty).Trim().ToLowerInvariant())
            .Where(w => w is DiscordBridgeSettings.WatchWhisper or DiscordBridgeSettings.WatchTeleport)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// The routes in force: the configured list, or one fallback route from the GuildId/ChannelId pair (which keeps the default of everything on), or nothing when neither is configured.
    /// </summary>
    public static IReadOnlyList<EffectiveRoute> Resolve(
        IReadOnlyList<RouteConfig>? routes,
        ulong legacyGuildId,
        ulong legacyChannelId,
        ulong[] globalOwners,
        string globalPrefix)
    {
        ulong[] owners = globalOwners ?? [];
        string prefix = NormalizePrefix(globalPrefix, ".");

        var configured = (routes ?? [])
            .Where(r => r is not null && r.Name.Length > 0 && r.ChannelId != 0)
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(r => new EffectiveRoute(
                r.Name,
                r.GuildId,
                r.ChannelId,
                r.RelayOut,
                r.RelayIn,
                r.AllowCommands,
                r.OwnersIds is { Length: > 0 } own ? own : owners,
                NormalizePrefix(r.CommandPrefix, prefix)))
            .ToList();

        if (configured.Count > 0)
            return configured;

        if (legacyGuildId != 0 || legacyChannelId != 0)
            return [new EffectiveRoute("default", legacyGuildId, legacyChannelId, true, true, true, owners, prefix)];

        return [];
    }

    /// <summary>Finds a route by name (case-insensitive) or by guild and channel.</summary>
    public static EffectiveRoute? FindByName(IEnumerable<EffectiveRoute> routes, string? name)
        => routes.FirstOrDefault(r =>
            string.Equals(r.Name, (name ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds a route by guild and channel.</summary>
    public static EffectiveRoute? FindByChannel(IEnumerable<EffectiveRoute> routes, ulong? guildId, ulong channelId)
        => routes.FirstOrDefault(r => r.ChannelId == channelId && (guildId is null || r.GuildId == guildId));

    /// <summary>Parses a Discord snowflake id. Rejects blanks, signs, and overflow.</summary>
    public static bool TryParseId(string? text, out ulong id)
    {
        id = 0;
        text = (text ?? string.Empty).Trim().Trim('"');
        return text.Length > 0
            && text.All(char.IsAsciiDigit)
            && ulong.TryParse(text, out id)
            && id != 0;
    }

    /// <summary>Parses a blank-separated id list, skipping what does not parse.</summary>
    public static ulong[] ParseIds(string? text)
        => (text ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(part => TryParseId(part, out _))
            .Select(ulong.Parse)
            .Distinct()
            .ToArray();

    /// <summary>Parses on/off (and true/false/1/0) into a bool.</summary>
    public static bool TryParseSwitch(string? text, out bool value)
    {
        value = false;
        switch ((text ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "on":
            case "true":
            case "1":
                value = true;
                return true;
            case "off":
            case "false":
            case "0":
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// True when a watched event goes to the owners' DMs instead of the channels.
    /// Watched whispers and teleport requests skip the channel fan-out entirely, so a private message never lands on a server.
    /// </summary>
    public static bool IsDmExclusive(ChatKind kind, string[]? watchers)
    {
        if (watchers is null || watchers.Length == 0)
            return false;

        return (kind == ChatKind.PrivateMessage && watchers.Contains(DiscordBridgeSettings.WatchWhisper))
            || (kind == ChatKind.TeleportRequest && watchers.Contains(DiscordBridgeSettings.WatchTeleport));
    }

    /// <summary>Splits a leading command prefix off a message. Returns false for plain chat.</summary>
    public static bool TryStripPrefix(string? message, string prefix, out string command)
    {
        command = string.Empty;
        if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(prefix))
            return false;

        if (!message.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        command = message[prefix.Length..].TrimStart();
        return command.Length > 0;
    }

    /// <summary>Where one image request goes: named routes plus owner DMs. Both empty means broadcast.</summary>
    public sealed record ImageTargets(IReadOnlyList<string> Routes, IReadOnlyList<ulong> Dms);

    /// <summary>
    /// Parses an image destination: comma-separated <c>route:name</c> and <c>dm:owner-id</c> entries.
    /// Unknown entries are dropped.
    /// Empty means the default fan-out.
    /// </summary>
    public static ImageTargets ParseImageTargets(string? destination)
    {
        var routes = new List<string>();
        var dms = new List<ulong>();
        foreach (string part in (destination ?? string.Empty).Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("route:", StringComparison.OrdinalIgnoreCase)
                && part.Length > "route:".Length
                && part["route:".Length..].Trim() is { Length: > 0 } name)
                routes.Add(name);
            else if (part.StartsWith("dm:", StringComparison.OrdinalIgnoreCase)
                && TryParseId(part["dm:".Length..], out ulong id))
                dms.Add(id);
        }

        return new ImageTargets(
            routes.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            dms.Distinct().ToList());
    }
}
