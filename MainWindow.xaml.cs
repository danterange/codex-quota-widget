using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using CodexQuotaWidget.ViewModels;

namespace CodexQuotaWidget;

/// <summary>
/// 承载桌面悬浮窗的主窗口；窗口保持固定尺寸并贴近当前屏幕右下角显示。
/// </summary>
public partial class MainWindow : Window
{
    private readonly QuotaViewModel viewModel;

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;

    /// <summary>
    /// 初始化界面绑定和演示额度数据。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        viewModel = new QuotaViewModel();
        DataContext = viewModel;
    }

    /// <summary>
    /// 将窗口标记为工具窗口，避免任务栏按钮和 Alt+Tab 项目，同时保留顶层悬浮行为。
    /// </summary>
    private void WindowSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, (style | WsExToolWindow) & ~WsExAppWindow);
    }

    /// <summary>
    /// 在窗口首次加载时计算工作区坐标，避免遮挡任务栏并支持不同 DPI 的主屏幕。
    /// </summary>
    private void WindowLoaded(object sender, RoutedEventArgs e)
    {
        PositionInWorkArea();
    }

    /// <summary>
    /// 等待模板完成布局后再次定位，保证实际高度变化时右下角间距仍然准确。
    /// </summary>
    private void WindowContentRendered(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(PositionInWorkArea, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Activate();
    }

    /// <summary>
    /// 使用当前工作区而不是屏幕边界定位，避开任务栏并兼容负坐标副屏。
    /// </summary>
    private void PositionInWorkArea()
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

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint handle, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint handle, int index, nint value);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(nint handle, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(nint handle, int index, int value);

    private static int GetWindowLong(nint handle, int index)
    {
        return nint.Size == 8 ? (int)GetWindowLongPtr(handle, index) : GetWindowLong32(handle, index);
    }

    private static void SetWindowLong(nint handle, int index, int value)
    {
        if (nint.Size == 8)
        {
            SetWindowLongPtr(handle, index, value);
            return;
        }

        SetWindowLong32(handle, index, value);
    }
}
