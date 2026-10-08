# Codex 项目协作规则

## 授权与全流程入口

用户要求“按 AGENTS.md 执行”“完整发布”“自动提交并发版”或明确发布新版时，自动完成：

**核对范围 → 代码审查 → 修复并复审 → 本地测试与 UI 验证 → 本地提交 → 推送远程 → 递增版本并打标签 → 等待 GitHub Actions → 校验公开 Release 与附件 → 汇报。**

本流程允许代理自动提交本次任务的代码、测试、文档和发布说明，不再要求用户先手动提交。常规讨论、只读审查、仅本地测试不代表发布授权；用户明确要求“不提交/不推送/不发版”时优先遵守。下载、覆盖安装和关闭用户正在使用的进程不属于发布的默认步骤，须有对应授权。

## 1. 核对仓库与范围

- 先读取 `docs/发布新版本.md`、`Publish-Next-Release.bat`、`.github/workflows/release.yml`。
- 检查 `git status --short`、`git diff`、`git diff --cached`、当前分支、remote、最近提交及未跟踪文件；默认发布 `main`，不擅自切换分支。
- `git fetch origin --tags` 后确认远程关系、待推送提交和下一版本标签；落后、分叉、冲突、未知提交或远程不明确时停止并报告，不强推。
- 未提交内容全部属于本次已授权任务时继续自动审查和提交。发现无关或归属不明的变更时保留原样，询问处理方式；不得为了工作区干净而删除、还原、stash 或顺手提交。
- 检查 Git 推送能力和 GitHub 状态查询能力。可使用已有 Git 凭据；公开仓库允许用只读 GitHub REST API 查询，不强制要求 gh 登录。不得输出、提交或索取明文令牌。

## 2. 自动审查与修复

- 审查所有待发布差异与新增文件，包括 C#/WPF、脚本、工作流、资源和文档。
- 重点检查窗口关闭/最小化/最大化/置顶、资源释放、异步刷新、异常恢复、布局裁切、中文/英文、隐私和安装兼容性；新增/改动方法须有准确注释。
- 明确且安全的问题自动最小修复，补回归测试并再次审查。业务规则不明确、数据风险或重大依赖变更需用户决定。
- 记录审查范围、发现与修复、最后一轮结果、验证限制。未解决的发布阻断问题不能进入提交发布阶段。

## 3. 本地验证门禁

在仓库根目录依次运行；每个原生命令后检查 `$LASTEXITCODE`，失败立即停止后续发布：

```powershell
git diff --check
dotnet build .\CodexQuotaWidget.csproj -c Release
dotnet run --project .\tests\QuotaChecks\QuotaChecks.csproj -c Release
dotnet run --project .\tests\QuotaChecks\QuotaChecks.csproj -c Release -- --window-shell --demo
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-UiScreenshot.ps1
```

- UI 改动须实际查看截图，验证窄/宽窗口、两个页面、标题栏和语言；不能只凭编译成功宣称 UI 合格。
- 修改 PowerShell 脚本时检查语法及中文 UTF-8 BOM。真实账号可用且用户已授权读取时补跑 `--live`；CI 不应依赖个人登录。
- 构建输出被正在运行的程序占用时，使用隔离输出目录或报告，未经授权不结束用户进程。
- 安装、启动、覆盖安装、卸载由 Actions 的干净 Windows 环境验收；不要在用户现有安装上运行清理型安装测试。
- 验证结果可以保存在忽略的 `artifacts/`，公开文档不包含真实账号数据或个人路径。

## 4. 自动本地提交与推送

- 按脚本的十进制进位规则计算下一版本，先写好 `docs/releases/vX.Y.Z.md`；不要提前修改 csproj 版本，以免发布脚本重复递增。
- 同步 README 与功能说明，去除失效描述。需要截图时使用明确标注的演示数据。
- 按功能分批提交，每次明确列出路径暂存，复核 `git diff --cached --check` 与暂存差异后提交；禁止不加判断地 `git add .`。
- 提交说明简洁描述用户可见效果。提交文案技能只负责生成文案，实际 Git 写操作由本流程授权，不因调用文案技能而扩大范围。
- 提交后工作区必须干净，确认所有待推送提交属于本次范围；执行 `git push origin HEAD` 并检查退出码。
- 不使用 `--force`、`--no-verify`、重写历史或跳过测试；提交失败不进入发版。

## 5. 自动版本发布

产品代码和发布说明已提交、工作区干净且远程推送成功后，执行唯一版本入口：

```powershell
$env:RELEASE_NONINTERACTIVE = '1'
& '.\Publish-Next-Release.bat'
if ($LASTEXITCODE -ne 0) { throw '版本发布脚本失败，先检查现场，不重复递增版本' }
```

脚本负责递增 csproj 版本、创建版本提交、创建带注释标签、推送提交与标签。不要另行手工再次递增。仍保留脚本拒绝脏工作区的保护；自动产品提交发生在脚本之前。

## 6. Actions 与 Release 闭环

- 等待对应 **标签及提交 SHA** 的 `.github/workflows/release.yml`，不能拿其他提交的绿灯作证明；按合理间隔查询，并报告构建/测试/打包/发布进展。
- 构建作业应通过离线回归、英文窗口测试、安装器/ZIP 打包，以及安装/启动/重装/卸载验收；仅全部通过后 publish 作业才公开 Release。
- 检查 Release 为非草稿，tag、版本、目标提交一致，且包含非空的：
  - `codex-quota-widget-vX.Y.Z-windows-x64-setup.exe`
  - `codex-quota-widget-vX.Y.Z-windows-x64.zip`
  - `SHA256SUMS.txt`
- 下载本版本附件到独立校验目录，检查清单恰好涵盖安装包和 ZIP，文件名无路径穿越，两个 SHA-256 均匹配。下载校验不等于安装，不自动执行下载的程序。
- 完成条件是 **Actions 成功 + Release 公开 + 附件齐全且校验通过**，不是仅 `git push` 成功。
- 最终汇报版本、提交、Actions/Release 链接、审查测试结果、附件校验和未验证项，以及本地安装是否改变。

## 7. 失败与续跑

- 任一步失败保留日志和现场，先定位并修复；不得强行继续、伪称完成或清理用户修改。
- 若已产生版本提交/标签，不得直接重跑 BAT。先查本地/远程 tag、SHA、工作流和 Release，续跑同一版本缺失的推送或作业，避免跳号。
- 已公开的 tag 和附件不可覆盖或删除；需改代码时走新版本。未公开草稿只允许修复当前目标版本。
- 网络/权限/外部服务阻塞时说明已完成阶段、剩余步骤和所需用户操作；不重复无效尝试或索取明文密码。
