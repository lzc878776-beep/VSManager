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

### 变更 / Changed

- 目录结构整理为 `src/VSManager`（Assets / Core / Infrastructure / Services / UI）与 `tests/`，根目录只保留解决方案与说明文件。
  Source reorganized into `src/VSManager` (Assets / Core / Infrastructure / Services / UI) and `tests/`; the root only keeps the solution and documentation.
- 分层架构：任务状态机、任务调度器、外部依赖接口（VS 操作、Copilot 通道、语音、AI 客户端、文件系统）与统一日志入口，行为保持不变。
  Layered architecture: task state machine, task dispatcher, interfaces for external dependencies (VS operations, Copilot channel, voice, AI client, file system) and a unified log entry, without behavior changes.
- 默认主分支改为 `main`，新增集成分支 `develop`。
  The default branch is now `main`, with `develop` as the integration branch.

## [1.0.0] - 2026-09-23

### 新增 / Added

- 首个公开版本：多 VS 实例总览与多屏布局、应用内 Copilot 对话、调试控制、AI 总控助手与任务清单、豆包语音播报与语音输入、Web 远程控制与 AI Skill、历史归档、一键发布到 GitHub（含敏感信息自检）。
  First public release: multi-instance overview and multi-monitor layout, in-app Copilot chat, debug control, AI assistant with task list, Doubao voice announcements and voice input, web remote and AI skill, history archive, one-click publish to GitHub with a sensitive-content scan.
