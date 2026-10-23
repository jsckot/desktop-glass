using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DesktopGlass.App.Windows;

/// <summary>只为本进程的顶层非 layered 窗口申请系统实时背景；不截图、不持续轮询屏幕。</summary>
internal static class WindowBackdrop
{
    internal sealed record Result(bool Enabled, int AttributeResult, int FrameResult, string Reason);
    /// <summary>申请 Windows 11 Desktop Acrylic；返回结果用于区分真实系统材质与兼容回退。</summary>
    internal static Result Enable(Window window)
    {
        window.Dispatcher.VerifyAccess();
        var handle = new WindowInteropHelper(window).EnsureHandle();
        Native.GetWindowThreadProcessId(handle, out var pid);
        if (pid != Environment.ProcessId) throw new InvalidOperationException("只能设置自己的窗口材质。");
        if (SystemParameters.HighContrast) return new(false, 0, 0, "高对比度");
        if (((long)Native.GetWindowLongPtr(handle,-16) & 0x40000000L) != 0) return new(false, 0, 0, "桌面子窗口");
        int type = 3;
        int attribute = DwmSetWindowAttribute(handle,38,ref type,4);
        if (attribute < 0) return new(false,attribute,0,"系统不支持实时背景");
        var margins = new Margins { Left=-1, Right=-1, Top=-1, Bottom=-1 };
        int frame = DwmExtendFrameIntoClientArea(handle,ref margins);
        if (frame < 0) return new(false,attribute,frame,"系统玻璃边界不可用");
        HwndSource.FromHwnd(handle).CompositionTarget.BackgroundColor = Colors.Transparent;
        window.Background = Brushes.Transparent;
        return new(true,attribute,frame,"Desktop Acrylic");
    }
    internal static void Disable(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        Native.GetWindowThreadProcessId(handle,out var pid);
        if (pid != Environment.ProcessId) throw new InvalidOperationException("只能设置自己的窗口材质。");
        int type=1; Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(handle,38,ref type,4));
        var margins = new Margins(); Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(handle,ref margins));
        HwndSource.FromHwnd(handle).CompositionTarget.BackgroundColor = Color.FromRgb(173,194,206);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Margins { internal int Left,Right,Top,Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd,int attribute,ref int value,int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint hwnd,ref Margins margins);
}
