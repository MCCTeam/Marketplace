# Auto-attack

Attacks hostile mobs that come within reach, with the cooldown a player would have.

## Settings

In `plugins/AutoAttack/settings.toml`.

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Set to false to disable AutoAttack without unloading it. |
| `Mode` | `"single"` | single = hit one priority target per cooldown, multi = hit every targ... |
| `Priority` | `"distance"` | distance = closest first. health is accepted but falls back to distan... |
| `Interaction` | `"Attack"` | Attack = left click, Interact = right click. |
| `Attack_Range` | `4.0` | Attack reach in blocks. Clamped to 1.0 - 4.0. |
| `Only_In_Front` | `true` | Only attack entities you can see: ahead of you and not behind blocks. Set to false to attack in every direction, through walls. |
| `Front_FOV` | `180.0` | Total view-cone width in degrees when Only_In_Front is true. 180 keeps the legacy hemisphere, smaller values ignore peripheral targets. |
| `Attack_Hostile` | `true` | Attack hostile mobs. |
| `Attack_Passive` | `false` | Attack passive mobs. |
| `List_Mode` | `"whitelist"` | whitelist = only the listed types, blacklist = every allowed type exc... |
| `Entites_List` | `[ "Zombie", "Cow" ]` | Entity types. The legacy enum spelling (Zombie), a bare id or a names... |
| `Custom` | `false` | Use the values below instead of the vanilla attack-speed cooldown. |
| `RandomMode` | `false` | Pick a random cooldown between Min and Max on every swing. |
| `Min` | `1.5` | Minimum cooldown, seconds. |

## Notes

Needs `Entity` handling on. With `Only_In_Front` on (the default) only targets you can
see are hit: anything behind you, outside the `Front_FOV` cone, or behind blocks stays tracked
but is skipped until it becomes visible. With no terrain loaded (the `Terrain` switch off) every
target in the cone reads as visible.
Seeing is measured the way a player sees: sneaking lowers the eye, and each target is tested
at shin, torso and head height, so a mob stays hittable through a low gap (under a cauldron,
through a half-open hatch) and while you crouch under a slab. Glass still blocks sight, the
same as for a player aiming through it.
With `Only_In_Front` off the sight check is skipped entirely: everything in `Attack_Range` is
hit, through walls and in every direction. That is the legacy 1.x behavior and what a farm
grinder usually wants (a mob behind glass has no legal sight lane from any angle, so seeing
it can never start). The server still decides which swings land.
Stationary targets are re-discovered every swing, so walking up to a mob that never moves
still starts the attack.
`Attack_Range` decides which mobs get tracked, but the server decides which swings land:
survival servers only accept hits within about 3 blocks eye-to-target, so a wider range
tracks further without hitting further. Attacking through walls or at an impossible rate is
what anti-cheat looks for, so leave the cooldown alone unless you know the server.

## See also

`/man plugins` for enabling and reloading. `/plugins list` shows whether this one loaded.
