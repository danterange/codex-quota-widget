# 版本发布

## 普通用户

从 [最新发行版](https://github.com/danterange/codex-quota-widget/releases/latest) 下载 `*-setup.exe` 后双击安装。ZIP 是免安装版，需要全部解压后运行 EXE。

支持 Windows 10/11 x64，自带 .NET 8 运行时。读取真实额度仍要求已安装并登录 Codex。安装包采用当前用户目录，不要求管理员权限。

更新时先退出小组件，再运行新版安装包。当前版本不支持程序内自动更新。卸载保留 `%APPDATA%\CodexQuotaWidget\settings.json` 中的刷新偏好。

## 维护者：以后如何发版

完整自动流程以 [AGENTS.md](../AGENTS.md) 和 [发布新版本.md](发布新版本.md) 为准。向代理明确要求“按 AGENTS.md 执行”，会依次审查、测试、自动提交本次改动并推送，再通过唯一脚本递增版本并打标签。

1. 审查并验证本次代码、UI 与测试，写好下一版本的 `docs/releases/vX.Y.Z.md`。
2. 只提交本次范围内的明确文件并推送；不混入无关修改。
3. 确认工作区干净后执行（不要提前手改版本或另打标签）：

```powershell
$env:RELEASE_NONINTERACTIVE = '1'
& '.\Publish-Next-Release.bat'
if ($LASTEXITCODE -ne 0) { throw '先检查版本提交与标签现场，不直接重跑' }
```

到仓库 [Actions](https://github.com/danterange/codex-quota-widget/actions/workflows/release.yml) 查看结果。普通代码推送不会发布；`v*` 标签推送会自动完成回归检查、打包、安装启动测试和 Release 发布。标签必须与项目 Version 完全一致，否则流程失败。

流水线先创建草稿并上传完整附件，最后发布；失败应检查目标版本状态后仅续跑缺失步骤。已经公开的 Release 不覆盖，修复应发布新版本。完成后下载安装包和 ZIP，按 SHA256SUMS.txt 回验。版本脚本采用三段十进制进位，不处理预发布版本；工作流仍兼容已有的预发布标签。

不需要个人访问令牌或手工配置 Secrets，发布任务使用仓库自动提供的 GITHUB_TOKEN。工作流权限已限制为构建只读、发布写入。

Actions 页面也可点击 Run workflow 做手动构建测试；手动运行只生成可下载的构建附件，不公开 Release。

## 本地打包与验证

需要 .NET 8 SDK（或支持该目标的更高 SDK）和 Inno Setup 6。打包会联网还原官方 .NET 运行时依赖。

```powershell
./scripts/Build-Release.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/Test-Release.ps1
```

输出在 `artifacts/`：安装 EXE、便携 ZIP、SHA256SUMS.txt。安装验收使用临时目录，但会短暂注册当前用户的卸载入口；已有同产品安装时脚本会停止，请在干净 Windows 用户中运行。验收检查演示额度渲染、重新安装、ZIP 启动和卸载。日志与临时便携文件保存在 artifacts/smoke-* 中。

更新图标可执行 `./scripts/New-AppIcon.ps1`，然后提交生成的 assets/app.ico 和 app.png。

## 限制

- 安装包未签名，Windows 可能显示未知发布者或 SmartScreen 提示。
- Actions 验证演示数据及协议回归，不使用私人账号。真实账号读取需在本机验证。
- Windows ARM64、x86 暂不提供专用包。
