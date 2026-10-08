using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace CodexQuotaWidget.Services;

/// <summary>让自定义无边框标题栏最大化到所在显示器工作区，不覆盖任务栏。</summary>
internal static class WindowWorkArea
{
    /// <summary>注册 HWND 消息钩子；钩子随 HwndSource 销毁，不捕获窗口或业务对象。</summary>
    internal static void Attach(IntPtr handle)
    {
        HwndSource.FromHwnd(handle)?.AddHook(ConstrainMaximizedBounds);
    }

    /// <summary>按当前显示器而非主屏幕计算最大化矩形，保留系统最小尺寸及还原尺寸。</summary>
    private static IntPtr ConstrainMaximizedBounds(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int getMinMaxInfo = 0x0024;
        if (message != getMinMaxInfo) return IntPtr.Zero;

        var monitor = MonitorFromWindow(handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        // Win32 的最大化位置以显示器原点为基准；这里使用物理像素，不能与 WPF DIP 混算。
        var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        limits.MaxPosition = new NativePoint { X = info.Work.Left - info.Monitor.Left, Y = info.Work.Top - info.Monitor.Top };
        limits.MaxSize = new NativePoint { X = info.Work.Right - info.Work.Left, Y = info.Work.Bottom - info.Work.Top };
        Marshal.StructureToPtr(limits, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

    /// <summary>选择窗口所在或最近的显示器，支持负坐标的扩展屏。</summary>
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    /// <summary>读取系统工作区，自动排除所在显示器的任务栏区域。</summary>
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
