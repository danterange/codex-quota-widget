using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CodexQuotaWidget.Services;

/// <summary>用受支持的 DWM Acrylic 材质模糊窗口后方内容；旧系统或高对比度环境保持实色。</summary>
internal static class WindowBackdrop
{
    /// <summary>在 HWND 创建后申请 Acrylic 并同步系统标题栏明暗偏好，返回系统是否接受材质请求。</summary>
    internal static bool TryEnable(Window window, bool useDarkCaption)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) || SystemParameters.HighContrast)
            return false;
        var handle = new WindowInteropHelper(window).Handle;
        ApplyCaptionTheme(handle, useDarkCaption);
        var acrylic = 3; // DWMSBT_TRANSIENTWINDOW: documented Desktop Acrylic, not undocumented accent APIs.
        var rounded = 2;
        _ = DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));
        if (DwmSetWindowAttribute(handle, 38, ref acrylic, sizeof(int)) < 0)
            return false;
        if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
            target.BackgroundColor = Colors.Transparent;
        return true;
    }

    /// <summary>在主题切换时更新 DWM 的标题栏明暗提示；自定义标题栏不依赖该调用才能保持可读。</summary>
    internal static void ApplyCaptionTheme(Window window, bool useDarkCaption)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) || SystemParameters.HighContrast)
            return;

        ApplyCaptionTheme(new WindowInteropHelper(window).Handle, useDarkCaption);
    }

    /// <summary>向已验证的窗口句柄写入 DWM 深色模式偏好，失败时让 WPF 自身的主题资源继续工作。</summary>
    private static void ApplyCaptionTheme(IntPtr handle, bool useDarkCaption)
    {
        var dark = useDarkCaption ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }

    /// <summary>调用 Windows 桌面合成器；HRESULT 由调用方检查，失败时不阻断窗口启动。</summary>
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
