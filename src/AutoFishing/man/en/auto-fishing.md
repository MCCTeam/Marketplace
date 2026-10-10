# Auto-fishing

Casts a rod, watches for the bite, reels in, and casts again. Keeps a tally of what it caught.

## Commands

Run `/help autofishing` for the full syntax and worked examples.

## Settings

In `plugins/AutoFishing/settings.toml`.

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Set to false to disable AutoFishing without unloading it. |
| `Antidespawn` | `false` | Recast when the bobber despawns instead of waiting forever. |
| `Mainhand` | `true` | Cast with the main hand. false casts with the off hand. |
| `Auto_Start` | `true` | Start fishing automatically after joining or respawning. |
| `Cast_Delay` | `0.4` | Seconds to wait before every cast. |
| `Fishing_Delay` | `3.0` | Seconds to wait after joining or respawning before the first cast. |
| `Fishing_Timeout` | `300.0` | Seconds without a bite before recasting. |
| `Durability_Limit` | `2.0` | Minimum remaining rod durability, out of 64. |
| `Auto_Rod_Switch` | `true` | Swap in a fresh rod from the inventory when the held one is worn out. |
| `Stationary_Threshold` | `0.001` | Horizontal bobber movement below this counts as stationary. |
| `Hook_Threshold` | `0.2` | Vertical bobber movement above this while stationary is a bite. |
| `Enable_Velocity_Detection` | `true` | Also detect the bite from the bobber velocity. |
| `Velocity_Hook_Threshold` | `-0.2` | Bobber Y velocity at or below this is a bite. |
| `Enable_Sound_Detection` | `true` | Also detect the bite from the splash sound. |
| `Sound_Distance` | `5.0` | How near the splash has to be, in blocks, to count as this bot's. |

## Notes

A bite is spotted three ways: the bobber's motion, its velocity, and the splash sound. Velocity and sound each have their own switch, so fishing still works on a server that suppresses the splash. `autofishing status` shows the tally and whether it is currently waiting on a bite.

## See also

`/man plugins` for enabling and reloading. `/plugins list` shows whether this one loaded.
