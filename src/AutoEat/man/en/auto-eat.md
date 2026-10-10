# Auto-eat

Eats when your hunger drops below a threshold, from the held slot when it holds food and otherwise the first food in the hotbar.

## Settings

In `plugins/AutoEat/settings.toml`.

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Set to false to disable AutoEat without unloading it. |
| `Threshold` | `6` | Eat when food (0-20) is at or below this. Clamped to 0-20. |

## Notes

Needs `Inventory` handling on. It only eats from the hotbar, so keep food there; the slot you were holding is selected again afterwards.

## See also

`/man plugins` for enabling and reloading. `/plugins list` shows whether this one loaded.
