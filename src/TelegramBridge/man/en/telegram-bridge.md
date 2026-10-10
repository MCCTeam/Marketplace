# Telegram bridge

Chat with your Minecraft server from Telegram. Game chat lands in your
chat, your chat messages land in the game, and trusted chats can run MCC
commands with a dot prefix. The bridge also forwards map renders from
the Map plugin when you ask for them.

The plugin ships switched off. It stays off until you put a real bot
token in its settings. That fail-safe keeps a default install from
polling Telegram with nothing configured.

## Create the bot

Do this once. It takes about five minutes, most of it in Telegram.

1. Open Telegram and find @BotFather.
2. Press Start, send `/newbot`, and answer the questions. Pick a display
name and a username that ends in `bot`.
3. Copy the token @BotFather returns. This token is a password. Anyone
holding it controls the bot, so never share it.
4. Create a `.env` file inside your configurations folder, next to the
TOML files, with one line: `MCC_TELEGRAM_TOKEN=paste-the-token-here`.
MCC reads that file on startup.
5. Open `plugins/TelegramBridge/settings.toml` and set
`Token = "env:MCC_TELEGRAM_TOKEN"`. The TOML file holds the link, the
`.env` file holds the secret.
6. Start MCC and enable the plugin: `plugins enable telegram-bridge`.
The plugin loads at once and posts a connect notice to your chat once
you set the chat id.
7. In Telegram, find your new bot by its username and press Start to
open a direct chat.
8. Send `.chatid`. The bot replies with the id of that chat, for
example `2627844670`.
9. Put that number in `ChannelId` and add it to `Authorized_Chat_Ids`.
Save the file and run `plugins reload telegram-bridge`. Game chat
should now arrive in Telegram.

An empty `Authorized_Chat_Ids` authorizes nobody. The bridge answers
other chats with a refusal and logs the attempt. Add each chat that may
send messages or run commands, or the bridge stays listen-only.

## Talk to it

Type a plain line and the game hears it as chat. Start a line with a
dot and MCC runs the rest as a command. The answer comes back as a
reply to your message.

```
Hello from Telegram
.health
.chunk status
.chatid
```

Three inputs get special handling. The bridge ignores `/start`, so the
Telegram Start button stays quiet. `.chatid` reports the id of the
current chat. The bridge refuses a command with `quit` or `exit` in
it. You cannot stop the client from chat.

Commands run as internal MCC commands with your Telegram chat as the
source. Most answers print what the MCC console would print, with the
Minecraft color codes removed. Plain chat lines lose their codes on the
way out for the same reason. If MCC has no session open, the bridge
drops chat lines and commands still run where they make sense.

## Control the direction

```
tgbridge direction <both|mc|telegram>
```

`both` relays each way. `mc` relays Telegram into Minecraft only and
sends nothing out. `telegram` relays Minecraft into Telegram only and
ignores chat coming back, but dot commands still work. The setting
lasts until you change it or reload the plugin. It resets to `both`
on reload.

## What it posts on its own

Connect and disconnect notices go to the configured chat. Public chat,
whispers, and teleport requests use your message templates below.
Other server lines arrive verbatim.

Map renders arrive as documents. Turn on `Send_Rendered_To_Telegram`
in the Map plugin and its renders go to this same chat. The Map page
has the details.

## Troubleshooting

The plugin disables itself at startup. `Token` is empty, still the
placeholder, or the `env:` link points to a variable that is not set.
The log names the cause.

Nothing arrives in Telegram. `ChannelId` is empty or wrong. Send
`.chatid` again and compare the numbers.

Telegram messages never reach the game. The chat id is missing from
`Authorized_Chat_Ids`, or the direction is `telegram`. Add the id and
set the direction to `both`.

The probe command answers with refusal. Same cause. Only listed chats
may drive the bridge.

Sends time out. `Message_Send_Timeout` is too low for your link, or
Telegram is slow. Raise it by a second or two.

## Settings

In `plugins/TelegramBridge/settings.toml`.

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Set to false to disable TelegramBridge without unloading it. |
| `Token` | `""` | Bot token from @BotFather, or `env:NAME`. Required. The plugin self-disables without it. |
| `ChannelId` | `""` | Chat or channel id that receives game chat. Send `.chatid` to the bot to learn it. |
| `Authorized_Chat_Ids` | `[]` | Chat ids that may send chat and run commands. Empty authorizes nobody. |
| `Message_Send_Timeout` | `3` | Seconds a Telegram send may take before the bridge abandons it. Below 1 falls back to 3. |
| `PrivateMessageFormat` | `"*(Private Message)* {username}: {message}"` | Template for whispers. Placeholders: {username} {message} {timestamp} |
| `PublicMessageFormat` | `"{username}: {message}"` | Template for public chat. Same placeholders. |
| `TeleportRequestMessageFormat` | `"A new Teleport Request from **{username}**!"` | Template for teleport requests. Same placeholders. |

`{timestamp}` expands as `yyyy-MM-dd HH:mm:ss`. Keep the placeholder
names exactly as shown or they will not expand.

## See also

`/man plugins` for enabling and reloading. `/plugins list` shows whether this one loaded. `/man map` for rendered map images.
