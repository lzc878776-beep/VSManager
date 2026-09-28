# CAD 项目适配包 / CAD project adapter

本目录是 VSManager「CAD 动作执行器」的项目适配包模板。每个 CAD 插件项目一份适配包；换到新项目（例如管道、标注、参数化项目）只需新增一份适配包，**不需要修改 ai.exe 或 VSManager**。

This folder is the project adapter template of the VSManager "CAD action executor". Each CAD plug-in project gets one adapter; a new project (piping, annotation, parametric design, …) only needs a new adapter — **no change to ai.exe or VSManager**.

## 放在哪里 / Where it lives

- 复制本目录到 `%APPDATA%\VSManager\adapters\<名称>\`，再编辑 `adapter.json`。名称以 `_` 开头的目录（如本模板）不参与匹配。
  Copy this folder to `%APPDATA%\VSManager\adapters\<name>\` and edit `adapter.json`. Folders whose names start with `_` (such as this template) never match.
- 程序目录下的 `adapters\` 也会被读取；同名时 `%APPDATA%` 中的适配包优先。
  `adapters\` next to the program is read too; on a name clash the `%APPDATA%` adapter wins.

## adapter.json 字段 / Fields

| 字段 / Field | 说明 / Meaning |
|---|---|
| `name` | 适配包名称 / Adapter name |
| `match` | 匹配的解决方案 / 项目文件名，支持 `*` `?` / Solution or project file names, `*` and `?` allowed |
| `host` | CAD 宿主，目前内置 `AutoCAD` 预设 / CAD host; `AutoCAD` preset is built in |
| `commands` | 命令映射表：别名 → CAD 命令。`runCommand` 只能执行表中的别名或命令 / Command map: alias → CAD command. `runCommand` only runs entries in this map |
| `allowRawCommands` | 设为 `true` 才允许表外命令（缺省 `false`）/ `true` allows commands outside the map (default `false`) |
| `logs` | `getLog` 可读取的日志，支持环境变量与文件名通配符（取最新文件）/ Logs readable by `getLog`; environment variables and file-name wildcards allowed (newest file) |
| `launch.cadPath` | CAD 可执行文件；留空则使用项目调试属性里的启动程序 / CAD executable; empty uses the project's debug start program |
| `launch.extraDlls` | 在项目 DLL 之前 NETLOAD 的附加 DLL / Extra DLLs NETLOADed before the project DLL |
| `launch.startupCommands` | 写入启动脚本、在 NETLOAD 之后执行的命令行 / Command lines run from the startup script after NETLOAD |
| `launch.args` | 附加 CAD 启动参数，如 `/nologo` / Extra CAD launch arguments such as `/nologo` |
| `launch.drawing` | 未记录调试图纸时打开的默认图纸 / Default drawing when no debug drawing is recorded |
| `launch.api` | 非 AutoCAD 宿主时配置 `namespace`、`applicationClass`、`references`（CAD 目录下的托管 DLL）、`template` / For non-AutoCAD hosts configure `namespace`, `applicationClass`, `references` (managed DLLs in the CAD folder) and `template` |

可选 `actions.cs`（参考 `actions.example.cs`，C# 5 语法）可新增或覆盖动作，会编译进 CAD 引导 DLL。
An optional `actions.cs` (see `actions.example.cs`, C# 5 syntax) adds or overrides actions and is compiled into the CAD boot DLL.

## 工作方式 / How it works

1. 在 VSManager 中点击调试，启动程序是 CAD 时：VSManager 找到匹配的适配包，为该 CAD 版本编译引导 DLL（缓存在 `%TEMP%\VSManager\CadAgent\`），把附加 DLL、项目 DLL、引导 DLL 与启动命令写入 NETLOAD 启动脚本，并打开已记录的调试图纸（找不到则打开新图）。
   When VSManager starts debugging and the start program is a CAD host, it finds the matching adapter, compiles a boot DLL for that CAD version (cached in `%TEMP%\VSManager\CadAgent\`), writes extra DLLs, the project DLL, the boot DLL and startup commands to the NETLOAD script, and opens the recorded debug drawing (or a new drawing when it is missing).
2. 引导 DLL 在 CAD 中启动代理，通过 VSManager 本地 Web API 上线并等待动作（需在「属性 → Web 远程」开启）。CAD 命令 `VSM_AIAGENT` 显示代理状态。
   The boot DLL starts the agent inside CAD, which says hello over VSManager's local Web API and waits for actions (enable Properties → Web remote). The CAD command `VSM_AIAGENT` shows the agent status.
3. AI 助手把「待验证」项拆成动作序列调用 `run_cad_actions`；ai.exe 逐条下发（缺省每条 60 秒超时、连接类错误重试 1 次，超时或失败即停止），汇总结果交回 AI 判定或请用户确认。
   The AI assistant splits a "pending verification" item into an action sequence for `run_cad_actions`; ai.exe sends actions one by one (60 s default timeout, one retry for connection errors, stop on timeout or failure) and returns the summary for the AI to judge or for the user to confirm.

## 动作协议 v1 / Action protocol v1

统一入参 / Input: `{ "vs": "1", "timeoutMs": 60000, "action": "runCommand", "args": { "command": "MyCommand" } }`（`args` 的值均为字符串 / all `args` values are strings）

统一返回 / Output: `{ "ok": true, "errorCode": null, "message": "...", "artifacts": [...], "durationMs": 1234 }`

| 动作 / Action | 参数 / Args | 说明 / Notes |
|---|---|---|
| `openDrawing` | `path`?, `readOnly`?, `template`? | 省略 `path` 用已记录的调试图纸；文件不存在时按规则打开新图并说明 / Without `path` the recorded debug drawing is used; a missing file opens a new drawing and says so |
| `switchDrawing` | `name` | 文件名、完整路径或序号 / File name, full path or index |
| `closeAllDrawings` | `discard=true` | 丢弃未保存修改，必须显式确认 / Discards unsaved changes; must be explicit |
| `runCommand` | `command`, `wait`?, `settleMs`? | 等待命令结束（CMDACTIVE=0）/ Waits until the command ends (CMDACTIVE=0) |
| `getParam` | `name` | 系统变量，逗号分隔 / System variables, comma-separated |
| `screenshot` | `maxWidth`? | CAD 主窗口 PNG，只在内存中回传 / CAD main window PNG, returned in memory only |
| `getLog` | `name`?, `lines`?, `since`? | 只读适配包声明的日志 / Only adapter-declared logs |
| `getEntityCount` | `type`?, `layer`? | 模型空间实体数，`type` 为 DXF 名 / Model-space entity count; `type` is a DXF name |

错误码 / Error codes: `TIMEOUT`, `CAD_UNAVAILABLE`, `CAD_GONE`, `INVALID_ARGS`, `UNKNOWN_ACTION`, `NOT_FOUND`, `ACTION_FAILED`, `BUSY`, `CANCELLED`, `TRANSPORT`, `SKIPPED`.

## 项目自带验证接口（可选）/ Project verification interface (optional)

- 需要更精确的判定时，把 VSManager 程序目录中的 `sdk\VsmVerify.cs` 加入插件工程，调用 `VerifyEndpoint.Start("名称", "解决方案名")` 并 `Register` 检查项；AI 助手经 `list_verify_checks` / `run_verify_check` 通过 ai.exe 与本机命名管道调用，返回 pass / fail / inconclusive / error 与证据。无需适配包与 Web 远程。
  For more precise judgement, add `sdk\VsmVerify.cs` from the VSManager program folder to the plug-in, call `VerifyEndpoint.Start("name", "solution")` and `Register` checks; the assistant calls them via `list_verify_checks` / `run_verify_check` through ai.exe over a local named pipe and gets pass / fail / inconclusive / error with evidence. No adapter or Web remote is required.

## 安全边界 / Safety

- 同一时刻只驱动一个 CAD 实例；CAD 崩溃或退出后返回 `CAD_GONE`，不会自动重启。
  One CAD instance at a time; a crash or exit returns `CAD_GONE` and CAD is never restarted automatically.
- 截图与日志只在内存中回传并在 VSManager 窗口展示，不写入用户目录；日志只读取适配包声明的文件。
  Screenshots and logs are returned in memory and shown in VSManager, never written to user folders; only adapter-declared log files are read.
- 通道只接受本机回环连接并使用 Web 远程访问密钥；AI 文件授权范围不变。
  The channel accepts loopback connections only and uses the Web remote access key; AI file grants are unchanged.
- 引导 DLL 位于 `%TEMP%\VSManager\CadAgent\`。若 CAD 的 SECURELOAD 弹出安全提示，请选择加载，或把该目录加入 CAD 的受信任路径。
  The boot DLL lives in `%TEMP%\VSManager\CadAgent\`. If CAD's SECURELOAD shows a security prompt, choose to load it or add that folder to CAD's trusted paths.
- v1 引导 DLL 面向 .NET Framework 版 CAD（如 AutoCAD 2024 及更早）；基于 .NET 8 的 AutoCAD 2025+ 暂不支持，编译失败时调试仍正常进行，只是不加载代理。
  The v1 boot DLL targets .NET Framework CAD hosts (such as AutoCAD 2024 and earlier); .NET 8 based AutoCAD 2025+ is not supported yet — when compilation fails, debugging continues without the agent.
