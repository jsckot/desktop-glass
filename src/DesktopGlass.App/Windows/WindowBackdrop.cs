using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using global::Windows.UI.Composition;
using global::Windows.UI.Composition.Desktop;
using WinRT;

namespace DesktopGlass.App.Windows;

/// <summary>将系统实时背景与 WPF 前景分置自有顶层窗口；不读取壁纸、不截图、不轮询屏幕。</summary>
internal static class WindowBackdrop
{
    // Enabled 表示系统合成资源已初始化；实际透明度仍服从系统高对比度、透明效果及电源策略。
    internal sealed record Result(bool Enabled, int AttributeResult, int FrameResult, string Reason);
    private static readonly ConditionalWeakTable<Window, LiveMaterial> materials = new();
    private static readonly Dictionary<nint, nint> backgroundOwners = new();
    [ThreadStatic] private static global::Windows.System.DispatcherQueueController? ownedQueue;

    /// <summary>申请 HostBackdrop 实时背景；失败时保留调用方的中性兼容底色。</summary>
    internal static Result Enable(Window window)
    {
        window.Dispatcher.VerifyAccess();
        var handle = new WindowInteropHelper(window).EnsureHandle();
        EnsureOwnWindow(handle);
        if (materials.TryGetValue(window, out var existing)) { existing.Sync(); return new(true, 0, 0, "HostBackdrop 已初始化"); }
        if (SystemParameters.HighContrast) return new(false, 0, 0, "高对比度");
        if (((long)Native.GetWindowLongPtr(handle, -16) & 0x40000000L) != 0)
            return new(false, 0, 0, "桌面子窗口不支持此系统背景；使用中性兼容底色");
        if (((long)Native.GetWindowLongPtr(handle, -20) & 0x80000L) != 0)
            return new(false, 0, 0, "layered 窗口不支持此系统背景");
        int enabled = 1;
        var attribute = DwmSetWindowAttribute(handle, 17, ref enabled, 4);
        if (attribute < 0) return new(false, attribute, 0, "系统不支持 HostBackdrop");
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        var frame = DwmExtendFrameIntoClientArea(handle, ref margins);
        if (frame < 0)
        {
            enabled = 0; Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(handle, 17, ref enabled, 4));
            return new(false, attribute, frame, "系统玻璃边界不可用");
        }
        LiveMaterial? material = null;
        try
        {
            EnsureQueue();
            material = new LiveMaterial(window, handle);
            materials.Add(window, material);
            HwndSource.FromHwnd(handle).CompositionTarget.BackgroundColor = Colors.Transparent;
            window.Background = Brushes.Transparent;
            material.Sync();
            return new(true, attribute, frame, "隔离 HostBackdrop；系统策略决定透明效果");
        }
        catch (Exception error)
        {
            materials.Remove(window); material?.Dispose(); ResetNative(handle);
            if (error is COMException or TypeLoadException or NotSupportedException)
                return new(false, attribute, error.HResult, "HostBackdrop 初始化失败：" + error.Message);
            throw;
        }
    }

    /// <summary>撤销自己的实时材质并释放每窗口合成资源；共享线程队列保留到 UI 线程退出。</summary>
    internal static void Disable(Window window)
    {
        window.Dispatcher.VerifyAccess();
        if (materials.TryGetValue(window, out var material)) { materials.Remove(window); material.Dispose(); }
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || !Native.IsWindow(handle)) return;
        EnsureOwnWindow(handle); ResetNative(handle);
        if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } composition)
            composition.BackgroundColor = Color.FromRgb(224, 230, 233);
    }

    /// <summary>前景定位或裁剪后同步背景；未启用材质或已关闭时无操作。</summary>
    internal static void Sync(Window window)
    {
        window.Dispatcher.VerifyAccess();
        if (materials.TryGetValue(window, out var material)) material.Sync();
    }

    /// <summary>查询自有背景的前景 HWND，供共享桌面事件归属使用；未知窗口返回零。</summary>
    internal static nint ContentHandleFor(nint handle) => backgroundOwners.TryGetValue(handle, out var content) ? content : 0;

    /// <summary>判断 HWND 是否为本产品仍存活的专用背景层。</summary>
    internal static bool IsBackdropWindow(nint handle) => backgroundOwners.ContainsKey(handle);

    private static void EnsureOwnWindow(nint handle)
    {
        Native.GetWindowThreadProcessId(handle, out var pid);
        if (!Native.IsWindow(handle) || pid != Environment.ProcessId) throw new InvalidOperationException("只能设置自己的窗口材质。");
    }

    private static void ResetNative(nint handle)
    {
        int enabled = 0; Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(handle, 17, ref enabled, 4));
        var margins = new Margins(); Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(handle, ref margins));
    }

    private static void EnsureQueue()
    {
        if (global::Windows.System.DispatcherQueue.GetForCurrentThread() is not null) return;
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(new QueueOptions { Size = 12, ThreadType = 2, Apartment = 2 }, out var raw));
        try { ownedQueue = MarshalInterface<global::Windows.System.DispatcherQueueController>.FromAbi(raw); }
        finally { Marshal.Release(raw); }
        // 同一 UI 线程上的全部窗口复用队列；关闭单个分组不能终止其他分组的合成服务。
        Dispatcher.CurrentDispatcher.ShutdownStarted += (_, _) =>
        {
            if (ownedQueue is { } controller) { _ = controller.ShutdownQueueAsync(); ownedQueue = null; }
        };
    }

    private sealed class LiveMaterial : IDisposable
    {
        private readonly Window window;
        private readonly nint contentHandle;
        private readonly HwndSource contentSource;
        private HwndSource? backgroundSource;
        private Compositor? compositor;
        private DesktopWindowTarget? target;
        private SpriteVisual? sprite;
        private CompositionBackdropBrush? brush;
        private bool disposed, syncing;

        internal LiveMaterial(Window window, nint handle)
        {
            this.window = window; contentHandle = handle; contentSource = HwndSource.FromHwnd(handle);
            try
            {
                // 背景无 owner/parent，无 WPF 内容，不激活、不命中输入，也不出现在任务栏。
                backgroundSource = new HwndSource(new HwndSourceParameters("DesktopGlass.HostBackdrop")
                {
                    WindowStyle = unchecked((int)0x88000000), ExtendedWindowStyle = 0x080000A0,
                    Width = 1, Height = 1, ParentWindow = 0
                });
                var background = backgroundSource.Handle;
                backgroundOwners.Add(background, handle);
                backgroundSource.CompositionTarget.BackgroundColor = Colors.Transparent;
                int enabled = 1; Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(background, 17, ref enabled, 4));
                var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(background, ref margins));
                compositor = new Compositor();
                compositor.As<ICompositorDesktopInterop>().CreateDesktopWindowTarget(background, false, out var raw);
                try { target = MarshalInterface<DesktopWindowTarget>.FromAbi(raw); }
                finally { Marshal.Release(raw); }
                brush = compositor.CreateHostBackdropBrush();
                sprite = compositor.CreateSpriteVisual();
                sprite.RelativeSizeAdjustment = Vector2.One; sprite.Brush = brush; target.Root = sprite;
                contentSource.AddHook(OnContentMessage);
                window.IsVisibleChanged += OnVisibleChanged;
                window.Closed += OnClosed;
            }
            catch { Dispose(); throw; }
        }

        internal void Sync()
        {
            if (disposed || syncing || backgroundSource is null) return;
            syncing = true;
            try
            {
                var background = backgroundSource.Handle;
                if (!Native.IsWindow(contentHandle) || !window.IsVisible || !IsWindowVisible(contentHandle) || IsIconic(contentHandle))
                { if (IsWindowVisible(background)) ShowWindow(background, 0); return; }
                if (!GetWindowRect(contentHandle, out var bounds)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                CopyRegion(background);
                var topmost = (Native.GetWindowLongPtr(contentHandle, -20).ToInt64() & 8) != 0;
                var backgroundTopmost = (Native.GetWindowLongPtr(background, -20).ToInt64() & 8) != 0;
                if (topmost != backgroundTopmost && !Native.SetWindowPos(background, topmost ? -1 : -2, 0, 0, 0, 0, 0x213))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "背景 Topmost 同步失败。");
                // 相邻关系已满足时不触发新的 Z 序事件，防止多个组件互相反复重排。
                var flags = 0x210u | 0x40u | (GetWindow(contentHandle, 2) == background ? 0x4u : 0u);
                if (!Native.SetWindowPos(background, contentHandle, bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, flags))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "组件背景定位失败。");
            }
            finally { syncing = false; }
        }

        private void CopyRegion(nint background)
        {
            var region = CreateRectRgn(0, 0, 0, 0);
            if (region == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            try
            {
                // 没有显式区域时清除旧裁剪；成功设置后 region 所有权交给系统。
                if (GetWindowRgn(contentHandle, region) == 0)
                {
                    if (SetWindowRgn(background, 0, true) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
                else
                {
                    if (SetWindowRgn(background, region, true) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                    region = 0;
                }
            }
            finally { if (region != 0) DeleteObject(region); }
        }

        private nint OnContentMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            // WINDOWPOSCHANGED 在系统完成移动/显示/隐藏后触发，也覆盖 WPF 之外的 Win+D 与最小化。
            if (message == 0x82) { materials.Remove(window); Dispose(); }
            else if ((message == 0x18 && wParam == 0) || (message == 0x5 && wParam == 1))
            { if (backgroundSource is { } background) ShowWindow(background.Handle, 0); }
            else if (message is 0x47 or 0x18 or 0x5) Sync();
            return 0;
        }
        private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs args) => Sync();
        private void OnClosed(object? sender, EventArgs args) { materials.Remove(window); Dispose(); }

        /// <summary>先撤销事件归属与前景钩子，再释放合成树和背景窗口，防止关闭时恢复残留层。</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            window.Closed -= OnClosed; window.IsVisibleChanged -= OnVisibleChanged;
            contentSource.RemoveHook(OnContentMessage);
            if (backgroundSource is { } background) backgroundOwners.Remove(background.Handle);
            try
            {
                try { if (target is not null) target.Root = null; }
                finally
                {
                    try { sprite?.Dispose(); }
                    finally { try { brush?.Dispose(); } finally { try { target?.Dispose(); } finally { compositor?.Dispose(); } } }
                }
            }
            finally { backgroundSource?.Dispose(); backgroundSource = null; }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct QueueOptions { internal int Size, ThreadType, Apartment; }
    [StructLayout(LayoutKind.Sequential)] private struct Margins { internal int Left, Right, Top, Bottom; }
    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool topmost, out nint target);
    }
    [DllImport("coremessaging.dll")] private static extern int CreateDispatcherQueueController(QueueOptions options, out nint controller);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint window, out Native.Rect bounds);
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
}
