// The pure half of TelegramBridge: turning a classified chat line into the text that goes out to Telegram, and deciding which way the bridge relays.
// No client and no network, so it is testable headlessly.

using System.Globalization;
using System.Text.RegularExpressions;
using DMCBK.PluginSdk;

namespace DMCBK.OfficialPlugins.TelegramBridge;

/// <summary>Which way the bridge currently relays.</summary>
public enum BridgeDirection
{
    /// <summary>Relay Minecraft to Telegram and Telegram to Minecraft.</summary>
    Both = 0,

    /// <summary>Relay only Telegram to Minecraft (nothing is sent out to Telegram).</summary>
    Minecraft,

    /// <summary>Relay only Minecraft to Telegram (Telegram messages are ignored, except bridge commands).</summary>
    Telegram,
}

/// <summary>The message templates, passed as a value so formatting stays free of the settings object.</summary>
/// <param name="PrivateMessage">Template for a whisper directed at the client.</param>
/// <param name="PublicMessage">Template for a normal chat line.</param>
/// <param name="TeleportRequest">Template for a teleport request.</param>
public sealed record BridgeFormats(string PrivateMessage, string PublicMessage, string TeleportRequest);

/// <summary>The chat-line to bridge-text projection.</summary>
public static partial class BridgeFormatting
{
    /// <summary>The format the <c>{timestamp}</c> placeholder expands to.</summary>
    public const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>
    /// Renders one classified chat line, or null when the line should not be relayed.
    /// A line that does not classify is relayed verbatim.
    /// </summary>
    public static string? Render(ClassifiedChat chat, BridgeFormats formats, DateTime timestamp)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(formats);

        string raw = chat.Raw.Trim();
        if (raw.Length == 0)
            return null;

        return chat.Kind switch
        {
            ChatKind.PrivateMessage => Expand(formats.PrivateMessage, chat.Sender, chat.Body, timestamp),
            ChatKind.TeleportRequest => Expand(formats.TeleportRequest, chat.Sender, chat.Body, timestamp),
            ChatKind.Chat => Expand(formats.PublicMessage, chat.Sender, chat.Body, timestamp),
            _ => raw,
        };
    }

    /// <summary>Expands {username}, {message} and {timestamp}, then trims.</summary>
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

    /// <summary>Strips every section code, for plain-text Telegram messages where color is wrong.</summary>
    public static string StripCodes(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        // Background escapes first: "§§7" is three chars, and the single-code pass would eat only the first two and leave the "7" behind.
        return RemainingCodes().Replace(BackgroundCodes().Replace(text, string.Empty), string.Empty);
    }

    /// <summary>
    /// Picks the Telegram reply body for a dispatched command: what the command printed wins, because that is what the console shows; the one-line result message is the fallback.
    /// Both arrive stripped.
    /// </summary>
    public static string ReplyBody(string? captured, string? message)
    {
        if (!string.IsNullOrWhiteSpace(captured))
            return StripCodes(captured).Trim();

        return StripCodes(message).Trim();
    }

    /// <summary>True when the bridge may send a message OUT to Telegram in this direction.</summary>
    public static bool CanSendOut(BridgeDirection direction) => direction != BridgeDirection.Minecraft;

    /// <summary>True when the bridge may relay an inbound Telegram message INTO Minecraft chat.</summary>
    public static bool CanRelayIn(BridgeDirection direction) => direction != BridgeDirection.Telegram;

    /// <summary>True when the chat id may drive the bridge. An empty allow-list authorizes nobody.</summary>
    public static bool IsAuthorized(long chatId, IReadOnlyList<long> authorized)
    {
        ArgumentNullException.ThrowIfNull(authorized);
        foreach (long id in authorized)
        {
            if (id == chatId)
                return true;
        }

        return false;
    }

    /// <summary>Classifies an inbound Telegram message into what the bridge should do with it.</summary>
    public static TelegramAction ClassifyInbound(string? text, BridgeDirection direction)
    {
        if (string.IsNullOrWhiteSpace(text))
            return TelegramAction.Ignore;

        string trimmed = text.Trim();
        if (trimmed.Contains("/start", StringComparison.OrdinalIgnoreCase))
            return TelegramAction.Ignore;

        if (trimmed.Contains(".chatid", StringComparison.OrdinalIgnoreCase))
            return TelegramAction.ReportChatId;

        if (trimmed.StartsWith('.'))
        {
            string command = trimmed[1..];
            if (command.Contains("quit", StringComparison.OrdinalIgnoreCase)
                || command.Contains("exit", StringComparison.OrdinalIgnoreCase))
                return TelegramAction.RefuseQuit;

            return TelegramAction.RunCommand;
        }

        // Telegram-only direction relays nothing into Minecraft; bridge commands above still work.
        return CanRelayIn(direction) ? TelegramAction.SendChat : TelegramAction.Ignore;
    }

    // The section sign is written as an escape so the source stays pure ASCII.
    [GeneratedRegex("\u00a7\u00a7.")]
    private static partial Regex BackgroundCodes();

    [GeneratedRegex("\u00a7.")]
    private static partial Regex RemainingCodes();
}

/// <summary>What the bridge does with one inbound Telegram message.</summary>
public enum TelegramAction
{
    /// <summary>Drop it.</summary>
    Ignore,

    /// <summary>Reply with the chat id (the discovery helper).</summary>
    ReportChatId,

    /// <summary>Run it as an internal MCC command and reply with the result.</summary>
    RunCommand,

    /// <summary>Refuse it: quitting the client from chat is not allowed.</summary>
    RefuseQuit,

    /// <summary>Send it to the Minecraft server as chat.</summary>
    SendChat,
}
