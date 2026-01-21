# ShoutRunner

Dalamud plugin that lets you build a timed macro composed of:
- `Shout` messages (sent with `/sh`)
- Teleports to any aetheryte name (sent with `/tele`)
- World visits and data center transfers (sent with `/visit` and `/datacenter`)

You can schedule the macro to repeat at a custom interval (hours/minutes/seconds) and optionally add a delay between steps to avoid flooding commands.

## Usage
1) Build and install the plugin through your Dalamud dev environment.
2) In-game, run `/shoutrunner` to open the window.
3) Add actions in the order you want them executed.
4) Set the repeat interval and per-action delay.
5) Press **Start** to run once (or keep repeating if enabled). Press **Stop** to cancel the next run.

> Note: Teleport/world/DC steps use the normal chat commands, so make sure the payload matches how you would type it in-game (e.g., `/tele Limsa Lominsa` or `/visit Zalera`).
