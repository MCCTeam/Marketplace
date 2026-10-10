// TelegramBridge relays Minecraft chat into a Telegram chat and Telegram messages back into the game, and runs internal MCC commands sent from an authorized chat with a "." prefix.
// It needs a bot token from
// @BotFather; with no token configured the plugin self-disables and says so.
//
// The bot client is a client-lifetime resource: created once in ActivateAsync and disposed once, so a reconnect never leaves a second long-poll receiver running.
// Only the chat subscription is per session.

using System.Collections.Concurrent;
using System.Globalization;
using DMCBK.Core.Commands;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Tomlet.Attributes;
using Umpk.Client.Events;
using Umpk.Commands;

namespace DMCBK.OfficialPlugins.TelegramBridge;

/// <summary>The user-editable, persisted settings for TelegramBridge (serialized to settings.toml).</summary>
public sealed class TelegramBridgeSettings : IValidatablePluginSettings
{
    /// <summary>The shipped placeholder token, treated as "unset".</summary>
    public const string TokenPlaceholder = "your bot token here";

    /// <summary>Links a string setting to an environment variable: "env:NAME".</summary>
    public const string EnvPrefix = "env:";

    /// <summary>Master switch for the plugin.</summary>
    [TomlInlineComment("$settings.enabled$")]
    public bool Enabled { get; set; } = true;

    /// <summary>The Telegram bot token from BotFather, or "env:NAME" to read it from the environment.</summary>
    [TomlInlineComment("$settings.token$")]
    public string Token { get; set; } = string.Empty;

    /// <summary>The resolved token. Never serialized: Save writes the reference back, not the secret.</summary>
    [TomlNonSerialized]
    public string ResolvedToken { get; set; } = string.Empty;

    /// <summary>The name of the linked variable, for diagnostics. Never serialized.</summary>
    [TomlNonSerialized]
    public string? TokenEnvVar { get; set; }

    /// <summary>The chat or channel id messages are relayed to.</summary>
    [TomlInlineComment("$settings.channel_id$")]
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>The chat ids allowed to drive the bridge.</summary>
    [TomlPrecedingComment("$settings.authorized_chat_ids$")]
    public long[] Authorized_Chat_Ids { get; set; } = [];

    /// <summary>How long, in seconds, a send may take before it is abandoned.</summary>
    [TomlInlineComment("$settings.message_send_timeout$")]
    public int Message_Send_Timeout { get; set; } = 3;

    /// <summary>Template for a whisper directed at the client.</summary>
    [TomlPrecedingComment("$settings.formats$")]
    public string PrivateMessageFormat { get; set; } = "*(Private Message)* {username}: {message}";

    /// <summary>Template for a normal chat line.</summary>
    public string PublicMessageFormat { get; set; } = "{username}: {message}";

    /// <summary>Template for a teleport request.</summary>
    public string TeleportRequestMessageFormat { get; set; } = "A new Teleport Request from **{username}**!";

    /// <summary>Normalizes on load or reload, and self-disables when no token is configured.</summary>
    public void Validate()
    {
        Token ??= string.Empty;
        ChannelId ??= string.Empty;
        Authorized_Chat_Ids ??= [];
        PrivateMessageFormat ??= string.Empty;
        PublicMessageFormat ??= string.Empty;
        TeleportRequestMessageFormat ??= string.Empty;

        if (Message_Send_Timeout <= 0)
            Message_Send_Timeout = 3;

        TokenEnvVar = EnvVarName(Token);
        ResolvedToken = ResolveToken(Token);

        if (!HasToken(ResolvedToken))
            Enabled = false;
    }

    /// <summary>The name of the linked variable, or null when the value is a literal token.</summary>
    public static string? EnvVarName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string text = value.Trim();
        if (!text.StartsWith(EnvPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        string name = text[EnvPrefix.Length..].Trim();
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

    /// <summary>True when the configured token is a real one and not blank or the shipped placeholder.</summary>
    public static bool HasToken(string? token)
        => !string.IsNullOrWhiteSpace(token)
            && !string.Equals(token.Trim(), TokenPlaceholder, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The TelegramBridge plugin entry point.</summary>
public sealed class TelegramBridgePlugin : IPlugin
{
    private readonly ConcurrentQueue<(string Path, string Caption)> _pendingImages = new();
    private readonly object _gate = new();

    private TelegramBridgeSettings _settings = new();
    private PluginContext? _context;
    private ILogger? _logger;
    private IPluginLocalization? _strings;
    private TelegramBotClient? _botClient;
    private CancellationTokenSource? _receiveCts;
    private IDisposable? _imageSubscription;
    private BridgeDirection _direction = BridgeDirection.Both;
    private BridgeFormats _formats = new(string.Empty, string.Empty, string.Empty);

    /// <summary>True once the long-poll receiver is running.</summary>
    public bool IsConnected { get; private set; }

    /// <inheritdoc/>
    public void Configure(PluginDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        descriptor.Id = "telegram-bridge";
        descriptor.Version = "2.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
        descriptor.WithSettings<TelegramBridgeSettings>();
    }

    /// <inheritdoc/>
    public Task ActivateAsync(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _settings = context.Settings.Load<TelegramBridgeSettings>();
        _context = context;
        _logger = context.Logger;
        _strings = context.Strings;
        _formats = new BridgeFormats(
            _settings.PrivateMessageFormat, _settings.PublicMessageFormat, _settings.TeleportRequestMessageFormat);

        // Registered once, not per session, so the command still answers with a reason when the bridge is disabled.
        // Use the plugin command scope, not RegisterHostCommand: that one has no unregister and would keep this instance, and the plugin's whole load context, rooted in the dispatcher on unload.
        context.Commands.Register(new TelegramBridgeCommand(this, context.Strings));

        if (!_settings.Enabled)
        {
            _logger?.LogInformation("{Message}", MissingTokenText());
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(_settings.ChannelId))
            _logger?.LogWarning("{Message}", _strings?.Get("missing_channel_id"));

        if (_settings.Authorized_Chat_Ids.Length == 0)
            _logger?.LogWarning("{Message}", _strings?.Get("missing_authorized_chats"));

        // Images handed over by the Map plugin.
        // Cross-plugin messages are BCL-typed because each plugin loads in its own collectible context.
        _imageSubscription = context.Messenger.Subscribe<string[]>(OnBridgeMessage);

        context.SessionStarted += (_, e) => OnSessionStarted(e.Session);

        StartReceiver();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cancels the long-poll receiver and releases the bot client.
    /// Without this the receiver keeps polling Telegram and calling back into an unloaded assembly.
    /// </summary>
    public Task DeactivateAsync(CancellationToken ct)
    {
        _imageSubscription?.Dispose();
        _imageSubscription = null;
        Disconnect();
        return Task.CompletedTask;
    }

    private void OnSessionStarted(ISessionScope session)
    {
        // Released at session end, so a reconnect never stacks two subscriptions.
        session.Events.Subscribe<ChatMessageReceived>(OnChatMessage);
    }

    private void OnChatMessage(ChatMessageReceived message)
    {
        if (_context is not { } context || !BridgeFormatting.CanSendOut(_direction) || !CanSend())
            return;

        ClassifiedChat classified = context.Chat.Classify(message);
        string? text = BridgeFormatting.Render(classified, _formats, DateTime.Now);
        string stripped = BridgeFormatting.StripCodes(text);
        if (!string.IsNullOrEmpty(stripped))
            _ = SendTextAsync(stripped, useMarkdown: false);
    }

    // Telegram takes text, never null, and the table is only null before activation.
    private string Text(string key) => _strings?.Get(key) ?? key;

    private string MissingTokenText()
        => _settings.TokenEnvVar is { } name
            ? Format("token_env_missing", name)
            : Text("missing_token");

    private string Format(string key, params object?[] args) => _strings?.Format(key, args) ?? key;

    private void StartReceiver()
    {
        try
        {
            var client = new TelegramBotClient(_settings.ResolvedToken.Trim());
            var cts = new CancellationTokenSource();

            client.StartReceiving(
                HandleUpdateAsync,
                HandlePollingErrorAsync,
                new ReceiverOptions { AllowedUpdates = [] },
                cts.Token);

            lock (_gate)
            {
                _botClient = client;
                _receiveCts = cts;
            }

            IsConnected = true;
            _logger?.LogInformation("{Message}", _strings?.Get("connected"));
            _ = SendTextAsync(Text("connected_notice"), useMarkdown: true);
            FlushPendingImages();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            IsConnected = false;
            _logger?.LogWarning(ex, "{Message}", _strings?.Format("unknown_error", ex.Message));
        }
    }

    private void Disconnect()
    {
        TelegramBotClient? client;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            client = _botClient;
            cts = _receiveCts;
            _botClient = null;
            _receiveCts = null;
        }

        if (client is null)
            return;

        try
        {
            client.SendMessage(_settings.ChannelId.Trim(), Text("disconnected_notice"))
                .Wait(TimeSpan.FromSeconds(_settings.Message_Send_Timeout));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogDebug(ex, "{Message}", _strings?.Get("canceled_sending"));
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The receiver was already torn down.
        }
        finally
        {
            cts?.Dispose();
            IsConnected = false;
        }
    }

    private bool CanSend()
        => IsConnected && _botClient is not null && !string.IsNullOrWhiteSpace(_settings.ChannelId);

    private async Task SendTextAsync(string text, bool useMarkdown)
    {
        TelegramBotClient? client;
        lock (_gate)
            client = _botClient;

        if (client is null || string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(_settings.ChannelId))
            return;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.Message_Send_Timeout));
            await client.SendMessage(
                    _settings.ChannelId.Trim(),
                    text,
                    parseMode: useMarkdown ? ParseMode.Markdown : ParseMode.None,
                    cancellationToken: timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogDebug(ex, "{Message}", _strings?.Get("canceled_sending"));
        }
    }

    /// <summary>Handles a cross-plugin message; only the image requests addressed to this bridge are ours.</summary>
    private void OnBridgeMessage(string[] message)
    {
        if (!TryReadImageRequest(message, out string path, out string caption))
            return;

        if (!CanSend())
        {
            // The bridge is not up yet, so hold the request until the receiver starts.
            _pendingImages.Enqueue((path, caption));
            return;
        }

        _ = SendImageAsync(path, caption);
    }

    /// <summary>Reads the Map plugin's tagged image request, if this message is one addressed to Telegram.</summary>
    public static bool TryReadImageRequest(string[]? message, out string filePath, out string caption)
    {
        filePath = string.Empty;
        caption = string.Empty;
        // Five-element messages carry a Discord/Telegram destination the Telegram bridge ignores.
        if (message is not { Length: 4 } and not { Length: 5 }
            || !string.Equals(message[0], "mcc.plugins.bridge.image.v1", StringComparison.Ordinal)
            || !string.Equals(message[1], "telegram", StringComparison.Ordinal))
            return false;

        filePath = message[2];
        caption = message[3];
        return true;
    }

    private void FlushPendingImages()
    {
        while (CanSend() && _pendingImages.TryDequeue(out (string Path, string Caption) pending))
            _ = SendImageAsync(pending.Path, pending.Caption);
    }

    private async Task SendImageAsync(string filePath, string? caption)
    {
        TelegramBotClient? client;
        lock (_gate)
            client = _botClient;

        if (client is null || !File.Exists(filePath))
            return;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.Message_Send_Timeout));
            await using FileStream stream = File.OpenRead(filePath);
            await client.SendDocument(
                    _settings.ChannelId.Trim(),
                    document: InputFile.FromStream(stream, Path.GetFileName(filePath)),
                    caption: caption,
                    parseMode: ParseMode.Markdown,
                    cancellationToken: timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger?.LogDebug(ex, "{Message}", _strings?.Get("canceled_sending"));
        }
    }

    private async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken ct)
    {
        if (update.Message is not { } message || message.Text is not { } text || _context is not { } context)
            return;

        long chatId = message.Chat.Id;
        TelegramAction action = BridgeFormatting.ClassifyInbound(text, _direction);

        if (action == TelegramAction.ReportChatId)
        {
            await client.SendMessage(
                    chatId,
                    Format("chat_id_reply", chatId),
                    replyParameters: message.MessageId,
                    cancellationToken: ct)
                .ConfigureAwait(false);
            return;
        }

        if (action == TelegramAction.Ignore)
            return;

        if (!BridgeFormatting.IsAuthorized(chatId, _settings.Authorized_Chat_Ids))
        {
            _logger?.LogDebug("{Message}", _strings?.Format("unauthorized", chatId));
            await client.SendMessage(
                    chatId,
                    Text("unauthorized_reply"),
                    replyParameters: message.MessageId,
                    cancellationToken: ct)
                .ConfigureAwait(false);
            return;
        }

        switch (action)
        {
            case TelegramAction.RefuseQuit:
                await client.SendMessage(
                        chatId, Text("quit_disabled"), replyParameters: message.MessageId,
                        cancellationToken: ct)
                    .ConfigureAwait(false);
                return;

            case TelegramAction.RunCommand:
                // The capture is the reply: commands that print their body and return a bare Done read the same from Telegram as they do on the console.
                // The one-line result is the fallback when the command printed nothing.
                (CmdResult result, string captured) = await context.Client.Commands
                    .DispatchCapturedAsync(text.Trim()[1..], ct).ConfigureAwait(false);
                string body = BridgeFormatting.ReplyBody(captured, result.Message ?? result.Status.ToString());
                if (body.Length == 0)
                    body = result.Status.ToString();

                await client.SendMessage(
                        chatId,
                        Format("command_executed", body),
                        replyParameters: message.MessageId,
                        cancellationToken: ct)
                    .ConfigureAwait(false);
                return;

            case TelegramAction.SendChat:
                if (context.InSession)
                    await context.Game.Chat.SendAsync(text.Trim(), ct).ConfigureAwait(false);

                return;

            default:
                return;
        }
    }

    private Task HandlePollingErrorAsync(ITelegramBotClient client, Exception exception, CancellationToken ct)
    {
        string reason = exception is ApiRequestException api
            ? string.Format(CultureInfo.InvariantCulture, "[{0}] {1}", api.ErrorCode, api.Message)
            : exception.Message;
        _logger?.LogWarning("{Message}", _strings?.Format("polling_error", reason));
        return Task.CompletedTask;
    }

    /// <summary>The <c>tgbridge direction</c> command body.</summary>
    internal int SetDirection(CommandContext ctx, BridgeDirection direction, IPluginLocalization strings)
    {
        if (!_settings.Enabled)
            return ctx.Result.Fail(strings.Get("disabled_short"));

        _direction = direction;
        string name = direction switch
        {
            BridgeDirection.Minecraft => strings.Get("direction_minecraft"),
            BridgeDirection.Telegram => strings.Get("direction_telegram"),
            _ => strings.Get("direction_both"),
        };

        return ctx.Result.Ok(strings.Format("direction", name));
    }
}

/// <summary>The <c>tgbridge</c> command: which way the bridge relays.</summary>
internal sealed class TelegramBridgeCommand(TelegramBridgePlugin plugin, IPluginLocalization strings) : CommandBase
{
    private readonly TelegramBridgePlugin _plugin = plugin;
    private readonly IPluginLocalization _strings = strings;

    /// <inheritdoc/>
    public override string CmdName => "tgbridge";

    /// <inheritdoc/>
    public override string CmdDesc => _strings.Get("command_desc");

    /// <inheritdoc/>
    public override string CmdUsage => _strings.Get("command_usage");

    /// <inheritdoc/>
    public override void Register(CommandBuilder<CommandContext> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        CommandNodeBuilder<CommandContext> help = RegisterHelp(builder, CmdName, ShowUsage);

        builder.Literal(CmdName, l => l
            .ThenLiteral("direction", h => h
                .ThenLiteral("both", a => a
                    .Executes(ctx => _plugin.SetDirection(ctx.Source, BridgeDirection.Both, _strings)))
                .ThenLiteral("mc", a => a
                    .Executes(ctx => _plugin.SetDirection(ctx.Source, BridgeDirection.Minecraft, _strings)))
                .ThenLiteral("telegram", a => a
                    .Executes(ctx => _plugin.SetDirection(ctx.Source, BridgeDirection.Telegram, _strings))))
            .ThenLiteral("_help", h => h
                .Executes(ctx => ShowUsage(ctx.Source))
                .RedirectTo(help)));
    }
}
