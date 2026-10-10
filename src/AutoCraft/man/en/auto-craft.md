# Auto-craft

Crafts items from your chests at a bench the bot finds itself. You register chests once, queue what you want in chat, and it pulls ingredients, crafts, and delivers the results.

There is no order file. Everything runs through `/autocraft`.

## The 60-second setup

Put a sign on a chest that reads `Main`, drop coal and sticks in it, put a sign reading `Finished` on a second chest, then:

```
autocraft add input chest "Main"
autocraft add output chest "Finished"
autocraft craft 64 torches
```

The bot binds the nearest crafting table within 8 blocks, crafts in batches, and moves torches to `Finished`. Small 2x2 recipes skip the bench and use pockets.

## Chests

```
autocraft add <input|output> <chest|barrel|shulker> <"sign"|x y z> [item]
autocraft remove <input|output> <chest|barrel|shulker> <"sign"|x y z>
autocraft chests
```

A sign name tracks the chest even after a rebuild, so prefer labels for anything permanent. Coordinates pin one block. A shulker has to be placed to count; one riding in inventory does not.

Leave off the item and the chest is shared: anything the recipe needs can come out of it. Name one and the chest is filtered to it:

```
autocraft add input chest "Main"
autocraft add input barrel 12 64 -30 iron_ingot
autocraft add output shulker "Torches" torch
```

For one job the bot reads filtered chests first, then shared ones, nearest first. A locked, full, mined, or unreachable chest gets skipped for a while, not argued with. Withdrawals can split across chests, so 20 coal from one and 12 from the next still counts as one batch.

## Crafting

```
autocraft craft 64 torches
autocraft craft 2s "Stone Bricks"
autocraft keep 128 torches
autocraft queue
autocraft cancel 2
autocraft status
autocraft stop
```

Counts accept stacks: `2s` is 128. `craft` runs once and stops. `keep` holds stock at the target: it pauses with `stock is full` when your pockets hold enough and picks back up when you take some. `status` always answers in one line what the bot does, what it waits on, and the fix.

Names are forgiving: case, spaces, underscores, and hyphens do not matter, and aliases like `sticks` or `workbench` work. A miss suggests: `No recipe called "toch". Did you mean: Torch?`

## Recipes

```
autocraft recipes
autocraft recipes rail
autocraft info torches
autocraft show "Steel Gear"
```

`recipes` lists everything known, `info` and `show` print the grid as plain ASCII that survives any console:

```
Torch x4  [any]
+---+---+
|coa|   |
+---+---+
|sti|   |
+---+---+
needs: coal x1, stick x1
```

The header carries yield per craft and station. `show` is the same grid; check `status` and `chests` before a big run to confirm supply.

## Bench

Table recipes need a 3x3 grid, so the bot scans around itself (default 8 blocks, cap 12 candidates per attempt), walks to the closest table it can stand next to, and binds the first one that actually opens. A bench that fails stays ignored for a minute while the next gets tried.

```
autocraft bench radius 12
autocraft bench status
autocraft bench set 121 64 -31
autocraft bench auto
autocraft bench rescan
```

`set` pins one bench for laggy or protected areas. `auto` goes back to scanning. If nothing is in range the job parks with `Missing crafting bench` and resumes on its own when a table shows up, usually within seconds. The same file holds `BenchRadius` and `Bench` if you would rather set them in `settings.toml`.

## Custom recipes

Servers invent items, so `settings.toml` takes them:

```toml
[[CustomRecipes]]
Name = "Copper Coil"
Type = "table"
Result = "copper_coil"
Count = 2
Aliases = [ "coil" ]
Slots = [ "copper_ingot", "", "copper_ingot", "copper_ingot", "iron_ingot", "copper_ingot", "copper_ingot", "", "copper_ingot" ]
```

`Type` is `player`, `table`, or `any` (fits 2x2, crafted in pockets). A custom with the same name as a built-in wins. A bad entry fails that entry with a message, never the whole file.

## When it parks instead of dying

On `wait` (the sensible default in `OnFailure`) a missing ingredient parks the job and rechecks every 30 seconds: `Waiting for iron: checked "Bulk", still need 16. Add more or run autocraft stop.` A full output parks the same way instead of dropping anything on the ground. `abort` stops the queue outright. `autocraft stop` clears everything; the grid goes back to pockets first.

## Settings

In `plugins/AutoCraft/settings.toml`.

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Master switch. The craft command refuses while this is off. |
| `BenchRadius` | `8` | Bench search radius, clamped to 2-32. |
| `Bench` | `""` | Pinned bench as `"x y z"`. Empty means automatic. |
| `OnFailure` | `"abort"` | `abort` stops the queue on a missing material, `wait` parks and rechecks. |

Needs `Inventory` handling on. Movement is best effort: the walk to a chest or bench can lose to another bot steering at the same time.

## See also

`/man plugins` for enabling and reloading. `/plugins list` shows whether this one loaded.
