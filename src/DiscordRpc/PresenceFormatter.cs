// The pure half of DiscordRpc: placeholder expansion, the masking each Show* toggle applies, the display names, and the Discord field truncation.
// It is a function of a facts snapshot plus the settings, so it can be tested without a session or a Discord connection.

using System.Globalization;

namespace DMCBK.OfficialPlugins.DiscordRpc;

/// <summary>The live facts one presence update is built from.</summary>
/// <param name="ServerHost">The connected server host.</param>
/// <param name="ServerPort">The connected server port.</param>
/// <param name="Username">The logged-in profile name.</param>
/// <param name="Health">The current health (0-20).</param>
/// <param name="Food">The current food level (0-20).</param>
/// <param name="X">The player's block X.</param>
/// <param name="Y">The player's block Y.</param>
/// <param name="Z">The player's block Z.</param>
/// <param name="Dimension">The raw dimension id, for example <c>minecraft:the_nether</c>.</param>
/// <param name="GameMode">The game mode ordinal (0 survival, 1 creative, 2 adventure, 3 spectator).</param>
/// <param name="PlayerCount">How many players the tab list reports.</param>
/// <param name="Protocol">The negotiated protocol number.</param>
public sealed record PresenceFacts(
    string ServerHost,
    int ServerPort,
    string Username,
    float Health,
    int Food,
    int X,
    int Y,
    int Z,
    string Dimension,
    int GameMode,
    int PlayerCount,
    int Protocol);

/// <summary>The presence text builder.</summary>
public static class PresenceFormatter
{
    /// <summary>The Discord limit for the details/state/image-text fields.</summary>
    public const int DiscordFieldLimit = 128;

    /// <summary>The value substituted for a fact the user chose not to publish.</summary>
    public const string Hidden = "Hidden";

    /// <summary>The value substituted for a numeric fact the user chose not to publish.</summary>
    public const string MaskedNumber = "?";

    /// <summary>The value substituted for a masked port.</summary>
    public const string MaskedPort = "****";

    /// <summary>Expands the placeholders in <paramref name="template"/>, honouring the privacy toggles.</summary>
    public static string Format(string? template, PresenceFacts facts, DiscordRpcSettings settings)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrEmpty(template))
            return string.Empty;

        string serverHost = settings.ShowServerAddress ? facts.ServerHost : Hidden;
        string serverPort = settings.ShowServerAddress
            ? facts.ServerPort.ToString(CultureInfo.InvariantCulture)
            : MaskedPort;

        string health = settings.ShowHealth
            ? ((int)Math.Ceiling(facts.Health)).ToString(CultureInfo.InvariantCulture)
            : MaskedNumber;
        string maxHealth = settings.ShowHealth ? "20" : MaskedNumber;
        string food = settings.ShowHealth ? facts.Food.ToString(CultureInfo.InvariantCulture) : MaskedNumber;

        string x = settings.ShowCoordinates ? facts.X.ToString(CultureInfo.InvariantCulture) : MaskedNumber;
        string y = settings.ShowCoordinates ? facts.Y.ToString(CultureInfo.InvariantCulture) : MaskedNumber;
        string z = settings.ShowCoordinates ? facts.Z.ToString(CultureInfo.InvariantCulture) : MaskedNumber;

        string dimension = settings.ShowDimension ? DimensionDisplayName(facts.Dimension) : Hidden;
        string gameMode = settings.ShowGamemode ? GameModeDisplayName(facts.GameMode) : Hidden;

        return template
            .Replace("{server_host}", serverHost, StringComparison.Ordinal)
            .Replace("{server_port}", serverPort, StringComparison.Ordinal)
            .Replace("{username}", facts.Username, StringComparison.Ordinal)
            .Replace("{health}", health, StringComparison.Ordinal)
            .Replace("{max_health}", maxHealth, StringComparison.Ordinal)
            .Replace("{food}", food, StringComparison.Ordinal)
            .Replace("{dimension}", dimension, StringComparison.Ordinal)
            .Replace("{gamemode}", gameMode, StringComparison.Ordinal)
            .Replace("{x}", x, StringComparison.Ordinal)
            .Replace("{y}", y, StringComparison.Ordinal)
            .Replace("{z}", z, StringComparison.Ordinal)
            .Replace("{player_count}", facts.PlayerCount.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{protocol}", facts.Protocol.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    /// <summary>Strips the <c>minecraft:</c> prefix and prettifies the three vanilla dimensions.</summary>
    public static string DimensionDisplayName(string? dimension)
    {
        if (string.IsNullOrWhiteSpace(dimension))
            return "Unknown";

        string name = dimension.StartsWith("minecraft:", StringComparison.Ordinal)
            ? dimension["minecraft:".Length..]
            : dimension;

        return name switch
        {
            "overworld" => "Overworld",
            "the_nether" => "The Nether",
            "the_end" => "The End",
            _ => name,
        };
    }

    /// <summary>The vanilla game-mode display names, by ordinal.</summary>
    public static string GameModeDisplayName(int gameMode) => gameMode switch
    {
        0 => "Survival",
        1 => "Creative",
        2 => "Adventure",
        3 => "Spectator",
        _ => "Unknown",
    };

    /// <summary>Truncates to the Discord field limit with an ellipsis.</summary>
    public static string Truncate(string? value, int maxLength = DiscordFieldLimit)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
    }
}
