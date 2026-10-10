// ChatLog - appends every received line to a text file in the plugin's data/ folder, one line per message, with the filter mode deciding which kinds are kept.
//
// One gap worth knowing about: the internal_msg filter logs nothing.
// CommandService raises no event for internal command results, so a plugin cannot observe them.
// The mode is still accepted, so an existing config keeps loading, and it says so once at activation.

using System.Globalization;
using System.Text;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Tomlet.Attributes;
using Umpk.Client.Events;

namespace DMCBK.OfficialPlugins.ChatLog;

/// <summary>The message filter modes, spelled as the config spells them.</summary>
public enum MessageFilter
{
    /// <summary>Everything.</summary>
    All,

    /// <summary>Public chat and private messages.</summary>
    Messages,

    /// <summary>Public chat only.</summary>
    Chat,

    /// <summary>Private messages only.</summary>
    PrivateChat,

    /// <summary>Internal command results only.</summary>
    InternalMsg,
}

/// <summary>What one filter mode saves.</summary>
/// <param name="Chat">Save public chat.</param>
/// <param name="Private">Save private messages.</param>
/// <param name="Other">Save anything else.</param>
/// <param name="Internal">Save internal command results.</param>
public readonly record struct FilterSelection(bool Chat, bool Private, bool Other, bool Internal);

/// <summary>The filter and format decisions of ChatLog.</summary>
public static class ChatLogFormat
{
    /// <summary>The timestamp format written in front of a line.</summary>
    public const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>Parses a filter mode; an unknown value falls back to messages.</summary>
    public static bool TryParseFilter(string? value, out MessageFilter filter)
    {
        filter = MessageFilter.Messages;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        switch (value.Trim().ToLowerInvariant())
        {
            case "all":
                filter = MessageFilter.All;
                return true;
            case "messages":
                filter = MessageFilter.Messages;
                return true;
            case "chat":
                filter = MessageFilter.Chat;
                return true;
            case "private_chat":
                filter = MessageFilter.PrivateChat;
                return true;
            case "internal_msg":
                filter = MessageFilter.InternalMsg;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Renders a filter mode as the config spells it.</summary>
    public static string FilterName(MessageFilter filter) => filter switch
    {
        MessageFilter.All => "all",
        MessageFilter.Chat => "chat",
        MessageFilter.PrivateChat => "private_chat",
        MessageFilter.InternalMsg => "internal_msg",
        _ => "messages",
    };

    /// <summary>Which message kinds each mode saves.</summary>
    public static FilterSelection Select(MessageFilter filter) => filter switch
    {
        MessageFilter.All => new FilterSelection(Chat: true, Private: true, Other: true, Internal: true),
        MessageFilter.Chat => new FilterSelection(Chat: true, Private: false, Other: false, Internal: true),
        MessageFilter.PrivateChat => new FilterSelection(Chat: false, Private: true, Other: false, Internal: true),
        MessageFilter.InternalMsg => new FilterSelection(Chat: false, Private: false, Other: false, Internal: true),
        _ => new FilterSelection(Chat: true, Private: true, Other: false, Internal: true),
    };

    /// <summary>
    /// Builds the log line for one classified message, or null when the filter drops it.
    /// The order of the tests matters: chat, then private, then the catch-all "Other".
    /// </summary>
    public static string? BuildLine(FilterSelection selection, ChatKind kind, string? sender, string body, string raw)
    {
        if (selection.Chat && kind == ChatKind.Chat)
            return string.Create(CultureInfo.InvariantCulture, $"Chat {sender ?? string.Empty}: {body}");

        if (selection.Private && kind == ChatKind.PrivateMessage)
            return string.Create(CultureInfo.InvariantCulture, $"Private {sender ?? string.Empty}: {body}");

        if (selection.Other)
            return string.Create(CultureInfo.InvariantCulture, $"Other: {raw}");

        return null;
    }

    /// <summary>Prepends the timestamp when requested.</summary>
    public static string ApplyTimestamp(string line, bool addDateTime, DateTime now)
        => addDateTime
            ? string.Concat(now.ToString(TimestampFormat, CultureInfo.InvariantCulture), " ", line)
            : line;

    /// <summary>
    /// Expands the %username% and %serverip% placeholders, then replaces the characters a file system will not take.
    /// </summary>
    public static string ExpandFileName(string template, string username, string serverIp)
    {
        ArgumentNullException.ThrowIfNull(template);
        string expanded = template
            .Replace("%username%", username, StringComparison.OrdinalIgnoreCase)
            .Replace("%serverip%", serverIp, StringComparison.OrdinalIgnoreCase);

        var builder = new StringBuilder(expanded.Length);
        foreach (char c in expanded)
            builder.Append(char.IsControl(c) || c == ':' || c == '*' || c == '?' || c == '"' ? '_' : c);

        return builder.ToString();
    }
}


/// <summary>The persisted settings for ChatLog.</summary>
public sealed class ChatLogSettings : IValidatablePluginSettings
{
    /// <summary>Master switch.</summary>
    [TomlInlineComment("$settings.enabled$")]
    public bool Enabled { get; set; }

    /// <summary>Whether to prepend a yyyy-MM-dd HH:mm:ss timestamp to every line.</summary>
    [TomlInlineComment("$settings.add_datetime$")]
    public bool Add_DateTime { get; set; } = true;

    /// <summary>The log file, resolved inside the plugin's data/ folder.</summary>
    [TomlInlineComment("$settings.log_file$")]
    public string Log_File { get; set; } = "chatlog-%username%-%serverip%.txt";

    /// <summary>The filter mode: all, messages, chat, private_chat or internal_msg.</summary>
    [TomlInlineComment("$settings.filter$")]
    public string Filter { get; set; } = "messages";

    /// <summary>Normalizes on load or reload.</summary>
    public void Validate()
    {
        Log_File ??= string.Empty;
        if (Log_File.Trim().Length == 0)
            Log_File = "chatlog-%username%-%serverip%.txt";

        Filter = ChatLogFormat.FilterName(
            ChatLogFormat.TryParseFilter(Filter, out MessageFilter parsed) ? parsed : MessageFilter.Messages);
    }
}

/// <summary>The ChatLog plugin entry point.</summary>
public sealed class ChatLogPlugin : IPlugin
{
    private readonly object _fileGate = new();
    private ChatLogSettings _settings = new();
    private FilterSelection _selection = ChatLogFormat.Select(MessageFilter.Messages);

    /// <inheritdoc/>
    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "chat-log";
        descriptor.Version = "2.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
        descriptor.WithSettings<ChatLogSettings>();
    }

    /// <inheritdoc/>
    public Task ActivateAsync(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _settings = context.Settings.Load<ChatLogSettings>();

        if (!ChatLogFormat.TryParseFilter(_settings.Filter, out MessageFilter filter))
        {
            context.Logger.LogWarning("{Message}", context.Strings.Format("unknown_filter", _settings.Filter));
            filter = MessageFilter.Messages;
        }

        _selection = ChatLogFormat.Select(filter);
        if (_settings.Enabled && filter == MessageFilter.InternalMsg)
            context.Logger.LogWarning("{Message}", context.Strings.Get("internal_unavailable"));

        // Re-subscribe on every session, so a reconnect keeps logging.
        context.SessionStarted += (_, e) => e.Session.Events.Subscribe<ChatMessageReceived>(
            message => OnChat(context, message));
        return Task.CompletedTask;
    }

    private void OnChat(PluginContext context, ChatMessageReceived message)
    {
        if (!_settings.Enabled)
            return;

        ClassifiedChat classified = context.Chat.Classify(message);
        string? line = ChatLogFormat.BuildLine(
            _selection, classified.Kind, classified.Sender, classified.Body, classified.Raw);
        if (line is null)
            return;

        Save(context, ChatLogFormat.ApplyTimestamp(line, _settings.Add_DateTime, DateTime.Now));
    }

    private void Save(PluginContext context, string line)
    {
        string username = context.CurrentSession?.State.Self.Username ?? string.Empty;
        string serverIp = context.Client.Configuration is { } config
            ? string.Create(CultureInfo.InvariantCulture, $"{config.ResolvedHost}-{config.ResolvedPort}")
            : string.Empty;

        string name = ChatLogFormat.ExpandFileName(_settings.Log_File, username, serverIp);
        try
        {
            string path = context.Storage.GetPath(name);
            lock (_fileGate)
                File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (ArgumentException ex)
        {
            context.Logger.LogWarning(ex, "{Message}", context.Strings.Format("invalid_file", name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            context.Logger.LogWarning(ex, "{Message}", context.Strings.Format("write_failed", name));
        }
    }
}
