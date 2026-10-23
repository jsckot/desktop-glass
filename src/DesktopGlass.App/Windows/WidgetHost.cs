using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using DesktopGlass.App.UI;

namespace DesktopGlass.App.Windows;

/// <summary>将单个不透明组件附加为原生桌面图标视图的兄弟窗口；不修改 Explorer 窗口或图标。</summary>
internal sealed class WidgetHost : IDisposable
{
    private const long ChildStyle = 0x40000000L, PopupStyle = 0x80000000L, LayeredStyle = 0x80000L;
    private readonly GroupWidget window;
    private readonly uint explorerPid;
    private readonly long explorerStart;
    private readonly bool foreground;
    private bool closed;
    internal nint Handle { get; }
    internal nint Parent { get; }
    internal nint ListHandle { get; }

    /// <summary>创建组件 HWND，校验桌面身份并附加；失败时关闭自己的组件。</summary>
    /// <param name="window">尚未显示、未附加的本进程独立组件。</param>
    /// <param name="physicalScreenBounds">虚拟屏幕中的物理像素矩形，允许负坐标。</param>
    /// <param name="radiusDip">组件圆角半径，DIP，范围 0～48。</param>
    /// <param name="scale">当前显示器 DPI/96；跨 DPI 由调用方重建宿主。</param>
    /// <exception cref="InvalidOperationException">桌面身份、DPI 模式或附加结果不符合约束。</exception>
    /// <exception cref="Win32Exception">本组件的原生定位或裁剪失败。</exception>
    internal WidgetHost(GroupWidget window, Rect physicalScreenBounds, double radiusDip, double scale, bool foreground = false)
    {
        this.window = window; this.foreground = foreground;
        window.Dispatcher.VerifyAccess();
        try
        {
            ListHandle = Native.DesktopListView(); Parent = Native.GetParent(ListHandle);
            Native.GetWindowThreadProcessId(ListHandle, out explorerPid);
            using var explorer = Process.GetProcessById(checked((int)explorerPid));
            explorerStart = explorer.StartTime.ToUniversalTime().Ticks;
            if (!DesktopMatches()) throw new InvalidOperationException("没有找到身份匹配的 Explorer 原生桌面。");
            Handle = new WindowInteropHelper(window).EnsureHandle();
            EnsureOwnWindow(Handle);
            if (((long)Native.GetWindowLongPtr(Handle, -20) & LayeredStyle) != 0)
                throw new InvalidOperationException("独立分组不支持 layered 窗口。");
            if (!AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(Handle), GetWindowDpiAwarenessContext(Parent)))
                throw new InvalidOperationException("组件和 Explorer 的 DPI 模式不一致，已取消附加以避免进程 DPI 状态被重置。");
            var style = (long)Native.GetWindowLongPtr(Handle, -16);
            Marshal.SetLastPInvokeError(0);
            var previous = SetWindowLongPtrW(Handle, -16, (nint)(foreground ? (style & ~ChildStyle) | PopupStyle : (style & ~PopupStyle) | ChildStyle));
            if (previous == 0 && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (!DesktopMatches()) throw new InvalidOperationException("Explorer 已重建，已取消附加。");
            if (!foreground)
            {
                Native.SetParent(Handle, Parent);
                if (Native.GetParent(Handle) != Parent) throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法将组件附加到原生桌面。");
            }
            window.Closed += (_, _) => closed = true;
            SetBounds(physicalScreenBounds, radiusDip, scale);
            window.Show();
            if (foreground) window.SetForegroundMaterial(WindowBackdrop.Enable(window));
            // WPF Show 会同步窗口尺寸；最后再次以物理像素定位，确保子窗口位置不被 DIP 坐标覆盖。
            SetBounds(physicalScreenBounds, radiusDip, scale);
            if (((long)Native.GetWindowLongPtr(Handle, -16) & (PopupStyle | ChildStyle)) != (foreground ? PopupStyle : ChildStyle))
                throw new InvalidOperationException("组件窗口样式与原生子窗口契约不符。");
        }
        catch { Close(); throw; }
    }

    /// <summary>宿主和原桌面仍属于同一次 Explorer 生命周期时返回 true。</summary>
    internal bool Alive => !closed && Native.IsWindow(Handle) && DesktopMatches() &&
        (foreground ? ((long)Native.GetWindowLongPtr(Handle, -16) & ChildStyle) == 0 : Native.GetParent(Handle) == Parent);

    /// <summary>以物理屏幕坐标定位自己的组件，并同步 WPF 尺寸和原生圆角。</summary>
    /// <param name="physicalScreenBounds">虚拟桌面物理像素矩形；宽高必须为正且有限。</param>
    /// <param name="radiusDip">0～48 DIP 的圆角半径。</param>
    /// <param name="scale">当前显示器有效缩放倍率。</param>
    /// <exception cref="ArgumentOutOfRangeException">矩形、半径或缩放无效。</exception>
    /// <exception cref="InvalidOperationException">Explorer 重建或本窗口已关闭。</exception>
    internal void SetBounds(Rect physicalScreenBounds, double radiusDip, double scale)
    {
        window.Dispatcher.VerifyAccess(); ValidateBounds(physicalScreenBounds, radiusDip, scale);
        if (!Alive) throw new InvalidOperationException("组件或 Explorer 宿主已失效，必须重建窗口。");
        EnsureOwnWindow(Handle);
        var width = checked((int)Math.Round(physicalScreenBounds.Width));
        var height = checked((int)Math.Round(physicalScreenBounds.Height));
        var point = new Native.Point { X = checked((int)Math.Round(physicalScreenBounds.X)), Y = checked((int)Math.Round(physicalScreenBounds.Y)) };
        if (!foreground && !Native.ScreenToClient(Parent, ref point)) throw new Win32Exception(Marshal.GetLastPInvokeError(), "屏幕坐标转换失败。");
        window.Width = width / scale; window.Height = height / scale;
        // HWND_TOP=0：明确提升本组件至桌面图标的兄弟层顶端，不使用 SWP_NOZORDER，也不激活窗口。
        if (!Native.SetWindowPos(Handle, foreground ? -1 : 0, point.X, point.Y, width, height, 0x10 | 0x40 | 0x20))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "组件定位失败。");
        RoundOwnWindow(Handle, width, height, radiusDip, scale);
        window.UpdateLayout();
    }

    /// <summary>原位更新外观，保持 HWND、实际矩形和原生桌面层级不变。</summary>
    /// <param name="appearance">仅使用名称、玻璃浓度和圆角字段。</param>
    /// <param name="scale">当前显示器 DPI/96。</param>
    /// <exception cref="InvalidOperationException">组件失效，交由控制器处理重建。</exception>
    internal void ApplyAppearance(Group appearance, double scale)
    {
        window.Dispatcher.VerifyAccess();
        if (!Alive) throw new InvalidOperationException("组件宿主已失效。");
        window.ApplyAppearance(appearance);
        var bounds = GetBounds();
        RoundOwnWindow(Handle, (int)bounds.Width, (int)bounds.Height, appearance.Radius, scale);
        window.UpdateLayout();
    }

    /// <summary>读取自己的物理屏幕矩形，不依赖 WPF 子窗口 Left/Top 的坐标解释。</summary>
    internal Rect GetBounds() => ReadWindowBounds(Handle);

    /// <summary>关闭自己的窗口；不恢复或改写 Explorer 的窗口状态。</summary>
    internal void Close()
    {
        window.Dispatcher.VerifyAccess();
        if (closed) return;
        closed = true; window.Close();
    }

    /// <summary>释放本组件窗口；不改变任何原生桌面图标。</summary>
    public void Dispose() => Close();

    internal static Rect ReadWindowBounds(nint handle)
    {
        if (!GetWindowRect(handle, out var bounds)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return new Rect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
    }

    internal static Rect ReadClientScreenBounds(nint handle)
    {
        if (!GetClientRect(handle, out var bounds)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var origin = new Native.Point();
        if (!ClientToScreen(handle, ref origin)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return new Rect(origin.X, origin.Y, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
    }

    internal static void RoundOwnWindow(nint handle, int width, int height, double radiusDip, double scale)
    {
        EnsureOwnWindow(handle);
        var diameter = checked((int)Math.Round(Math.Min(Math.Min(width, height), radiusDip * scale * 2)));
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, diameter, diameter);
        if (region == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "组件圆角区域创建失败。");
        // SetWindowRgn 成功后系统接管 region；失败时仍由本进程释放，绝不裁剪 Explorer 本身。
        if (SetWindowRgn(handle, region, true) != 0) return;
        var error = Marshal.GetLastPInvokeError(); DeleteObject(region); throw new Win32Exception(error, "组件圆角设置失败。");
    }

    internal static uint WindowDpi(nint handle) => GetDpiForWindow(handle);

    private bool DesktopMatches()
    {
        if (ListHandle == 0 || Parent == 0 || !Native.IsWindow(ListHandle) || !Native.IsWindow(Parent) ||
            Native.DesktopListView() != ListHandle || Native.GetParent(ListHandle) != Parent ||
            ClassName(ListHandle) != "SysListView32" || ClassName(Parent) != "SHELLDLL_DefView") return false;
        var root = Native.GetParent(Parent);
        if (ClassName(root) is not ("Progman" or "WorkerW")) return false;
        Native.GetWindowThreadProcessId(ListHandle, out var listPid); Native.GetWindowThreadProcessId(Parent, out var parentPid);
        Native.GetWindowThreadProcessId(root, out var rootPid); Native.GetWindowThreadProcessId(GetShellWindow(), out var shellPid);
        if (listPid != explorerPid || parentPid != explorerPid || rootPid != explorerPid || shellPid != explorerPid) return false;
        try
        {
            using var process = Process.GetProcessById(checked((int)explorerPid));
            return !process.HasExited && process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
                process.StartTime.ToUniversalTime().Ticks == explorerStart;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
    }

    private static string ClassName(nint handle)
    { var name = new StringBuilder(128); Native.GetClassName(handle, name, name.Capacity); return name.ToString(); }

    private static void EnsureOwnWindow(nint handle)
    {
        Native.GetWindowThreadProcessId(handle, out var pid);
        if (!Native.IsWindow(handle) || pid != Environment.ProcessId) throw new InvalidOperationException("只能操作当前进程拥有的组件窗口。");
    }

    private static void ValidateBounds(Rect bounds, double radius, double scale)
    {
        if (bounds.IsEmpty || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
            !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) || bounds.Width < 1 || bounds.Height < 1 ||
            bounds.Width > 20000 || bounds.Height > 20000 || Math.Abs(bounds.X) > 1000000 || Math.Abs(bounds.Y) > 1000000 ||
            !double.IsFinite(radius) || radius is < 0 or > 48 || !double.IsFinite(scale) || scale <= 0 || scale > 8)
            throw new ArgumentOutOfRangeException(nameof(bounds), "组件物理矩形、圆角或 DPI 缩放无效。");
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowLongPtrW(nint handle, int index, nint value);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern nint GetWindowDpiAwarenessContext(nint handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint handle);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint handle, out Native.Rect bounds);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint handle, out Native.Rect bounds);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint handle, ref Native.Point point);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(nint handle, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
}
