# Follow player

Paths towards a player and keeps up with them.

## Commands

Run `/help follow` for the full syntax and worked examples.

## Settings

In `plugins/FollowPlayer/settings.toml`.

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Set to false to disable FollowPlayer without unloading it. |
| `Update_Limit` | `1.5` | Minimum seconds between two re-paths toward the followed player. |
| `Stop_At_Distance` | `3.0` | Stop moving once the followed player is closer than this many blocks. |

## Notes

Needs `Entity` and `Pathfinding` on. A player who moves faster than the walk speed, or who teleports, will be lost; the plugin keeps trying until you stop it.

## See also

`/man plugins` for enabling and reloading. `/plugins list` shows whether this one loaded.
