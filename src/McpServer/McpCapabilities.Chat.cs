using DMCBK.Core.Commands;

namespace DMCBK.OfficialPlugins.McpServer;

/// <summary>Chat, command, respawn and session-control tools.</summary>
public sealed partial class McpCapabilities
{
    /// <summary>
    /// mcc_send_chat: chat-only send.
    /// A leading <c>/</c> is refused: server commands go through <c>mcc_server_command</c> so chat and command execution never share one tool.
    /// </summary>
    public async Task<McpResult> SendChatAsync(string text, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.ChatAndCommands);
        if (!gate.Success)
            return gate;

        if (string.IsNullOrWhiteSpace(text))
            return McpResult.Fail(McpErrorCodes.InvalidArgs, "text must not be empty.");

        string trimmed = text.Trim();
        if (trimmed.StartsWith('/'))
        {
            return McpResult.Fail(
                McpErrorCodes.CommandTextBlocked,
                "mcc_send_chat sends chat text only. Use mcc_server_command for server commands.");
        }

        if (string.Equals(trimmed, "quit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "exit", StringComparison.OrdinalIgnoreCase))
        {
            return McpResult.Fail(
                McpErrorCodes.CommandTextBlocked,
                "Quitting through chat text is blocked. Shut the client down from its host console.");
        }

        try
        {
            await _context.Game.Chat.SendAsync(trimmed, ct).ConfigureAwait(false);
            return McpResult.Ok(new { sent = true });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>
    /// mcc_server_command: runs one <c>/</c> command on the Minecraft server as the player.
    /// Separate from <c>mcc_send_chat</c> on purpose: operator commands need a deliberate tool call, not a chat string that happens to start with a slash.
    /// </summary>
    public async Task<McpResult> SendServerCommandAsync(string command, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.ChatAndCommands);
        if (!gate.Success)
            return gate;

        if (string.IsNullOrWhiteSpace(command))
            return McpResult.Fail(McpErrorCodes.InvalidArgs, "command must not be empty.");

        string line = command.Trim();
        if (!line.StartsWith('/'))
            line = "/" + line;

        try
        {
            await _context.Game.Chat.SendAsync(line, ct).ConfigureAwait(false);
            return McpResult.Ok(new
            {
                sent = true,
                effectNote = "Completion is the send, not the effect: verify through state, chat history, or another direct observation.",
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>
    /// mcc_respawn: requests a respawn.
    /// The outcome names what the server is known to have done (confirmed vs. not-dead), instead of reporting the completed send as a respawn.
    /// </summary>
    public async Task<McpResult> RespawnAsync(CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.ChatAndCommands);
        if (!gate.Success)
            return gate;

        try
        {
            Umpk.Client.Actions.RespawnOutcome outcome =
                await _context.Game.Player.RespawnAsync(ct).ConfigureAwait(false);
            return outcome switch
            {
                Umpk.Client.Actions.RespawnOutcome.Confirmed
                    => McpResult.Ok(new { respawned = true, outcome = outcome.ToString() }),
                Umpk.Client.Actions.RespawnOutcome.NotDead
                    => McpResult.Fail(
                        McpErrorCodes.InvalidState,
                        "The player is alive; the server ignored the respawn request.",
                        new { outcome = outcome.ToString() }),
                _ => McpResult.Fail(
                    McpErrorCodes.ActionIncomplete,
                    "No respawn was observed inside the confirmation window.",
                    new { outcome = outcome.ToString() }),
            };
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>
    /// mcc_disconnect: disconnects from the server without stopping the MCP endpoint.
    /// The next session-dependent tool reports <c>disconnected</c>; the transport itself stays up by design.
    /// </summary>
    public Task<McpResult> DisconnectAsync(CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.ChatAndCommands);
        if (!gate.Success)
            return Task.FromResult(gate);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(150, ct).ConfigureAwait(false);
                await _context.Client.StopAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }, CancellationToken.None);

        return Task.FromResult(McpResult.Ok(new { disconnecting = true }));
    }

    /// <summary>
    /// mcc_quit_client: requests a clean client shutdown through the host.
    /// The host tears down exactly as for a typed <c>quit</c> (BeforeExit hooks, disconnect, exit code 0), so agents get a real quit instead of a killed process.
    /// The MCP endpoint goes down with the client.
    /// </summary>
    public Task<McpResult> QuitClientAsync(CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.ChatAndCommands);
        if (!gate.Success)
            return Task.FromResult(gate);

        _context.Client.RequestShutdown();
        return Task.FromResult(McpResult.Ok(new { quitting = true }));
    }

    /// <summary>
    /// mcc_run_internal_command: runs one internal MCC command (no leading slash, <c>%var%</c> expanded) and returns its result plus captured output.
    /// Server commands and the destructive client commands (plugin load/install, scripts, connect, reload, quit, ...) are refused.
    /// </summary>
    public async Task<McpResult> RunInternalCommandAsync(string command, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.ChatAndCommands);
        if (!gate.Success)
            return gate;

        if (string.IsNullOrWhiteSpace(command))
            return McpResult.Fail(McpErrorCodes.InvalidArgs, "command must not be empty.");

        string line = command.Trim().TrimStart('/');
        if (line.StartsWith('/'))
            line = line.TrimStart('/');

        // DispatchAsync takes an already-unprefixed, already-expanded command (no server fallthrough): expand here so %variables% behave like typed input.
        line = _context.Variables.Expand(line);

        string head = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (BlockedInternalCommands.Contains(head))
        {
            return McpResult.Fail(
                McpErrorCodes.InvalidArgs,
                $"The '{head}' command is not available through MCP (remote code execution or session pivot).");
        }

        try
        {
            (CmdResult Result, string Output) answered = await _context.Client.Commands
                .DispatchCapturedAsync(line, ct).ConfigureAwait(false);
            return McpResult.Ok(new
            {
                success = answered.Result.IsSuccess,
                status = answered.Result.Status.ToString(),
                output = answered.Output,
                message = answered.Result.Message,
            });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_animation: swings the main or off hand.</summary>
    public async Task<McpResult> PlayAnimationAsync(string hand, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.Movement);
        if (!gate.Success)
            return gate;

        if (!TryParseHand(hand, out Umpk.Client.Actions.Hand parsedHand))
            return McpResult.Fail(McpErrorCodes.InvalidArgs, "hand must be MainHand or OffHand.");

        try
        {
            await _context.Game.Movement
                .SwingAsync(parsedHand == Umpk.Client.Actions.Hand.Main, ct).ConfigureAwait(false);
            return McpResult.Ok(new { success = true, hand });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_toggle_sneak: sets the sneak input state.</summary>
    public async Task<McpResult> ToggleSneakAsync(bool enabled, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.Movement);
        if (!gate.Success)
            return gate;

        try
        {
            await _context.Game.Movement.SetSneakingAsync(enabled, ct).ConfigureAwait(false);
            return McpResult.Ok(new { success = true, sneaking = enabled });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }

    /// <summary>mcc_toggle_sprint: sets the sprint input state.</summary>
    public async Task<McpResult> ToggleSprintAsync(bool enabled, CancellationToken ct)
    {
        McpResult gate = Guard(McpCapability.Movement);
        if (!gate.Success)
            return gate;

        try
        {
            await _context.Game.Movement.SetSprintingAsync(enabled, ct).ConfigureAwait(false);
            return McpResult.Ok(new { success = true, sprinting = enabled });
        }
        catch (Exception ex)
        {
            return MapSessionException(ex);
        }
    }
}
