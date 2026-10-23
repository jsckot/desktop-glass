using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using global::Windows.UI.Composition;
using global::Windows.UI.Composition.Desktop;
using WinRT;

namespace DesktopGlass.App.Windows;

/// <summary>为本进程顶层窗口附加系统合成背景；不读取壁纸、不截图、不轮询屏幕。</summary>
internal static class WindowBackdrop
{
    // Enabled 表示系统合成资源已初始化；实际透明度仍服从系统高对比度、透明效果及电源策略。
    internal sealed record Result(bool Enabled, int AttributeResult, int FrameResult, string Reason);
    private static readonly ConditionalWeakTable<Window, LiveMaterial> materials = new();
    [ThreadStatic] private static global::Windows.System.DispatcherQueueController? ownedQueue;

    /// <summary>申请 HostBackdrop 实时背景；仅支持顶层 HWND，失败时保留调用方的中性兼容底色。</summary>
    internal static Result Enable(Window window)
    {
        window.Dispatcher.VerifyAccess();
        var handle = new WindowInteropHelper(window).EnsureHandle();
        EnsureOwnWindow(handle);
        if (materials.TryGetValue(window, out _)) return new(true, 0, 0, "HostBackdrop 已初始化");
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
        try
        {
            EnsureQueue();
            var material = new LiveMaterial(window, handle);
            materials.Add(window, material);
            HwndSource.FromHwnd(handle).CompositionTarget.BackgroundColor = Colors.Transparent;
            window.Background = Brushes.Transparent;
            return new(true, attribute, frame, "HostBackdrop；系统策略决定透明效果");
        }
        catch (Exception error) when (error is COMException or TypeLoadException or NotSupportedException)
        {
            // 资源构造失败必须撤回原生设置；仅将已知平台能力失败转为可见的兼容结果。
            ResetNative(handle);
            return new(false, attribute, error.HResult, "HostBackdrop 初始化失败：" + error.Message);
        }
    }

    /// <summary>撤销自己的实时材质并释放每窗口合成资源；共享线程队列保留到 UI 线程退出。</summary>
    internal static void Disable(Window window)
    {
        window.Dispatcher.VerifyAccess();
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        EnsureOwnWindow(handle);
        if (materials.TryGetValue(window, out var material)) { materials.Remove(window); material.Dispose(); }
        ResetNative(handle);
        HwndSource.FromHwnd(handle).CompositionTarget.BackgroundColor = Color.FromRgb(224, 230, 233);
    }

    private static void EnsureOwnWindow(nint handle)
    {
        Native.GetWindowThreadProcessId(handle, out var pid);
        if (pid != Environment.ProcessId) throw new InvalidOperationException("只能设置自己的窗口材质。");
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
        private Compositor? compositor;
        private DesktopWindowTarget? target;
        private SpriteVisual? sprite;
        private CompositionBackdropBrush? brush;
        private bool disposed;

        internal LiveMaterial(Window window, nint handle)
        {
            this.window = window;
            try
            {
                compositor = new Compositor();
                compositor.As<ICompositorDesktopInterop>().CreateDesktopWindowTarget(handle, false, out var raw);
                try { target = MarshalInterface<DesktopWindowTarget>.FromAbi(raw); }
                finally { Marshal.Release(raw); }
                brush = compositor.CreateHostBackdropBrush();
                sprite = compositor.CreateSpriteVisual();
                // 使用相对尺寸由合成器跟随客户区变化，不增加 WPF resize 回调或逐帧工作。
                sprite.RelativeSizeAdjustment = Vector2.One; sprite.Brush = brush; target.Root = sprite;
                window.Closed += OnClosed;
            }
            catch { Dispose(); throw; }
        }

        private void OnClosed(object? sender, EventArgs args) { materials.Remove(window); Dispose(); }

        /// <summary>按依赖顺序释放此窗口的合成树；不释放其他窗口共享的线程队列。</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; window.Closed -= OnClosed;
            if (target is not null) target.Root = null;
            sprite?.Dispose(); brush?.Dispose(); target?.Dispose(); compositor?.Dispose();
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
}
