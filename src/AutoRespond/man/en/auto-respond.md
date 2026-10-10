# Auto-respond

Matches chat against patterns and answers, or runs an MCC command.

## Settings

In `plugins/AutoRespond/settings.toml`.

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Set to false to disable AutoRespond without unloading it. |
| `Matches_File` | `"matches.ini"` | Rules file, resolved inside this plugin's data/ folder. A sample is w... |
| `Match_Colors` | `false` | Keep formatting codes in the text the rules match against. |

## Notes

A rule matches with either `match=`, a case-insensitive substring, or `regex=`, a regular expression whose groups become `$1` and up in the action. A rule that matches your own output will loop.

## See also

`/man plugins` for enabling and reloading. `/plugins list` shows whether this one loaded.
