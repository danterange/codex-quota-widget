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
    private WidgetSettings settings = new();

    /// <summary>
    /// 显式创建并显示主窗口；应用不再修改 Windows 的开机启动项。
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        settings = WidgetSettingsStore.Load();
        MainWindow = new MainWindow(settings, SaveSettings);
        CreateTrayIcon();
        MainWindow.Show();
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

    /// <summary>恢复或激活唯一主窗口；窗口关闭按钮只隐藏到托盘，不应在此重新创建实例。</summary>
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

        window.Activate();
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
        var menu = trayIcon?.ContextMenuStrip;
        trayIcon?.Dispose();
        trayIcon = null;
        menu?.Dispose();
        applicationIcon?.Dispose();
        applicationIcon = null;
        base.OnExit(e);
    }
}
