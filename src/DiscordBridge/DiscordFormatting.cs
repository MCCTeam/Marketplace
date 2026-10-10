// DiscordFormatting - the text side of the bridge: the Minecraft-to-Discord projection, section codes to Markdown, and the aggregation buffer packing.
// No client and no network.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DMCBK.PluginSdk;

namespace DMCBK.OfficialPlugins.DiscordBridge;

/// <summary>What one relayed line becomes on the Discord side.</summary>
/// <param name="Text">The rendered message.</param>
/// <param name="IsTeleportRequest">Whether it should be posted as an embed with accept/deny buttons.</param>
public sealed record RelayedMessage(string Text, bool IsTeleportRequest);

/// <summary>The message templates.</summary>
/// <param name="PrivateMessage">Template for a whisper directed at the client.</param>
/// <param name="PublicMessage">Template for a normal chat line.</param>
/// <param name="TeleportRequest">Template for a teleport request.</param>
public sealed record BridgeFormats(string PrivateMessage, string PublicMessage, string TeleportRequest);

/// <summary>The chat-line to Discord-text projection.</summary>
public static partial class DiscordFormatting
{
    /// <summary>The format the <c>{timestamp}</c> placeholder expands to.</summary>
    public const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>Soft cap on one Discord message, leaving room under the hard 2000 limit.</summary>
    public const int MessageCharacterBudget = 1900;

    /// <summary>
    /// Renders one classified chat line.
    /// Returns null when the line must not be relayed: a line the classifier could not categorize goes out only when <paramref name="relayAllMessages"/> is set.
    /// </summary>
    public static RelayedMessage? Render(
        ClassifiedChat chat, BridgeFormats formats, bool relayAllMessages, DateTime timestamp)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(formats);

        string raw = chat.Raw.Trim();
        if (raw.Length == 0)
            return null;

        switch (chat.Kind)
        {
            case ChatKind.PrivateMessage:
                return new RelayedMessage(
                    Expand(formats.PrivateMessage, chat.Sender, chat.Body, timestamp), IsTeleportRequest: false);

            case ChatKind.TeleportRequest:
                return new RelayedMessage(
                    Expand(formats.TeleportRequest, chat.Sender, chat.Body, timestamp), IsTeleportRequest: true);

            case ChatKind.Chat:
                return new RelayedMessage(
                    Expand(formats.PublicMessage, chat.Sender, chat.Body, timestamp), IsTeleportRequest: false);

            default:
                return relayAllMessages ? new RelayedMessage(raw, IsTeleportRequest: false) : null;
        }
    }

    /// <summary>Expands the three placeholders and trims.</summary>
    public static string Expand(string? template, string? username, string? message, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(template))
            return string.Empty;

        return template
            .Replace("{username}", username ?? string.Empty, StringComparison.Ordinal)
            .Replace("{message}", message ?? string.Empty, StringComparison.Ordinal)
            .Replace("{timestamp}", timestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Trim();
    }

    /// <summary>Strips every section code, for places like command output where Markdown is wrong.</summary>
    public static string StripCodes(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        // Background escapes first: "§§7" is three chars, and the single-code pass would eat only the first two and leave the "7" behind.
        return RemainingCodes().Replace(BackgroundCodes().Replace(text, string.Empty), string.Empty);
    }

    /// <summary>
    /// Picks the Discord reply body for a dispatched command: what the command printed wins, because that is what the console shows; the one-line result message is the fallback.
    /// Both arrive stripped.
    /// </summary>
    public static string ReplyBody(string? captured, string? message)
    {
        if (!string.IsNullOrWhiteSpace(captured))
            return StripCodes(captured).Trim();

        return StripCodes(message).Trim();
    }

    /// <summary>
    /// Converts Minecraft section formatting codes to Discord Markdown and strips the rest, handling both properly closed runs (bold then reset) and runs left open to the end of the line.
    /// </summary>
    public static string ToDiscordMarkdown(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        string result = BoldRun().Replace(text, "**$1**");
        result = StrikethroughRun().Replace(result, "~~$1~~");
        result = UnderlineRun().Replace(result, "__$1__");
        result = ItalicRun().Replace(result, "*$1*");
        return RemainingCodes().Replace(BackgroundCodes().Replace(result, string.Empty), string.Empty);
    }

    /// <summary>
    /// Splits long Markdown (manual pages) into Discord-sized messages: pack lines until the next would overflow, and hard-cut any single line longer than the budget.
    /// Never drops text.
    /// </summary>
    public static IReadOnlyList<string> Chunk(string? text, int budget = MessageCharacterBudget)
    {
        var messages = new List<string>();
        if (string.IsNullOrEmpty(text))
            return messages;

        var current = new StringBuilder();
        void Flush()
        {
            if (current.Length > 0)
            {
                messages.Add(current.ToString());
                current.Clear();
            }
        }

        foreach (string raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = raw;
            while (line.Length > budget)
            {
                Flush();
                messages.Add(line[..budget]);
                line = line[budget..];
            }

            if (current.Length > 0 && current.Length + line.Length + 1 > budget)
                Flush();

            if (current.Length > 0)
                current.Append('\n');

            current.Append(line);
        }

        Flush();
        return messages;
    }

    /// <summary>
    /// Packs the buffered lines into as few Discord messages as the character budget allows: append until the next line would overflow, then start a new message.
    /// </summary>
    public static IReadOnlyList<string> PackAggregated(
        IEnumerable<string> lines, int budget = MessageCharacterBudget)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var messages = new List<string>();
        var current = new StringBuilder();

        foreach (string line in lines)
        {
            if (current.Length > 0 && current.Length + line.Length + 1 > budget)
            {
                messages.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
                current.Append('\n');

            current.Append(line);
        }

        if (current.Length > 0)
            messages.Add(current.ToString());

        return messages;
    }

    // The section sign is written as an escape so the source stays pure ASCII.
    [GeneratedRegex("\u00a7l(.*?)(?:\u00a7r|$)")]
    private static partial Regex BoldRun();

    [GeneratedRegex("\u00a7m(.*?)(?:\u00a7r|$)")]
    private static partial Regex StrikethroughRun();

    [GeneratedRegex("\u00a7n(.*?)(?:\u00a7r|$)")]
    private static partial Regex UnderlineRun();

    [GeneratedRegex("\u00a7o(.*?)(?:\u00a7r|$)")]
    private static partial Regex ItalicRun();

    [GeneratedRegex("\u00a7\u00a7.")]
    private static partial Regex BackgroundCodes();

    [GeneratedRegex("\u00a7.")]
    private static partial Regex RemainingCodes();
}
