# Farmer

Farms one crop for you: harvests what's ripe, picks up the drops,
replants, and feeds the young ones bone meal. Then it loops until you
stop it. Use area mode. It farms exactly the box you give it, unlike
radius mode, which draws a circle around the bot and grabs eight blocks
above and below, whether your farm is there or not.

## Quick start

1. Stand near your farm and note two opposite corners of the field.
   Press F3 and read the XYZ numbers, or stand on each corner and read
   them from `/blockinfo`.
2. Tell Farmer the crop and the box:
   `farmer start wheat area 100 63 200 132 70 240`. Corner order does
   not matter. Both corners count as inside.
3. Give it chests. Label a chest with a sign that says Seeds, Crops, or
   Bonemeal, or point at coordinates directly (see below). Without
   chests it still farms, but it carries everything and never restocks.
4. Walk away. It harvests, collects, replants, bone meals, and repeats.
5. `farmer stop` ends the run.

## Commands

```
farmer start <crop> area <x1> <y1> <z1> <x2> <y2> <z2>
farmer start <crop> radius [1-150]
farmer storage <bonemeal|seeds|crops> name <label>
farmer storage <bonemeal|seeds|crops> position <x> <y> <z>
farmer storage <bonemeal|seeds|crops> position clear
farmer storage margin <blocks>
farmer storage list
farmer stop
```

Crop names complete as you type. `sugar_cane`, `nether_wart`,
`cocoa_beans`, `wheat_seeds`, and `melons_and_pumpkins` all work, and
short forms like `sugarcane` and `cocoa` do too. For melons and
pumpkins plant-side, use `melon`, `pumpkin`, or `melons_and_pumpkins`.
Radius defaults to 30 and tops out at 150. Boxes top out at 4,000,000
cells, so a typo cannot scan the whole world.

## Examples

Wheat field, exact box, chests by sign labels:

```
farmer start wheat area 100 63 200 132 70 240
farmer storage seeds name Seeds
farmer storage crops name Crops
farmer storage bonemeal name Bonemeal
farmer storage list
```

Sugar cane along a wall, chest at fixed coordinates:

```
farmer start sugar_cane area 100 63 200 100 70 260
farmer storage crops position 120 64 220
```

Nether wart on soul sand (no bone meal needed, wart ignores it):

```
farmer start nether_wart area 50 40 50 80 45 80
```

Cocoa on jungle logs:

```
farmer start cocoa area 100 63 200 132 72 240
```

Done for the day: `farmer stop`.

## Storage

Three chests cover everything: Seeds holds planting material, Crops
takes the harvest, Bonemeal feeds the bone meal passes. Farmer takes
normal chests, trapped chests, and barrels. Put a sign with the label
on any face of the chest, or give exact coordinates. Coordinates win
when both exist. `storage margin` sets how far past the farm it looks
for a labelled chest. `storage list` shows what it found.

Two fallbacks save complaining trips: wheat, beetroot, melon, and
pumpkin seeds fall back to the Crops chest when Seeds runs dry, and
carrot, potato, wart, cane, bamboo, cactus, and cocoa replant from
their own harvest. Farmer reports a missing or unreachable chest and
keeps farming.

## Crops

Wheat, beetroot, carrot, potato: harvested and replanted. Nether wart:
same, on soul sand, and it skips bone meal. Melon and pumpkin: the bot
picks the fruit and bone meals the stem, which stays planted. `melons_and_pumpkins`
farms both in one area. Sugar cane, bamboo, cactus: the second block
snaps off, the base stays, no replanting and no bone meal. Cocoa:
ripe pods only, replanted on the same jungle log face.

Beetroot needs Minecraft 1.9 or newer, bamboo 1.14 or newer. Older
servers refuse the command instead of farming air.

## Settings

In `plugins/Farmer/settings.toml`.

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Set to false to disable Farmer without unloading it. |
| `Delay_Between_Tasks` | `1.0` | Seconds between farming cycles. Never below 1.0. |
| `Verbose_Debug` | `true` | Prints a trace per phase. Turn off once it works. |
| `Storage_Search_Margin` | `32` | Blocks past the farm to hunt for labelled chests. From 1 to 128. |
| `Bonemeal_Chest_Name` | `Bonemeal` | Sign label for the bone meal chest. |
| `Seeds_Chest_Name` | `Seeds` | Sign label for the seed chest. |
| `Crops_Chest_Name` | `Crops` | Sign label for the crop chest. |
| `*_Chest_Coordinates` | empty | Fixed chest coordinates in `x y z` form. Beats sign search. |

## Advanced

Each cycle crosses five barriers in order: harvest the ripe, sweep the
drops, deposit surplus and replant, refill and spread bone meal, back
to harvest. A phase runs to completion before the next starts, so drops
never sit while the bot plants.

Farmer maps the field once, then works it in serpentine rows along its
longer axis, starting from the nearer end. The map refreshes on its own
as the farm changes shape. It rechecks each target after walking. It
collects drops ground-level in short sweeps. It skips a drop with no safe
stance for the rest of the run instead of stalling the loop.

Needs `Terrain`, `Inventory`, `Entities`, and `Physics` on. MCC uses
`Pathfinding` when enabled, plain movement otherwise. Farmer gives up on
an unreachable target after a bounded attempt and moves on.

## See also

`/man plugins` for enabling and reloading. `/plugins list` shows whether this one loaded.
