# Replay capture

Records the session as a ReplayMod file you can open in the mod.

## Commands

Run `/help replay` for the full syntax and worked examples.

## Settings

In `plugins/ReplayCapture/settings.toml`.

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Set to false to disable ReplayCapture without unloading it. |
| `Backup_Interval` | `300.0` | Seconds between backup snapshots of the in-progress recording. -1 dis... |

## Notes

Recording starts with the session. The file is written out when the session ends, when the plugin is unloaded, or when you run `replay stop`. `replay save` copies the recording so far without stopping it, and `Backup_Interval` does the same on a timer.

## See also

`/man plugins` for enabling and reloading. `/plugins list` shows whether this one loaded.
