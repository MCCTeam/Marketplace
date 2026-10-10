# Discord bridge

Chat with your Minecraft server from Discord. Game chat lands in your
channels, your channel messages land in the game, and trusted users can
run MCC commands with a short prefix. On its own it posts connects and
disconnects, deaths with coordinates, and effect gains and losses.

## Set up the bot

Do this once. It takes about ten minutes, most of it clicking through the
Discord portal.

1. Open https://discord.com/developers/applications and press New
   Application. Name it anything. You do not need a bot token yet.
2. Open the Bot tab and press Add Bot. Press Reset Token and copy the
   token. This token is a password. Anyone holding it controls the bot,
   so never give it to anyone else.
3. On the same Bot page, turn on the Message Content intent. This is the
   step everyone misses. Without it the bot connects fine but hears
   nothing, which looks exactly like a dead bridge.
4. Invite the bot to your server. Tick the `bot` scope and these
   permissions: View Channels, Send Messages, Read Message History,
   Embed Links, Attach Files. The last two cover the cards and the map
   screenshots. The easy path is https://discordapi.com/permissions.html:
   paste the Application ID as the Client ID, tick the permissions, and
   open the generated link. If permission math bores you, Administrator
   covers it all.
5. In Discord, open User Settings, Advanced, and turn on Developer Mode.
   Right-click your server and Copy Server ID. Right-click a channel and
   Copy Channel ID. Send a line in that channel, right-click your own
   name, Copy User ID. Those three numbers go into the config.
6. Create a `.env` file inside your configurations folder, next to the
   TOML files, with one line: `MCC_DISCORD_TOKEN=paste-the-token-here`.
   MCC loads that file on startup, so the token is there with no extra
   step. Then set `Token = "env:MCC_DISCORD_TOKEN"` in
   `plugins/DiscordBridge/settings.toml`. The TOML file holds the link,
   the `.env` file holds the secret. Put your user id in `OwnersIds`.
   An empty owner list authorizes nobody, which fails safe.
7. Start MCC. Type `plugins enable discord-bridge` into the MCC console
   and press enter. The plugin loads at once. Then run
   `dscbridge channel add general <server-id> <channel-id>` followed by
   `dscbridge channel test general`. One probe line should land in the
   channel. If it does, the wiring works end to end.

## Talk to it

Type in a bridged channel and the game hears it. Start a line with the
channel prefix, usually a dot, and MCC runs the rest as a command. Each
channel can set its own prefix, see below:

```
Hello from Discord
.health
.chunk status
```

The answer comes back where you asked. Most commands print exactly what
the MCC console would show, inside a code block. Three commands answer
with custom renders: `man` sends the page itself, `health` a health
card, and `effects` an emoji list.

## Shape your channels

One channel rarely fits. A mirror channel wants chat both ways and no
commands. A control room wants commands and nothing else. Each bridged
channel, called a route, carries three switches: chat out, chat in, and
commands. Out sends game chat to Discord, in sends Discord chat to the
game, cmd runs commands. Set them per channel:

| You want | out | in | cmd |
| --- | --- | --- | --- |
| Everything in one channel | on | on | on |
| Chat mirror, no control | on | on | off |
| Announcements plus commands, chatter stays out | on | off | on |
| Listen only | off | on | off |
| Control room, commands only | off | off | on |

```
dscbridge status
dscbridge channels
dscbridge channel add <name> <server-id> <channel-id>
dscbridge channel remove <name>
dscbridge channel set <name> out|in|cmd <on|off>
dscbridge channel owners <name> <id...> | inherit
dscbridge channel prefix <name> <char> | default
dscbridge channel test <name>
```

A fresh route starts safe: chat flows, commands stay off until you say
otherwise. `test` posts one probe line and reports back, which is the
fastest way to check a channel id you pasted. It checks reachability
only, not the switches. `status` shows the gateway, the session, and
every route's last send. Every change saves `settings.toml` at once.
Owners and the prefix can differ per channel or inherit the global ones.

## Examples

Type these into the MCC console. A typical server ends up with three
routes: a mirror for everyone, a quiet announcements channel, and a
locked control room.

```
dscbridge channel add mirror 111222333444555666 777888999000111222
dscbridge channel set mirror cmd off
dscbridge channel test mirror
```

The mirror relays chat both ways and runs nothing. Next, announcements:
game chat goes out, chatter stays out, but commands stay on so moderators
can steer the bot from there.

```
dscbridge channel add announce 111222333444555666 888999000111222333
dscbridge channel set announce in off
dscbridge channel set announce cmd on
dscbridge channel test announce
```

The control room runs commands and nothing else. Give it tighter owners
and a prefix that never collides with normal chat:

```
dscbridge channel add control 111222333444555666 999000111222333444
dscbridge channel set control out off
dscbridge channel set control in off
dscbridge channel set control cmd on
dscbridge channel owners control 123456789012345678
dscbridge channel prefix control \
dscbridge channel test control
```

From Discord the same commands work with the route's prefix. In
#control with the backslash prefix, `dscbridge status` becomes
`\dscbridge status`, and `.health` becomes `\health`.

Check the whole table any time:

```
dscbridge channels
dscbridge status
```

## DMs

```
dscbridge dm on|off
dscbridge dm messages on|off
dscbridge dm <owner> <text>
```

`dm on` lets owners command the bot from DMs. `dm messages on` also
mirrors game chat into owner DMs. Two Discord rules apply, and both bite:
the owner must share a server with the bot, and that server must allow
DMs from its members. A bot cannot write first, so message the bot once
and replies flow after that. Whispers and teleport requests can also DM
the owners on their own. Set `DmOwnersOn` to `["whisper", "teleport"]`
for both. Watched whispers skip the channels entirely.

## What it posts on its own

Connect and disconnect notices go to every channel that takes game chat.
Death posts a red embed with block coordinates, unless `DeathEmbed` is
off. Gaining or losing an effect posts one line. Death and respawn move
effects around behind the scenes. The bridge stays silent for those, so
a dimension hop never spams the channel. Effects lasting a day or more
show as infinite, in MCC and here.

Map renders arrive as images. With no destination they fan out like
chat, except a render asked from Discord answers back where it was
asked. Name recipients in the Map plugin's `Discord_Target` instead:
`route:name` for a channel, `dm:owner-id` for a DM, comma-separated for
several. A named route gets the image even with its chat-out switch
off.

## Troubleshooting

The bot connects but hears nothing. The Message Content intent is off.
Flip it on in the portal and restart MCC.

DMs fail or never arrive. Check the two DM rules above, then message the
bot first. If the log shows a 403, Discord is refusing the DM. That is a
Discord setting, not a bridge bug.

The probe never arrives. The channel id is wrong, or the bot cannot see
the channel. Check the id and the channel permissions.

The plugin disables itself at startup. `Token` is empty or the `env:`
link points nowhere. The log names the variable.

A channel answers "does not run commands". That route has commands off.
Run `dscbridge channel set <name> cmd on`.

## Settings

In `plugins/DiscordBridge/settings.toml`. `GuildId` and `ChannelId` are the
old single channel and apply only when no routes exist.

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Set to false to disable DiscordBridge without unloading it. |
| `Token` | `""` | Bot token or `env:NAME`. Required. The plugin self-disables without it. |
| `OwnersIds` | `[]` | Discord user ids allowed to relay and run commands. Empty authorizes nobody. |
| `AllowDirectMessages` | `false` | Accept DMs from owners. |
| `DmRelayOut` | `false` | Mirror game chat into owner DMs. |
| `DeathEmbed` | `true` | Post a red embed with death coordinates. |
| `DmOwnersOn` | `[]` | Game events that DM the owners instead of channels: whisper, teleport. |
| `CommandPrefix` | `"."` | Default command prefix. |
| `Routes` | `[]` | The bridged channels, as `[[Routes]]` tables. The commands above write these for you. |
| `Message_Send_Timeout` | `3` | Seconds a Discord send may take before the bridge abandons it. |
| `Allow_Other_Bot_Messages` | `false` | Relay messages posted by other bots into the game. |
| `Relay_All_Messages` | `false` | Relay every line, including unclassified server output. |
| `Message_Aggregation_Interval` | `3.0` | Seconds to batch chat lines into one Discord message. |

## See also

`/man plugins` for enabling and reloading. `/plugins list` shows whether this one loaded.
