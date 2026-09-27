# 更新日志 / Changelog

本文件记录 VSManager 的主要变更，格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循[语义化版本](https://semver.org/lang/zh-CN/)。
All notable changes to VSManager are documented here. The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project follows [Semantic Versioning](https://semver.org/).

## [未发布 / Unreleased]

### 变更 / Changed

- 任务结果阅读目标 VS 的最终反馈：新增回执 `UNVERIFIED`；即使回执为 `FAILED`，只要反馈说明功能已实现（构建 / 测试通过）、仅尚未在运行中的程序里实际验证，且没有构建失败、未实现或需要用户决定等问题，任务即进入新的「未验证」状态（`unverified`，与「已完成」「失败」并列，黄色 ◐ 标记），不再判为失败，也不阻塞后续任务；右键「标记为已验证」转为已完成，也可手动重新排队；状态写入 tasks.json，重启后保留；AI 助手按未验证汇报。缺少回执仍判失败。
  Task results now read the target VS's final feedback: a new `UNVERIFIED` receipt, and even with a `FAILED` receipt, feedback saying the work is implemented (build / tests pass) with only runtime verification pending, and no build errors, missing implementation or required user decisions, puts the task in a new "unverified" status (`unverified`, alongside done and failed, shown with a yellow ◐) instead of failed, without blocking successors. "Mark as verified" turns it into done, and it can be requeued manually; the status is saved in tasks.json and survives restarts; the AI assistant reports it as unverified. Missing receipts still fail.
- 笔记本改用本地 SQLite 数据库（`%APPDATA%\VSManager\Notebooks\notebook.db`），文件夹与笔记合并为「页面」：每个页面都有正文，也可包含子页面（右键「新建子页面 / 新建同级页面」）。首次启动时一次性导入原有 `.md` 文件、目录与图片（同名目录与笔记合并为一页），原文件保留作备份、不再读取；删除为数据库内软删除（含子页面）；侧边栏「导出 Markdown」可随时导出为文件夹结构。页面链接使用 `page:<id>`，重命名后仍然有效。
  The notebook now uses a local SQLite database (`%APPDATA%\VSManager\Notebooks\notebook.db`) and merges folders and notes into pages: every page has content and can contain subpages (context menu "New subpage / New sibling page"). Existing `.md` files, folders and images are imported once on first start (a folder and a note with the same name become one page); the original files are kept as a backup and no longer read. Deleting is a soft delete in the database (including subpages). Use "Export Markdown" in the sidebar to export a folder tree at any time. Page links use `page:<id>` and survive renames.

### 新增 / Added

- 任务队列放行等级：AI 总控助手顶栏新增三刻度滑块（已完成 / 待验证 / 失败），决定同一 VS 的前序以什么结果结束时自动执行下一项，设置持久化并兼容旧版「跳过失败前序」开关；被暂停时可「补充信息后重试」（每个任务最多 3 次）或「放行后续任务」；新增 AI 工具 `set_release_level`、`retry_task_with_info`、`release_task`，失败通知会提示 AI 在自行补充重试与转交用户之间判断。
  Task queue release level: a three-stop slider in the AI assistant header (Completed / Awaiting verification / Failed) decides which predecessor outcomes let the next task on the same VS run automatically; the setting persists and stays compatible with the legacy "Skip failed predecessors" switch. Paused entries can be retried with supplementary info (at most 3 times per task) or released. New AI tools `set_release_level`, `retry_task_with_info` and `release_task`; failure notices ask the AI to choose between supplementing and retrying by itself or handing over to the user.

- 任务结果更细分
  Finer task outcomes: a new `NEEDS_USER` receipt (changes done, but the user must test or confirm) shows the task as "Done (awaiting user verification)" instead of failed; `FAILED` is only for work the task itself did not finish, and unrelated pre-existing errors do not count. Failures are classified (delivery / VS closed / read error / missing receipt / reported by Copilot); failure notices to the AI assistant include the Copilot reply and cause-specific guidance; the AI cannot resend a content-failed task verbatim, and resends or requeues automatically carry the previous failure feedback.

- 一键布局 Copilot 对话：把各 VS 的 Copilot 对话窗格浮动并在第二屏幕（可指定）横向均布或网格排列并置顶显示，可最小化 VS 主窗口；最小宽度限制下自动换行；记录原布局并可一键还原（同时取消置顶）。新增 AI 工具 `arrange_copilot_panes`、`restore_copilot_layout` 与实例列表右键菜单入口。
  One-click Copilot chat layout: floats the Copilot chat pane of each VS and spreads the panes side by side (or in a grid) on the second screen (configurable), kept on top, optionally minimizing the VS main windows; wraps to more rows under a minimum width; the previous layout is recorded and can be restored (which also clears always-on-top). New AI tools `arrange_copilot_panes` and `restore_copilot_layout`, plus instance list context menu entries.
- 已完成任务自动写入笔记本：按天建立「yyyy.M.d 任务记录」页面，其子页面「已完成任务」列出时间与简述，点击进入任务详情子页面；笔记中的页面链接可在阅读视图中直接打开。设置项 `RecordCompletedTasksInNotebook`，默认开启。
  Completed tasks are recorded in the notebook: a daily "yyyy.M.d 任务记录" page with a "已完成任务" subpage listing times and summaries that link to detail pages; page links open inside the notebook preview. Setting `RecordCompletedTasksInNotebook`, on by default.
- 笔记本嵌入主界面：左侧按 VS 线程、AI 总控助手、Notion 树形目录分区，VS 和 Notion 可独立折叠；点击 VS、AI 或笔记，在同一主内容区切换对话与笔记，不再弹出独立窗口。切换前自动保存，保存失败时保留笔记页和草稿；VS 定时刷新不会打断笔记编辑，原有 Markdown 存储路径不变。
  Embedded notebooks in the main window: the sidebar has VS threads, the existing AI assistant, and a Notion notebook tree. VS and Notion sections collapse independently; selecting a VS, AI or note switches the shared content area without opening another window. Notes save before switching; failed saves keep the notebook and draft active. VS refresh does not interrupt editing, and the Markdown storage location is unchanged.

- 本地 Markdown 笔记本：主界面侧边栏「笔记本」入口，浅色树形目录与居中阅读布局，支持嵌套目录、新建 / 重命名、名称与正文搜索、编辑 / 阅读 / 分栏、停止输入后自动保存、插入本地图片及手动复制带来源的 AI 参考资料。文件保存在 `%APPDATA%\VSManager\Notebooks`；可通过「打开本地目录」导入 `.md` 文件后刷新。保存保留上一版 `.bak`，删除移入 `.trash`；外部修改冲突时保留编辑草稿并阻止覆盖，可「另存草稿」后重载。单篇笔记和图片上限 4 MB；禁用原始 HTML、远程图片和自动上传，外部链接需确认打开。AI 参考资料目前为手动复制，不会自动检索或写入笔记。
  Local Markdown notebooks: open from the main sidebar, with a light tree sidebar and centered reading layout, nested folders, create / rename, title and content search, edit / read / split modes, debounced autosave, local images and explicit AI-context copying with source names. Files live in `%APPDATA%\VSManager\Notebooks`; use Open folder to import `.md` files and then refresh. Saves retain a previous-version `.bak`; deleted items move to `.trash`. External edits block overwrites and preserve the draft; use Save copy before reloading. Notes and images are limited to 4 MB each. Raw HTML, remote images and automatic uploads are disabled; opening external links requires confirmation. AI context is manually copied, not automatically retrieved or written back.


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

- 任务结果阅读目标 VS 的最终反馈：新增回执 `UNVERIFIED`；即使回执为 `FAILED`，只要反馈说明功能已实现（构建 / 测试通过）、仅尚未在运行中的程序里实际验证，且没有构建失败、未实现或需要用户决定等问题，任务即进入新的「未验证」状态（`unverified`，与「已完成」「失败」并列，黄色 ◐ 标记），不再判为失败，也不阻塞后续任务；右键「标记为已验证」转为已完成，也可手动重新排队；状态写入 tasks.json，重启后保留；AI 助手按未验证汇报。缺少回执仍判失败。
  Task results now read the target VS's final feedback: a new `UNVERIFIED` receipt, and even with a `FAILED` receipt, feedback saying the work is implemented (build / tests pass) with only runtime verification pending, and no build errors, missing implementation or required user decisions, puts the task in a new "unverified" status (`unverified`, alongside done and failed, shown with a yellow ◐) instead of failed, without blocking successors. "Mark as verified" turns it into done, and it can be requeued manually; the status is saved in tasks.json and survives restarts; the AI assistant reports it as unverified. Missing receipts still fail.
- AI 助手快捷按钮精简为「同步 git」（本地主分支与远程主分支一致）与「worktree 并入主分支」，由 AI 派给对应仓库的 VS 执行，不做强推或丢弃修改。
  AI assistant quick buttons reduced to "Sync git" (local main matches remote main) and "Merge worktrees into main", dispatched by the AI to the VS owning the repository, without force pushes or discarding changes.

- 目录结构整理为 `src/VSManager`（Assets / Core / Infrastructure / Services / UI）与 `tests/`，根目录只保留解决方案与说明文件。
  Source reorganized into `src/VSManager` (Assets / Core / Infrastructure / Services / UI) and `tests/`; the root only keeps the solution and documentation.
- 分层架构：任务状态机、任务调度器、外部依赖接口（VS 操作、Copilot 通道、语音、AI 客户端、文件系统）与统一日志入口，行为保持不变。
  Layered architecture: task state machine, task dispatcher, interfaces for external dependencies (VS operations, Copilot channel, voice, AI client, file system) and a unified log entry, without behavior changes.
- 默认主分支改为 `main`，新增集成分支 `develop`。
  The default branch is now `main`, with `develop` as the integration branch.

### 修复 / Fixed

- 输入框 @ 候选弹层不再频闪：定时刷新时候选未变化则不重建列表、不重设选中项与位置，且弹层首次显示即定位到光标处。
  The @ mention popup no longer flickers: periodic refreshes skip rebuilding unchanged candidates, selection and bounds, and the popup opens at the caret on first show.

- 打开 VSManager 后 Visual Studio 明显卡顿：Copilot 忙碌探测每 1.5 秒对每个 VS 的整棵 UI Automation 树做后代搜索（实测每个 VS 每轮 3–6 秒，由 VS 界面线程响应）。现在复用已缓存的对话窗格，只在窗格直接子元素中查找停止 / 发送按钮与附件列表（约 30–40 毫秒）；仅在子级找不到时才每 30 秒深度搜索一次，未找到窗格时的完整搜索冷却由 10 秒逐步延长到 30 秒。
  Visual Studio lagged noticeably while VSManager was running: the Copilot busy probe ran a descendant search over each VS's entire UI Automation tree every 1.5 s (measured at 3–6 s per VS per round, served by the VS UI thread). The probe now reuses the cached chat pane and looks for the stop / send buttons and the attachment list among the pane's direct children only (about 30–40 ms); a deep search runs at most every 30 s when no child match exists, and the full search cooldown for a missing pane grows from 10 s to 30 s.

- 向 Copilot 发送任务时误报「未能确认消息已粘贴到 Copilot 输入框」：对话中含代码块时，代码块（同为 WpfTextView）被当成输入框，导致无法聚焦、粘贴落空并连续失败。现在只在输入框宿主 WpfTextViewHost 下查找；粘贴确认改为规范化比对（换行、空白、全角半角、零宽字符）、带退避的轮询与可配置超时，失败时区分「没写进去 / 仍在粘贴 / 内容不一致 / 无法读取」，自动重试一次并在发送日志中记录诊断信息。
  False "could not confirm the message was pasted into the Copilot input box" errors: when the conversation contained a code block, the code block (also a WpfTextView) was taken for the input box, so focusing and pasting failed repeatedly. The input is now looked up only under its WpfTextViewHost; paste confirmation uses normalized comparison (line breaks, whitespace, full/half width, zero-width characters), back-off polling with a configurable timeout, distinguishes "not written / still pasting / different content / unreadable", retries once automatically and writes diagnostics to the send log.
- 上一项修复后偶发连续报「未找到 Copilot 输入框」：长回合结束后窗格的 UI Automation 树暂未刷新，而重试仍复用缓存的窗格、只在控件视图中查找且不记录原因。现在按 L1 WpfTextViewHost → L2 对话列表外最靠下的 WpfTextView → L3 最靠下的可编辑文本元素 → L4 旧方式（校验可编辑）→ L5 位置命中测试分层降级，每级记录诊断；带退避轮询并在每次轮询时丢弃缓存重新查找窗格，失败后重新打开窗格自动重试一次；区分「窗格未找到 / 输入框未找到 / 输入框不可编辑」并给出可操作提示。
  Intermittent repeated "Copilot input box not found" after the fix above: after a long turn the pane's UI Automation tree was not refreshed yet, while the retry reused the cached pane, searched only the control view and logged no reason. The input is now located through a layered fallback L1 WpfTextViewHost → L2 lowest WpfTextView outside the conversation list → L3 lowest editable text element → L4 legacy lookup (editability checked) → L5 hit test, with diagnostics per level; polling backs off and re-finds the pane without the cache on every poll, then reopens the pane and retries once; "pane not found / input not found / input read-only" are reported separately with actionable hints.
- 调用 DTE 命令前就记录 VS 是否在前台，使后台发送后切回原窗口的逻辑在命令把 VS 切到前台时也能生效。
  Whether VS was in the foreground is now recorded before any DTE command, so background sending switches back to the previous window even when the command brought VS forward.

## [1.0.0] - 2026-09-23

### 新增 / Added

- 已完成任务自动写入笔记本：按天建立「yyyy.M.d 任务记录」目录，清单页列出时间与简述，点击进入任务详情；笔记中的相对 .md 链接可在阅读视图中直接打开。设置项 `RecordCompletedTasksInNotebook`，默认开启。
  Completed tasks are recorded in the notebook: a daily "yyyy.M.d 任务记录" folder with a list page of times and summaries linking to detail notes; relative .md links now open inside the notebook preview. Setting `RecordCompletedTasksInNotebook`, on by default.
- 首个公开版本：多 VS 实例总览与多屏布局、应用内 Copilot 对话、调试控制、AI 总控助手与任务清单、豆包语音播报与语音输入、Web 远程控制与 AI Skill、历史归档、一键发布到 GitHub（含敏感信息自检）。
  First public release: multi-instance overview and multi-monitor layout, in-app Copilot chat, debug control, AI assistant with task list, Doubao voice announcements and voice input, web remote and AI skill, history archive, one-click publish to GitHub with a sensitive-content scan.
