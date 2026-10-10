# Auto-drop

Throws away item types you do not want, as they arrive.

## Commands

Run `/help autodrop` for the full syntax and worked examples.

## Settings

In `plugins/AutoDrop/settings.toml`.

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Set to false to disable AutoDrop without unloading it. |
| `Mode` | `"include"` | include = drop the listed items, exclude = keep only them, everything... |
| `Items` | `[ "Cobblestone", "Dir...` | Item names. The legacy enum spelling (Cobblestone), a bare id or a na... |

## Notes

The list works two ways. In `include` mode the listed items are dropped; in `exclude` mode everything EXCEPT the listed items is dropped. Get that backwards and it throws away your tools.

## See also

`/man plugins` for enabling and reloading. `/plugins list` shows whether this one loaded.
