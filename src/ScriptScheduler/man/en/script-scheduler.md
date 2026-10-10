# Script scheduler

Runs lists of MCC commands on login, when the server shows a dialog, at set times of day, or on a repeating interval.

## Settings

In `plugins/ScriptScheduler/settings.toml`.

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Set to false to disable ScriptScheduler without unloading it. |
| `Task_Name` | `"Task Name 1"` |  |
| `Trigger_On_First_Login` | `false` |  |
| `Trigger_On_Login` | `false` |  |
| `Trigger_On_Dialog` | `false` | Fire when the server shows a dialog (1.21.6+), in configuration or in play. |
| `Dialog_Match` | `""` | Only fire when the dialog title or one of its input keys contains this text; empty matches every dialog. |
| `Action` | `"send /hello"` |  |
| `Enable` | `true` | Fire at the times listed below. |
| `Times` | `[ "14:00:00" ]` | Times of day, as "HH:mm:ss" strings. |
| `Enable` | `true` | Fire repeatedly, with a random delay between MinTime and MaxTime. |
| `MinTime` | `3.6` |  |
| `MaxTime` | `4.8` |  |
| `Task_Name` | `"Task Name 2"` |  |
| `Trigger_On_First_Login` | `false` |  |

## Notes

A task fires on the first login, on every login, when a matching dialog appears, at the times in `Trigger_On_Times`, or on the `Trigger_On_Interval` timer. There is no disconnect trigger. Scheduling is wall-clock, so a task keeps its time across reconnects.

`Trigger_On_Login` never fires for a login dialog shown during configuration, since the join has not finished yet. That is what `Trigger_On_Dialog` is for. Match on the input key rather than the title: titles get retranslated, keys do not. Matching tasks run in list order in one background pass, so a fill step listed before its press step dispatches first:

```toml
[[TaskList]]
Task_Name = "Auth fill"
Trigger_On_Dialog = true
Dialog_Match = "auth_login_password"
Action = "dialog set auth_login_password s3cret"

[[TaskList]]
Task_Name = "Auth press"
Trigger_On_Dialog = true
Dialog_Match = "auth_login_password"
Action = "dialog click 1"
```

Keep secrets in this file, never in chat. One caution: a `dialog set` step reports the
value it staged, and that report lands in the log, so a password filled this way ends up
in log files. For passwords prefer a Beacon script's `dialog.answer`, which returns only
yes or no and never echoes the value.

## See also

`/man plugins` for enabling and reloading. `/plugins list` shows whether this one loaded.
