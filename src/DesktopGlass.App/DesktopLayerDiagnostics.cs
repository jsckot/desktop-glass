using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopGlass.App.Windows;

namespace DesktopGlass.App;

/// <summary>仅用本进程离屏窗口验证桌面层级，不获取租约或操作 Explorer。</summary>
internal static class DesktopLayerDiagnostics
{
    /// <summary>运行原生层级与释放检查，并将证据写入调用方指定路径。</summary>
    /// <param name="args">必须包含 --output 和输出 JSON 路径。</param>
    /// <returns>全部检查通过为 0，否则为 1。</returns>
    internal static int Run(string[] args)
    {
        var index = Array.IndexOf(args, "--output");
        if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("需要 --output 路径。");
        var output = WidgetDiagnostics.CheckedOutput(args[index + 1]);
        var passed = new List<string>();
        var foregroundTrace = new List<object>();
        void TraceForeground(string stage)
        {
            var handle = GetForegroundWindow(); Native.GetWindowThreadProcessId(handle, out var process);
            foregroundTrace.Add(new { Stage = stage, Handle = handle.ToInt64(), Process = process, Own = process == Environment.ProcessId });
        }
        var windows = new List<Window>();
        Application? app = null;
        DesktopLayer? layer = null, second = null;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            passed.Add(name);
        }
        Window Create(string name)
        {
            var window = new Window { Title = name, Width = 80, Height = 60, Left = -30000,
                Top = -30000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            windows.Add(window); window.Show();
            // ShowInTaskbar=false 的 WPF 默认共用隐藏 owner；替身必须模拟互不拥有的真实桌面与应用。
            Marshal.SetLastPInvokeError(0);
            var previous = SetWindowLongPtrW(new WindowInteropHelper(window).Handle, -8, 0);
            if (previous == 0 && Marshal.GetLastPInvokeError() != 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
            return window;
        }
        nint Handle(Window window) => new WindowInteropHelper(window).Handle;
        try
        {
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var anchor = Create("DesktopLayer 自有桌面锚点");
            var ordinary = Create("DesktopLayer 自有普通应用");
            var widget = Create("DesktopLayer 自有组件");
            var anchorHandle = Handle(anchor); var appHandle = Handle(ordinary); var widgetHandle = Handle(widget);
            // 刻意构造错误层级，让诊断在没有桌面层管理器时必然失败。
            Position(appHandle, 0); Position(anchorHandle, appHandle); Position(widgetHandle, 0);
            var foreground = GetForegroundWindow();
            TraceForeground("before-layer");
            layer = new DesktopLayer(widget, anchorHandle); layer.Place();
            Check(Above(appHandle, widgetHandle) && Above(widgetHandle, anchorHandle), "组件位于普通应用下、桌面上");
            Check(GetForegroundWindow() == foreground, "构造和定位不抢前台");
            Check((GetWindowLongPtrW(widgetHandle, -20).ToInt64() & 0x08000080L) == 0x08000080L,
                "自有组件使用 TOOLWINDOW 与 NOACTIVATE");
            Check((GetWindowLongPtrW(widgetHandle, -20).ToInt64() & 8L) == 0, "自有组件从未成为 Topmost");
            Check(GetWindow(widgetHandle, 4) != anchorHandle &&
                (GetWindowLongPtrW(widgetHandle, -16).ToInt64() & 0x40000000L) == 0, "组件为非 child 顶层窗口且不使用桌面 owner");
            var other = Create("DesktopLayer 第二组件");
            second = new DesktopLayer(other, anchorHandle); second.Place(); layer.Place();
            Check(Above(appHandle, widgetHandle) && Above(appHandle, Handle(other)) &&
                Above(widgetHandle, anchorHandle) && Above(Handle(other), anchorHandle), "多个组件共享桌面层且不高于普通应用");
            Check(DesktopLayer.RegisteredCount == 2 && DesktopLayer.HookCount == 2, "多个组件共享两个全局事件钩子");
            // 模拟系统原生隐藏；不发送 Win+D，不移动鼠标，不操作其他进程窗口。
            ShowWindow(widgetHandle, 0); Wait();
            Check(IsWindowVisible(widgetHandle), "仍注册且需显示的组件在原生隐藏后恢复");
            Check(Above(appHandle, anchorHandle), "原生隐藏恢复不提升桌面 owner");
            TraceForeground("before-native-minimize");
            // SW_MINIMIZE(6) 会主动激活下一顶层窗，不能用它检验“不抢前台”；7 保留最小化但不激活。
            ShowWindow(widgetHandle, 7);
            TraceForeground("after-native-minimize");
            Check(Above(appHandle, anchorHandle), "原生最小化动作不提升桌面 owner");
            Wait();
            TraceForeground("after-minimize-recovery");
            Check(IsWindowVisible(widgetHandle) && !IsIconic(widgetHandle), "仍注册且需显示的组件在原生最小化后恢复");
            Check(Above(appHandle, anchorHandle), "原生最小化恢复不提升桌面 owner");
            Position(widgetHandle, 0); layer.Place();
            Check(Above(appHandle, widgetHandle) && Above(widgetHandle, anchorHandle), "外部层级变化后可重新落入桌面层");
            widget.Hide(); Wait();
            Check(!IsWindowVisible(widgetHandle), "WPF 主动隐藏不被复活");
            widget.Show(); Wait();
            Check(Above(appHandle, widgetHandle), "WPF 再次显示后恢复桌面层");
            Check(GetWindow(widgetHandle, 4) != anchorHandle, "WPF Hide/Show 不引入桌面 owner");
            second.Dispose(); second = null;
            Check(DesktopLayer.RegisteredCount == 1 && DesktopLayer.HookCount == 2, "释放部分组件保留共享钩子");
            ShowWindow(widgetHandle, 0);
            layer.Dispose(); layer = null;
            Check(DesktopLayer.RegisteredCount == 0 && DesktopLayer.HookCount == 0, "释放最后组件卸载所有钩子");
            Wait();
            Check(!IsWindowVisible(widgetHandle), "挂起恢复回调在注销后不会复活窗口");
            TraceForeground("before-final-focus-check");
            Check(GetForegroundWindow() == foreground, "所有非激活窗口测试保持用户前台");
            widget.Close(); Wait();
            Check(!IsWindow(widgetHandle), "关闭后没有残留自有 HWND");
            var closing = Create("DesktopLayer 关闭注销检查");
            using var closingLayer = new DesktopLayer(closing, anchorHandle);
            ShowWindow(Handle(closing), 0); closing.Close(); Wait();
            Check(DesktopLayer.RegisteredCount == 0 && DesktopLayer.HookCount == 0, "窗口直接关闭时注销且不复活");
            File.WriteAllText(output, JsonSerializer.Serialize(new { Passed = true, Tests = passed,
                ForegroundTrace = foregroundTrace, LeaseAcquired = false, RealDesktopItemsCollected = 0, RealWinDTested = false }));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { Passed = false, Tests = passed, ForegroundTrace = foregroundTrace, Error = error.ToString() }));
            return 1;
        }
        finally
        {
            second?.Dispose(); layer?.Dispose();
            foreach (var window in windows) window.Close();
            app?.Shutdown();
        }
    }

    private static bool Above(nint upper, nint lower)
    {
        for (var current = GetWindow(upper, 2); current != 0; current = GetWindow(current, 2))
            if (current == lower) return true;
        return false;
    }
    private static void Position(nint window, nint after)
    {
        if (!SetWindowPos(window, after, 0, 0, 0, 0, 0x13))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
    }
    private static void Wait()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
}
