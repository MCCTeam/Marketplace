# Auto-dig

Breaks the block at the crosshair, or works through a list of positions from `settings.toml`.

## Commands

Run `/help autodig` for the full syntax and worked examples.

## Settings

In `plugins/AutoDig/settings.toml`.

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Set to false to disable AutoDig without unloading it. |
| `Auto_Tool_Switch` | `false` | Switch to the tool recommended for the block before digging (needs in... |
| `Apply_Efficiency_Enchantments` | `true` | Kept for compatibility. The new dig action has no client-side mining ... |
| `Apply_Haste_Effects` | `true` | Kept for compatibility. The new dig action has no client-side mining ... |
| `Durability_Limit` | `2` | Never pick a tool with less remaining durability than this. 0 disable... |
| `Drop_Low_Durability_Tools` | `false` | Drop the worn-out tool after switching away from it. |
| `Mode` | `"lookat"` | lookat = dig the crosshair block, fixedpos = dig Locations, both = cr... |
| `Location_Order` | `"distance"` | distance = nearest position first, index = config order. |
| `Auto_Start_Delay` | `3.0` | Seconds to wait after joining or respawning before the first dig. |
| `Dig_Timeout` | `60.0` | Seconds before a dig that never completes re-arms the state machine. |
| `Log_Block_Dig` | `true` | Log every dig and every reason for not digging. |
| `List_Type` | `"whitelist"` | whitelist = only the listed blocks, blacklist = everything except the... |

## Notes

Needs `Terrain` handling on. Break speed is the server's decision, so a wrong tool means it takes as long as it would for a player.

## See also

`/man plugins` for enabling and reloading. `/plugins list` shows whether this one loaded.
