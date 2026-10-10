# Items collector

Walks onto dropped items nearby so they get picked up.

## Commands

Run `/help itemscollector` for the full syntax and worked examples.

## Settings

In `plugins/ItemsCollector/settings.toml`.

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Set to false to disable ItemsCollector without unloading it. |
| `Collect_All_Item_Types` | `true` | Collect every dropped item type. Leave true: the whitelist is inert o... |
| `Items_Whitelist` | `[ "Diamond", "Netheri...` | Item whitelist. Not evaluated on this core because dropped-item conte... |
| `Delay_Between_Tasks` | `300` | Milliseconds between collection cycles. Clamped to at least 100. |
| `Collection_Radius` | `30.0` | How far to look for dropped items, in blocks. |
| `Always_Return_To_Start` | `true` | Walk back to the starting position when nothing is in range. |
| `Prioritize_Clusters` | `false` | Order targets around the densest cluster instead of nearest first. |

## Notes

Needs `Entity`, `Terrain` and `Pathfinding` on. Items despawn after five minutes, so a big radius on a busy floor will lose some anyway.

## See also

`/man plugins` for enabling and reloading. `/plugins list` shows whether this one loaded.
