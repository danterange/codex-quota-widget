using System.Windows;

namespace CodexQuotaWidget;

/// <summary>
/// 应用程序入口，负责初始化 WPF 资源并创建主窗口。
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 显式创建并显示主窗口，避免无标题栏悬浮窗在启动 URI 初始化阶段被隐藏。
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
