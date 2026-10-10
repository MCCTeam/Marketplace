# Remote control

Runs MCC commands sent to you as a whisper by players you have allowed.

## Settings

In `plugins/RemoteControl/settings.toml`.

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Set to false to disable RemoteControl without unloading it. |
| `AutoTpaccept` | `true` | Automatically accept teleport requests (still restricted to bot owner... |
| `AutoTpaccept_Everyone` | `false` | Accept teleport requests from ANY player, not only from the bot owners. |

## Notes

Anyone on the allow list can run any MCC command as you, including ones that change configuration. The allow list is the whole security model.

## See also

`/man plugins` for enabling and reloading. `/plugins list` shows whether this one loaded.
