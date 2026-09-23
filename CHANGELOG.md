# 更新日志 / Changelog

本文件记录 VSManager 的主要变更，格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循[语义化版本](https://semver.org/lang/zh-CN/)。
All notable changes to VSManager are documented here. The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project follows [Semantic Versioning](https://semver.org/).

## [未发布 / Unreleased]

### 新增 / Added

- AI 助手自动重启：内部异常、请求连续失败或长时间无响应时自动重建，状态栏提示；可选进程看门狗（默认关闭）在异常退出后自动拉起；防重启风暴（默认 5 分钟最多 3 次）；「⟳ 重启」菜单与托盘菜单提供手动重启。
  AI assistant auto-restart after internal errors, repeated request failures or hangs, with a status-bar notice; an optional process watchdog (off by default) relaunches the app after an abnormal exit; restart-storm guard (default 3 per 5 minutes); manual restart from the "⟳" menu and the tray menu.
- 内存面板：按 VSManager / 各 VS 实例（含子进程）/ 共享组件统计工作集与私有字节，支持温和清理、前后对比与可选的超阈值策略。
  Memory panel: working set and private bytes per VSManager / VS instance (with child processes) / shared components, gentle cleanup with before/after numbers and an optional threshold policy.
- AI 助手对话记录持久化（`agent-chat.jsonl`）与「📜 对话记录」查看窗口（搜索、按日期筛选、正序 / 倒序）。
  Persistent AI assistant chat history (`agent-chat.jsonl`) and a "📜" history viewer (search, date filter, sort order).
- 单元测试项目 `tests/VSManager.Tests`（MSTest），覆盖任务队列状态流转、编号分配、配置读写与默认值、发送重试判定、自动重启与防风暴。
  Unit test project `tests/VSManager.Tests` (MSTest) covering task state transitions, id allocation, settings defaults and persistence, send retry rules, auto-restart and the storm guard.
- 文档：README 全部配置项与默认值、分支策略、常见问题；新增 CONTRIBUTING.md 与 CHANGELOG.md。
  Docs: full settings reference, branching and FAQ in the README; new CONTRIBUTING.md and CHANGELOG.md.
- 发送确认配置项：`SendConfirmTimeoutSeconds`（默认 10 秒）、`SendAutoRetry`（默认开启）、`SendRetryCount`（默认 1 次），可在「属性 → 发送确认」修改。
  Send confirmation settings: `SendConfirmTimeoutSeconds` (default 10 s), `SendAutoRetry` (on by default) and `SendRetryCount` (default 1), editable in Settings → Send confirmation.
- 输入框定位配置项：`SendLocateTimeoutSeconds`（默认 6 秒，1–60）、`SendLocateRetryCount`（默认 1 次，0–5），可在「属性 → 发送确认」修改。
  Input locate settings: `SendLocateTimeoutSeconds` (default 6 s, 1–60) and `SendLocateRetryCount` (default 1, 0–5), editable in Settings → Send confirmation.
- 失败任务重新排队后自动隐藏原失败条目：按规范化正文的 SHA-256 指纹与目标 VS 判定同一任务（正文过短或目标不同时保留并写入任务日志，支持「重发 #编号」标记），只在界面隐藏（`settings.json` 的 `HiddenResentTasks`），不修改 `tasks.json` 与归档；「历史」可查看，右键「恢复显示该失败条目」或「撤销清除」可恢复；新增 `AutoHideResentFailedTasks`（默认开启）与 `AutoHideResentFailedNotify`（默认开启），通知 / 语音播报「已重新排队，原失败条目已隐藏」。
  Auto-hide the original failed entry when a task is requeued: the same task is identified by the SHA-256 fingerprint of the normalized text plus the target VS (too-short texts or a different target are kept and logged; "resend #id" markers are supported); hidden in the UI only (`HiddenResentTasks` in `settings.json`), `tasks.json` and the archive are untouched; History shows them and right-click "Show this failed entry again" or "Undo clear" restores them; new `AutoHideResentFailedTasks` (on by default) and `AutoHideResentFailedNotify` (on by default), with a notification / voice message "Requeued; the original failed entry is hidden".
- 解决方案登记与 VS 开关：`%APPDATA%\VSManager\solutions.json` 登记表（别名、路径、同义词、说明、默认 VS 编号）与登记窗口（支持从已打开的 VS 一键登记）；别名模糊匹配与同义词；新增 AI 工具 `list_solutions`、`open_solution`、`close_vs`（有未保存修改时拒绝，绝不强制结束进程），`list_vs` 显示登记别名。
  Solution registry and VS open/close: `%APPDATA%\VSManager\solutions.json` (alias, path, synonyms, description, default VS number) with a registry window (one-click registration from an open VS); fuzzy alias matching with synonyms; new AI tools `list_solutions`, `open_solution` and `close_vs` (refuses when there are unsaved changes, never kills the process); `list_vs` shows registry aliases.
- 任务暂存：目标 VS 未打开时任务进入「等待目标 VS」（`waiting_vs`）并持久化，打开后自动推送，并弹出通知 / 语音播报；新增配置 `SolutionCloseConfirm`（true）、`SolutionOpenWaitSeconds`（90）、`PendingVsSettleSeconds`（20）、`PendingVsNotify`（true）。旧版本会把 `waiting_vs` 任务视为已取消。
  Parked tasks: a task whose target VS is not open waits as "waiting for target VS" (`waiting_vs`), is persisted and pushed automatically once the VS opens, with a notification / voice message; new settings `SolutionCloseConfirm` (true), `SolutionOpenWaitSeconds` (90), `PendingVsSettleSeconds` (20) and `PendingVsNotify` (true). Older versions treat `waiting_vs` tasks as cancelled.

### 变更 / Changed

- 目录结构整理为 `src/VSManager`（Assets / Core / Infrastructure / Services / UI）与 `tests/`，根目录只保留解决方案与说明文件。
  Source reorganized into `src/VSManager` (Assets / Core / Infrastructure / Services / UI) and `tests/`; the root only keeps the solution and documentation.
- 分层架构：任务状态机、任务调度器、外部依赖接口（VS 操作、Copilot 通道、语音、AI 客户端、文件系统）与统一日志入口，行为保持不变。
  Layered architecture: task state machine, task dispatcher, interfaces for external dependencies (VS operations, Copilot channel, voice, AI client, file system) and a unified log entry, without behavior changes.
- 默认主分支改为 `main`，新增集成分支 `develop`。
  The default branch is now `main`, with `develop` as the integration branch.

### 修复 / Fixed

- 向 Copilot 发送任务时误报「未能确认消息已粘贴到 Copilot 输入框」：对话中含代码块时，代码块（同为 WpfTextView）被当成输入框，导致无法聚焦、粘贴落空并连续失败。现在只在输入框宿主 WpfTextViewHost 下查找；粘贴确认改为规范化比对（换行、空白、全角半角、零宽字符）、带退避的轮询与可配置超时，失败时区分「没写进去 / 仍在粘贴 / 内容不一致 / 无法读取」，自动重试一次并在发送日志中记录诊断信息。
  False "could not confirm the message was pasted into the Copilot input box" errors: when the conversation contained a code block, the code block (also a WpfTextView) was taken for the input box, so focusing and pasting failed repeatedly. The input is now looked up only under its WpfTextViewHost; paste confirmation uses normalized comparison (line breaks, whitespace, full/half width, zero-width characters), back-off polling with a configurable timeout, distinguishes "not written / still pasting / different content / unreadable", retries once automatically and writes diagnostics to the send log.
- 上一项修复后偶发连续报「未找到 Copilot 输入框」：长回合结束后窗格的 UI Automation 树暂未刷新，而重试仍复用缓存的窗格、只在控件视图中查找且不记录原因。现在按 L1 WpfTextViewHost → L2 对话列表外最靠下的 WpfTextView → L3 最靠下的可编辑文本元素 → L4 旧方式（校验可编辑）→ L5 位置命中测试分层降级，每级记录诊断；带退避轮询并在每次轮询时丢弃缓存重新查找窗格，失败后重新打开窗格自动重试一次；区分「窗格未找到 / 输入框未找到 / 输入框不可编辑」并给出可操作提示。
  Intermittent repeated "Copilot input box not found" after the fix above: after a long turn the pane's UI Automation tree was not refreshed yet, while the retry reused the cached pane, searched only the control view and logged no reason. The input is now located through a layered fallback L1 WpfTextViewHost → L2 lowest WpfTextView outside the conversation list → L3 lowest editable text element → L4 legacy lookup (editability checked) → L5 hit test, with diagnostics per level; polling backs off and re-finds the pane without the cache on every poll, then reopens the pane and retries once; "pane not found / input not found / input read-only" are reported separately with actionable hints.
- 调用 DTE 命令前就记录 VS 是否在前台，使后台发送后切回原窗口的逻辑在命令把 VS 切到前台时也能生效。
  Whether VS was in the foreground is now recorded before any DTE command, so background sending switches back to the previous window even when the command brought VS forward.

## [1.0.0] - 2026-09-23

### 新增 / Added

- 首个公开版本：多 VS 实例总览与多屏布局、应用内 Copilot 对话、调试控制、AI 总控助手与任务清单、豆包语音播报与语音输入、Web 远程控制与 AI Skill、历史归档、一键发布到 GitHub（含敏感信息自检）。
  First public release: multi-instance overview and multi-monitor layout, in-app Copilot chat, debug control, AI assistant with task list, Doubao voice announcements and voice input, web remote and AI skill, history archive, one-click publish to GitHub with a sensitive-content scan.
