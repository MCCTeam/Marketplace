# Auto-relog

Reconnects after a kick, with a delay you choose.

## Settings

In `plugins/AutoRelog/settings.toml`.

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Set to false to disable AutoRelog without unloading it. |
| `Retries` | `3` | Rejoin attempts before giving up. -1 means unlimited. The budget rese... |
| `Ignore_Kick_Message` | `false` | Rejoin on any unexpected disconnect, ignoring Kick_Messages. |
| `Kick_Messages` | `[ "Connection has bee...` | Case-insensitive substrings of the kick message that trigger a rejoin. |
| `min` | `3.0` |  |
| `max` | `3.0` |  |

## Notes

Separate from `[Connection.Reconnect]` in `client.toml`, which handles dropped connections. This one is about kicks: a ban or a whitelist rule will kick you again immediately, so a short delay and unlimited retries is a good way to get IP-banned.

## See also

`/man plugins` for enabling and reloading. `/plugins list` shows whether this one loaded.
