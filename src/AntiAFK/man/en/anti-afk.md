# Anti-AFK

Does something small at intervals so the server does not count you as idle and disconnect you.

## Settings

In `plugins/AntiAFK/settings.toml`.

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Set to false to disable AntiAFK without unloading it. |
| `Command` | `"/ping"` | Command or chat line sent to stay non-idle. Leave empty to send nothing. |
| `Use_Sneak` | `false` | Toggle sneak on each action. |
| `Use_Terrain_Handling` | `false` | Walk to a random nearby spot instead of only sending the command. Nee... |
| `Walk_Range` | `5` | Maximum X/Z offset of the random walk goal, in blocks. Must be positive. |
| `Walk_Retries` | `20` | How many walk goals to try before falling back to sending the command. |
| `min` | `60.0` |  |
| `max` | `60.0` |  |

## Notes

Servers vary in what they accept as activity. A command that the server does not have produces an error in chat every time it fires, so check `Command` in `settings.toml` against the server you are on.

## See also

`/man plugins` for enabling and reloading. `/plugins list` shows whether this one loaded.
