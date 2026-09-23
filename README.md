# Codex Quota Widget

一个适用于 Windows 的 Codex 额度桌面小组件。它以 VS Code 深色界面风格显示当前账号剩余的 5 小时额度、7 天额度、重置倒计时、会员到期时间和可用重置次数。

> 默认模式通过本机 Codex app-server 的只读 JSON-RPC 接口读取当前账号。使用 `--demo` 可运行静态演示数据。公开协议当前没有稳定的会员到期字段：组件只会使用响应中名称明确的会员/订阅到期字段，缺失时可在设置页填写本地兜底日期；不会把额度重置时间误显示成会员到期。

## 本机效果

![Codex Quota Widget 本机运行效果](docs/local-screenshot.png)

截图来自本机真实账号模式，记录的是组件实际渲染效果；Pro 20× 只有周额度时不会强行显示 5 小时行。

## 当前能力

- 右下角固定尺寸主界面，首个标签页显示额度仪表，第二个标签页集中设置低频选项。
- 深色、低干扰、接近 VS Code 的界面风格。
- 展示 5 小时和 7 天两个额度窗口，以及各自的剩余时间。
- 自动展示响应中明确提供的会员/订阅到期日期和可用重置次数；没有自动数据时使用可选的本地兜底日期。
- 中文会员日期严格显示为 `到期：2026/09/22 12:00:00(剩余0天0小时16分)`；English 显示等义日期和剩余时间。
- 关闭主界面会隐藏至托盘并继续刷新；只能通过托盘“退出小组件”完全退出。
- 每 10 秒自动刷新额度；刷新中的请求不会并行叠加。
- 支持简体中文和 English；切换后主界面、设置页、托盘菜单和状态文本即时更新。
- Windows 托盘区常驻图标，菜单仅有“主界面”和“退出小组件”；双击图标也打开主界面。
- 主界面支持手动刷新、设置 5/10/30/60/120 秒刷新间隔和设置语言。
- 刷新间隔和语言保存到当前 Windows 用户配置；默认 10 秒、中文，间隔范围限制为 1 到 3600 秒。
- 自动优先使用 Codex 桌面应用自带的原生 CLI，兼容 `bin/<版本散列>/codex.exe` 安装路径，避免误选缺少平台依赖的 npm 启动脚本。
- 启动、登录、网络和协议错误会显示可操作的中文提示，不把服务端原始诊断写入界面。
- 额度数据通过 `IQuotaProvider` 抽象，后续接入真实数据不需要重写 UI。
- Pro 20× 只有周额度时自动隐藏 5 小时行，不把不存在的额度窗口显示成 0%。

## 下载与安装

[下载最新版本](https://github.com/danterange/codex-quota-widget/releases/latest)

Windows 10/11 x64 用户下载 `*-setup.exe` 后双击安装；也可以下载 ZIP，全部解压后运行 `CodexQuotaWidget.exe`。两者都自带 .NET 8 运行时，不需要安装 SDK。

真实额度需要本机已安装并登录 Codex。安装包尚未签名，Windows 可能显示未知发布者提示。后续升级时退出小组件，再安装新版；当前没有程序内自动更新。

## 从源码运行

需要 Windows、.NET 8 SDK 和桌面运行时：

```powershell
dotnet run --project .\CodexQuotaWidget.csproj
```

需要只验证 UI 时：

```powershell
dotnet run --project .\CodexQuotaWidget.csproj -- --demo
```

真实模式要求本机 Codex 桌面应用已登录 ChatGPT 账号。组件使用只读的 `initialize`、`account/read` 和 `account/rateLimits/read` 请求读取额度；API Key 登录不包含 Plus / Pro 订阅额度。

## 验证

```powershell
dotnet build .\CodexQuotaWidget.csproj -c Release --no-restore
dotnet run --project .\tests\QuotaChecks\QuotaChecks.csproj -c Release
dotnet .\tests\QuotaChecks\bin\Release\net8.0-windows\QuotaChecks.dll --live
```

构建自包含安装包和免安装 ZIP（需要 Inno Setup 6）：

```powershell
./scripts/Build-Release.ps1
```

发布者操作步骤和自动发布说明见 [版本发布文档](docs/RELEASING.md)。

## 目录

- `MainWindow.xaml`：额度与设置两页布局和 VS Code 风格视觉。
- `ViewModels/QuotaViewModel.cs`：倒计时、自动刷新、日期格式和界面状态。
- `Services/QuotaProvider.cs`：额度提供器、明确命名的会员到期字段探测与本地演示实现。

## 许可证

MIT License
