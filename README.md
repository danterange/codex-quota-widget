# Codex Quota Widget

一个适用于 Windows 的 Codex 额度桌面小组件。它以 VS Code 深色界面风格显示当前账号的 5 小时额度、7 天额度、重置倒计时、会员到期时间和可用重置次数。

> 当前版本提供完整可运行的本地演示数据提供器。真实额度接口需要根据可用的 Codex/OpenAI 账户数据接口接入，界面和数据边界已经分离。

## 本机效果

![Codex Quota Widget 本机运行效果](docs/local-screenshot.png)

## 当前能力

- 右下角固定尺寸悬浮窗，尺寸约 250×148 px。
- 深色、低干扰、接近 VS Code 的界面风格。
- 展示 5 小时和 7 天两个额度窗口，以及各自的剩余时间。
- 展示 Pro 会员到期日期和可用重置次数。
- 窗口可拖动，关闭后释放刷新计时器。
- 额度数据通过 `IQuotaProvider` 抽象，后续接入真实数据不需要重写 UI。

## 运行

需要 Windows、.NET 8 SDK 和桌面运行时：

```powershell
dotnet run --project .\CodexQuotaWidget.csproj
```

发布单文件版本：

```powershell
dotnet publish .\CodexQuotaWidget.csproj -c Release -r win-x64 --self-contained false
```

## 目录

- `MainWindow.xaml`：小组件布局和 VS Code 风格视觉。
- `ViewModels/QuotaViewModel.cs`：倒计时和界面状态。
- `Services/QuotaProvider.cs`：额度提供器接口与本地演示实现。
- `AI开发记录/`：需求、规划、UI、测试和审查记录。

## 后续计划

1. 增加系统托盘入口和开机启动选项。
2. 增加账号切换配置，但主悬浮窗始终只展示一个当前账号。
3. 在确认可用的官方额度数据接口后，实现真实额度提供器。

## 许可证

MIT License
