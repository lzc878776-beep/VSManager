# 贡献指南

感谢你愿意改进 VSManager！本文说明分支策略、开发流程、提交规范与开源约束。English version below.

## 开源约束（必读）

- README、文档、代码注释、提交信息、发布说明中**不得出现个人信息**：真实姓名、邮箱、机器名、用户名、本机盘符路径、公司 / 客户信息。
- 示例路径使用 `%APPDATA%`、`%TEMP%`、`%USERPROFILE%` 等环境变量或 `<owner>`、`<ArchiveRoot>` 这类通用占位符。
- 提交作者建议使用 `VSManager contributors` 与 GitHub 的 noreply 邮箱（`<id>+<login>@users.noreply.github.com`）。
- 开源说明文档与代码注释一律中英双语：中文在前、英文在后，或并列呈现（例如 `/// 中文。/ English.`）。
- 不要提交 `settings.json`、`tasks.json`、`agent-chat*.jsonl`、日志、归档目录、构建输出；这些已写入 `.gitignore`。
- API Key / Token 只通过「属性」（DPAPI 加密）或环境变量（`VSMANAGER_AGENT_API_KEY`、`VSMANAGER_DOUBAO_API_KEY`、`VSMANAGER_GITHUB_TOKEN`）提供，绝不写入代码或配置示例。

## 分支策略

| 分支 | 用途 | 从哪里创建 | 合并到 |
|---|---|---|---|
| `main` | 默认主分支，始终可构建、可发布；版本打标签 `vX.Y.Z` | — | — |
| `develop` | 集成分支 | `main` | `main`（发布时） |
| `feature/xxx` | 新功能，如 `feature/memory-panel` | `develop` | `develop` |
| `fix/xxx` | 缺陷修复，如 `fix/task-duplicate` | `develop`（紧急修复可从 `main`） | 来源分支；从 `main` 创建的还要合并到 `develop` |

分支名使用小写英文与连字符。`main` 与 `develop` 只通过 Pull Request 合并，**禁止强制推送**。

## 开发流程

```powershell
git switch develop
git pull                                   # 先拉取远程最新内容
git switch -c feature/xxx
# 修改代码……
dotnet build VSManager.slnx "-p:OutputPath=%TEMP%\vsm-build\"   # 程序运行中时输出到临时目录，避免覆盖正在运行的 exe
dotnet test  VSManager.slnx "-p:OutputPath=%TEMP%\vsm-build\"
git add -A
git commit -m "feat: 新增内存面板 / add memory panel"
git pull --rebase origin develop           # 推送前再同步一次
git push -u origin feature/xxx
```

然后在 GitHub 上向 `develop` 发起 Pull Request。发布版本时由维护者把 `develop` 合并到 `main`，更新 [CHANGELOG.md](CHANGELOG.md) 并打标签。

## 提交信息规范

格式：`类型: 中文简述 / English summary`，简述不超过一行；需要时在空行后补充正文（同样中英双语）。

| 类型 | 用途 |
|---|---|
| `feat` | 新功能 |
| `fix` | 缺陷修复 |
| `docs` | 仅文档 |
| `refactor` | 重构（不改变行为） |
| `test` | 测试 |
| `build` / `chore` | 构建、依赖、杂项 |

示例：`fix: 修复重启后任务重复发布 / avoid republishing tasks after a restart`

## 提交前检查清单

- [ ] 构建 0 错误，`dotnet test` 全部通过。
- [ ] 未改动的业务行为保持不变；配置项名称与默认值、用户数据文件格式保持兼容。
- [ ] 新增文案与注释为中英双语。
- [ ] 已用「🚀 发布 → 仅自检」做敏感信息自检且无命中（规则：盘符绝对路径、用户名 / 机器名、邮箱、Key/Token、本机已配置的密钥、自定义词表 `%APPDATA%\VSManager\publish-scan-terms.txt`）。
- [ ] 用户可见的变化已写入 [CHANGELOG.md](CHANGELOG.md) 的「未发布」部分。

## 许可证

提交贡献即表示你同意以本项目的 [MIT 许可证](LICENSE) 发布你的贡献。

---

# Contributing

Thank you for improving VSManager! This guide covers branching, the development workflow, commit messages and the open-source rules.

## Open-source rules (please read)

- README, docs, code comments, commit messages and release notes **must not contain personal information**: real names, e-mail addresses, machine names, user names, local drive paths, company / customer information.
- Use environment variables such as `%APPDATA%`, `%TEMP%`, `%USERPROFILE%` or generic placeholders such as `<owner>` and `<ArchiveRoot>` in example paths.
- We recommend committing as `VSManager contributors` with your GitHub noreply address (`<id>+<login>@users.noreply.github.com`).
- Documentation and code comments are bilingual: Chinese first, then English, or side by side (e.g. `/// 中文。/ English.`).
- Never commit `settings.json`, `tasks.json`, `agent-chat*.jsonl`, logs, archive folders or build output; they are listed in `.gitignore`.
- Provide API keys / tokens only through Settings (DPAPI-encrypted) or environment variables (`VSMANAGER_AGENT_API_KEY`, `VSMANAGER_DOUBAO_API_KEY`, `VSMANAGER_GITHUB_TOKEN`), never in code or example configs.

## Branching

| Branch | Purpose | Branched from | Merged into |
|---|---|---|---|
| `main` | Default branch, always buildable and releasable; releases are tagged `vX.Y.Z` | — | — |
| `develop` | Integration branch | `main` | `main` (on release) |
| `feature/xxx` | New features, e.g. `feature/memory-panel` | `develop` | `develop` |
| `fix/xxx` | Bug fixes, e.g. `fix/task-duplicate` | `develop` (urgent fixes may start from `main`) | The source branch; fixes from `main` are also merged into `develop` |

Use lowercase English words with hyphens in branch names. `main` and `develop` are only updated through pull requests, and **force-pushing is not allowed**.

## Workflow

```powershell
git switch develop
git pull                                   # get the latest remote changes first
git switch -c feature/xxx
# change code ...
dotnet build VSManager.slnx "-p:OutputPath=%TEMP%\vsm-build\"   # build to a temp folder while the app is running
dotnet test  VSManager.slnx "-p:OutputPath=%TEMP%\vsm-build\"
git add -A
git commit -m "feat: 新增内存面板 / add memory panel"
git pull --rebase origin develop           # sync once more before pushing
git push -u origin feature/xxx
```

Then open a pull request into `develop` on GitHub. For a release, a maintainer merges `develop` into `main`, updates [CHANGELOG.md](CHANGELOG.md) and creates a tag.

## Commit messages

Format: `type: 中文简述 / English summary` on one line, optionally followed by a blank line and a bilingual body.

| Type | Use |
|---|---|
| `feat` | New feature |
| `fix` | Bug fix |
| `docs` | Documentation only |
| `refactor` | Refactoring without behavior changes |
| `test` | Tests |
| `build` / `chore` | Build, dependencies, housekeeping |

Example: `fix: 修复重启后任务重复发布 / avoid republishing tasks after a restart`

## Checklist before committing

- [ ] The build has 0 errors and `dotnet test` passes.
- [ ] Existing behavior is unchanged; setting names, defaults and user data formats stay compatible.
- [ ] New UI text and comments are bilingual.
- [ ] The sensitive-content scan ("🚀 发布 → 仅自检" / Publish → Scan only) reports no hits (rules: absolute drive paths, user / machine name, e-mail addresses, keys/tokens, secrets configured on this machine, the custom term list `%APPDATA%\VSManager\publish-scan-terms.txt`).
- [ ] User-visible changes are added to the "Unreleased" section of [CHANGELOG.md](CHANGELOG.md).

## License

By contributing you agree that your contribution is released under the project's [MIT License](LICENSE).
