# Maps

Collects the map images the server sends and draws them in the console.

## Commands

Run `/help maps` for the full syntax and worked examples.

## Settings

In `plugins/Map/settings.toml`.

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Set to false to disable Map without unloading it. |
| `Render_In_Console` | `true` | Print the map to the console as coloured half-blocks. |
| `Save_To_File` | `false` | Write the map to a .bmp in this plugin's data/Rendered_Maps folder. |
| `Auto_Render_On_Update` | `false` | Render on every map update instead of only on 'maps render <id>'. |
| `Delete_All_On_Unload` | `true` | Clear previously rendered map files when the plugin starts (and on un... |
| `Notify_On_First_Update` | `true` | Log a line the first time each map id is received. |
| `Rasize_Rendered_Image` | `false` | Resize the saved image to Resize_To x Resize_To pixels (legacy spelli... |
| `Resize_To` | `512` | Edge length in pixels for the resize. Must be positive; falls back to... |
| `Send_Rendered_To_Discord` | `false` | Hand the rendered image to the bridge plugins (they must be loaded an... |
| `Discord_Target` | `""` | Where the Discord render goes: `route:name` and `dm:owner-id` entries, comma-separated. Empty means every channel that takes game chat. |
| `Send_Rendered_To_Telegram` | `false` |  |

## Discord targets

`Discord_Target` picks the recipients. Leave it empty and the render
goes to every channel that takes game chat, which is the old behavior.
Name routes, owners, or both:

```
Discord_Target = "route:general"
Discord_Target = "route:general,route:alerts"
Discord_Target = "dm:123456789012345678"
Discord_Target = "route:general,dm:123456789012345678"
```

A named route gets the image even when its chat-out switch is off:
naming it is the opt-in. A broken target logs a warning and drops the
image instead of spraying every channel. Ask for a render from Discord
(`.maps render 5`) with no target set and the image answers back where
you asked. The last asker wins when two race.

## Bigger maps

Native renders are 128 by 128 pixels, small in a Discord embed. Set
`Rasize_Rendered_Image` to true and `Resize_To` to 512. Files and
bridge images both grow. Nearest neighbor scaling keeps the chunky
pixels.

## Notes

Maps arrive as the server decides to send them, usually when you hold one. `maps list` shows what has arrived so far. In TUI mode the render opens a pane instead of printing.

## See also

`/man plugins` for enabling and reloading. `/plugins list` shows whether this one loaded.
