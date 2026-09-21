# 版本发布

## 普通用户

从 [最新发行版](https://github.com/danterange/codex-quota-widget/releases/latest) 下载 `*-setup.exe` 后双击安装。ZIP 是免安装版，需要全部解压后运行 EXE。

支持 Windows 10/11 x64，自带 .NET 8 运行时。读取真实额度仍要求已安装并登录 Codex。安装包采用当前用户目录，不要求管理员权限。

更新时先退出小组件，再运行新版安装包。当前版本不支持程序内自动更新。卸载保留 `%APPDATA%\CodexQuotaWidget\settings.json` 中的刷新偏好。

## 维护者：以后如何发版

1. 修改代码，把 `CodexQuotaWidget.csproj` 的 `<Version>` 改为例如 `0.1.1`。
2. 可添加 `docs/releases/v0.1.1.md`，写中文更新说明。没有此文件时使用 GitHub 自动生成的说明。
3. 测试后提交并推送代码，再创建同名版本标签：

```powershell
git add .
git commit -m "准备发布 v0.1.1"
git push origin main
git tag -a v0.1.1 -m "发布 v0.1.1"
git push origin v0.1.1
```

到仓库 [Actions](https://github.com/danterange/codex-quota-widget/actions/workflows/release.yml) 查看结果。普通代码推送不会发布；`v*` 标签推送会自动完成回归检查、打包、安装启动测试和 Release 发布。标签必须与项目 Version 完全一致，否则流程失败。

流水线先创建草稿并上传完整附件，最后发布；失败可在 Actions 重跑。已经公开的 Release 不覆盖，修复应发布新版本。版本带 `-beta.1` 等后缀时自动标记为预发布；普通 `0.1.0` 作为当前 Latest，但不意味着达到 1.0 稳定程度。

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
