using DesktopGlass.App;
using DesktopGlass.App.UI;
using DesktopGlass.App.Windows;
using System.Text.Json;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;

/// <summary>在自有彩色背景上验证生产分组的最终屏幕像素；不使用 PrintWindow 或离屏图替代合成结果。</summary>
internal static class CompositionTests
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--output")
        { Console.Error.WriteLine("需要 --output <项目 .codex 下的证据目录>；测试需交互式桌面。"); return 2; }
        var output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var checks = new List<object>();
        Window? background = null; WidgetHost? host = null; WidgetHost? secondHost = null;
        try
        {
            var display = Native.Displays().First(d => d.Primary);
            var scene = new Grid { Background = Brushes.Coral };
            scene.ColumnDefinitions.Add(new ColumnDefinition()); scene.ColumnDefinitions.Add(new ColumnDefinition());
            var blue = new Border { Background = Brushes.SteelBlue }; Grid.SetColumn(blue, 1); scene.Children.Add(blue);
            background = new Window { Title = "Desktop Glass 合成回归背景", WindowStyle = WindowStyle.None,
                Width = 600, Height = 450, Left = 200, Top = 150, Topmost = true, ShowInTaskbar = false,
                ShowActivated = false, Content = scene };
            background.Show();
            var group = new Group { Name = "常驻分组验证", DisplayId = display.Id, Width = 350, Height = 300, Tint = .18 };
            var callbacks = new WidgetCallbacks { Open = _ => { }, Collect = (_, _) => false, Release = _ => { },
                Edit = () => { }, Remove = () => { }, Create = () => { }, ToggleLock = () => { }, Exit = () => { },
                MoveBy = (_, _) => { }, ResizeBy = (_, _) => { }, CommitBounds = _ => { } };
            var widget = new GroupWidget(group, display, [], new DesktopCatalog(), false, false, callbacks);
            host = new WidgetHost(widget, new Rect(260 * display.Scale, 190 * display.Scale, 350 * display.Scale, 300 * display.Scale),
                12, display.Scale, true);
            Wait(800); CheckInk("initial-compact", 14, 12, 115, 22);
            var auxiliary = OwnedVisibleWindows().Where(h => h != host.Handle && h != new WindowInteropHelper(background).Handle).ToArray();
            Require(auxiliary.Length == 1, "一个组件仅创建一个可见背景层");
            for (var i = 0; i < 3; i++)
            {
                host.SetExpanded(true); Wait(300); CheckInk("expanded-" + i, 20, 15, 160, 24);
                if (i == 0)
                {
                    using var pixels = Capture("real-blur-seam");
                    var backBounds = WidgetHost.ReadClientScreenBounds(new WindowInteropHelper(background).Handle);
                    var seam = (int)(backBounds.Left + backBounds.Width / 2 - host.GetBounds().Left);
                    var row = (int)(140 * display.Scale);
                    var largestStep = 0;
                    for (var x = seam - 20; x < seam + 20; x++)
                        largestStep = Math.Max(largestStep, Math.Abs(pixels.GetPixel(x + 1, row).R - pixels.GetPixel(x, row).R));
                    var contrast = Math.Abs(pixels.GetPixel(seam - 20, row).R - pixels.GetPixel(seam + 20, row).R);
                    Require(contrast > 50 && largestStep < 40, $"彩块交界真实模糊：总差{contrast}，最大相邻差{largestStep}");
                }
                host.SetExpanded(false); Wait(550); CheckInk("collapsed-" + i, 14, 12, 115, 22);
            }
            var before = Sample(10, 75);
            scene.Background = Brushes.LightGreen; Wait(500);
            var after = Sample(10, 75);
            Require(Math.Abs(before.R - after.R) + Math.Abs(before.G - after.G) + Math.Abs(before.B - after.B) > 70,
                "背景变化传入实时玻璃");
            background.Activate();
            // 两个自有测试窗均为 Topmost；背景激活会覆盖组件，需仅恢复测试叠放而不激活组件。
            Native.SetWindowPos(host.Handle, -1, 0, 0, 0, 0, 0x13);
            Wait(500);
            Require(GetForegroundWindow() == new WindowInteropHelper(background).Handle, "非激活组件不抢焦点");
            CheckInk("inactive-compact", 14, 12, 115, 22);
            scene.Background = Brushes.Coral;
            host.UpdatePresentation(new Size(400 * display.Scale, 310 * display.Scale), new Point(280 * display.Scale, 210 * display.Scale));
            host.SetExpanded(true); Wait(300); CheckInk("moved-expanded", 20, 15, 160, 24);
            foreach (var handle in auxiliary)
                Require(WidgetHost.ReadWindowBounds(handle) == host.GetBounds(), "背景层跟随物理移动和尺寸");
            host.SetExpanded(false); Wait(300); CheckInk("moved-compact", 14, 12, 115, 22);
            foreach (var handle in auxiliary)
                Require(WidgetHost.ReadWindowBounds(handle) == host.GetBounds(), "背景层跟随收起尺寸");
            widget.Hide(); Wait(300);
            Require(auxiliary.All(h => !Native.IsWindowVisible(h)), "Hide不遗留玻璃背景");
            widget.Show(); Wait(500); CheckInk("reshown-compact", 14, 12, 115, 22);
            widget.WindowState = WindowState.Minimized; Wait(300);
            Require(auxiliary.All(h => !Native.IsWindowVisible(h)), "最小化不遗留玻璃背景");
            widget.WindowState = WindowState.Normal; Wait(500); CheckInk("restored-compact", 14, 12, 115, 22);
            var contentHandle = host.Handle;
            host.Close(); host = null;
            Wait(250);
            Require(!Native.IsWindow(contentHandle) && auxiliary.All(h => !Native.IsWindow(h)), "关闭释放内容和背景窗口");
            var entries = Enumerable.Range(1, 4).Select(i =>
            {
                var file = Path.Combine(output, "入口" + i + ".ico");
                using var bitmap = new System.Drawing.Bitmap(32, 32);
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                { graphics.Clear(System.Drawing.Color.White); graphics.FillRectangle(System.Drawing.Brushes.MidnightBlue, 9, 9, 14, 14); }
                var nativeIcon = bitmap.GetHicon();
                try { using var icon = System.Drawing.Icon.FromHandle(nativeIcon); using var stream = File.Create(file); icon.Save(stream); }
                finally { Native.DestroyIcon(nativeIcon); }
                return new DesktopEntry(file, "入口" + i);
            }).ToArray();
            widget = new GroupWidget(group, display, entries, new DesktopCatalog(), false, false, callbacks);
            host = new WidgetHost(widget, new Rect(260 * display.Scale, 190 * display.Scale, 350 * display.Scale, 300 * display.Scale), 12, display.Scale, true);
            Wait(600); CheckInk("four-icons-compact", 14, 12, 115, 22);
            host.SetExpanded(true); Wait(300); CheckInk("four-icons-expanded", 20, 15, 160, 24);
            host.SetExpanded(false); Wait(500); CheckInk("four-icons-collapsed", 14, 12, 115, 22);
            using (var pixels = Capture("four-icons-visible"))
            {
                var scale = display.Scale;
                // 自建白色图标不受用户文件关联影响；四个区域都必须出现图标，而非只验证列表非空。
                foreach (var cell in new[] { new Point(30, 44), new Point(72, 44), new Point(30, 75), new Point(72, 75) })
                {
                    var paper = 0;
                    for (var y = (int)(cell.Y * scale); y < (cell.Y + 31) * scale; y++)
                        for (var x = (int)(cell.X * scale); x < (cell.X + 42) * scale; x++)
                        { var pixel = pixels.GetPixel(x, y); if (pixel.R > 190 && pixel.G > 190 && pixel.B > 190) paper++; }
                    Require(paper > 30, "紧凑预览图标可见 " + cell);
                }
            }
            host.Close(); host = null;
            // 默认桌面模式必须将两个组件的每一对窗口放在普通应用下，不只验证临时前景模式。
            var foregroundBeforeDesktop = GetForegroundWindow();
            widget = new GroupWidget(group, display, entries, new DesktopCatalog(), false, false, callbacks);
            host = new WidgetHost(widget, new Rect(30 * display.Scale, 30 * display.Scale, 350 * display.Scale, 300 * display.Scale), 12, display.Scale);
            var secondWidget = new GroupWidget(group, display, [], new DesktopCatalog(), false, false, callbacks);
            secondHost = new WidgetHost(secondWidget, new Rect(210 * display.Scale, 30 * display.Scale, 350 * display.Scale, 300 * display.Scale), 12, display.Scale);
            Wait(400);
            var firstBackground = GetWindow(host.Handle, 2); var secondBackground = GetWindow(secondHost.Handle, 2);
            Require(WindowBackdrop.ContentHandleFor(firstBackground) == host.Handle && WindowBackdrop.ContentHandleFor(secondBackground) == secondHost.Handle,
                "默认桌面多组件背景紧邻各自前景");
            foreach (var handle in new[] { host.Handle, firstBackground, secondHost.Handle, secondBackground })
                Require((Native.GetWindowLongPtr(handle, -20).ToInt64() & 8) == 0, "桌面组件及背景不置顶");
            Require(GetForegroundWindow() == foregroundBeforeDesktop, "创建桌面组件不抢焦点");
            Require(Above(new WindowInteropHelper(background).Handle, host.Handle) && Above(new WindowInteropHelper(background).Handle, secondHost.Handle),
                "已有应用在桌面组件及其背景上方");
            for (var i = 0; i < 4; i++)
            {
                host.SetExpanded(i % 2 == 0); secondHost.SetExpanded(i % 2 != 0); Wait(80);
                Require(GetWindow(host.Handle, 2) == firstBackground && GetWindow(secondHost.Handle, 2) == secondBackground,
                    "多组件改变尺寸不打乱背景归属");
            }
            ShowWindow(firstBackground, 0); Wait(250);
            Require(Native.IsWindowVisible(firstBackground), "仅辅助层被系统隐藏时跟随可见组件恢复");
            widget.Hide(); Wait(250);
            Require(!Native.IsWindowVisible(host.Handle) && !Native.IsWindowVisible(firstBackground), "桌面Hide不被恢复队列复活");
            Require(Native.IsWindowVisible(secondHost.Handle) && Native.IsWindowVisible(secondBackground), "隐藏一个分组不影响其他分组");
            widget.Show(); Wait(250);
            Require(Native.IsWindowVisible(firstBackground) && GetWindow(host.Handle, 2) == firstBackground, "桌面Show恢复配对层级");
            ShowWindow(host.Handle, 7); Wait(350);
            Require(!IsIconic(host.Handle) && Native.IsWindowVisible(firstBackground), "原生最小化按桌面恢复契约成对恢复");
            host.Close(); host = null; Wait(200);
            Require(!Native.IsWindow(firstBackground) && Native.IsWindow(secondBackground), "关闭一个分组只释放其背景");
            secondHost.Close(); secondHost = null; Wait(200);
            Require(!Native.IsWindow(secondBackground) && DesktopLayer.RegisteredCount == 0 && DesktopLayer.HookCount == 0,
                "最后分组退出释放背景与共享钩子");
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Passed = true, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS {checks.Count} 屏幕合成检查"); return 0;

            void CheckInk(string name, double x, double y, double width, double height)
            {
                var bounds = host.GetBounds(); var scale = display.Scale;
                using var bitmap = Capture(name);
                var dark = 0;
                for (var row = (int)(y * scale); row < Math.Min(bitmap.Height, (y + height) * scale); row++)
                    for (var column = (int)(x * scale); column < Math.Min(bitmap.Width, (x + width) * scale); column++)
                    {
                        var pixel = bitmap.GetPixel(column, row);
                        if (pixel.R < 65 && pixel.G < 105 && pixel.B < 125) dark++;
                    }
                // 背景两种色块均不含深色；标题笔画必须真的出现在屏幕，而非仅存在于 WPF 树中。
                checks.Add(new { Name = name, TitleInkPixels = dark, Bounds = bounds.ToString() });
                if (dark < 50) throw new InvalidOperationException($"{name}: 屏幕标题不可见，仅 {dark} 个墨色像素。");
                // 左边框与内侧底色需可分辨；固定在没有文字图标的区域检查，不以框架属性代替像素。
                var edge = bitmap.GetPixel((int)Math.Round(scale), (int)(60 * scale));
                var inside = bitmap.GetPixel((int)(7 * scale), (int)(60 * scale));
                if (Math.Abs(edge.R - inside.R) + Math.Abs(edge.G - inside.G) + Math.Abs(edge.B - inside.B) < 12)
                    throw new InvalidOperationException(name + ": 边框与内部像素无法分辨。");
            }
            System.Drawing.Bitmap Capture(string name)
            {
                var bounds = host.GetBounds();
                var bitmap = new System.Drawing.Bitmap((int)bounds.Width, (int)bounds.Height);
                using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                    graphics.CopyFromScreen((int)bounds.X, (int)bounds.Y, 0, 0, bitmap.Size);
                bitmap.Save(Path.Combine(output, name + ".png")); return bitmap;
            }
            System.Drawing.Color Sample(double x, double y)
            {
                using var image = Capture("background-" + checks.Count);
                return image.GetPixel((int)(x * display.Scale), (int)(y * display.Scale));
            }
            void Require(bool condition, string name)
            { if (!condition) throw new InvalidOperationException(name); checks.Add(new { Name = name }); }
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Passed = false, Error = error.ToString(), Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
            Console.Error.WriteLine(error.Message); return 1;
        }
        finally { host?.Close(); secondHost?.Close(); background?.Close(); app.Shutdown(); }
    }

    private static List<nint> OwnedVisibleWindows()
    {
        var result = new List<nint>();
        EnumWindows((handle, _) => { Native.GetWindowThreadProcessId(handle, out var pid);
            if (pid == Environment.ProcessId && Native.IsWindowVisible(handle)) result.Add(handle); return true; }, 0);
        return result;
    }
    private delegate bool EnumWindowProc(nint handle, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint handle, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint handle);
    private static bool Above(nint upper, nint lower)
    {
        for (var handle = GetWindow(upper, 2); handle != 0; handle = GetWindow(handle, 2)) if (handle == lower) return true;
        return false;
    }

    private static void Wait(int milliseconds)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
}
