using System.Windows;
using System.Drawing;
using Forms = System.Windows.Forms;
using CodexQuotaWidget.Models;
using CodexQuotaWidget.Services;

namespace CodexQuotaWidget;

/// <summary>
/// 应用程序入口，负责初始化 WPF 资源并创建主窗口。
/// </summary>
public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? trayIcon;
    private Icon? applicationIcon;
    private SingleInstanceCoordinator? singleInstance;
    private WidgetSettings settings = new();

    /// <summary>在创建任何窗口或托盘图标前取得主实例资格；后续进程只通知已有小组件置前并立即结束。</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var suppressPersistenceForAutomation = e.Args.Any(argument => string.Equals(argument, "--test-no-persist-settings", StringComparison.OrdinalIgnoreCase));
        singleInstance = new SingleInstanceCoordinator(GetAutomatedTestInstanceScope(e.Args, suppressPersistenceForAutomation));
        if (!singleInstance.TryBecomePrimary())
        {
            singleInstance.SignalPrimary(TimeSpan.FromSeconds(2));
            singleInstance.Dispose();
            singleInstance = null;
            Shutdown();
            return;
        }

        settings = WidgetSettingsStore.Load();
        settings = ApplyCommandLineThemeOverride(settings, e.Args);
        MainWindow = new MainWindow(settings, suppressPersistenceForAutomation ? _ => { } : SaveSettings);
        CreateTrayIcon();
        MainWindow.Show();
        singleInstance.BeginActivationListener(Dispatcher, ShowMainWindow);
    }

    /// <summary>仅为 UI 验证接受临时主题参数；结合测试专用不持久化参数时，演示主题循环不会改写用户真实偏好。</summary>
    private static WidgetSettings ApplyCommandLineThemeOverride(WidgetSettings current, IEnumerable<string> arguments)
    {
        var value = arguments.FirstOrDefault(argument => argument.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase));
        if (value is null || !Enum.TryParse<WidgetTheme>(value["--theme=".Length..], ignoreCase: true, out var theme))
        {
            return current;
        }

        return current with { Theme = WidgetSettingsStore.Normalize(current with { Theme = theme }).Theme };
    }

    /// <summary>仅在自动化无持久化模式下接受 GUID 作用域，隔离测试进程而不为普通用户提供绕过单实例限制的启动参数。</summary>
    private static string? GetAutomatedTestInstanceScope(IEnumerable<string> arguments, bool automationMode)
    {
        if (!automationMode)
        {
            return null;
        }

        var value = arguments.FirstOrDefault(argument => argument.StartsWith("--instance-scope=", StringComparison.OrdinalIgnoreCase));
        return value is not null && Guid.TryParse(value["--instance-scope=".Length..], out var scope)
            ? "test-" + scope.ToString("N")
            : null;
    }

    /// <summary>创建托盘图标；菜单会随语言设置重建，但始终只提供主界面和退出两个操作。</summary>
    private void CreateTrayIcon()
    {
        // 从程序集资源加载，安装路径和当前工作目录不会影响托盘图标。
        using var iconStream = GetResourceStream(new Uri("pack://application:,,,/assets/app.ico")).Stream;
        using var sourceIcon = new Icon(iconStream, Forms.SystemInformation.SmallIconSize);
        applicationIcon = (Icon) sourceIcon.Clone();
        trayIcon = new Forms.NotifyIcon
        {
            Icon = applicationIcon,
            Visible = true
        };
        RebuildTrayMenu();
        trayIcon.DoubleClick += TrayIconDoubleClicked;
    }

    /// <summary>保存主界面提交的当前用户偏好，并立即同步托盘语言。</summary>
    private void SaveSettings(WidgetSettings updatedSettings)
    {
        settings = WidgetSettingsStore.Normalize(updatedSettings);
        WidgetSettingsStore.Save(settings);
        RebuildTrayMenu();
    }

    /// <summary>重建仅含两个固定操作的托盘菜单，并用当前语言更新菜单标题和悬浮提示。</summary>
    private void RebuildTrayMenu()
    {
        if (trayIcon is null)
        {
            return;
        }

        var text = LocalizedTextProvider.Get(settings.Language);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(text.OpenMainWindow, null, TrayOpenMainWindowClicked);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(text.ExitWidget, null, TrayExitClicked);
        var oldMenu = trayIcon.ContextMenuStrip;
        trayIcon.ContextMenuStrip = menu;
        trayIcon.Text = text.WindowTitle;
        oldMenu?.Dispose();
    }

    /// <summary>处理托盘菜单的主界面操作，恢复隐藏窗口并将其置于前台。</summary>
    private void TrayOpenMainWindowClicked(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    /// <summary>双击托盘图标时也显示主界面，减少隐藏窗口后找回入口的成本。</summary>
    private void TrayIconDoubleClicked(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    /// <summary>恢复或激活唯一主窗口；窗口关闭按钮只隐藏到托盘，二次启动也复用此路径而不重新创建实例。</summary>
    private void ShowMainWindow()
    {
        if (MainWindow is not MainWindow window)
        {
            return;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        // 临时置前后还原用户的图钉偏好，既能让隐藏窗口在第二次启动时可见，也不强行改变长期置顶设置。
        var wasTopmost = window.Topmost;
        window.Topmost = true;
        window.Activate();
        window.Focus();
        if (!wasTopmost)
        {
            _ = Dispatcher.BeginInvoke(() => window.Topmost = false);
        }
    }

    /// <summary>通过已有托盘图标展示短暂的重启结果，不额外创建会出现在任务栏中的提示窗口。</summary>
    internal void ShowNotification(string title, string message, Forms.ToolTipIcon icon = Forms.ToolTipIcon.Info)
    {
        trayIcon?.ShowBalloonTip(7000, title, message, icon);
    }

    /// <summary>通过托盘菜单关闭窗口和托盘资源，确保进程完全退出。</summary>
    private void TrayExitClicked(object? sender, EventArgs e)
    {
        if (MainWindow is MainWindow window)
        {
            window.RequestApplicationExit();
        }

        Shutdown();
    }

    /// <summary>应用退出时释放托盘图标，即使窗口通过系统消息关闭也不遗留图标。</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        singleInstance?.Dispose();
        singleInstance = null;
        var menu = trayIcon?.ContextMenuStrip;
        trayIcon?.Dispose();
        trayIcon = null;
        menu?.Dispose();
        applicationIcon?.Dispose();
        applicationIcon = null;
        base.OnExit(e);
    }
}
