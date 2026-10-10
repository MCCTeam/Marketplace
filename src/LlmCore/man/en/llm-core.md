# llm-core

llm-core is the shared AI brain for your plugins. It talks to AI
models so other plugins do not have to. Alerts asks it whether a chat
line deserves a ping. Future plugins will ask it their own questions.

It answers nothing itself. It has no chat command that chats. It only
serves. Turn it on, connect it to a model, and let the other plugins
use it.

## Start in five minutes

You need two things. A place that runs a model. A name for it in the
core. The easy path is a local model with Ollama or LM Studio. The
core talks to anything that speaks the OpenAI format.

1. Start your local model server. Ollama listens on port 11434.
2. Run `llm setup local http://localhost:11434/v1/ - local/qwen3-8b`.
3. The core probes the address. It lists the models it finds.
4. Run `llm status`. Your provider and model show up.

The `-` means no key needed. Local servers rarely ask for one. Now
point a plugin at it. Alerts with `Model = "local/qwen3-8b"` judges
through your own machine, free of charge.

No local server? Any OpenAI-style address works the same way. Put your
key in an environment variable first. Never paste keys into settings
files.

```
llm setup openrouter https://openrouter.ai/api/v1/ OPENROUTER_KEY openrouter/deepseek/deepseek-chat
```

`OPENROUTER_KEY` is the variable name, not the key itself. The core
reads the value when it calls. Anyone reading your files finds no keys.

## Providers

A provider is one AI address. Localhost, a cloud account, a friend's
server. Each holds its own models and its own key.

```
llm provider list
llm provider add local http://localhost:11434/v1/
llm provider edit local keyenv=OLLAMA_KEY
llm provider enable local
llm provider disable local
llm provider status local
llm provider remove local local
```

Adding probes the address first. A dead address never lands in your
settings. Editing changes the address, the format, or the key
variable. Disabling keeps the entry and skips it. Removing asks you to
repeat the name, so stray keystrokes delete nothing.

Providers live in `settings.toml` under `[[Providers]]` too. Each row
names the address, the format (`responses` or the older `chat`), and
the key variable. Commands are the easy path. The file is the backup.

```
[[Providers]]
Name = "local"
Endpoint = "http://localhost:11434/v1"
Api = "responses"
Enabled = true
ApiKeyEnv = ""
```

## Models

Each provider holds models. The core asks the address for the list and
caches it for a day. Some servers hide models from the list. Pin those
by hand.

```
llm model list local
llm model refresh local
llm model add local my-custom-model
llm model remove local my-custom-model
llm cache clear local
```

Listing reads the cache. Refresh asks the server again. Adding pins an
id the server forgot to mention. The cache stores what the server said
apart from what you pinned, so clearing never eats your pins.

Plugins name models per task as `provider/model-id`. A fast small
model can sort chat while a big one answers questions. Empty means the
core default. Set it once and forget it.

```
llm use local/qwen3-8b
```

## Money

AI calls cost time and often money. Two commands watch the wallet.
`llm status` shows providers, the default model, and spend so far.
`llm budget` shows and sets ceilings.

```
llm budget
llm budget 1000000
llm budget alerts 50000
```

The first form only looks. The second caps the whole core per day.
The third caps one plugin per day. Zero means no cap. A spent budget
fails calls with a plain error instead of quietly spending more.

`llm why` explains the last answer. Which model ran. Which tools and
skills took part. How many tokens went each way. Paste its output when
you ask for help with a weird answer.

## Health

`llm doctor` probes everything and prints fixes you can paste. Dead
addresses. Missing keys. Empty model lists. Run it first when anything
AI stops working. It answers most questions before you ask them.

```
llm doctor
```

## Skills

Skills are folders of know-how the model can pull while it works. Each
holds a `SKILL.md` file with plain instructions, plus extra files it
links to. The core serves four read-only tools for them. List them.
Read one. Search them. Read one file. Nothing ever writes or runs.

```
llm skill list
```

Plugins register their own skill folders through the core. Skill texts
load only when needed, so idle skills cost nothing.

## MCP servers

MCP servers lend the model outside tools over the network. A wiki
search. A calculator. Anything with an MCP address. Remote tools show
up named like `wiki_search` and run only when a call names them.

```
llm mcp list
llm mcp check wiki
llm mcp add wiki http://localhost:3000/mcp search
llm mcp remove wiki wiki
```

Adding connects first and verifies the tool list. A server that fails
the check never lands in your settings. Removing asks twice like
providers do. Shared servers live under `[mcp.servers.NAME]` in
settings with their address and tool list.

## Settings file

Almost everything above writes `settings.toml` for you. Read it to
learn. Edit it by hand when commands feel slow. The knobs that matter:

- `DefaultModel` is the model used when a call names none.
- `DefaultMaxTokens` caps answer length. Zero means the server decides.
- `DefaultMaxContext` caps context size for long talks.
- `DailyTokenBudget` caps the whole core per day. Zero means no cap.
- `AllowLocalEndpoints` must be true for `http://localhost` addresses.
- `[[Providers]]` rows mirror `llm provider add`.
- `[mcp.servers.NAME]` tables mirror `llm mcp add`.

## When it breaks

Start with `llm doctor`. Then check the usual suspects.

No providers listed means none configured. Run `llm setup` again.
`401` or `auth` errors mean the key variable is empty or wrong. Print
it with `echo $NAME` and compare. Model-not-found means the id is
misspelled or belongs to another provider. `llm model list` shows the
true names. Localhost refused means the server is down or
`AllowLocalEndpoints` is false. Timeouts mean an overloaded server or
a dead address. Point a browser or curl at the address to tell which.
