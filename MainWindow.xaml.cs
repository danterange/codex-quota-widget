using System.Windows;
using System.Windows.Input;
using CodexQuotaWidget.ViewModels;

namespace CodexQuotaWidget;

/// <summary>
/// 承载桌面悬浮窗的主窗口；窗口保持固定尺寸并贴近当前屏幕右下角显示。
/// </summary>
public partial class MainWindow : Window
{
    private readonly QuotaViewModel viewModel = new();

    /// <summary>
    /// 初始化界面绑定和演示额度数据。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>
    /// 在窗口首次加载时计算工作区坐标，避免遮挡任务栏并支持不同 DPI 的主屏幕。
    /// </summary>
    private void WindowLoaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 12;
        Top = workArea.Bottom - ActualHeight - 12;
    }

    /// <summary>
    /// 在窗口关闭时停止倒计时器，避免后台回调继续引用已关闭的界面。
    /// </summary>
    private void WindowClosed(object? sender, EventArgs e)
    {
        viewModel.Dispose();
    }

    /// <summary>
    /// 允许用户拖动无标题栏窗口，保持小组件可移动而不增加额外控件。
    /// </summary>
    private void WindowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    /// <summary>
    /// 响应右键菜单的手动刷新，适用于用户刚完成登录或额度刚发生变化的场景。
    /// </summary>
    private async void RefreshClicked(object sender, RoutedEventArgs e)
    {
        await viewModel.RefreshNowAsync();
    }

    /// <summary>
    /// 通过上下文菜单结束应用，确保 ViewModel 释放刷新计时器。
    /// </summary>
    private void ExitClicked(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
