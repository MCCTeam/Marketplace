# Chat log

Writes chat to a file, with a timestamp and a choice of formats.

## Settings

In `plugins/ChatLog/settings.toml`.

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Set to false to disable ChatLog without unloading it. |
| `Add_DateTime` | `true` | Prepend a yyyy-MM-dd HH:mm:ss timestamp to every logged line. |
| `Log_File` | `"chatlog-%username%-%...` | Log file inside this plugin's data/ folder. %username% and %serverip%... |
| `Filter` | `"messages"` | all, messages, chat, private_chat or internal_msg. |

## Notes

Independent of `/console-chat`: hiding chat on screen does not stop it being logged.

## See also

`/man plugins` for enabling and reloading. `/plugins list` shows whether this one loaded.
