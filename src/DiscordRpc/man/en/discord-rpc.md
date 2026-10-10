# Discord rich presence

Puts your MCC session on your Discord profile: which server you play
on, your health, your dimension. Friends see it under your name. This
needs no bot and no token. It only needs an application entry and the
Discord desktop app on the same machine.

## Set up the application

1. Open https://discord.com/developers/applications and press New
   Application. Name it anything. Copy the Application ID on the
   General Information page. That number is public, not a secret.
2. You can stop there. Rich presence reads the game through your local
   Discord app, so there is nothing to invite, no bot user to create,
   and no intent to flip. If you came from the bridge setup, forget all
   of that here.
3. Paste the Application ID into `plugins/DiscordRpc/settings.toml`
   as `ApplicationId`.
4. Keep the Discord desktop app running on the same machine as MCC. The
   plugin talks to Discord through a local pipe. Browser Discord and
   phones never see rich presence.
5. Start MCC and type `plugins enable discord-rpc` into the MCC console.
   Join a server. Your profile should show the presence within about
   ten seconds. The plugin sends nothing while you sit idle with no server.

## Make it yours

Two lines make the presence. Both accept placeholders:

```
PresenceDetails = "Playing on {server_host}:{server_port}"
PresenceState = "{dimension} - HP: {health}/{max_health}"
```

| Placeholder | Shows |
| --- | --- |
| `{server_host}` | server address |
| `{server_port}` | server port |
| `{username}` | your game name |
| `{health}` | current health |
| `{max_health}` | full health |
| `{food}` | food level |
| `{dimension}` | overworld, nether, or end |
| `{gamemode}` | survival, creative, and friends |
| `{x}` `{y}` `{z}` | your coordinates |
| `{player_count}` | players online |
| `{protocol}` | game version number |

A hunter might write `"{username} at {x}, {y}, {z} in {dimension}"`.
A minimalist writes `"{server_host}"` and nothing else.

Pictures come from Art Assets. Open your application, Rich Presence,
Art Assets, and upload an image of at least 512 by 512 pixels. Name it
`mcc_icon` and set `LargeImageKey` to the same name. The names must
match exactly. `LargeImageText` is the hover text. Leave `SmallImageKey`
empty until you want a second badge.

## Privacy switches

Each `Show` setting masks one placeholder group when false:

| Off | Masks |
| --- | --- |
| `ShowServerAddress` | address reads Hidden, port reads **** |
| `ShowCoordinates` | coordinates read ? |
| `ShowHealth` | health and food read ? |
| `ShowDimension` | dimension reads Hidden |
| `ShowGamemode` | game mode reads Hidden |

`ShowElapsedTime` adds a session timer. `ShowPlayerCount` shares the
online count as a party size. `UpdateIntervalSeconds` sets the refresh
rate and defaults to 10.

## Troubleshooting

Nothing shows on the profile. Check three things in order: the Discord
desktop app runs on the same machine, the Application ID matches, and
Discord shares your activity. That last one hides under User Settings,
Activity Privacy. Set your status to Online. A custom status overrides
the presence.

Text shows but the picture does not. The asset name and `LargeImageKey`
differ, or the upload is still processing. Set the key empty, reload,
and check the text alone. Then fix the name.

The plugin disables itself at startup. `ApplicationId` is empty. The log
says so.

## Settings

In `plugins/DiscordRpc/settings.toml`.

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Set to false to disable DiscordRpc without unloading it. |
| `ApplicationId` | `""` | Application ID from the portal. Required. The plugin self-disables without it. |
| `PresenceDetails` | `"Playing on {server_host}:{server_port}"` | First presence line. |
| `PresenceState` | `"{dimension} - HP: {health}/{max_health}"` | Second presence line. |
| `LargeImageKey` | `"mcc_icon"` | Large image asset name. Empty disables the image. |
| `LargeImageText` | `"Minecraft Console Client"` | Large image hover text. Placeholders work here too. |
| `SmallImageKey` | `""` | Small image asset name. Empty disables the small image. |
| `SmallImageText` | `""` | Small image hover text. |
| `ShowServerAddress` | `true` | Publish the server address. |
| `ShowCoordinates` | `true` | Publish the player coordinates. |
| `ShowHealth` | `true` | Publish health and food. |
| `ShowDimension` | `true` | Publish the dimension. |
| `ShowGamemode` | `true` | Publish the game mode. |
| `ShowElapsedTime` | `true` | Show the elapsed session time. |
| `ShowPlayerCount` | `true` | Publish the online player count as a party size. |
| `UpdateIntervalSeconds` | `10` | Seconds between presence refreshes. |

## See also

`/man plugins` for enabling and reloading. `/plugins list` shows whether this one loaded.
