---
name: vsmanager
description: Control every running Visual Studio instance on this Windows machine through VSManager (多 VS 管理工具) — list VS instances, send coding tasks to each VS's GitHub Copilot chat and wait for the reply, read the chat, start/stop debugging, build, read the error list, dock the Copilot pane as a tool window, and keep a role description for each VS so tasks can be routed automatically. Use when the user wants to dispatch work to, monitor, debug or build in Visual Studio / VS Copilot.
---

# VSManager — multi Visual Studio control

VSManager runs on this machine and exposes a local, key-protected HTTP API. All calls go through the
helper script next to this file, which reads the port and access key from
`%APPDATA%\VSManager\settings.json` automatically. Never print or ask for the key.

Script: `vsm.ps1` in this skill's directory. Run it with Windows PowerShell:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "<skill dir>\vsm.ps1" <command> [args]
```

## Commands

| Command | Purpose |
|---|---|
| `list` | List VS instances: index, name, PID, Copilot state (busy / idle / pane not found), debug state |
| `send <vs> "<text>" [-Wait] [-Timeout 600]` | Send a task to that VS's Copilot chat. `-Wait` blocks until Copilot finishes and prints its final reply |
| `wait <vs> [-Timeout 600]` | Wait until that VS's Copilot finishes; prints the last reply |
| `reply <vs>` | Print the last Copilot reply |
| `chat <vs>` | Print the recent conversation (up to 30 messages; `>` lines are tool/progress steps) |
| `debug <vs> <action>` | `go` (start/continue F5), `run` (Ctrl+F5), `break`, `stop`, `restart`, `build`, `rebuild`, `cancelbuild`, `stepover`, `stepinto`, `stepout` |
| `errors <vs> [-Max 50]` | Read the Error List (errors and warnings), usually after `build` |
| `stop <vs>` | Stop the running Copilot response |
| `new <vs>` | Start a new Copilot chat thread |
| `dock` | Switch the Copilot chat pane of every VS to a docked tool window (fixes "pane not found" caused by it being a hidden document tab) |
| `note <vs> ["<role>"] [-Clear]` | Show, set or clear the VS's role description (what project/modules it owns). Stored by VSManager and shown by `list` |

`<vs>` is the index from `list` (`1`, `#2`), part of the VS/solution name, or `-VsPid <pid>`.
Add `-Json` to any command for raw JSON output.

## Workflow

1. Always run `list` first to see which VS instances exist, their numbers, `role:` descriptions and
   `solution:` paths.
   - **Routing**: when the user doesn't name a VS, pick the one whose role / solution matches the
     task. Ask only if several match equally. Don't make the user confirm obvious choices.
   - **Role descriptions**: if a VS has no `role:` (or the user asks to describe them), inspect its
     `solution:` folder with your own file tools (projects, folders, main classes, README) plus
     `chat <vs>`, then save a stable 40–80 character summary with `note <vs> "<role>"` — what the
     project is, host / tech stack, main modules. Don't include one-off tasks, window titles or
     debug state. When the user tells you what a VS is for, save it the same way.
2. To delegate work: `send <vs> "<clear, self-contained task>" -Wait`. Write the task the way you'd
   brief a developer: goal, files/areas, constraints, and how to verify. Copilot in that VS has the
   solution context; you do not.
3. Don't send a new task to a VS whose Copilot is `busy` — `wait` for it first (or `stop` if asked).
4. Independent tasks for different VS instances can be sent without `-Wait` and collected later with
   `wait <vs>`.
5. After code changes, `debug <vs> build` then `errors <vs>` to verify; report failures back to the
   user or send a follow-up fix task.
6. If `list` shows "pane not found" for a VS, run `dock`, then `list` again.
7. `send` types into the VS in the background and doesn't switch the user's screen; never activate
   VS windows unless asked.
8. Starting/stopping debugging affects the user's running session — only do it when asked.

## Where things are stored

VSManager keeps all settings — including role descriptions and aliases — in
`%APPDATA%\VSManager\settings.json` (atomic save with a `.bak` backup). Send logs are in
`%APPDATA%\VSManager\logs\`. Don't edit these files directly; use the commands above.

## Troubleshooting

- "VSManager API is disabled": enable 「属性 → 手机网页遥控」 in VSManager, or reinstall the skill
  from VSManager's settings (it enables the API).
- "API call failed … Is VSManager running?": ask the user to start VSManager.
- `401` / invalid key: the key was reset in VSManager; the script re-reads it each run, so just retry.
