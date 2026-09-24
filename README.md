# VSManager · 多 VS 管理工具

VSManager 是一个 Windows 桌面工具（WinForms / .NET Framework 4.8），用于同时管理本机上多个 Visual Studio 实例及其中的 GitHub Copilot 对话。

## 功能

- **VS 实例总览**：自动发现正在运行的 Visual Studio，显示解决方案、调试状态与 Copilot 忙碌 / 空闲状态；一键布局到多块屏幕。
- **应用内 Copilot 对话**：在 VSManager 中向任意 VS 的 Copilot 发送消息（支持图片），实时查看回复（Markdown 渲染）。
- **调试控制**：开始 / 停止 / 中断 / 重新启动调试，生成 / 重新生成，读取错误列表。
- **AI 总控助手**：接入任意 OpenAI 兼容接口（默认 DeepSeek），通过函数调用查看各 VS 状态、分派任务、等待结果。
- **任务清单**：助手或用户发布的任务在 VS 忙碌时自动排队，空闲后自动发布；同时显示各 VS 中手动进行的 Copilot 对话。
- **解决方案登记与 VS 开关**：按常用名称（别名 / 同义词，支持模糊匹配）登记解决方案，AI 助手可据此打开 / 关闭 VS；目标 VS 未打开时任务自动暂存，打开后自动推送。
- **语音**：可选接入豆包语音，任务完成后播报摘要（中文 / English 可选，AI 助手回复语言随之切换），并支持按住说话输入。
- **Web 远程控制与 AI Skill**：在局域网内用手机浏览器操作（需访问令牌）；可把控制 API 安装为 Copilot CLI / Claude Code 等的 Skill。
- **历史归档**：任务流水、助手对话、各 VS 对话与发送日志按天写入 JSONL，默认永久保留。
- **发布到 GitHub**：一键 git init / 提交 / 创建或关联远程仓库 / 推送，发布前自动做敏感信息自检。
- **内存监控**：按 VSManager / 各 VS 实例（含子进程）/ 共享组件分组显示工作集与私有字节，支持温和清理与超阈值提醒。
- **自动重启**：AI 助手出现异常、请求连续失败或长时间无响应时自动重建；可选进程看门狗在 VSManager 异常退出后自动拉起，并有防重启风暴限制。

## 运行环境

- Windows 10 / 11
- Visual Studio 2022 或更高版本，并安装 GitHub Copilot
- .NET Framework 4.8
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)（Windows 11 已自带）

## 构建与运行

```powershell
git clone https://github.com/<owner>/VSManager.git
cd VSManager
dotnet build VSManager.slnx -c Release
.\src\VSManager\bin\Release\net48\VSManager.exe
```

`<owner>` 替换为仓库所有者；默认分支为 `main`，开发中的内容在 `develop`（见[分支策略](#分支策略)）。

也可以直接用 Visual Studio 打开根目录的 `VSManager.slnx`（项目文件位于 `src\VSManager\VSManager.csproj`）生成并运行。

运行单元测试（MSTest，测试数据全部写入系统临时目录，不会读写 %APPDATA%\VSManager 中的真实数据）：

```powershell
dotnet test VSManager.slnx
```

若本机 NuGet 配置中有不可用的源导致还原失败，可先执行 `dotnet restore VSManager.slnx --source https://api.nuget.org/v3/index.json`。

## 目录结构

```text
VSManager/
├─ VSManager.slnx              解决方案
├─ README.md / LICENSE / .gitignore / .gitattributes
├─ CONTRIBUTING.md             贡献指南（分支策略、提交规范、自检）
├─ CHANGELOG.md                更新日志
├─ settings.example.json       配置示例（真实配置在 %APPDATA%\VSManager\，不入库）
├─ src/
│  └─ VSManager/               主程序项目（WinForms，.NET Framework 4.8）
│     ├─ VSManager.csproj
│     ├─ Program.cs / app.manifest
│     ├─ Assets/               图标与嵌入资源（Web 页面、Copilot Skill）
│     │  ├─ app.ico
│     │  ├─ Web/               remote.html、transcript.html
│     │  └─ Skill/             SKILL.md、vsm.ps1
│     ├─ Core/                 领域层：不依赖界面与 Win32
│     │  ├─ Models/            数据模型（外部对话、图片附件、Copilot 状态）
│     │  ├─ Tasks/             任务模型、任务状态机、发送重试判定、任务清单
│     │  └─ TextUtil.cs        通用文本工具
│     ├─ Infrastructure/       基础设施层
│     │  ├─ Config/            配置读写（AppSettings）、数据目录（AppPaths）
│     │  ├─ Http/              AI 接口客户端工厂、请求体策略
│     │  ├─ IO/                文件系统抽象、原子写入
│     │  ├─ Logging/           统一日志入口（AppLog）、发送日志
│     │  ├─ Storage/           tasks.json 存储
│     │  ├─ Win32/             Win32 互操作、内存裁剪
│     │  └─ QrCode.cs          二维码
│     ├─ Services/             服务层
│     │  ├─ Abstractions/      外部依赖接口（VS 操作、Copilot 通道、语音）
│     │  ├─ Agent/             AI 总控助手、提示词、对话记录
│     │  ├─ Publish/           发布到 GitHub
│     │  ├─ Remote/            局域网网页遥控、Skill 安装
│     │  ├─ Storage/           历史归档
│     │  ├─ Tasks/             任务调度器（发布、重试、完成、失败）
│     │  ├─ VisualStudio/      VS 实例、Copilot 对话 / 监控、代码扫描、内存监控
│     │  └─ Voice/             豆包语音合成与识别
│     └─ UI/                   界面层：主窗口、主题、通用控件
│        ├─ Forms/             设置、发布、对话记录、内存等窗口
│        └─ Panels/            Copilot 对话、AI 助手、任务清单面板
└─ tests/
   └─ VSManager.Tests/         单元测试（MSTest）
```

所有源码仍使用同一个命名空间 `VSManager`，目录只用于按职责组织文件。构建输出（`bin/`、`obj/`）与测试结果（`TestResults/`）已被 `.gitignore` 排除。

### 分层架构

- **Core（领域层）**：任务模型 `QueuedTask`、状态机 `TaskStateMachine`（排队 → 发送中 → 执行中 → 已完成 / 失败 / 已取消）、发送重试判定 `SendRetryPolicy`、任务清单 `TaskQueue`（编号分配、历史裁剪、归档流水）。只依赖接口 `ITaskStore`、`ITaskArchiveSink` 与可替换时钟，可直接单元测试。
- **Services（服务层）**：VS 管理、Copilot 消息发送、AI 助手、语音、归档、发布。`TaskDispatcher` 负责任务调度，通过 `ITaskDispatchHost` 与主窗口交互；外部依赖通过 `IVsOperations`、`ICopilotChannel`、`IVoiceService`、`IAiClientFactory` 抽象。
- **Infrastructure（基础设施层）**：Win32 封装、配置与数据目录、文件系统抽象 `IFileSystem` 与原子写入 `AtomicFile`、统一日志 `AppLog`（含未处理异常记录到 crash.log）、HTTP 客户端创建。
- **UI（界面层）**：窗体与控件，只负责展示与交互，业务动作委托给服务层。

## 配置

首次运行时会自动生成配置文件 `%APPDATA%\VSManager\settings.json`，所有选项都可以在主窗口「⚙ 属性」中修改。配置缺失、字段不全或已损坏时会使用默认值（损坏的文件会另存为 `settings.json.corrupt-*`），不会导致程序崩溃。

各字段及默认值见 [`settings.example.json`](settings.example.json)。常用的有：

| 配置项 | 说明 | 默认值 |
|---|---|---|
| `AgentEndpoint` / `AgentModel` | AI 助手接口地址与模型（OpenAI 兼容，需支持函数调用） | DeepSeek |
| `AgentKeyProtected` | AI 助手 API Key（DPAPI 加密，仅本机当前用户可解密） | 空 |
| `VoiceKeyProtected` | 豆包语音 API Key（DPAPI 加密） | 空 |
| `VoiceLanguage` | 语音语言：`zh` 中文 / `en` English；同时决定播报提示词、音色与 AI 总控助手的回复语言，切换后立即生效 | `zh` |
| `VoiceSpeakerEn` | 英文播报的音色 ID（seed-tts-*）或声音描述（seed-audio-*）；不可用时自动回退到默认音色 `zh_female_vv_uranus_bigtts` 并提示 | 英文声音描述 |
| `AgentMax*` | AI 额度：工具返回、单条消息、任务文本、历史消息等的单次上限 | 见示例 |
| `WebEnabled` / `WebPort` / `WebToken` | Web 远程控制开关、端口、访问令牌（为空时自动生成） | 关闭 / 8765 |
| `ArchiveEnabled` / `ArchiveRoot` / `ArchiveRetentionDays` | 历史归档开关、根目录（支持 `%环境变量%`）、保留天数（0 表示永久保留） | 开启 / 见下 / 0 |
| `PublishRepoPath` / `PublishOwner` / `PublishRepoName` / `PublishVisibility` / `PublishBranch` | 发布到 GitHub：本地目录（空＝自动查找）、所有者（空＝Token 用户）、仓库名、可见性、默认分支 | 空 / 空 / VSManager / public / main |
| `PublishAuthorName` / `PublishAuthorEmail` | 提交作者与邮箱（邮箱为空时使用 GitHub noreply 地址） | VSManager contributors / 空 |
| `GitHubTokenProtected` | GitHub Token（DPAPI 加密） | 空 |

<details>
<summary>全部配置项与默认值（点击展开）</summary>

| 分组 | 配置项 | 默认值 | 说明 |
|---|---|---|---|
| 窗口与布局 | `MainScreen` / `ToolScreen` | 空 | 主界面 / 工具窗口所在屏幕（空＝自动） |
| | `Layout` | `上下` | 工具窗口布局方式 |
| | `ActivateMoveMain` / `ActivateMoveTools` | true / false | 激活 VS 时是否移动主界面 / 工具窗口 |
| | `TopMost` / `MinimizeToTray` / `Hotkeys` | false / true / true | 置顶、最小化到托盘、全局热键 |
| | `ClickToActivate` | false | 单击列表即激活 VS |
| | `SidebarWidth` / `AgentHeight` / `TaskPanelCollapsed` | 0 / 0 / false | 界面尺寸记忆（0＝默认） |
| Copilot 监听与发送 | `MonitorCopilot` / `PollMs` | true / 1500 | 监听 Copilot 状态及轮询间隔（毫秒） |
| | `Sound` / `Popup` | true / true | 完成时提示音、托盘气泡 |
| | `CopilotPaneKeyword` / `BusyButtonIds` | `Copilot` / `CancelButton` | 识别 Copilot 窗格与「忙碌」按钮 |
| | `BackgroundSend` / `BackgroundSync` / `AutoOpenChat` | true / true / true | 后台发送、后台同步对话、自动打开对话窗格 |
| | `RestoreCopilotPane` / `ShowChatSteps` | true / true | 窗格被切走时自动切回、显示对话步骤 |
| | `SendConfirmTimeoutSeconds` / `SendAutoRetry` / `SendRetryCount` | 10 / true / 1 | 写入 Copilot 输入框后的确认超时（秒，2–120）、粘贴未确认或未找到输入框时是否自动重试及粘贴重试次数（0–5） |
| | `SendLocateTimeoutSeconds` / `SendLocateRetryCount` | 6 / 1 | 每轮定位 Copilot 输入框的轮询超时（秒，1–60）、未找到时重新打开窗格并重试的次数（0–5，`SendAutoRetry=false` 时不重试） |
| 解决方案登记 | `SolutionCloseConfirm` | true | AI 关闭 VS 前总是弹窗确认（关闭时仍会检查未保存修改） |
| | `SolutionOpenWaitSeconds` | 90 | AI 打开解决方案后等待 VS 出现的最长时间（秒，10–600） |
| | `PendingVsSettleSeconds` | 20 | 暂存任务在目标 VS 出现后再等待的秒数，让解决方案与 Copilot 加载完成（0–300） |
| | `PendingVsNotify` | true | 任务暂存 / 自动推送时弹出通知并语音播报（语音需另行开启） |
| | `WatchConversations` / `ExternalRestoreLimit` / `ExternalRestoreHours` | true / 50 / 24 | 监听各 VS 中的手动对话及启动时恢复的数量与时间范围 |
| | `Aliases` / `VsNotes` | [] / [] | VS 别名与职责描述 |
| AI 总控助手 | `AgentEnabled` | true | 启用 AI 助手 |
| | `AgentEndpoint` / `AgentModel` | `https://api.deepseek.com` / `deepseek-flash` | OpenAI 兼容接口与模型 |
| | `AgentKeyProtected` | 空 | API Key（DPAPI 加密；也可用 `VSMANAGER_AGENT_API_KEY`） |
| | `AgentInstructions` / `AgentConfirm` / `AgentAutoFollowUp` | 空 / false / true | 自定义要求、执行前确认、任务完成后自动跟进 |
| | `AgentMaxToolText` / `AgentMaxMessageText` / `AgentMaxTaskText` | 120000 / 30000 / 12000 | 单次文本上限（字符） |
| | `AgentMaxOutputTokens` / `AgentMaxHistory` / `AgentMaxIterations` | 0 / 800 / 320 | 输出上限（0＝模型默认）、历史条数、单轮工具调用次数 |
| | `AgentMaxFileLines` / `AgentMaxReadCount` | 4000 / 400 | 读取文件行数、读取对话条数上限 |
| | `AgentChatKeepDays` / `AgentChatMaxRecords` | 30 / 2000 | 对话记录保留天数与条数（0＝不限） |
| 自动重启 | `AgentAutoRestart` / `AgentHangTimeoutSeconds` / `AgentFailureThreshold` | true / 120 / 3 | 见[自动重启](#自动重启) |
| | `ProcessWatchdogEnabled` | false | 进程看门狗 |
| | `AutoRestartMaxCount` / `AutoRestartWindowMinutes` | 3 / 5 | 防重启风暴 |
| 任务清单 | `TaskHistoryLimit` / `TaskNextId` | 0 / 自动 | 历史条数上限（0＝全部保留）、下一个任务编号 |
| | `AutoHideResentFailedTasks` / `AutoHideResentFailedNotify` | true / true | 同一任务重新发布（重新排队）时只在界面隐藏原失败条目、隐藏时是否弹出通知并播报；隐藏记录在 `HiddenResentTasks`（默认 []，最多 2000 条），`tasks.json` 与归档不变 |
| 语音 | `VoiceAnnounce` / `VoiceAiSummary` / `VoiceTranslate` / `VoiceIncludeName` | true / true / true / true | 完成播报、AI 摘要、按语言翻译、播报 VS 名称 |
| | `VoiceLanguage` / `VoiceResource` | `zh` / `seed-audio-1.0` | 语音语言与资源 |
| | `VoiceSpeaker` / `VoiceSpeakerEn` | 内置中文 / 英文声音描述 | 音色 |
| | `AsrEnabled` / `AsrResource` | true / `volc.seedasr.sauc.duration` | 按住说话输入 |
| | `VoiceKeyProtected` | 空 | 豆包语音 API Key（DPAPI 加密；也可用 `VSMANAGER_DOUBAO_API_KEY`） |
| Web 远程 | `WebEnabled` / `WebPort` / `WebToken` | false / 8765 / 空（自动生成） | 局域网远程控制 |
| 归档 | `ArchiveEnabled` / `ArchiveRoot` / `ArchiveRetentionDays` | true / 空 / 0 | 历史归档 |
| 内存 | `VsMemoryAutoEnabled` / `VsMemoryThresholdMB` / `VsMemoryAutoClean` | false / 6144 / false | 见[内存监控](#内存监控) |
| 发布 | `PublishRepoPath` / `PublishOwner` / `PublishRepoName` | 空 / 空 / `VSManager` | 本地目录、所有者、仓库名 |
| | `PublishVisibility` / `PublishBranch` | `public` / `main` | 可见性、默认分支 |
| | `PublishAuthorName` / `PublishAuthorEmail` | `VSManager contributors` / 空（noreply） | 提交作者 |
| | `GitHubTokenProtected` | 空 | GitHub Token（DPAPI 加密；也可用 `VSMANAGER_GITHUB_TOKEN`） |

</details>

### 环境变量

API Key 不要写进任何需要提交的文件。可以在「属性」中填写（加密保存在本机），也可以用环境变量提供：

| 环境变量 | 作用 |
|---|---|
| `VSMANAGER_AGENT_API_KEY` | 「属性」中未填写时使用的 AI 助手 API Key |
| `VSMANAGER_DOUBAO_API_KEY` | 「属性」中未填写时使用的豆包语音 API Key |
| `VSMANAGER_ARCHIVE_ROOT` | `ArchiveRoot` 为空时使用的归档根目录 |
| `VSMANAGER_GITHUB_TOKEN` | 「发布到 GitHub」中未填写 Token 时使用 |

未设置 `ArchiveRoot` 与 `VSMANAGER_ARCHIVE_ROOT` 时，归档写入 `%APPDATA%\VSManager\archive`。配置的目录不可用（磁盘不存在、无写入权限）时会自动回退到该目录，并在界面上提示。

如需把本机归档放到自定义目录（例如容量更大的数据盘），设置用户级环境变量后重启 VSManager 即可（也可以直接在「属性 → 归档」中修改）：

```powershell
setx VSMANAGER_ARCHIVE_ROOT "%USERPROFILE%\Documents\VSManagerArchive"
```

### 本地数据位置

| 路径 | 内容 |
|---|---|
| `%APPDATA%\VSManager\settings.json` | 配置（含加密的 Key 与 Web 令牌） |
| `%APPDATA%\VSManager\tasks.json` | 任务清单 |
| `%APPDATA%\VSManager\solutions.json` | 解决方案登记表（含本机路径，仅存本机） |
| `%APPDATA%\VSManager\agent-chat.jsonl` | AI 助手对话记录（每行一条 JSON，仅存本机） |
| `%APPDATA%\VSManager\logs\` | 程序日志 |
| `%APPDATA%\VSManager\publish-scan-terms.txt` | 发布自检的自定义词表 |
| `<ArchiveRoot>\tasks\`、`chat\`、`logs\` | 历史归档 |

以上文件都已写入 `.gitignore`，请勿提交；发布自检也会拦下这些本机数据文件。

### AI 助手对话记录

点击主窗口顶部的「📜 对话记录」打开历史窗口，可按关键词（或「#任务编号」）搜索、按日期筛选、切换正序 / 倒序，并可显示工具调用步骤。主界面的 AI 对话区只显示本次运行的会话（重启后清空），完整历史在该窗口中查看。每条记录包含时间、角色（user / assistant / notice）、文本以及关联的任务编号；写入时逐条追加并立即刷盘。容量由「属性 → AI 总控助手 → 对话记录」中的 `AgentChatKeepDays`（默认 30 天）与 `AgentChatMaxRecords`（默认 2000 条）控制，超出时只裁剪该文件中最旧的记录，不影响任务清单与归档。

### 内存监控

点击主窗口顶部的「🧠 内存」打开内存面板：按 VSManager 本体、各 VS 实例（devenv 及其全部子进程，如 ServiceHub、WebView2、MSBuild、Copilot 语言服务）和无归属的共享组件（如 VBCSCompiler）分组，显示每个进程的 PID、所属 VS、工作集、私有字节与占比，默认每 5 秒自动刷新。

- 「清理 VSManager」：完整 GC 并修剪 VSManager 及其 WebView2 的工作集。
- 「温和清理」（单个 VS 或全部）：若 VS 提供 `Tools.ForceGC` 命令则先触发 VS 内部 GC，再修剪标为「可安全清理」的进程的工作集。修剪只是把不常用的内存页移出物理内存，需要时自动换回；不会结束任何进程、不会丢失未保存内容，私有字节通常不变。
- 调试器组件、测试宿主、终端 / Copilot 代理命令、被调试的程序等标为「不建议」，只展示不清理。
- 每次清理的前后数值显示在面板底部，并写入 `%APPDATA%\VSManager\logs\memory.log`。
- 超阈值自动策略（默认关闭）：`VsMemoryAutoEnabled`（默认 false）、`VsMemoryThresholdMB`（默认 6144）、`VsMemoryAutoClean`（默认 false = 只提示）。每分钟检查一次，同一 VS 30 分钟内最多处理一次；VS 正在调试 / 生成 / Copilot 运行中时只提示。

### 自动重启

- **AI 助手自动重启**（默认开启，`AgentAutoRestart`）：AI 助手内部出现未处理异常、请求连续失败 `AgentFailureThreshold` 次（默认 3，仅统计网络错误 / 超时 / 5xx 等临时故障）或运行中 `AgentHangTimeoutSeconds` 秒没有任何进展（默认 120；等待用户确认时不计）时，自动取消当前一轮、重建 AI 客户端并恢复可用，对话中与状态栏会显示「AI 助手已自动重启」。任务清单不受影响。
- **进程看门狗**（默认关闭，`ProcessWatchdogEnabled`）：开启后会启动一个独立的看门狗进程；VSManager 异常退出（崩溃、被结束）后约 2 秒自动重新拉起。任务清单每次变更都会写盘，崩溃时还会再尽力保存一次；重启后恢复任务队列，执行中的任务继续跟踪，退出时「发送中」的任务可能已送达，因此标为失败并提示手动重新排队，避免重复发布。正常退出不会被拉起。
- **防重启风暴**：`AutoRestartWindowMinutes` 分钟内最多自动重启 `AutoRestartMaxCount` 次（默认 5 分钟 3 次，AI 助手与进程分别计数），超过后停止自动重启并提示查看日志。
- **手动入口**：主窗口顶部「⟳ 重启」菜单与托盘菜单提供「重启 AI 助手」「重启 VSManager…」（需确认；正在发送时拒绝，任务清单与配置先保存），菜单内还可切换上述两个开关、打开日志目录。
- 日志：`%APPDATA%\VSManager\logs\agent.log`（AI 助手故障与重启）、`watchdog.log`（看门狗）、`crash.log`（未处理异常）。

### 解决方案登记与 VS 开关

**登记表**保存在 `%APPDATA%\VSManager\solutions.json`（独立于 settings.json，写入时保留 `.bak` 备份，主文件损坏时自动回退到备份并另存 `.corrupt-时间` 副本）。格式：

```json
{
  "_comment": "…",
  "Solutions": [
    {
      "Alias": "订单项目",
      "Path": "%USERPROFILE%\\source\\repos\\OrderSystem\\OrderSystem.sln",
      "Synonyms": [ "订单", "下单", "order" ],
      "Description": "可选说明",
      "DefaultVs": 0
    }
  ]
}
```

> 示例中的路径仅为示意，请填写真实的 `.sln` / `.slnx` 完整路径（环境变量不会自动展开）。`DefaultVs` 为可选的 VS 编号（0 = 未设置），仅在读不到某个 VS 的解决方案路径时，用来判断该解决方案是否已在该编号的 VS 中打开。

**界面入口**：「属性 → 解决方案登记」卡片的「管理登记表…」、实例列表右键菜单「登记此解决方案」（一键登记已打开的 VS）与「解决方案登记…」。登记窗口支持新增 / 修改 / 删除、浏览选择解决方案、「从已打开的 VS 登记」、直接打开所选解决方案，并显示每条是否已打开及对应 VS 编号。

**别名匹配规则**（不区分大小写与全角半角，忽略空白与标点）：

1. 完整路径一致、别名完全一致 → 命中；同义词完全一致、文件名（不含扩展名）一致次之；
2. 去掉「解决方案 / 项目 / 工程 / 方案 / 代码 / solution / project / repo / 仓库」等通用后缀后一致（如「下单项目」→「下单」）；
3. 互相包含（至少 2 个字符）、按顺序出现的字符、说明中包含、二元组相似度等模糊规则得分较低；
4. 最高分唯一 → 直接使用；多条得分接近 → 列出候选让用户或 AI 选择；无匹配 → 报错并列出全部已登记别名。

**AI 工具**：

| 工具 | 参数 | 说明 |
|---|---|---|
| `list_solutions` | 无 | 列出登记的别名、同义词、路径，以及是否已打开、对应 VS 编号 |
| `open_solution` | `solution`：别名或完整路径 | 已打开则只激活该 VS 窗口；否则确认后启动 VS 打开，并等待其出现（`SolutionOpenWaitSeconds`） |
| `close_vs` | `target`：VS 编号 / 名称或登记别名 | 先检查：Copilot 忙、有执行中任务、正在生成 / 调试、存在未保存修改或无法检查时拒绝并说明原因；通过后确认，再发送普通关闭请求（等同点击关闭按钮），绝不强制结束进程 |
| `list_vs` | 无 | 列出已打开的 VS、其解决方案及对应的登记别名 |
| `send_task` | `vs` 可填登记别名 | 目标 VS 未打开时任务进入「等待目标 VS」并暂存 |

**暂存与自动推送**：`send_task` 的目标是登记别名且对应 VS 未打开时，任务以 `waiting_vs`（等待目标 VS）状态写入 tasks.json（重启后仍保留），任务清单显示「⏳ 「别名」打开后自动推送」，并弹出通知 / 播报「任务已暂存，等待打开订单项目 / Task parked, waiting for 订单项目 to open」。调度器每 2 秒检查一次；无论 VS 由 `open_solution` 还是用户手动打开，只要检测到对应解决方案，就把任务转为排队（`waiting`），再等待 `PendingVsSettleSeconds` 秒后按正常流程发布。状态流转：`waiting_vs → waiting → sending → running → done`；`waiting_vs` 也可直接取消（`cancelled`）。

> 兼容性：旧版本 VSManager 读取到 `waiting_vs` 状态会把该任务视为已取消。

### 一键布局：集中查看 Copilot 对话

把各 VS 的 Copilot 对话窗格切换为浮动窗口，在指定屏幕（默认第二屏幕）按工作区宽度横向均布，并最小化 VS 主窗口，只留下纯净的对话内容。

- **入口**：实例列表右键菜单「一键布局：Copilot 对话 → 副屏横向均布（最小化 VS）」与「还原 Copilot 对话布局」；或对 AI 助手说“最小化所有 VS，把对话框排到副屏”。
- **排列规则**：每格宽度不小于 360 像素（按系统 DPI 缩放），一行放不下时自动换行并平均分配到各行；也可选网格排列。只有一块屏幕时排在该屏幕。
- **降级处理**：未连接自动化接口（DTE）、找不到或无法浮动对话窗格的 VS 会跳过并说明原因，不会被最小化；窗格未打开时会先自动打开。
- **还原**：执行前记录主窗口位置 / 最大化状态与窗格的停靠状态（仅保存在内存中，重启 VSManager 后不再可还原）；还原时先恢复主窗口，再把原本停靠的窗格放回原位。
- **说明**：浮动工具窗口归 VS 主窗口所有，主窗口最小化时 Windows 会一并隐藏它们，VSManager 会在最小化后以不激活的方式重新显示窗格。前台粘贴发送消息时可能会把对应 VS 恢复到前台。

| 工具 | 参数 | 说明 |
|---|---|---|
| `arrange_copilot_panes` | `screen`（屏幕编号，0 = 自动）、`layout`（`horizontal` / `grid`）、`minimizeVs`（默认 true）、`vs`（可选，如 `"1,3"`） | 一键布局；遵守「AI 操作需要确认」（`AgentConfirm`） |
| `restore_copilot_layout` | 无 | 还原一键布局之前的窗口布局；同样遵守 `AgentConfirm` |

## 发布到 GitHub

点击主窗口顶部的「🚀 发布」打开发布窗口，流程为：检查 git → 未初始化时 `git init` → 补齐 `.gitignore`（排除 bin/obj/dist、settings.json、tasks.json、日志、归档目录）→ 敏感信息自检 → `git add` → 提交（中英双语提交信息）→ 创建或关联远程仓库 → `git push`。

- **Token**：在发布窗口填写（DPAPI 加密保存），或设置环境变量 `VSMANAGER_GITHUB_TOKEN`。Token 只在内存中通过 HTTP 头传给 git，不会写入远程地址、`.git/config`、日志或界面。
  - fine-grained Token：仓库权限 **Contents：读写**；需要自动创建仓库时再加 **Administration：读写**（组织仓库需组织授权）。
  - classic Token：`repo` 范围（仅公开仓库可用 `public_repo`）；推送 `.github/workflows` 还需要 `workflow`。
- **敏感信息自检**：扫描所有待提交文件以及提交信息、提交作者，规则包括盘符绝对路径、当前用户名与机器名、邮箱（noreply 与 example 域名除外）、常见 Key/Token 格式与疑似密钥赋值、本机已配置的密钥，以及自定义词表 `%APPDATA%\VSManager\publish-scan-terms.txt`（每行一个，适合填写内部项目名、客户名，文件不会提交）。有命中时发布暂停并列出「文件:行号 + 命中内容」（密钥已打码），由你确认继续或取消；「仅自检」只扫描、不修改仓库。
- **日志**：`%APPDATA%\VSManager\logs\publish-yyyyMMdd.log`。失败时界面会给出原因与修复建议；远程已有本地没有的提交时不会强制推送。

## 分支策略

| 分支 | 用途 | 合并方式 |
|---|---|---|
| `main` | 默认主分支，始终可构建、可发布；每个版本打标签（如 `v1.0.0`） | 只接受来自 `develop`（发布）或 `fix/xxx`（紧急修复）的合并 |
| `develop` | 集成分支，汇总已完成的功能与修复 | 接受 `feature/xxx`、`fix/xxx` 的 Pull Request |
| `feature/xxx` | 新功能，从 `develop` 创建 | 完成后向 `develop` 发起 Pull Request |
| `fix/xxx` | 缺陷修复，一般从 `develop` 创建；线上紧急问题从 `main` 创建 | 合并回来源分支；从 `main` 创建的修复还需同步合并到 `develop` |

```powershell
git switch develop; git pull
git switch -c feature/memory-panel
# ……开发、构建、测试 / develop, build, test……
git push -u origin feature/memory-panel   # 然后在 GitHub 上向 develop 发起 Pull Request
```

- 推送前先 `git pull` 拉取远程最新内容；**不要强制推送**（`--force`）到 `main` 与 `develop`。
- 提交信息格式为「类型: 简述」，中英双语，详见 [CONTRIBUTING.md](CONTRIBUTING.md)。
- 发布前使用「🚀 发布 → 仅自检」做敏感信息自检，确认无命中后再提交。

## 常见问题

- **看不到某个 VS 实例？** VSManager 与 VS 需以相同权限运行（都以管理员或都不以管理员运行），并且 VS 已完全加载解决方案。
- **Copilot 状态一直是「未知」？** 确认已安装 GitHub Copilot 并打开过 Copilot 对话窗格；若窗格标题不同，可在「属性」中修改 `CopilotPaneKeyword`。
- **发送失败并提示粘贴未确认？** 右键 VS →「查看发送日志」，每次失败都会记录「粘贴确认诊断」（目标 VS、窗口状态、窗格 / 输入框位置与焦点、读取到的文本片段、剪贴板与耗时）。常见原因：VS 被模态对话框阻挡、剪贴板被同步工具改写、超长消息粘贴较慢（可在「属性 → 发送确认」调大超时）。
- **发送失败并提示「未找到 Copilot 输入框 / 对话窗格 / 输入框不可编辑」？** 输入框按 L1（WpfTextViewHost）→ L2（对话列表外最靠下的 WpfTextView）→ L3（最靠下的可编辑文本元素）→ L4（旧方式，需可编辑）→ L5（位置命中测试）逐级降级定位，每级的候选数量、名称、AutomationId、是否可编辑与耗时都写入发送日志，最终失败时还会记录窗格结构。长时间的 Copilot 回合刚结束时窗格的 UI Automation 树可能暂未刷新，程序会带退避轮询并重新打开窗格重试；仍失败时请切换到该 VS 单击一次输入框，或在「属性 → 发送确认」调大定位超时 / 重试次数。「不可编辑」通常表示 Copilot 正等待你确认操作。
- **AI 助手没有回复 / 提示未配置？** 在「属性 → AI 总控助手」中填写 API Key（或设置 `VSMANAGER_AGENT_API_KEY`），并确认接口支持函数调用。
- **`dotnet restore` 失败？** 本机 NuGet 配置中可能有不可用的源，执行 `dotnet restore VSManager.slnx --source https://api.nuget.org/v3/index.json`。
- **生成时提示 VSManager.exe 被占用？** 程序正在运行时，请先退出（托盘 → 退出），或把输出指定到临时目录：`dotnet build VSManager.slnx "-p:OutputPath=%TEMP%\vsm-build\"`。
- **推送到 GitHub 失败？** 检查 Token 权限（classic：`repo` 或仅公开仓库的 `public_repo`；fine-grained：Contents 读写）与网络；远程有新提交时先 `git pull`，工具不会强制推送。
- **配置或任务丢失？** 配置在 `%APPDATA%\VSManager\settings.json`，损坏时另存为 `settings.json.corrupt-*`；任务清单在 `tasks.json`，历史归档在 `<ArchiveRoot>\tasks\`。
- **重新发布失败任务后，原失败条目不见了？** 这是 `AutoHideResentFailedTasks`（默认开启）的行为：新任务正文规范化（统一换行、去掉每行首尾空白与空行、合并连续空格、去掉零宽字符）后的 SHA-256 指纹与某条更早的失败任务相同、且目标 VS 相同时，原条目只在界面隐藏。正文过短（少于 12 字符）或目标不同时不会隐藏，原因写入任务日志；正文以「重发 #26：」开头或以「（重试 #26）」结尾时去掉标记后比对，不受长度限制。点任务清单顶部「历史」可查看，右键「恢复显示该失败条目」或「撤销清除」可恢复。

## 安全提示

- Web 远程控制默认关闭；开启后会在局域网内监听端口，凭访问令牌控制所有 VS，只应在可信网络中使用。令牌可以在「属性」中重置。
- AI 助手会把 VS 名称、职责描述、对话内容和代码片段发送给你配置的模型服务商；语音功能会把播报文本发送给豆包语音。请根据所在组织的数据政策选择服务商。

## 第三方依赖

| 组件 | 许可证 |
|---|---|
| [Markdig](https://github.com/xoofx/markdig) | BSD-2-Clause |
| [Microsoft.Extensions.AI / Microsoft.Extensions.AI.OpenAI](https://github.com/dotnet/extensions) | MIT |
| [OpenAI .NET SDK](https://github.com/openai/openai-dotnet)（间接依赖） | MIT |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) | Microsoft WebView2 SDK 许可（BSD 风格，允许再分发） |
| [NAudio.WinMM](https://github.com/naudio/NAudio) | MIT |

二维码编码器（`QrCode.cs`）与应用图标（`app.ico`，渐变底色 + 文字）均为本项目自行制作，随本项目按 MIT 许可证发布。使用 DeepSeek、豆包语音等在线服务时，需另行遵守各服务商的服务条款。

## 贡献指南

欢迎提交 Issue 与 Pull Request，完整流程见 [CONTRIBUTING.md](CONTRIBUTING.md)，版本变更见 [CHANGELOG.md](CHANGELOG.md)。要点：

- 本项目是开源项目：README、文档、代码注释、提交信息与发布说明中不要出现个人信息（真实姓名、邮箱、机器名、用户名、本机盘符路径、公司 / 客户信息），示例路径请使用 `%APPDATA%` 等环境变量或通用示例。
- 开源说明文档与代码注释提供中英两份（中文在前、英文在后，或并列呈现）。
- 功能开发使用 `feature/xxx`，修复使用 `fix/xxx`，向 `develop` 发起 Pull Request；提交前构建 0 错误、单元测试全部通过。

## 声明

本项目为独立的开源工具，与 Microsoft、GitHub 及文中提到的各服务商无隶属或背书关系。Visual Studio、GitHub Copilot 等名称为其各自所有者的商标。

## 许可证

本项目以 [MIT 许可证](LICENSE) 发布：可自由使用、修改与再分发，需保留版权与许可声明；软件按「原样」提供，不附带任何担保。第三方组件遵循各自的许可证（见上表）。

---

# VSManager · Multi Visual Studio Manager (English)

VSManager is a Windows desktop tool (WinForms / .NET Framework 4.8) for managing several Visual Studio instances on one machine together with their GitHub Copilot chats.

## Features

- **Instance overview**: discovers running Visual Studio instances and shows the solution, debug state and whether Copilot is busy or idle; one-click layout across monitors.
- **In-app Copilot chat**: send messages (images supported) to the Copilot of any instance and read the replies live (Markdown rendering).
- **Debug control**: start / stop / break / restart debugging, build / rebuild, read the error list.
- **AI assistant**: works with any OpenAI-compatible endpoint (DeepSeek by default) and uses function calling to inspect instances, dispatch tasks and wait for results.
- **Task list**: tasks from the assistant or the user are queued while the target instance is busy and sent automatically when it becomes idle; manual Copilot chats in each instance are listed as well.
- **Solution registry & VS open/close**: register solutions under everyday names (aliases / synonyms with fuzzy matching) so the AI assistant can open and close Visual Studio by name; tasks for a solution that is not open are parked and pushed automatically once it opens.
- **Voice**: optional Doubao speech service for spoken summaries when tasks finish (Chinese / English selectable; the AI assistant reply language follows it), plus push-to-talk input.
- **Web remote & AI skill**: control everything from a phone browser on the LAN (access token required); the control API can be installed as a skill for Copilot CLI / Claude Code and similar agents.
- **History archive**: task history, assistant chats, per-instance chats and send logs are written to daily JSONL files and kept forever by default.
- **Publish to GitHub**: one-click git init / commit / create or link the remote / push, with an automatic sensitive-content scan before publishing.
- **Memory monitor**: working set and private bytes grouped by VSManager / each VS instance (with child processes) / shared components, with gentle cleanup and threshold alerts.
- **Auto restart**: the AI assistant is rebuilt automatically after an error, repeated request failures or a hang; an optional process watchdog relaunches VSManager after an abnormal exit, with a restart-storm limit.

## Requirements

- Windows 10 / 11
- Visual Studio 2022 or later with GitHub Copilot
- .NET Framework 4.8
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (included in Windows 11)

## Build and run

```powershell
git clone https://github.com/<owner>/VSManager.git
cd VSManager
dotnet build VSManager.slnx -c Release
.\src\VSManager\bin\Release\net48\VSManager.exe
```

Replace `<owner>` with the repository owner. The default branch is `main`; work in progress lives in `develop` (see [Branching](#branching)).

You can also open `VSManager.slnx` at the repository root in Visual Studio (the project file is `src\VSManager\VSManager.csproj`) and build / run it there.

Run the unit tests (MSTest; all test data goes to the system temp folder, real data under %APPDATA%\VSManager is never read or written):

```powershell
dotnet test VSManager.slnx
```

If restore fails because the local NuGet configuration lists an unavailable source, run `dotnet restore VSManager.slnx --source https://api.nuget.org/v3/index.json` first.

## Directory layout

```text
VSManager/
├─ VSManager.slnx              Solution
├─ README.md / LICENSE / .gitignore / .gitattributes
├─ CONTRIBUTING.md             Contribution guide (branching, commit format, scan)
├─ CHANGELOG.md                Changelog
├─ settings.example.json       Example config (the real one lives in %APPDATA%\VSManager\ and is not committed)
├─ src/
│  └─ VSManager/               Main project (WinForms, .NET Framework 4.8)
│     ├─ VSManager.csproj
│     ├─ Program.cs / app.manifest
│     ├─ Assets/               Icon and embedded resources (web pages, Copilot skill)
│     │  ├─ app.ico
│     │  ├─ Web/               remote.html, transcript.html
│     │  └─ Skill/             SKILL.md, vsm.ps1
│     ├─ Core/                 Domain layer: no UI or Win32 dependencies
│     │  ├─ Models/            Data models (external chats, image attachments, Copilot state)
│     │  ├─ Tasks/             Task model, task state machine, send retry rules, task list
│     │  └─ TextUtil.cs        Shared text helpers
│     ├─ Infrastructure/       Infrastructure layer
│     │  ├─ Config/            Settings load / save (AppSettings), data folder (AppPaths)
│     │  ├─ Http/              AI client factory, request body policy
│     │  ├─ IO/                File-system abstraction, atomic writes
│     │  ├─ Logging/           Unified log entry (AppLog), send log
│     │  ├─ Storage/           tasks.json store
│     │  ├─ Win32/             Win32 interop, memory trimming
│     │  └─ QrCode.cs          QR code
│     ├─ Services/             Service layer
│     │  ├─ Abstractions/      Interfaces for external dependencies (VS operations, Copilot channel, voice)
│     │  ├─ Agent/             AI assistant, prompts, chat history
│     │  ├─ Publish/           Publish to GitHub
│     │  ├─ Remote/            LAN web remote, skill installer
│     │  ├─ Storage/           History archive
│     │  ├─ Tasks/             Task dispatcher (publish, retry, completion, failure)
│     │  ├─ VisualStudio/      VS instances, Copilot chat / monitor, code scanner, memory monitor
│     │  └─ Voice/             Doubao text-to-speech and speech recognition
│     └─ UI/                   UI layer: main window, theme, shared controls
│        ├─ Forms/             Settings, publish, chat history, memory windows
│        └─ Panels/            Copilot chat, AI assistant and task list panels
└─ tests/
   └─ VSManager.Tests/         Unit tests (MSTest)
```

All source files still share the single namespace `VSManager`; folders only group files by responsibility. Build output (`bin/`, `obj/`) and test results (`TestResults/`) are excluded by `.gitignore`.

### Layered architecture

- **Core (domain)**: the task model `QueuedTask`, the state machine `TaskStateMachine` (waiting → sending → running → done / failed / cancelled), the send retry rules `SendRetryPolicy` and the task list `TaskQueue` (id allocation, history trimming, archive journal). It only depends on the `ITaskStore` and `ITaskArchiveSink` interfaces and a replaceable clock, so it can be unit-tested directly.
- **Services**: VS management, Copilot messaging, AI assistant, voice, archive and publishing. `TaskDispatcher` dispatches tasks and talks to the main window through `ITaskDispatchHost`; external dependencies are abstracted by `IVsOperations`, `ICopilotChannel`, `IVoiceService` and `IAiClientFactory`.
- **Infrastructure**: Win32 wrappers, settings and data folder, the file-system abstraction `IFileSystem` with atomic writes `AtomicFile`, the unified log `AppLog` (unhandled exceptions go to crash.log) and HTTP client creation.
- **UI**: forms and controls only handle display and interaction; business actions are delegated to the service layer.

## Configuration

On first run the configuration file `%APPDATA%\VSManager\settings.json` is created automatically; every option can be changed from "⚙ 属性" (Settings) in the main window. Missing, incomplete or corrupted settings fall back to defaults (a corrupted file is kept as `settings.json.corrupt-*`) and never crash the app.

See [`settings.example.json`](settings.example.json) for all fields and defaults. Commonly used ones:

| Setting | Description | Default |
|---|---|---|
| `AgentEndpoint` / `AgentModel` | Assistant endpoint and model (OpenAI-compatible, function calling required) | DeepSeek |
| `AgentKeyProtected` | Assistant API key (DPAPI-encrypted, only the current Windows user can decrypt it) | empty |
| `VoiceKeyProtected` | Doubao speech API key (DPAPI-encrypted) | empty |
| `VoiceLanguage` | Voice language: `zh` Chinese / `en` English; also selects the summary prompt, the voice and the AI assistant reply language; applies immediately | `zh` |
| `VoiceSpeakerEn` | English voice ID (seed-tts-*) or voice description (seed-audio-*); falls back to the default voice `zh_female_vv_uranus_bigtts` with a notice if unavailable | English voice description |
| `AgentMax*` | Assistant quotas: per-call limits for tool output, message length, task text, history, etc. | see example |
| `WebEnabled` / `WebPort` / `WebToken` | Web remote switch, port and access token (generated when empty) | off / 8765 |
| `ArchiveEnabled` / `ArchiveRoot` / `ArchiveRetentionDays` | Archive switch, root folder (`%ENV%` supported) and retention days (0 = keep forever) | on / see below / 0 |
| `PublishRepoPath` / `PublishOwner` / `PublishRepoName` / `PublishVisibility` / `PublishBranch` | Publish to GitHub: local folder (empty = auto-detect), owner (empty = token user), repository name, visibility, default branch | empty / empty / VSManager / public / main |
| `PublishAuthorName` / `PublishAuthorEmail` | Commit author and e-mail (empty e-mail = GitHub noreply address) | VSManager contributors / empty |
| `GitHubTokenProtected` | GitHub token (DPAPI-encrypted) | empty |

<details>
<summary>All settings and defaults (click to expand)</summary>

| Group | Setting | Default | Description |
|---|---|---|---|
| Window & layout | `MainScreen` / `ToolScreen` | empty | Screen for the main window / tool windows (empty = auto) |
| | `Layout` | `上下` (stacked) | Tool-window layout |
| | `ActivateMoveMain` / `ActivateMoveTools` | true / false | Move the main window / tool windows when activating a VS |
| | `TopMost` / `MinimizeToTray` / `Hotkeys` | false / true / true | Always on top, minimize to tray, global hotkeys |
| | `ClickToActivate` | false | A single click in the list activates the VS |
| | `SidebarWidth` / `AgentHeight` / `TaskPanelCollapsed` | 0 / 0 / false | Remembered UI sizes (0 = default) |
| Copilot monitoring & sending | `MonitorCopilot` / `PollMs` | true / 1500 | Monitor Copilot state and polling interval (ms) |
| | `Sound` / `Popup` | true / true | Sound and tray balloon on completion |
| | `CopilotPaneKeyword` / `BusyButtonIds` | `Copilot` / `CancelButton` | How the Copilot pane and its "busy" button are recognized |
| | `BackgroundSend` / `BackgroundSync` / `AutoOpenChat` | true / true / true | Send in the background, sync chats in the background, open the chat pane automatically |
| | `RestoreCopilotPane` / `ShowChatSteps` | true / true | Switch back to the pane when it is replaced, show chat steps |
| | `SendConfirmTimeoutSeconds` / `SendAutoRetry` / `SendRetryCount` | 10 / true / 1 | Confirmation timeout after writing to the Copilot input box (seconds, 2–120), whether an unconfirmed paste or a missing input box is retried automatically, and how often a paste is retried (0–5) |
| | `SendLocateTimeoutSeconds` / `SendLocateRetryCount` | 6 / 1 | Polling timeout of each round locating the Copilot input box (seconds, 1–60) and how often the pane is reopened and the lookup retried when it is not found (0–5; no retry when `SendAutoRetry=false`) |
| Solution registry | `SolutionCloseConfirm` | true | Always ask before the AI closes a VS (unsaved changes are checked regardless) |
| | `SolutionOpenWaitSeconds` | 90 | How long the AI waits for VS to appear after opening a solution (seconds, 10–600) |
| | `PendingVsSettleSeconds` | 20 | Extra seconds a parked task waits after its VS appears so the solution and Copilot can load (0–300) |
| | `PendingVsNotify` | true | Show a notification and speak when a task is parked / pushed (voice must be enabled separately) |
| | `WatchConversations` / `ExternalRestoreLimit` / `ExternalRestoreHours` | true / 50 / 24 | Watch manual chats in each VS, and how many / how recent to restore at start |
| | `Aliases` / `VsNotes` | [] / [] | VS aliases and role descriptions |
| AI assistant | `AgentEnabled` | true | Enable the assistant |
| | `AgentEndpoint` / `AgentModel` | `https://api.deepseek.com` / `deepseek-flash` | OpenAI-compatible endpoint and model |
| | `AgentKeyProtected` | empty | API key (DPAPI-encrypted; or `VSMANAGER_AGENT_API_KEY`) |
| | `AgentInstructions` / `AgentConfirm` / `AgentAutoFollowUp` | empty / false / true | Custom instructions, confirm before acting, follow up after tasks finish |
| | `AgentMaxToolText` / `AgentMaxMessageText` / `AgentMaxTaskText` | 120000 / 30000 / 12000 | Per-call text limits (characters) |
| | `AgentMaxOutputTokens` / `AgentMaxHistory` / `AgentMaxIterations` | 0 / 800 / 320 | Output limit (0 = model default), history messages, tool calls per round |
| | `AgentMaxFileLines` / `AgentMaxReadCount` | 4000 / 400 | File lines and chat messages read at most |
| | `AgentChatKeepDays` / `AgentChatMaxRecords` | 30 / 2000 | Chat history retention in days / records (0 = unlimited) |
| Auto restart | `AgentAutoRestart` / `AgentHangTimeoutSeconds` / `AgentFailureThreshold` | true / 120 / 3 | See [Auto restart](#auto-restart) |
| | `ProcessWatchdogEnabled` | false | Process watchdog |
| | `AutoRestartMaxCount` / `AutoRestartWindowMinutes` | 3 / 5 | Restart-storm guard |
| Task list | `TaskHistoryLimit` / `TaskNextId` | 0 / automatic | History limit (0 = keep all), next task id |
| | `AutoHideResentFailedTasks` / `AutoHideResentFailedNotify` | true / true | When the same task is published again (requeued), hide the original failed entry in the UI only, and whether to notify / speak when doing so; marks are kept in `HiddenResentTasks` (default [], at most 2000); `tasks.json` and the archive are untouched |
| Voice | `VoiceAnnounce` / `VoiceAiSummary` / `VoiceTranslate` / `VoiceIncludeName` | true / true / true / true | Completion announcement, AI summary, translate to the voice language, include the VS name |
| | `VoiceLanguage` / `VoiceResource` | `zh` / `seed-audio-1.0` | Voice language and resource |
| | `VoiceSpeaker` / `VoiceSpeakerEn` | built-in Chinese / English voice descriptions | Voices |
| | `AsrEnabled` / `AsrResource` | true / `volc.seedasr.sauc.duration` | Push-to-talk input |
| | `VoiceKeyProtected` | empty | Doubao speech API key (DPAPI-encrypted; or `VSMANAGER_DOUBAO_API_KEY`) |
| Web remote | `WebEnabled` / `WebPort` / `WebToken` | false / 8765 / empty (generated) | LAN remote control |
| Archive | `ArchiveEnabled` / `ArchiveRoot` / `ArchiveRetentionDays` | true / empty / 0 | History archive |
| Memory | `VsMemoryAutoEnabled` / `VsMemoryThresholdMB` / `VsMemoryAutoClean` | false / 6144 / false | See [Memory monitor](#memory-monitor) |
| Publish | `PublishRepoPath` / `PublishOwner` / `PublishRepoName` | empty / empty / `VSManager` | Local folder, owner, repository name |
| | `PublishVisibility` / `PublishBranch` | `public` / `main` | Visibility, default branch |
| | `PublishAuthorName` / `PublishAuthorEmail` | `VSManager contributors` / empty (noreply) | Commit author |
| | `GitHubTokenProtected` | empty | GitHub token (DPAPI-encrypted; or `VSMANAGER_GITHUB_TOKEN`) |

</details>

### Environment variables

Never put API keys into files that get committed. Enter them in Settings (stored encrypted on this machine) or provide them through environment variables:

| Variable | Purpose |
|---|---|
| `VSMANAGER_AGENT_API_KEY` | Assistant API key used when none is set in Settings |
| `VSMANAGER_DOUBAO_API_KEY` | Doubao speech API key used when none is set in Settings |
| `VSMANAGER_ARCHIVE_ROOT` | Archive root used when `ArchiveRoot` is empty |
| `VSMANAGER_GITHUB_TOKEN` | GitHub token used when none is entered in the publish window |

Without `ArchiveRoot` and `VSMANAGER_ARCHIVE_ROOT` the archive goes to `%APPDATA%\VSManager\archive`. If the configured folder is unusable (missing drive, no write permission) the app falls back to that folder and shows a notice.

To keep the archive in a custom folder (for example a larger data drive), set a user-level environment variable and restart VSManager (or change it in Settings → Archive):

```powershell
setx VSMANAGER_ARCHIVE_ROOT "%USERPROFILE%\Documents\VSManagerArchive"
```

### Local data

| Path | Content |
|---|---|
| `%APPDATA%\VSManager\settings.json` | Settings (including encrypted keys and the web token) |
| `%APPDATA%\VSManager\tasks.json` | Task list |
| `%APPDATA%\VSManager\solutions.json` | Solution registry (contains local paths, local only) |
| `%APPDATA%\VSManager\agent-chat.jsonl` | AI assistant chat history (one JSON object per line, local only) |
| `%APPDATA%\VSManager\logs\` | Application logs |
| `%APPDATA%\VSManager\publish-scan-terms.txt` | Custom terms for the publish scan |
| `<ArchiveRoot>\tasks\`, `chat\`, `logs\` | History archive |

All of these are listed in `.gitignore`; do not commit them. The publish scan also blocks these local data files.

### AI assistant chat history

Click "📜 对话记录" (Chat history) at the top of the main window to open the history window: search by keywords (or "#task-id"), filter by date, switch between oldest-first and newest-first, and optionally show tool-call steps. The chat area in the main window only shows the current session (cleared on restart); the full history lives in this window. Each record holds the time, role (user / assistant / notice), text and related task ids, and is appended and flushed to disk immediately. Capacity is controlled by `AgentChatKeepDays` (default 30 days) and `AgentChatMaxRecords` (default 2000) in Settings → AI assistant → Chat history; only the oldest records in this file are trimmed, the task list and archive are not affected.

### Memory monitor

Click "🧠 内存" (Memory) at the top of the main window to open the memory panel. It groups processes into VSManager itself, each VS instance (devenv and all its descendants such as ServiceHub, WebView2, MSBuild and the Copilot language server) and orphaned shared components (such as VBCSCompiler), and shows PID, owner, working set, private bytes and share for each process; it refreshes every 5 seconds by default.

- "Clean VSManager": full GC and working-set trim of VSManager and its WebView2 processes.
- "Gentle clean" (one VS or all): runs VS's own `Tools.ForceGC` command when available, then trims the working set of processes marked "Safe". Trimming only moves rarely used pages out of RAM (paged back in on demand); nothing is terminated, unsaved work is untouched, and private bytes usually stay the same.
- Debugger components, test hosts, terminal / Copilot agent commands and the program under debugging are marked "Not advised" and are only displayed.
- Before/after numbers of every cleanup are shown at the bottom of the panel and written to `%APPDATA%\VSManager\logs\memory.log`.
- Threshold policy (off by default): `VsMemoryAutoEnabled` (default false), `VsMemoryThresholdMB` (default 6144), `VsMemoryAutoClean` (default false = notify only). Checked once a minute, at most once per 30 minutes per VS; a VS that is debugging / building / running Copilot is only notified.

### Auto restart

- **AI assistant auto-restart** (on by default, `AgentAutoRestart`): when the assistant hits an unhandled error, `AgentFailureThreshold` consecutive request failures (default 3; only transient failures such as network errors, timeouts and 5xx count) or makes no progress for `AgentHangTimeoutSeconds` while running (default 120; waiting for the user's confirmation does not count), the current round is cancelled, the AI client is rebuilt and the assistant becomes usable again. The chat and the status bar show "AI 助手已自动重启" (AI assistant restarted). The task list is not affected.
- **Process watchdog** (off by default, `ProcessWatchdogEnabled`): starts a separate watchdog process that relaunches VSManager about 2 seconds after an abnormal exit (crash, killed). The task list is saved on every change and once more on a crash; after the restart the queue is restored and running tasks are tracked again. Tasks that were "sending" at the exit may already have been delivered, so they are marked failed with a hint to requeue manually instead of being published twice. A normal exit is never relaunched.
- **Restart-storm guard**: at most `AutoRestartMaxCount` automatic restarts per `AutoRestartWindowMinutes` minutes (default 3 per 5 minutes, counted separately for the assistant and the process); beyond that automatic restarts stop and you are asked to check the logs.
- **Manual entries**: the "⟳ 重启" (Restart) menu at the top of the main window and the tray menu provide "Restart AI assistant" and "Restart VSManager…" (asks for confirmation; refused while a message is being sent; tasks and settings are saved first). The menu also toggles both switches and opens the log folder.
- Logs: `%APPDATA%\VSManager\logs\agent.log` (assistant faults and restarts), `watchdog.log` (watchdog), `crash.log` (unhandled exceptions).

### Solution registry & VS open/close

**The registry** lives in `%APPDATA%\VSManager\solutions.json` (separate from settings.json; a `.bak` backup is kept on every write, and a damaged main file falls back to the backup and is copied to `.corrupt-<time>`). Format:

```json
{
  "_comment": "…",
  "Solutions": [
    {
      "Alias": "订单项目",
      "Path": "%USERPROFILE%\\source\\repos\\OrderSystem\\OrderSystem.sln",
      "Synonyms": [ "订单", "下单", "order" ],
      "Description": "optional note",
      "DefaultVs": 0
    }
  ]
}
```

> The path above is only an illustration; enter the real full path of the `.sln` / `.slnx` (environment variables are not expanded). `DefaultVs` is an optional VS number (0 = unset), used only when the solution path of a VS cannot be read, to decide whether the solution is already open in that VS.

**UI entries**: "Manage registry…" in the Settings → Solution registry card, and "Register this solution" (one-click registration of an open VS) / "Solution registry…" in the instance list context menu. The registry window supports add / edit / delete, browsing for a solution, "Register from an open VS", opening the selected solution, and shows whether each entry is open and in which VS.

**Alias matching** (case, full/half width, whitespace and punctuation are ignored):

1. Same full path or exact alias → hit; exact synonym or file name (without extension) comes next;
2. Equal after removing generic suffixes such as 解决方案 / 项目 / 工程 / solution / project / repo (e.g. "下单项目" → "下单");
3. Fuzzy rules (containment of at least 2 characters, characters in order, description contains, bigram similarity) score lower;
4. A single best score is used directly; several close scores are listed as candidates for the user or the AI to pick; no match is an error that lists all registered aliases.

**AI tools**:

| Tool | Parameters | Description |
|---|---|---|
| `list_solutions` | none | Lists aliases, synonyms and paths, whether each is open, and the VS number |
| `open_solution` | `solution`: alias or full path | Activates the VS if already open; otherwise asks for confirmation, launches VS and waits for it to appear (`SolutionOpenWaitSeconds`) |
| `close_vs` | `target`: VS number / name or registered alias | Refuses with a reason when Copilot is busy, a task is running, a build / debug session is active, or there are unsaved changes (or they cannot be checked); otherwise asks for confirmation and sends a normal close request (like clicking the close button); the process is never killed |
| `list_vs` | none | Lists open VS instances, their solutions and matching registry aliases |
| `send_task` | `vs` may be a registered alias | If that VS is not open, the task is parked as "waiting for target VS" |

**Parking and auto push**: when `send_task` targets a registered alias whose VS is not open, the task is saved in tasks.json with status `waiting_vs` (kept across restarts), the task list shows "⏳ pushed once it opens", and a notification / voice message says "任务已暂存，等待打开订单项目 / Task parked, waiting for 订单项目 to open". The dispatcher checks every 2 seconds; as soon as the solution is detected — whether opened by `open_solution` or manually — the task moves to `waiting` and is published through the normal flow after `PendingVsSettleSeconds`. Transitions: `waiting_vs → waiting → sending → running → done`; `waiting_vs` can also be cancelled (`cancelled`).

> Compatibility: older VSManager versions treat a `waiting_vs` task as cancelled.

### One-click layout: watch the Copilot chats together

Floats the Copilot chat pane of every VS, spreads the panes side by side over the work area of a chosen screen (the second screen by default) and minimizes the VS main windows, leaving just the conversations.

- **Entry points**: instance list context menu "一键布局：Copilot 对话 → 副屏横向均布 / Arrange Copilot panes" and "还原 Copilot 对话布局 / Restore Copilot layout"; or ask the AI assistant to "minimize all VS and put the chats on the second screen".
- **Layout rules**: each cell is at least 360 px wide (scaled by the system DPI); when a row is full the panes wrap and are balanced across rows; a grid layout is also available. With a single screen the panes go to that screen.
- **Fallbacks**: a VS without the automation interface (DTE), or whose pane cannot be found or floated, is skipped with the reason and is not minimized; a closed pane is opened first.
- **Restore**: the main window position / maximized state and the pane docking state are recorded first (in memory only, so they cannot be restored after VSManager restarts); restoring brings the main windows back first and then re-docks the panes that were docked.
- **Note**: floating tool windows are owned by the VS main window, so Windows hides them when it is minimized; VSManager shows the panes again without activating them. A foreground paste when sending a message may bring that VS back to the front.

| Tool | Parameters | Description |
|---|---|---|
| `arrange_copilot_panes` | `screen` (screen number, 0 = auto), `layout` (`horizontal` / `grid`), `minimizeVs` (default true), `vs` (optional, e.g. `"1,3"`) | One-click layout; honors "confirm AI actions" (`AgentConfirm`) |
| `restore_copilot_layout` | none | Restores the layout from before the one-click layout; also honors `AgentConfirm` |

## Publish to GitHub

Click "🚀 发布" (Publish) at the top of the main window. The flow is: check git → `git init` if needed → complete `.gitignore` (bin/obj/dist, settings.json, tasks.json, logs, archive folders) → sensitive-content scan → `git add` → commit (bilingual message) → create or link the remote repository → `git push`.

- **Token**: enter it in the publish window (stored DPAPI-encrypted) or set `VSMANAGER_GITHUB_TOKEN`. The token is only kept in memory and passed to git as an HTTP header; it is never written to the remote URL, `.git/config`, logs or the UI.
  - Fine-grained token: repository permission **Contents: read & write**; add **Administration: read & write** if the repository should be created automatically (organization repositories need organization approval).
  - Classic token: `repo` scope (`public_repo` is enough for public repositories); pushing `.github/workflows` also needs `workflow`.
- **Sensitive-content scan**: scans every file to be committed plus the commit message and author for absolute drive paths, the current user and machine name, e-mail addresses (except noreply / example domains), common key/token formats and suspicious secret assignments, secrets configured on this machine, and the custom term list `%APPDATA%\VSManager\publish-scan-terms.txt` (one per line, e.g. internal project or customer names; never committed). On any hit publishing pauses and lists "file:line + match" (secrets masked) so you can continue or cancel. "仅自检" (Scan only) scans without touching the repository.
- **Log**: `%APPDATA%\VSManager\logs\publish-yyyyMMdd.log`. Failures show the reason and a suggested fix; the tool never force-pushes when the remote has commits you don't have.

## Branching

| Branch | Purpose | Merges |
|---|---|---|
| `main` | Default branch; always buildable and releasable; every release is tagged (e.g. `v1.0.0`) | Only from `develop` (releases) or `fix/xxx` (hotfixes) |
| `develop` | Integration branch collecting finished features and fixes | Pull requests from `feature/xxx` and `fix/xxx` |
| `feature/xxx` | New features, branched from `develop` | Pull request into `develop` when done |
| `fix/xxx` | Bug fixes, usually from `develop`; urgent production fixes from `main` | Merged back into the source branch; fixes from `main` are also merged into `develop` |

```powershell
git switch develop; git pull
git switch -c feature/memory-panel
# ... develop, build, test ...
git push -u origin feature/memory-panel   # then open a pull request into develop on GitHub
```

- Always `git pull` before pushing; **never force-push** (`--force`) to `main` or `develop`.
- Commit messages use "type: summary" in both Chinese and English; see [CONTRIBUTING.md](CONTRIBUTING.md).
- Run "🚀 发布 → 仅自检" (Publish → Scan only) before committing and continue only when there are no hits.

## FAQ

- **A VS instance is missing?** VSManager and VS must run with the same privileges (both elevated or both not), and the solution must be fully loaded.
- **Copilot state stays "unknown"?** Make sure GitHub Copilot is installed and its chat pane has been opened; if the pane title differs, change `CopilotPaneKeyword` in Settings.
- **Sending fails because the paste could not be confirmed?** Right-click the VS → "查看发送日志" (send log); every failure records "paste diagnostics" (target VS, window state, pane / input position and focus, the text snippet read back, clipboard and elapsed time). Common causes: a modal dialog blocks VS, a clipboard sync tool rewrites the clipboard, or a very long message pastes slowly (increase the timeout in Settings → Send confirmation).
- **The AI assistant does not answer / says it is not configured?** Enter the API key in Settings → AI assistant (or set `VSMANAGER_AGENT_API_KEY`) and make sure the endpoint supports function calling.
- **`dotnet restore` fails?** The local NuGet configuration may list an unavailable source; run `dotnet restore VSManager.slnx --source https://api.nuget.org/v3/index.json`.
- **Build says VSManager.exe is in use?** Exit the running app first (tray → 退出 / Exit) or build to a temporary folder: `dotnet build VSManager.slnx "-p:OutputPath=%TEMP%\vsm-build\"`.
- **Pushing to GitHub fails?** Check the token permissions (classic: `repo`, or `public_repo` for public repositories only; fine-grained: Contents read & write) and the network; if the remote has new commits run `git pull` first — the tool never force-pushes.
- **Settings or tasks lost?** Settings live in `%APPDATA%\VSManager\settings.json` (a corrupted file is kept as `settings.json.corrupt-*`); the task list is `tasks.json` and the history archive is under `<ArchiveRoot>\tasks\`.
- **The failed entry disappeared after I republished the task?** That is `AutoHideResentFailedTasks` (on by default): when the SHA-256 fingerprint of the new task's normalized text (unified line breaks, per-line trimming, blank lines dropped, repeated spaces collapsed, zero-width characters removed) equals that of an earlier failed task for the same target VS, the original entry is hidden in the UI only. Texts shorter than 12 characters or a different target are never hidden, and the reason is written to the task log; a leading "resend #26:" or trailing "(retry #26)" marker is stripped before comparing and lifts the length limit. Click History at the top of the task list to see hidden entries; right-click "Show this failed entry again" or "Undo clear" to restore them.

## Security notes

- The web remote is off by default. When enabled it listens on the LAN and anyone with the access token can control every instance, so use it only on trusted networks. The token can be reset in Settings.
- The assistant sends instance names, descriptions, chat content and code snippets to the model provider you configure; the voice feature sends spoken text to Doubao. Choose providers according to your organization's data policy.

## Third-party components

| Component | License |
|---|---|
| [Markdig](https://github.com/xoofx/markdig) | BSD-2-Clause |
| [Microsoft.Extensions.AI / Microsoft.Extensions.AI.OpenAI](https://github.com/dotnet/extensions) | MIT |
| [OpenAI .NET SDK](https://github.com/openai/openai-dotnet) (transitive) | MIT |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) | Microsoft WebView2 SDK license (BSD-style, redistributable) |
| [NAudio.WinMM](https://github.com/naudio/NAudio) | MIT |

The QR code encoder (`QrCode.cs`) and the application icon (`app.ico`, gradient background + text) were made for this project and are released under the MIT license with it. Online services such as DeepSeek and Doubao speech are subject to their own terms of service.

## Contributing

Issues and pull requests are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md) for the full workflow and [CHANGELOG.md](CHANGELOG.md) for release notes. In short:

- This is an open-source project: README, docs, code comments, commit messages and release notes must not contain personal information (real names, e-mail addresses, machine names, user names, local drive paths, company / customer information). Use environment variables such as `%APPDATA%` or generic examples for paths.
- Documentation and code comments are provided in both Chinese and English (Chinese first, then English, or side by side).
- Use `feature/xxx` for features and `fix/xxx` for fixes, and open pull requests into `develop`; the build must have 0 errors and all unit tests must pass.

## Disclaimer

This is an independent open-source tool and is not affiliated with or endorsed by Microsoft, GitHub or any service provider mentioned here. Visual Studio, GitHub Copilot and other names are trademarks of their respective owners.

## License

Released under the [MIT License](LICENSE): you may use, modify and redistribute it as long as the copyright and license notice are kept; the software is provided "as is" without warranty. Third-party components follow their own licenses (see the table above).
