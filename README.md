# MCC Marketplace

Plugins for [Minecraft Console Client](https://github.com/MCCTeam/Minecraft-Console-Client): automate game tasks, save chat and replays, connect Discord or Telegram, and add tools for AI agents.

## Use the marketplace in MCC

Current MCC 2.0 builds register this marketplace as `official` by default. Enter these commands in MCC:

```text
/plugins search fishing in official
/plugins install auto-fishing@official
/plugins enable auto-fishing
/plugins info auto-fishing
```

If `/plugins marketplace list` does not show `official`, add it first:

```text
/plugins marketplace add https://raw.githubusercontent.com/MCCTeam/Marketplace/master/marketplace/mcc-marketplace.toml as official
```

Replace `auto-fishing` with a plugin ID from the table below. Read its linked manual to configure it. Use `/plugins list` to check which plugins are active.

New releases become installable after CI publishes their archives and updates the catalogue. Current releases need an MCC build that uses DMCBK preview.7 or later.

To check for updates later:

```text
/plugins marketplace refresh official
/plugins outdated
/plugins update all
```

## Plugins

| Plugin ID | What it does |
| --- | --- |
| [alerts](src/Alerts/man/en/alerts.md) | Notifies you about matching chat messages and game events. |
| [anti-afk](src/AntiAFK/man/en/anti-afk.md) | Performs small actions at intervals to stay active. |
| [auto-attack](src/AutoAttack/man/en/auto-attack.md) | Attacks nearby hostile mobs with the normal attack cooldown. |
| [auto-craft](src/AutoCraft/man/en/auto-craft.md) | Crafts items using registered chests and a crafting bench. |
| [auto-dig](src/AutoDig/man/en/auto-dig.md) | Mines the block you look at or a list of chosen positions. |
| [auto-drop](src/AutoDrop/man/en/auto-drop.md) | Drops unwanted item types as you pick them up. |
| [auto-eat](src/AutoEat/man/en/auto-eat.md) | Eats when hunger falls below your chosen level. |
| [auto-fishing](src/AutoFishing/man/en/auto-fishing.md) | Casts, detects bites, reels in and casts again. |
| [auto-relog](src/AutoRelog/man/en/auto-relog.md) | Reconnects after a kick with a configurable delay. |
| [auto-respond](src/AutoRespond/man/en/auto-respond.md) | Replies to matching chat or runs an MCC command. |
| [chat-log](src/ChatLog/man/en/chat-log.md) | Saves timestamped chat messages to a file. |
| [discord-bridge](src/DiscordBridge/man/en/discord-bridge.md) | Relays messages between Minecraft and Discord. |
| [discord-rpc](src/DiscordRpc/man/en/discord-rpc.md) | Shows your client activity in Discord rich presence. |
| [farmer](src/Farmer/man/en/farmer.md) | Plants, fertilizes, harvests and collects crops in a chosen area. |
| [follow-player](src/FollowPlayer/man/en/follow-player.md) | Follows a player at a distance you choose. |
| [items-collector](src/ItemsCollector/man/en/items-collector.md) | Walks to nearby dropped items and picks them up. |
| [llm-core](src/LlmCore/man/en/llm-core.md) | Configures AI providers, models and shared tools for other plugins. |
| [llm-test](src/LlmTest) | Developer tool that checks the shared AI service and saves results. |
| [map](src/Map/man/en/map.md) | Displays or exports maps received from the server. |
| [mcp-server](src/McpServer/man/en/mcp-server.md) | Lets external AI tools inspect and control the game through MCP. |
| [player-list-logger](src/PlayerListLogger/man/en/player-list-logger.md) | Records online players and when they join or leave. |
| [remote-control](src/RemoteControl/man/en/remote-control.md) | Runs commands sent in whispers by players you allow. |
| [replay-capture](src/ReplayCapture/man/en/replay-capture.md) | Records a session for playback in ReplayMod. |
| [script-scheduler](src/ScriptScheduler/man/en/script-scheduler.md) | Runs command lists on login, dialogs, timers or a daily schedule. |
| [telegram-bridge](src/TelegramBridge/man/en/telegram-bridge.md) | Relays messages between Minecraft and Telegram. |

## Build and contribute

Install .NET SDK `10.0.401` and Python 3.11 or later. Clone with submodules:

```sh
git clone --recurse-submodules https://github.com/MCCTeam/Marketplace.git
cd Marketplace
dotnet restore Marketplace.slnx --configfile NuGet.Config
dotnet clean Marketplace.slnx -c Release
dotnet build Marketplace.slnx -c Release --no-restore
dotnet test Marketplace.slnx -c Release --no-build
dotnet format Marketplace.slnx --verify-no-changes --no-restore
python3 -m unittest discover -s tests/release -v
```

For an existing clone, run `git submodule update --init --recursive`. Builds restore DMCBK `0.1.0-preview.8` and UMPK `0.9.0-beta.7` from NuGet.org. The UMPK and DMCBK submodules provide source references for development; they do not supply build dependencies. MCC-Skills supplies the shared agent skills through `.skills`, `.codex/skills`, `.agents/skills` and `.claude/skills`.

| Folder | Contents |
| --- | --- |
| `src/<Plugin>/` | Plugin code, settings, translations, manuals and release declarations |
| `tests/` | Plugin behavior, archive loading and release tooling tests |
| `templates/` | Examples for source, compiled and mixed packages |
| `tools/PluginPack/` | Packaging frontend using DMCBK's shared APIs |
| `tools/ArchiveSmoke/` | Loads generated archives in a clean host and checks a scripted session |
| `tools/release/` | Build, archive, tag and publication helpers |
| `marketplace/` | Plugin index and release catalogues |
| `out/` | Generated local output, excluded from Git |

## CI and releases

Pull requests and pushes to `master` run formatting, plugin tests, release tooling tests and a packaging rehearsal. CI executes the plugin suite on Linux, Windows and macOS with x64 and ARM64 runners, plus Windows x86. It also cross-builds Linux ARM32 and musl targets. A cross-build checks compilation; it does not execute the plugin on that target.

The current plugins use portable managed code, so each offers one `compiled`/`any` archive. Plugins that need a particular platform can declare separate targets in `release.toml`. MCC chooses the matching archive for its running process.

After a merge to `master` passes validation, CI publishes new plugin versions, verifies their public hashes and updates the catalogue. Users can then refresh the marketplace and install them. Annotated `plugin/<id>/v<version>` tags remain available for a manual per-plugin release. See [release instructions](docs/releases.md) for setup, retries and platform declarations, and [marketplace formats](docs/marketplace-v2.md) for compatibility and dependencies.

Older catalogue entries copied from the former repository remain recorded but are yanked because their asset URLs are unavailable. New patch versions use this repository's release URLs and the tested library versions.

This repository and its templates use the MIT license. Plugin folders retain their licenses and contributor attribution.
