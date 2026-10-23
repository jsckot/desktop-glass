using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DesktopGlass.App.Windows;

/// <summary>将自有非激活顶层组件保持在普通应用下方的桌面层；共享事件钩子，不修改 Explorer。</summary>
internal sealed class DesktopLayer : IDisposable
{
    private const uint ForegroundEvent = 3, MinimizeStartEvent = 0x16, MinimizeEndEvent = 0x17,
        ShowEvent = 0x8002, HideEvent = 0x8003, ReorderEvent = 0x8004;
    private const long ChildStyle = 0x40000000L, TopmostStyle = 8, AppWindowStyle = 0x40000L;
    private static readonly Dictionary<nint, DesktopLayer> Layers = new();
    private static readonly WinEventProc EventCallback = OnWinEvent;
    private static nint foregroundHook, objectHook;
    private readonly Window window;
    private readonly nint handle, desktopRoot;
    private bool disposed, queued;

    /// <summary>注册自有顶层组件；调用方仍负责验证桌面根窗口的 Explorer 生命周期。</summary>
    /// <param name="window">本 UI 线程的非 Topmost、非 WS_CHILD 窗口；需要显示时由 WPF Show。</param>
    /// <param name="desktopRoot">Explorer 桌面的顶层 Progman/WorkerW；诊断允许使用自有替身。</param>
    /// <exception cref="InvalidOperationException">窗口不属于当前进程、不是顶层窗口或 UI 线程不一致。</exception>
    /// <exception cref="Win32Exception">样式设置或共享事件钩子注册失败。</exception>
    internal DesktopLayer(Window window, nint desktopRoot)
    {
        this.window = window; this.desktopRoot = desktopRoot;
        window.Dispatcher.VerifyAccess();
        handle = new WindowInteropHelper(window).EnsureHandle();
        EnsureOwnWindow();
        if (!IsWindow(desktopRoot) || GetAncestor(desktopRoot, 2) != desktopRoot || desktopRoot == handle)
            throw new InvalidOperationException("桌面层必须指定有效的独立顶层锚点。");
        if (GetAncestor(handle, 2) != handle || (GetWindowLongPtrW(handle, -16).ToInt64() & ChildStyle) != 0)
            throw new InvalidOperationException("桌面玻璃组件必须使用自有顶层 HWND。");
        if (window.Topmost || (GetWindowLongPtrW(handle, -20).ToInt64() & TopmostStyle) != 0)
            throw new InvalidOperationException("桌面层不接受 Topmost 组件。");
        if (Layers.ContainsKey(handle) || Layers.Values.Any(layer => layer.window.Dispatcher != window.Dispatcher))
            throw new InvalidOperationException("桌面层不能重复注册，且全部组件必须使用同一 UI Dispatcher。");
        var styles = GetWindowLongPtrW(handle, -20).ToInt64();
        Marshal.SetLastPInvokeError(0);
        var previous = SetWindowLongPtrW(handle, -20, (nint)((styles | 0x08000080L) & ~AppWindowStyle));
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            if (Layers.Count == 0) InstallHooks();
            Layers.Add(handle, this);
            window.Closed += OnClosed;
            window.IsVisibleChanged += OnVisibleChanged;
            Place();
        }
        catch { Dispose(); throw; }
    }

    internal static int RegisteredCount => Layers.Count;
    internal static int HookCount => (foregroundHook != 0 ? 1 : 0) + (objectHook != 0 ? 1 : 0);

    /// <summary>只调整本组件的 Z 序，不激活、不移动、不设置 Topmost；锚点失效时等待宿主重建。</summary>
    /// <exception cref="Win32Exception">自有 HWND 定位失败。</exception>
    internal void Place()
    {
        window.Dispatcher.VerifyAccess();
        if (disposed || !IsWindow(handle) || !IsWindow(desktopRoot)) return;
        EnsureOwnWindow();
        if (AtDesktopLayer()) return;
        // Progman 保持系统桌面底部，先下放自己并检查实际顺序，避免引用附近受保护的 IME/系统前驱。
        // 不将 Explorer 设为 owner：恢复最小化 owned 窗口可能连带提升 owner。
        if (!SetWindowPos(handle, 1, 0, 0, 0, 0, 0x213))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"自有组件桌面层定位失败：HWND={handle:X}，桌面={desktopRoot:X}。");
        if (AtDesktopLayer()) return;
        // 普通替身或非底部 WorkerW 不具备 Progman 的底部约束；此时才需显式插入根之前。
        var above = GetWindow(desktopRoot, 3);
        while (Layers.ContainsKey(above)) above = GetWindow(above, 3);
        if (above != 0 && (GetWindowLongPtrW(above, -20).ToInt64() & TopmostStyle) != 0) above = 0;
        if (!SetWindowPos(handle, above, 0, 0, 0, 0, 0x213))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"自有组件桌面根前定位失败：HWND={handle:X}，前驱={above:X}。");
        if (!AtDesktopLayer()) throw new InvalidOperationException("自有组件未能进入桌面根上方的窗口层。");
    }

    private bool AtDesktopLayer()
    {
        // 同产品组件可有任意内部顺序；忽略隐藏辅助窗，避免组件之间反复提升。
        var below = GetWindow(handle, 2);
        while (below != 0 && (Layers.ContainsKey(below) || (!IsWindowVisible(below) && below != desktopRoot))) below = GetWindow(below, 2);
        return below == desktopRoot;
    }

    private static void InstallHooks()
    {
        foregroundHook = SetWinEventHook(ForegroundEvent, MinimizeEndEvent, 0, EventCallback, 0, 0, 0);
        if (foregroundHook == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "前台事件钩子注册失败。");
        objectHook = SetWinEventHook(ShowEvent, ReorderEvent, 0, EventCallback, 0, 0, 0);
        if (objectHook == 0)
        {
            var error = Marshal.GetLastPInvokeError();
            UnhookWinEvent(foregroundHook); foregroundHook = 0;
            throw new Win32Exception(error, "桌面层事件钩子注册失败。");
        }
    }

    private static void OnWinEvent(nint hook, uint kind, nint target, int objectId, int childId, uint thread, uint time)
    {
        if (objectId != 0 || childId != 0) return;
        if (kind == ForegroundEvent)
        {
            foreach (var layer in Layers.Values) layer.Queue();
            return;
        }
        // 自身 SetWindowPos 产生的 reorder 不入队；只观察桌面根重排与自有窗口显示变化。
        if (Layers.TryGetValue(target, out var own))
        {
            if (kind is ShowEvent or HideEvent or MinimizeStartEvent or MinimizeEndEvent) own.Queue();
            return;
        }
        if (kind == ReorderEvent)
            foreach (var layer in Layers.Values)
                if (target == layer.desktopRoot) layer.Queue();
    }

    private void Queue()
    {
        if (disposed || queued || window.Dispatcher.HasShutdownStarted) return;
        queued = true;
        window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            queued = false;
            if (disposed || !IsWindow(handle) || !window.IsVisible || !IsWindow(desktopRoot)) return;
            EnsureOwnWindow();
            // Win+D 可只隐藏 HWND 而 WPF 仍需显示；WPF Hide/Close 的 IsVisible=false 不得恢复。
            if (!IsWindowVisible(handle) || IsIconic(handle)) ShowWindow(handle, 4); // SW_SHOWNOACTIVATE
            Place();
        }));
    }

    private void EnsureOwnWindow()
    {
        GetWindowThreadProcessId(handle, out var pid);
        if (!IsWindow(handle) || pid != (uint)Environment.ProcessId)
            throw new InvalidOperationException("只允许调整当前进程的组件窗口。");
    }
    private void OnClosed(object? sender, EventArgs args) => Dispose();
    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs args) => Queue();

    /// <summary>先注销恢复资格，再卸载最后一组共享钩子；不关闭窗口或更改 Explorer。</summary>
    public void Dispose()
    {
        window.Dispatcher.VerifyAccess();
        if (disposed) return;
        disposed = true;
        window.Closed -= OnClosed; window.IsVisibleChanged -= OnVisibleChanged;
        if (Layers.TryGetValue(handle, out var layer) && ReferenceEquals(layer, this)) Layers.Remove(handle);
        if (Layers.Count != 0) return;
        if (objectHook != 0)
        {
            if (!UnhookWinEvent(objectHook)) throw new Win32Exception(Marshal.GetLastPInvokeError(), "桌面事件钩子释放失败。");
            objectHook = 0;
        }
        if (foregroundHook != 0)
        {
            if (!UnhookWinEvent(foregroundHook)) throw new Win32Exception(Marshal.GetLastPInvokeError(), "前台事件钩子释放失败。");
            foregroundHook = 0;
        }
    }

    private delegate void WinEventProc(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWinEventHook(uint minimum, uint maximum, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
