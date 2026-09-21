# Codex 项目协作规则

## 发布新版本

当用户明确要求“发布新版本”“打下一个 tag”“生成 Release”或类似操作时，先读取：

```text
docs/发布新版本.md
```

该文档定义了发布提示词、版本递增规则和脚本执行方式。实际执行文件是项目根目录的：

```text
Publish-Next-Release.bat
```

执行前必须检查：

1. 当前目录是 `codex-quota-widget` 仓库。
2. 当前分支明确，通常应为 `main`。
3. `git status --short` 没有未提交或未跟踪文件。
4. 用户已经提交了本次要发布的代码。

确认条件满足后，使用 PowerShell 执行：

```powershell
$env:RELEASE_NONINTERACTIVE = '1'
& '.\Publish-Next-Release.bat'
if ($LASTEXITCODE -ne 0) { throw '版本发布脚本失败' }
```

脚本会自动读取 `CodexQuotaWidget.csproj` 的版本号，按补位规则递增，提交版本号变更，创建 `vX.Y.Z` 标签，并推送版本提交和标签。标签推送后由 `.github/workflows/release.yml` 自动构建和发布 GitHub Release。

不要在用户没有明确要求发布时运行该脚本。若工作区不干净，先停止并把状态告诉用户，不要替用户提交产品代码或强行清理修改。
