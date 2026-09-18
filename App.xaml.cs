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
    private static readonly int[] RefreshIntervalOptions = { 5, 10, 30, 60, 120 };
    private Forms.NotifyIcon? trayIcon;
    private WidgetSettings settings = new();
    /// <summary>
    /// 显式创建并显示主窗口，避免无标题栏悬浮窗在启动 URI 初始化阶段被隐藏。
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        settings = WidgetSettingsStore.Load();
        MainWindow = new MainWindow(settings.RefreshIntervalSeconds);
        CreateTrayIcon();
        MainWindow.Show();
    }

    /// <summary>创建托盘图标和菜单，保证无标题栏悬浮窗仍有可靠的退出入口。</summary>
    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("刷新额度", null, TrayRefreshClicked);
        var intervalMenu = new Forms.ToolStripMenuItem("刷新间隔");
        foreach (var seconds in RefreshIntervalOptions)
        {
            var item = new Forms.ToolStripMenuItem($"{seconds} 秒") { Tag = seconds, Checked = seconds == settings.RefreshIntervalSeconds };
            item.Click += RefreshIntervalClicked;
            intervalMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(intervalMenu);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出小组件", null, TrayExitClicked);
        trayIcon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Codex 额度",
            ContextMenuStrip = menu,
            Visible = true
        };
        trayIcon.DoubleClick += TrayIconDoubleClicked;
    }

    /// <summary>响应托盘的手动刷新，不阻塞托盘菜单线程。</summary>
    private async void TrayRefreshClicked(object? sender, EventArgs e)
    {
        if (MainWindow is MainWindow window)
        {
            await window.RefreshNowAsync();
        }
    }

    /// <summary>保存并立即应用用户选择的秒级刷新间隔。</summary>
    private void RefreshIntervalClicked(object? sender, EventArgs e)
    {
        if (sender is not Forms.ToolStripMenuItem item || item.Tag is not int seconds)
        {
            return;
        }

        settings = settings with { RefreshIntervalSeconds = seconds };
        WidgetSettingsStore.Save(settings);
        if (MainWindow is MainWindow window)
        {
            window.SetRefreshIntervalSeconds(seconds);
        }
        if (item.OwnerItem is Forms.ToolStripMenuItem owner)
        {
            foreach (Forms.ToolStripMenuItem sibling in owner.DropDownItems.OfType<Forms.ToolStripMenuItem>())
            {
                sibling.Checked = ReferenceEquals(sibling, item);
            }
        }
    }

    /// <summary>双击托盘图标时显示并激活悬浮窗。</summary>
    private void TrayIconDoubleClicked(object? sender, EventArgs e)
    {
        if (MainWindow is null)
        {
            return;
        }

        MainWindow.Show();
        MainWindow.Activate();
    }

    /// <summary>通过托盘菜单关闭窗口和托盘资源，确保进程完全退出。</summary>
    private void TrayExitClicked(object? sender, EventArgs e)
    {
        trayIcon?.Dispose();
        trayIcon = null;
        MainWindow?.Close();
        Shutdown();
    }

    /// <summary>应用退出时释放托盘图标，即使窗口通过系统消息关闭也不遗留图标。</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        trayIcon?.Dispose();
        trayIcon = null;
        base.OnExit(e);
    }
}
