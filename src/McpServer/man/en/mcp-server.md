# mcp-server

The `mcp-server` plugin exposes the live MCC session as a Model Context Protocol server over
Streamable HTTP, so an external AI agent (opencode, Claude Code, Cursor, Codex) can inspect the
game and act as the player.

## Quick start

1. Enable the plugin: in `plugins/mcp-server/plugin.toml` set `enabled = true`, or run
   `plugins enable mcp-server` and reload.
2. Set a token in the configurations-folder `.env` file: `MCC_MCP_AUTH_TOKEN=<at-least-32-chars>`.
   Auth is required by default and the token never lives in `settings.toml`.
3. Start the client and connect. The plugin logs the endpoint, default
   `http://127.0.0.1:33333/mcp`. Check it any time with the `mcp` command.
4. Point the agent at the URL as a remote MCP server with auth off in the agent and a bearer
   header instead. For opencode, in `opencode.json`:

```json
{
  "mcp": {
    "servers": {
      "mcc": {
        "type": "remote",
        "url": "http://127.0.0.1:33333/mcp",
        "enabled": true,
        "oauth": false,
        "headers": { "Authorization": "Bearer ${MCC_MCP_AUTH_TOKEN}" },
        "timeout": 30000
      }
    }
  }
}
```

Long tools (moves, digs, transfers) can run past the default 5 s agent timeout: keep the
per-server `timeout` at 30000 or higher.

## Behaviour

- The endpoint stays up across reconnects so agents keep one stable address. While offline,
  session-dependent tools return `success:false` with `errorCode:disconnected`; poll
  `mcc_session_status` (it carries `connected`) instead of watching the socket.
- Every tool returns `{success, errorCode, message, data}` over HTTP 200. Check `success` on
  every call: the MCP protocol error flag is never set.
- `mcc_send_chat` sends chat text only (leading `/` refused). `mcc_server_command` runs one
  server slash-command, deliberately separate.
- `mcc_run_internal_command` refuses plugin loading, scripts, server switching, config reloads
  and quit paths.
- `mcc_place_block`, `mcc_use_item_on_hand`, `mcc_use_item_on_block` and `mcc_entity_interact`
  report the send, not the effect: verify the world afterwards.
- `mcc_move_to` runs planner navigation only; there is no teleport. `allowUnsafe` lifts
  fall-damage guards (lethal falls become planable).
- `mcc_quit_client` quits the whole client through the host, exactly like a typed `quit`
  (clean teardown, exit code 0). The MCP endpoint goes down with the client, so only call it when
  the session is truly over. An interactive console idling at its prompt applies the quit on the
  next entered line; scripted and file-input runs exit immediately.
- `mcc_items_list` reports identified dropped items (unidentified ones are counted, not
  listed); `mcc_items_pickup` verifies by inventory delta.
- Movement is exclusive: when another plugin holds the lease, move tools fail with
  `movement_held` naming the holder.

## Settings

`settings.toml` holds the bind (`bind-host`, default loopback), `port` (default 33333), `route`
(default `/mcp`), the auth switch plus token variable, and the five capability toggles
(`session-status`, `chat-and-commands`, `movement`, `inventory`, `entity-world`). A non-loopback
bind without auth falls back to loopback rather than exposing the endpoint.

## Prompts

The server also serves the `mcc_operator_prompt` prompt and the `mcc_agent_guidance` tool: the
canonical operating contract for agents, including the live capability snapshot.
