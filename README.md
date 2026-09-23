# VSManager · 多 VS 管理工具

VSManager 是一个 Windows 桌面工具（WinForms / .NET Framework 4.8），用于同时管理本机上多个 Visual Studio 实例及其中的 GitHub Copilot 对话。

## 功能

- **VS 实例总览**：自动发现正在运行的 Visual Studio，显示解决方案、调试状态与 Copilot 忙碌 / 空闲状态；一键布局到多块屏幕。
- **应用内 Copilot 对话**：在 VSManager 中向任意 VS 的 Copilot 发送消息（支持图片），实时查看回复（Markdown 渲染）。
- **调试控制**：开始 / 停止 / 中断 / 重新启动调试，生成 / 重新生成，读取错误列表。
- **AI 总控助手**：接入任意 OpenAI 兼容接口（默认 DeepSeek），通过函数调用查看各 VS 状态、分派任务、等待结果。
- **任务清单**：助手或用户发布的任务在 VS 忙碌时自动排队，空闲后自动发布；同时显示各 VS 中手动进行的 Copilot 对话。
- **语音**：可选接入豆包语音，任务完成后播报摘要（中文 / English 可选，AI 助手回复语言随之切换），并支持按住说话输入。
- **Web 远程控制与 AI Skill**：在局域网内用手机浏览器操作（需访问令牌）；可把控制 API 安装为 Copilot CLI / Claude Code 等的 Skill。
- **历史归档**：任务流水、助手对话、各 VS 对话与发送日志按天写入 JSONL，默认永久保留。
- **发布到 GitHub**：一键 git init / 提交 / 创建或关联远程仓库 / 推送，发布前自动做敏感信息自检。

## 运行环境

- Windows 10 / 11
- Visual Studio 2022 或更高版本，并安装 GitHub Copilot
- .NET Framework 4.8
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)（Windows 11 已自带）

## 构建与运行

```powershell
git clone <repo-url>
cd VSManager
dotnet build VSManager.csproj -c Release
.\bin\Release\net48\VSManager.exe
```

也可以直接用 Visual Studio 打开 `VSManager.csproj` 生成并运行。

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
| `%APPDATA%\VSManager\logs\` | 程序日志 |
| `%APPDATA%\VSManager\publish-scan-terms.txt` | 发布自检的自定义词表 |
| `<ArchiveRoot>\tasks\`、`chat\`、`logs\` | 历史归档 |

以上文件都已写入 `.gitignore`，请勿提交。

## 发布到 GitHub

点击主窗口顶部的「🚀 发布」打开发布窗口，流程为：检查 git → 未初始化时 `git init` → 补齐 `.gitignore`（排除 bin/obj/dist、settings.json、tasks.json、日志、归档目录）→ 敏感信息自检 → `git add` → 提交（中英双语提交信息）→ 创建或关联远程仓库 → `git push`。

- **Token**：在发布窗口填写（DPAPI 加密保存），或设置环境变量 `VSMANAGER_GITHUB_TOKEN`。Token 只在内存中通过 HTTP 头传给 git，不会写入远程地址、`.git/config`、日志或界面。
  - fine-grained Token：仓库权限 **Contents：读写**；需要自动创建仓库时再加 **Administration：读写**（组织仓库需组织授权）。
  - classic Token：`repo` 范围（仅公开仓库可用 `public_repo`）；推送 `.github/workflows` 还需要 `workflow`。
- **敏感信息自检**：扫描所有待提交文件以及提交信息、提交作者，规则包括盘符绝对路径、当前用户名与机器名、邮箱（noreply 与 example 域名除外）、常见 Key/Token 格式与疑似密钥赋值、本机已配置的密钥，以及自定义词表 `%APPDATA%\VSManager\publish-scan-terms.txt`（每行一个，适合填写内部项目名、客户名，文件不会提交）。有命中时发布暂停并列出「文件:行号 + 命中内容」（密钥已打码），由你确认继续或取消；「仅自检」只扫描、不修改仓库。
- **日志**：`%APPDATA%\VSManager\logs\publish-yyyyMMdd.log`。失败时界面会给出原因与修复建议；远程已有本地没有的提交时不会强制推送。
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

## 贡献约定

- 本项目是开源项目：README、文档、代码注释、提交信息与发布说明中不要出现个人信息（真实姓名、邮箱、机器名、用户名、本机盘符路径、公司 / 客户信息），示例路径请使用 `%APPDATA%` 等环境变量或通用示例。
- 开源说明文档与代码注释提供中英两份（中文在前、英文在后，或并列呈现）。
## 声明

本项目为独立的开源工具，与 Microsoft、GitHub 及文中提到的各服务商无隶属或背书关系。Visual Studio、GitHub Copilot 等名称为其各自所有者的商标。

## 许可证

[MIT](LICENSE)

---

# VSManager · Multi Visual Studio Manager (English)

VSManager is a Windows desktop tool (WinForms / .NET Framework 4.8) for managing several Visual Studio instances on one machine together with their GitHub Copilot chats.

## Features

- **Instance overview**: discovers running Visual Studio instances and shows the solution, debug state and whether Copilot is busy or idle; one-click layout across monitors.
- **In-app Copilot chat**: send messages (images supported) to the Copilot of any instance and read the replies live (Markdown rendering).
- **Debug control**: start / stop / break / restart debugging, build / rebuild, read the error list.
- **AI assistant**: works with any OpenAI-compatible endpoint (DeepSeek by default) and uses function calling to inspect instances, dispatch tasks and wait for results.
- **Task list**: tasks from the assistant or the user are queued while the target instance is busy and sent automatically when it becomes idle; manual Copilot chats in each instance are listed as well.
- **Voice**: optional Doubao speech service for spoken summaries when tasks finish (Chinese / English selectable; the AI assistant reply language follows it), plus push-to-talk input.
- **Web remote & AI skill**: control everything from a phone browser on the LAN (access token required); the control API can be installed as a skill for Copilot CLI / Claude Code and similar agents.
- **History archive**: task history, assistant chats, per-instance chats and send logs are written to daily JSONL files and kept forever by default.
- **Publish to GitHub**: one-click git init / commit / create or link the remote / push, with an automatic sensitive-content scan before publishing.

## Requirements

- Windows 10 / 11
- Visual Studio 2022 or later with GitHub Copilot
- .NET Framework 4.8
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (included in Windows 11)

## Build and run

```powershell
git clone <repo-url>
cd VSManager
dotnet build VSManager.csproj -c Release
.\bin\Release\net48\VSManager.exe
```

You can also open `VSManager.csproj` in Visual Studio and build / run it there.

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
| `%APPDATA%\VSManager\logs\` | Application logs |
| `%APPDATA%\VSManager\publish-scan-terms.txt` | Custom terms for the publish scan |
| `<ArchiveRoot>\tasks\`, `chat\`, `logs\` | History archive |

All of these are listed in `.gitignore`; do not commit them.

## Publish to GitHub

Click "🚀 发布" (Publish) at the top of the main window. The flow is: check git → `git init` if needed → complete `.gitignore` (bin/obj/dist, settings.json, tasks.json, logs, archive folders) → sensitive-content scan → `git add` → commit (bilingual message) → create or link the remote repository → `git push`.

- **Token**: enter it in the publish window (stored DPAPI-encrypted) or set `VSMANAGER_GITHUB_TOKEN`. The token is only kept in memory and passed to git as an HTTP header; it is never written to the remote URL, `.git/config`, logs or the UI.
  - Fine-grained token: repository permission **Contents: read & write**; add **Administration: read & write** if the repository should be created automatically (organization repositories need organization approval).
  - Classic token: `repo` scope (`public_repo` is enough for public repositories); pushing `.github/workflows` also needs `workflow`.
- **Sensitive-content scan**: scans every file to be committed plus the commit message and author for absolute drive paths, the current user and machine name, e-mail addresses (except noreply / example domains), common key/token formats and suspicious secret assignments, secrets configured on this machine, and the custom term list `%APPDATA%\VSManager\publish-scan-terms.txt` (one per line, e.g. internal project or customer names; never committed). On any hit publishing pauses and lists "file:line + match" (secrets masked) so you can continue or cancel. "仅自检" (Scan only) scans without touching the repository.
- **Log**: `%APPDATA%\VSManager\logs\publish-yyyyMMdd.log`. Failures show the reason and a suggested fix; the tool never force-pushes when the remote has commits you don't have.

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

- This is an open-source project: README, docs, code comments, commit messages and release notes must not contain personal information (real names, e-mail addresses, machine names, user names, local drive paths, company / customer information). Use environment variables such as `%APPDATA%` or generic examples for paths.
- Documentation and code comments are provided in both Chinese and English (Chinese first, then English, or side by side).

## Disclaimer

This is an independent open-source tool and is not affiliated with or endorsed by Microsoft, GitHub or any service provider mentioned here. Visual Studio, GitHub Copilot and other names are trademarks of their respective owners.

## License

[MIT](LICENSE)