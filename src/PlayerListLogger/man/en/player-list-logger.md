# Player list logger

Records who is online, and when they join or leave.

## Settings

In `plugins/PlayerListLogger/settings.toml`.

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Set to false to disable PlayerListLogger without unloading it. |
| `File` | `"playerlog.txt"` | Log file inside this plugin's data/ folder. |
| `Delay` | `60.0` | Seconds between two records. Clamped to at least 1. |

## See also

`/man plugins` for enabling and reloading. `/plugins list` shows whether this one loaded.
