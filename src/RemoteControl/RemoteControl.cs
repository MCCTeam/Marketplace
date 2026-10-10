// RemoteControl runs internal MCC commands on behalf of a remote player, so the authorisation check is the whole of it:
//
//   * a line is acted on ONLY when it is a private message (whisper) addressed to this client,
//   * AND the sender, trimmed and lowercased, is in permissions.bot-owners,
//   * the whole private-message body is then run as a command and the result is whispered back,
//   * a teleport request is auto-accepted only when AutoTpaccept is on AND (AutoTpaccept_Everyone is on OR
// the requesting player passes the same owner check),
//   * public chat is never acted on, and an empty owner list authorises nobody.
//
// Both sides of the name comparison are folded with ToLowerInvariant.
// Culture-sensitive folding is not safe here: under a Turkish locale ToLower() maps 'I' to a dotless 'i', so two different names can compare equal.

using System.Globalization;
using DMCBK.Core.Commands;
using DMCBK.PluginSdk;
using Microsoft.Extensions.Logging;
using Tomlet.Attributes;
using Umpk.Client.Events;

namespace DMCBK.OfficialPlugins.RemoteControl;

/// <summary>What RemoteControl decided to do with one incoming line.</summary>
public enum RemoteAction
{
    /// <summary>Ignore the line.</summary>
    Ignore,

    /// <summary>Run the body as an internal command and reply to the sender.</summary>
    RunCommand,

    /// <summary>Accept a teleport request.</summary>
    AcceptTeleport,
}

/// <summary>The authorisation and routing decisions, kept pure so the security rules are unit-testable.</summary>
public static class RemoteControlAuth
{
    /// <summary>
    /// True when the sender is a configured bot owner.
    /// Both sides are trimmed and lowercased; a blank sender or an empty owner list authorises nobody.
    /// </summary>
    public static bool IsOwner(string? sender, IReadOnlyCollection<string>? botOwners)
    {
        if (string.IsNullOrWhiteSpace(sender) || botOwners is null || botOwners.Count == 0)
            return false;

        string normalized = sender.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return false;

        foreach (string owner in botOwners)
        {
            if (owner is null)
                continue;

            if (string.Equals(owner.Trim().ToLowerInvariant(), normalized, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Decides what to do with one classified line.
    /// Public chat, system lines and unknown lines are always ignored: only a private message from an owner runs a command.
    /// </summary>
    public static RemoteAction Decide(
        ChatKind kind,
        string? sender,
        string body,
        IReadOnlyCollection<string>? botOwners,
        bool autoTpaccept,
        bool autoTpacceptEveryone)
    {
        if (kind == ChatKind.PrivateMessage)
        {
            return IsOwner(sender, botOwners) && !string.IsNullOrWhiteSpace(body)
                ? RemoteAction.RunCommand
                : RemoteAction.Ignore;
        }

        if (kind == ChatKind.TeleportRequest && autoTpaccept)
        {
            return autoTpacceptEveryone || IsOwner(sender, botOwners)
                ? RemoteAction.AcceptTeleport
                : RemoteAction.Ignore;
        }

        return RemoteAction.Ignore;
    }

    /// <summary>Builds the whisper that carries a command result back to the sender.</summary>
    public static string BuildReply(string privateMessageCommand, string sender, string message)
    {
        string command = string.IsNullOrWhiteSpace(privateMessageCommand) ? "tell" : privateMessageCommand.Trim();
        return string.Create(CultureInfo.InvariantCulture, $"/{command} {sender} {message}");
    }
}

/// <summary>The user-editable, persisted settings for RemoteControl (serialized to settings.toml).</summary>
public sealed class RemoteControlSettings
{
    /// <summary>Master switch.</summary>
    [TomlInlineComment("$settings.enabled$")]
    public bool Enabled { get; set; }

    /// <summary>Auto-accept teleport requests.</summary>
    [TomlInlineComment("$settings.auto_tpaccept$")]
    public bool AutoTpaccept { get; set; } = true;

    /// <summary>Accept teleport requests from anyone, not only from bot owners.</summary>
    [TomlInlineComment("$settings.auto_tpaccept_everyone$")]
    public bool AutoTpaccept_Everyone { get; set; }
}

/// <summary>The RemoteControl plugin entry point.</summary>
public sealed class RemoteControlPlugin : IPlugin
{
    private RemoteControlSettings _settings = new();

    /// <inheritdoc/>
    public void Configure(PluginDescriptor descriptor)
    {
        descriptor.Id = "remote-control";
        descriptor.Version = "2.0.0";
        descriptor.ApiVersion = PluginApiVersion.Major;
        descriptor.WithSettings<RemoteControlSettings>();
    }

    /// <inheritdoc/>
    public Task ActivateAsync(PluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _settings = context.Settings.Load<RemoteControlSettings>();

        if (_settings.Enabled && (context.Client.Configuration?.Permissions.BotOwners.Count ?? 0) == 0)
            context.Logger.LogWarning("{Message}", context.Strings.Get("no_owners"));

        context.SessionStarted += (_, e) => e.Session.Events.Subscribe<ChatMessageReceived>(
            message => OnChat(context, message));
        return Task.CompletedTask;
    }

    private void OnChat(PluginContext context, ChatMessageReceived message)
    {
        if (!_settings.Enabled)
            return;

        ClassifiedChat classified = context.Chat.Classify(message);
        IReadOnlyCollection<string>? owners = context.Client.Configuration?.Permissions.BotOwners;

        RemoteAction action = RemoteControlAuth.Decide(
            classified.Kind,
            classified.Sender,
            classified.Body,
            owners,
            _settings.AutoTpaccept,
            _settings.AutoTpaccept_Everyone);

        switch (action)
        {
            case RemoteAction.RunCommand:
                // Dispatch OFF the session loop.
                // Commands block on the loop (CommandContext.Run is sync-over-async), and this handler already runs on it, so dispatching inline deadlocks the session until the server times the client out.
                string sender = classified.Sender ?? string.Empty;
                string body = classified.Body.Trim();
                _ = Task.Run(() => RunAsync(context, sender, body));
                break;
            case RemoteAction.AcceptTeleport:
                context.Logger.LogInformation(
                    "{Message}", context.Strings.Format("accepting", classified.Sender ?? string.Empty));
                _ = SendAsync(context, "/tpaccept");
                break;
            default:
                if (classified.Kind == ChatKind.PrivateMessage)
                {
                    context.Logger.LogDebug(
                        "{Message}", context.Strings.Format("rejected", classified.Sender ?? string.Empty));
                }

                break;
        }
    }

    private static async Task RunAsync(PluginContext context, string sender, string command)
    {
        context.Logger.LogInformation("{Message}", context.Strings.Format("running", sender, command));

        string reply;
        try
        {
            CmdResult result = await context.Client.Commands.DispatchAsync(command).ConfigureAwait(false);
            reply = string.IsNullOrWhiteSpace(result.Message)
                ? context.Strings.Get("ok")
                : result.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.LogWarning(ex, "{Message}", context.Strings.Format("running", sender, command));
            return;
        }

        string privateMessageCommand = context.Client.Configuration?.Chat.PrivateMessageCommand ?? "tell";
        await SendAsync(context, RemoteControlAuth.BuildReply(privateMessageCommand, sender, reply))
            .ConfigureAwait(false);
    }

    private static async Task SendAsync(PluginContext context, string text)
    {
        try
        {
            await context.Game.Chat.SendAsync(text).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.LogWarning(ex, "{Message}", context.Strings.Get("send_failed"));
        }
    }
}
