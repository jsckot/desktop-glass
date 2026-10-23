using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopGlass.App.UI;
using DesktopGlass.App.Windows;

namespace DesktopGlass.App;

/// <summary>隔离的组件预览和桌面宿主诊断；从不创建桌面租约、不隐藏图标、不保存用户配置。</summary>
internal static class WidgetDiagnostics
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>运行明确指定的组件诊断入口，所有输出限制在当前项目的 .codex 下。</summary>
    /// <param name="args">包含一个组件模式、--output，以及可选 --hold-seconds；--widget-empty 复现默认空分组。</param>
    /// <returns>结构检查及原生桌面状态保持时为 0；参数或检查失败时为 1。</returns>
    internal static int Run(string[] args)
    {
        string? output = null;
        Application? app = null;
        GroupWidget? widget = null;
        WidgetHost? host = null;
        Exception? failure = null;
        NativeDesktopState? before = null, during = null, after = null;
        WindowState? shownWindow = null;
        ScreenshotState? screenshot = null;
        var counts = new Dictionary<string, int>();
        var commands = new List<object>();
        var lifecycle = new Dictionary<string, int>();
        var presentationTimeline = new List<object>();
        HwndSource? hookedSource = null;
        HwndSourceHook? diagnosticHook = null;
        var empty = args.Contains("--widget-empty");
        var hostMode = args.Contains("--widget-host-test");
        var mode = hostMode ? "widget-host-test" : "widget-preview";
        var ready = false;
        try
        {
            output = CheckedOutput(Argument(args, "--output") ?? throw new ArgumentException("组件诊断需要 --output。"));
            if (hostMode == args.Contains("--widget-preview")) throw new ArgumentException("必须且只能指定一个组件诊断模式。");
            var holdText = Argument(args, "--hold-seconds");
            var hold = holdText is null ? (hostMode ? 10 : 20) : int.Parse(holdText, CultureInfo.InvariantCulture);
            hold = Math.Clamp(hold, hostMode ? 1 : 20, 600);
            before = NativeState();
            var display = Native.Displays().FirstOrDefault(d => d.Primary) ?? throw new InvalidOperationException("未找到主显示器。");
            var catalog = new DesktopCatalog(); var entries = empty ? Array.Empty<DesktopEntry>() : catalog.Scan().Take(6).ToArray();
            var sample = new Group { Name = empty ? "常用工具" : "开发工具", DisplayId = display.Id, X = empty ? 36 : 84, Y = empty ? 96 : 100, Width = 350, Height = 300,
                Tint = empty ? .32 : .38, Radius = empty ? 24 : 26, Entries = entries.Select(e => e.Path).ToList() };
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var scale = display.Scale;
            Rect? gestureStart = null;
            void Record(string name, object? details = null)
            {
                counts[name] = counts.GetValueOrDefault(name) + 1;
                commands.Add(new { Command = name, Details = details, Time = DateTimeOffset.Now });
            }
            Rect CurrentBounds() => host?.GetBounds() ?? WidgetHost.ReadWindowBounds(new WindowInteropHelper(widget!).Handle);
            void ApplyBounds(Rect bounds)
            {
                if (host is not null) { host.SetBounds(bounds, sample.Radius, scale); return; }
                var handle = new WindowInteropHelper(widget!).Handle;
                widget!.Width = bounds.Width / scale; widget.Height = bounds.Height / scale;
                widget.Left = bounds.X / scale; widget.Top = bounds.Y / scale;
                if (!Native.SetWindowPos(handle, 0, (int)Math.Round(bounds.X), (int)Math.Round(bounds.Y),
                    (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height), 0x10 | 0x20 | 0x40))
                    throw new InvalidOperationException("预览窗口定位失败。");
                WidgetHost.RoundOwnWindow(handle, (int)Math.Round(bounds.Width), (int)Math.Round(bounds.Height), sample.Radius, scale);
                widget.UpdateLayout();
            }
            void WriteReady()
            {
                if (output is null || widget is null || !widget.IsVisible) return;
                shownWindow = WindowEvidence(widget, host);
                File.WriteAllText(Path.ChangeExtension(output, "ready.json"), JsonSerializer.Serialize(new
                { Ready = ready, Mode = mode, Pid = Environment.ProcessId, Window = shownWindow, Counts = counts, Commands = commands,
                    NativeBefore = before, NativeDuring = during, Screenshot = screenshot, SampleOnly = true, Empty = empty,
                    Lifecycle = lifecycle, PresentationTimeline = presentationTimeline }, JsonOptions));
            }
            var callbacks = new WidgetCallbacks
            {
                Open = path => { Record("Open", new { Path = path, Executed = false }); WriteReady(); },
                Collect = (paths, index) => { Record("Collect", new { Paths = paths, Index = index, Executed = false }); WriteReady(); return true; },
                Release = path => { Record("Release", new { Path = path, Executed = false }); WriteReady(); },
                Edit = () => { Record("Edit", new { Executed = false }); WriteReady(); },
                Remove = () => { Record("Remove", new { Executed = false }); WriteReady(); },
                Create = () => { Record("Create", new { Executed = false }); WriteReady(); },
                ToggleLock = () => { Record("ToggleLock", new { Executed = false }); WriteReady(); },
                Exit = () => { Record("Exit"); app.Shutdown(); },
                MoveBy = (dx, dy) =>
                {
                    var bounds = CurrentBounds(); gestureStart ??= bounds;
                    bounds.Offset(dx * scale, dy * scale); ApplyBounds(bounds); Record("MoveBy", new { DxDip = dx, DyDip = dy }); WriteReady();
                },
                ResizeBy = (dw, dh) =>
                {
                    var bounds = CurrentBounds(); gestureStart ??= bounds;
                    bounds.Width = Math.Clamp(bounds.Width + dw * scale, 240 * scale, Math.Min(2000 * scale, display.Width));
                    bounds.Height = Math.Clamp(bounds.Height + dh * scale, 160 * scale, Math.Min(1600 * scale, display.Height));
                    ApplyBounds(bounds); Record("ResizeBy", new { DwDip = dw, DhDip = dh }); WriteReady();
                },
                CommitBounds = canceled =>
                {
                    if (canceled && gestureStart is { } original) ApplyBounds(original);
                    gestureStart = null; Record("CommitBounds", new { Canceled = canceled }); WriteReady();
                }
            };
            widget = new GroupWidget(sample, display, entries, catalog, false, false, callbacks)
            { Title = hostMode ? "Desktop Glass · 桌面组件自检" : "Desktop Glass · 独立组件预览" };
            void Observe(string stage)
            {
                if (widget is null) return;
                lifecycle[stage] = lifecycle.GetValueOrDefault(stage) + 1;
                if (presentationTimeline.Count < 16)
                    presentationTimeline.Add(new { Stage = stage, Time = DateTimeOffset.UtcNow, Presentation = PresentationEvidence(widget) });
            }
            widget.SourceInitialized += (_, _) =>
            {
                Observe("SourceInitialized");
                hookedSource = HwndSource.FromHwnd(new WindowInteropHelper(widget).Handle);
                diagnosticHook = (nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
                {
                    var name = message switch { 0x0F => "WM_PAINT", 0x05 => "WM_SIZE", 0x18 => "WM_SHOWWINDOW",
                        0x47 => "WM_WINDOWPOSCHANGED", 0x02E0 => "WM_DPICHANGED", _ => null };
                    if (name is not null) lifecycle[name] = lifecycle.GetValueOrDefault(name) + 1;
                    return 0; // 仅观察自己的 HWND，绝不标记 handled 或改变消息结果。
                };
                hookedSource?.AddHook(diagnosticHook);
            };
            widget.Loaded += (_, _) => Observe("Loaded");
            widget.ContentRendered += (_, _) => Observe("ContentRendered");
            widget.IsVisibleChanged += (_, _) => Observe("IsVisibleChanged");
            widget.Unloaded += (_, _) => Observe("Unloaded");
            widget.Closed += (_, _) => app.Shutdown();
            app.DispatcherUnhandledException += (_, e) => { failure = e.Exception; e.Handled = true; app.Shutdown(1); };
            var bounds = new Rect(display.Left + sample.X * scale, display.Top + sample.Y * scale, sample.Width * scale, sample.Height * scale);
            if (hostMode) host = new WidgetHost(widget, bounds, sample.Radius, scale);
            else { widget.ShowInTaskbar = true; widget.ShowActivated = true; widget.Show(); ApplyBounds(bounds); }
            Observe("AfterShowAndAttach");
            var observedFiveSeconds = false;
            var lifetime = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += (_, _) =>
            {
                if (host is not null && !host.Alive) throw new InvalidOperationException("诊断期间 Explorer 已重建或组件宿主失效。");
                if (!ready)
                {
                    Observe("FirstTimer"); during = NativeState(); shownWindow = WindowEvidence(widget, host);
                    screenshot = CaptureOwnWindow(new WindowInteropHelper(widget).Handle, Path.ChangeExtension(output, "window.png"));
                    ready = true;
                }
                if (!observedFiveSeconds && lifetime.Elapsed >= TimeSpan.FromSeconds(5)) { observedFiveSeconds = true; Observe("FiveSeconds"); }
                WriteReady();
                if (lifetime.Elapsed >= TimeSpan.FromSeconds(hold)) { timer.Stop(); app.Shutdown(); }
            };
            timer.Start(); app.Run(); timer.Stop();
        }
        catch (Exception e) { failure = e; }
        finally
        {
            try
            {
                if (hookedSource is { IsDisposed: false } && diagnosticHook is not null) hookedSource.RemoveHook(diagnosticHook);
                host?.Dispose(); if (widget?.IsVisible == true) widget.Close(); app?.Shutdown();
            }
            catch (Exception e) { failure ??= e; }
            try { after = NativeState(); }
            catch (Exception e) { failure ??= e; }
        }
        var unchanged = before is not null && before == during && before == after;
        var visible = shownWindow is { Visible: true, ActualWidthDip: > 0, ActualHeightDip: > 0 };
        var passed = failure is null && unchanged && visible && ready;
        if (output is not null)
            File.WriteAllText(output, JsonSerializer.Serialize(new
            { Passed = passed, StructuralChecksOnly = true, Mode = mode, Pid = Environment.ProcessId, Error = failure?.ToString(),
                NativeUnchanged = unchanged, NativeBefore = before, NativeDuring = during, NativeAfter = after,
                Window = shownWindow, Screenshot = screenshot, Counts = counts, Commands = commands, Empty = empty,
                Lifecycle = lifecycle, PresentationTimeline = presentationTimeline,
                ManagedLifecycleObserved = lifecycle.GetValueOrDefault("Loaded") > 0 && lifecycle.GetValueOrDefault("ContentRendered") > 0,
                RealFileActionsExecuted = false, UserConfigurationSaved = false, FinalDesktopCompositionVerified = false }, JsonOptions));
        return passed ? 0 : 1;
    }

    private static WindowState WindowEvidence(GroupWidget widget, WidgetHost? host)
    {
        widget.UpdateLayout(); var handle = new WindowInteropHelper(widget).Handle;
        return new WindowState(handle.ToInt64(), Native.GetParent(handle).ToInt64(), Native.IsWindowVisible(handle),
            (long)Native.GetWindowLongPtr(handle, -16), BoundsEvidence.Of(WidgetHost.ReadWindowBounds(handle)),
            BoundsEvidence.Of(WidgetHost.ReadClientScreenBounds(handle)), widget.ActualWidth, widget.ActualHeight,
            WidgetHost.WindowDpi(handle), VisualTreeHelper.GetDpi(widget).DpiScaleX, host?.Alive, PresentationEvidence(widget));
    }

    private static object PresentationEvidence(GroupWidget widget)
    {
        var errors = new List<string>(); object? wpf = null, native = null;
        var handle = new WindowInteropHelper(widget).Handle;
        try
        {
            var source = handle == 0 ? null : HwndSource.FromHwnd(handle);
            var target = source?.CompositionTarget; var content = widget.Content as FrameworkElement;
            wpf = new { widget.IsInitialized, widget.IsLoaded, widget.IsVisible, Visibility = widget.Visibility.ToString(), widget.Opacity,
                widget.IsMeasureValid, widget.IsArrangeValid, widget.ActualWidth, widget.ActualHeight,
                SourcePresent = source is not null, SourceDisposed = source?.IsDisposed,
                RootVisualType = source?.RootVisual?.GetType().FullName, RootVisualIsWidget = ReferenceEquals(source?.RootVisual, widget),
                PresentationSourceMatches = source is not null && ReferenceEquals(PresentationSource.FromVisual(widget), source),
                CompositionTargetPresent = target is not null, RenderMode = target?.RenderMode.ToString(),
                UsesPerPixelOpacity = target?.UsesPerPixelOpacity, RenderTier = RenderCapability.Tier >> 16,
                ContentType = content?.GetType().FullName, ContentLoaded = content?.IsLoaded, ContentVisible = content?.IsVisible,
                ContentOpacity = content?.Opacity, ContentWidth = content?.ActualWidth, ContentHeight = content?.ActualHeight };
        }
        catch (Exception error) { errors.Add("WPF:" + error); }
        try { if (handle != 0 && Native.IsWindow(handle)) native = NativePresentation(handle); }
        catch (Exception error) { errors.Add("Native:" + error); }
        return new { Wpf = wpf, Native = native, Errors = errors };
    }

    private static object NativePresentation(nint handle)
    {
        Native.GetWindowThreadProcessId(handle, out var pid);
        if (pid != Environment.ProcessId) throw new InvalidOperationException("绘制诊断只检查自己的组件 HWND。");
        var bounds = WidgetHost.ReadWindowBounds(handle); var parent = Native.GetParent(handle);
        var center = new Native.Point { X = (int)(bounds.X + bounds.Width / 2), Y = (int)(bounds.Y + bounds.Height / 2) };
        var parentCenter = center; nint parentHit = 0;
        if (parent != 0 && Native.ScreenToClient(parent, ref parentCenter)) parentHit = ChildWindowFromPointEx(parent, parentCenter, 1 | 2 | 4);
        var screenHit = WindowFromPoint(center);
        var chain = new List<object>(); var cursor = handle;
        for (var i = 0; cursor != 0 && i < 8; i++) { chain.Add(WindowProbe(cursor)); cursor = Native.GetParent(cursor); }
        var above = new List<object>(); cursor = GetWindow(handle, 3);
        for (var i = 0; cursor != 0 && i < 12; i++) { above.Add(WindowProbe(cursor)); cursor = GetWindow(cursor, 3); }
        var siblings = new List<object>(); cursor = parent == 0 ? 0 : GetWindow(parent, 5);
        for (var i = 0; cursor != 0 && i < 20; i++) { siblings.Add(WindowProbe(cursor)); cursor = GetWindow(cursor, 2); }
        object? region = null; var scratch = CreateRectRgn(0, 0, 0, 0);
        if (scratch != 0)
        {
            try
            {
                var kind = GetWindowRgn(handle, scratch); var boxKind = GetRgnBox(scratch, out var box);
                region = new { Kind = kind, BoxKind = boxKind, Bounds = new { box.Left, box.Top, box.Right, box.Bottom },
                    CenterIncluded = kind > 0 ? (bool?)PtInRegion(scratch, (int)(bounds.Width / 2), (int)(bounds.Height / 2)) : null };
            }
            finally { DeleteObject(scratch); }
        }
        object? clip = null; var dc = GetDCEx(handle, 0, 0x02 | 0x10);
        if (dc != 0)
        {
            try { var kind = GetClipBox(dc, out var box); clip = new { Kind = kind, Bounds = new { box.Left, box.Top, box.Right, box.Bottom }, Scope = "own client DC, DCX_CACHE|DCX_CLIPSIBLINGS; no drawing" }; }
            finally { ReleaseDC(handle, dc); }
        }
        return new { ParentChain = chain, SiblingsAboveOwnWindow = above, SiblingsTopToBottom = siblings,
            CenterScreenPoint = new { center.X, center.Y }, ParentClientCenter = new { parentCenter.X, parentCenter.Y },
            ParentCenterHit = parentHit.ToInt64(), ParentCenterHitIsOwnWindow = parentHit == handle,
            ScreenCenterHit = screenHit.ToInt64(), ScreenCenterHitIsOwnWindow = screenHit == handle,
            ScreenHit = screenHit == 0 ? null : WindowProbe(screenHit), Region = region, Clip = clip };
    }

    private static object WindowProbe(nint handle)
    {
        var name = new StringBuilder(128); Native.GetClassName(handle, name, name.Capacity);
        Native.GetWindowThreadProcessId(handle, out var pid);
        var rect = WidgetHost.ReadWindowBounds(handle); var hr = DwmGetWindowAttribute(handle, 14, out var cloaked, sizeof(uint));
        return new { Handle = handle.ToInt64(), Parent = Native.GetParent(handle).ToInt64(), Class = name.ToString(), Pid = pid,
            Visible = Native.IsWindowVisible(handle), Iconic = IsIconic(handle), Style = (long)Native.GetWindowLongPtr(handle, -16),
            ExtendedStyle = (long)Native.GetWindowLongPtr(handle, -20), Bounds = BoundsEvidence.Of(rect),
            DwmCloakedHResult = hr, DwmCloaked = hr >= 0 ? (uint?)cloaked : null };
    }
    private static NativeDesktopState NativeState()
    {
        var list = Native.DesktopListView();
        if (list == 0 || !Native.IsWindow(list)) throw new InvalidOperationException("原生桌面图标视图不可用。");
        Native.GetWindowThreadProcessId(list, out var pid);
        using var explorer = Process.GetProcessById(checked((int)pid));
        if (SendMessageTimeoutW(list, 0x1004, 0, 0, 2, 1500, out var count) == 0)
            throw new InvalidOperationException("原生桌面图标计数读取超时。");
        return new NativeDesktopState(list.ToInt64(), Native.GetParent(list).ToInt64(), Native.IsWindowVisible(list),
            (long)Native.GetWindowLongPtr(list, -16), (long)Native.GetWindowLongPtr(list, -20), count.ToInt64(), pid,
            explorer.StartTime.ToUniversalTime().Ticks);
    }

    private static ScreenshotState CaptureOwnWindow(nint handle, string path)
    {
        Native.GetWindowThreadProcessId(handle, out var pid);
        if (pid != Environment.ProcessId) throw new InvalidOperationException("诊断截图只允许读取自己的窗口。");
        var bounds = WidgetHost.ReadWindowBounds(handle);
        using var bitmap = new System.Drawing.Bitmap((int)bounds.Width, (int)bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.Clear(System.Drawing.Color.Magenta);
        var dc = graphics.GetHdc(); bool success;
        try { success = PrintWindow(handle, dc, 0); }
        finally { graphics.ReleaseHdc(dc); }
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        var colors = new HashSet<int>();
        for (var y = 0; y < bitmap.Height; y += 8)
            for (var x = 0; x < bitmap.Width; x += 8) colors.Add(bitmap.GetPixel(x, y).ToArgb());
        return new ScreenshotState(path, "PrintWindow-own-HWND", success, bitmap.Width, bitmap.Height, colors.Count, false);
    }

    private static string? Argument(string[] args, string key)
    {
        var index = Array.IndexOf(args, key);
        if (index < 0) return null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException(key + " 缺少参数。");
        return args[index + 1];
    }

    internal static string CheckedOutput(string output)
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DesktopGlass.slnx"))) directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("诊断必须从本项目目录运行。");
        var allowed = Path.GetFullPath(Path.Combine(directory.FullName, ".codex")) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(output);
        if (!full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || Path.GetExtension(full) != ".json")
            throw new ArgumentException("--output 必须是本项目 .codex 下的 JSON 文件。");
        var parent = new DirectoryInfo(Path.GetDirectoryName(full)!);
        for (var current = parent; current is not null && current.FullName.Length >= allowed.Length - 1; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("诊断输出目录不能经过重解析点。");
        foreach (var target in new[] { full, Path.ChangeExtension(full, "ready.json"), Path.ChangeExtension(full, "window.png") })
            if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("诊断输出及辅助证据不能覆盖重解析文件。");
        Directory.CreateDirectory(parent.FullName); return full;
    }

    private sealed record NativeDesktopState(long Handle, long Parent, bool Visible, long Style, long ExtendedStyle, long ItemCount, uint ExplorerPid, long ExplorerStart);
    private sealed record BoundsEvidence(double X, double Y, double Width, double Height)
    { internal static BoundsEvidence Of(Rect value) => new(value.X, value.Y, value.Width, value.Height); }
    private sealed record WindowState(long Handle, long Parent, bool Visible, long Style, BoundsEvidence ScreenBounds,
        BoundsEvidence ClientScreenBounds, double ActualWidthDip, double ActualHeightDip, uint Dpi, double WpfScale, bool? HostAlive, object Presentation);
    private sealed record ScreenshotState(string Path, string Type, bool ApiSucceeded, int Width, int Height, int SampledColorCount, bool FinalDesktopComposition);
    [DllImport("user32.dll")] private static extern nint ChildWindowFromPointEx(nint parent, Native.Point point, uint flags);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Native.Point point);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint handle);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint handle, uint attribute, out uint value, int size);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint handle, nint region);
    [DllImport("gdi32.dll")] private static extern int GetRgnBox(nint region, out Native.Rect box);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PtInRegion(nint region, int x, int y);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
    [DllImport("user32.dll")] private static extern nint GetDCEx(nint handle, nint region, uint flags);
    [DllImport("gdi32.dll")] private static extern int GetClipBox(nint dc, out Native.Rect box);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint handle, nint dc);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SendMessageTimeoutW(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PrintWindow(nint window, nint dc, uint flags);
}
