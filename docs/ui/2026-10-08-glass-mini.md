# 2026-10-08 紧凑玻璃小组件

## 目标与约束

在现有 .NET 8 WPF 小组件中缩小界面，加入关闭、最小化、最大化/还原、置顶标题栏。保留真实额度、自动会员日期、完整秒级日期与剩余时间、中英文和托盘入口。不恢复开机启动或手动日期。用户授权决定视觉方向，本轮不提交、不推送、不发布、不覆盖安装目录。

## 三套设计与选择

| 方案 | 信息结构及主路径 | 优点 | 代价/适用条件 |
| --- | --- | --- | --- |
| A 单列玻璃面板（已实现） | 标题控制 → 状态/图标导航 → 5h/7d 卡片 → 会员日期；设置替换卡片区域 | 日期始终可见，去侧栏，宽度 350；复用现有绑定与设置 | 比极简状态条高，但无需展开才能读日期；适合日常常驻 |
| B 横向状态条 | 标题控制 → 两组百分比；点击详情展开日期和设置 | 收起时高度最低，适合屏幕边缘 | 完整到期时间需要额外点击；信息发现性弱，不符合此前日期直接展示偏好 |
| C 双列额度面板 | 标题控制 → 并列 5h/7d → 底部日期/设置；窄窗口回到单列 | 两额度可同时横向比较，适合宽屏 | 完整日期需要更多宽度或多行；最小面积不及 A |

三套均复用现有 QuotaViewModel、设置与协议，无新 UI 框架。根据“更小”和完整日期要求选择 A。B/C 仅设计备选，未制作可运行实现。

## 视觉与交互

- 深蓝至灰紫的低饱和渐变；原生 Desktop Acrylic 背景模糊，仅背景透明，正文不降低透明度。
- 标题栏 34 DIP，按钮 28 DIP，线框图标有工具提示及可访问名称；支持 hover、按下、禁用、键盘焦点。
- 默认置顶；图钉高亮与实际状态绑定。置顶选择仅在当前会话生效。
- 单次额度读取失败只显示连接中/重试，不立即显示异常；连续两次失败才进入红色异常状态，成功后立即恢复运行中。
- 标题可拖动、双击最大化；四边可调整大小。最大化按当前屏幕工作区计算，避免覆盖任务栏。
- 最小化保留任务栏入口；关闭沿用隐藏到托盘的语义，完全退出用托盘“退出”。
- 主界面默认 320 像素宽（本机 100% 缩放，双额度演示数据）；单额度真实账号会更矮。最小宽度 300。
- 日期完成时间改为日历图标悬浮提示，正文只显示剩余天、小时、分钟；额度比例 17 DIP，窄窗口换行/纵向滚动。
- 剩余百分比区间按 0–25%、25–50%、50–75%、75–100% 使用绿、紫、黄、红色，应用于卡片边框、标签、百分比和进度条。
- 导航自动适配高度；用户主动缩放后保留其尺寸，不强制跳回默认。
- 加载中有文字状态且禁用重复刷新；异常状态保留最后成功额度及诊断提示；不存在的额度卡片隐藏。

## 实现与证据

主要文件：MainWindow.xaml、MainWindow.xaml.cs、App.xaml、App.xaml.cs、Models/LocalizedText.cs、ViewModels/QuotaViewModel.cs。

新增 WindowBackdrop.cs 负责材质；WindowWorkArea.cs 负责 Win32 工作区最大化。无新增第三方依赖。按 UI 设计技能收敛单列/弱装饰/图标导航，按 C# 复审修正语言通知和最大化工作区。

验证命令（在仓库根目录运行）：

```powershell
dotnet build -c Release --no-restore
dotnet run --project tests/QuotaChecks -c Release
dotnet run --project tests/QuotaChecks -c Release -- --live
dotnet run --project tests/QuotaChecks -c Release -- --window-shell --demo
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File scripts/Test-UiScreenshot.ps1
```

本轮已验证：

- Release 编译零警告、零错误；协议/设置/日期/错误恢复/定时刷新回归通过。
- 真实桌面环境读取额度和自动会员到期成功，连续自动刷新成功。
- 中文与英文主界面、设置页的实际 WPF 截图；英文测试不写用户偏好。
- 320×280 窄窗口、600×360 宽窗口截图，图标边界断言；可自由缩放。
- 默认置顶、取消/恢复置顶、最小化/还原、最大化/还原及还原尺寸一致。
- 最大化工作区测试曾发现覆盖任务栏，添加消息钩子后回归通过。
- 正常 Close 隐藏窗口，同一窗口可再次 Show；验收参数下 Close 能结束测试进程。
- PowerShell 语法/UTF-8 BOM 检查通过；git diff --check 无空白问题。

截图输出在忽略目录 artifacts/ui-test：dashboard.png、settings.png、desktop-glass.png、narrow.png、wide.png、english-dashboard.png、english-settings.png。截图同时检查剩余时间简化、图标悬浮提示、颜色区间和连接状态；演示数据不代表真实账号。

## 边界与后续验收

仅实测当前 Windows 11 本机与 100% 缩放；多显示器混合 DPI、Win10、屏幕阅读器和高对比度切换未实测。Win11 22621 以下及启动时高对比度环境使用不透明渐变；系统关闭透明效果/节能状态也会影响材质。

材质使用微软公开 API：[DWM_SYSTEMBACKDROP_TYPE](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type)。不使用未经公开支持的 Accent API。视觉自评约 8.5/10，属主观评价，不替代用户验收。

首次交付为本地编译版本，用户随后确认视觉效果，并授权按更新后的 AGENTS.md 自动审查、提交、推送与发布 v0.1.6。该授权不包含覆盖本地安装，安装目录保持不变。
