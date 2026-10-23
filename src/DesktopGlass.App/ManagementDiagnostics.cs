using System.Text.Json;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopGlass.App.UI;
using DesktopGlass.App.Windows;

namespace DesktopGlass.App;

/// <summary>在项目隔离目录中渲染面板、编辑器及自有 Logo，不创建桌面租约或修改用户配置。</summary>
internal static class ManagementDiagnostics
{
    internal static int Run(string[] args)
    {
        var index = Array.IndexOf(args, "--output");
        if (index < 0 || index + 1 >= args.Length) return 1;
        var output = WidgetDiagnostics.CheckedOutput(args[index + 1]);
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var work = new Workspace(); var displays = new[] { new DisplayArea("preview","当前屏幕",0,0,1600,1000,1,true) };
            ManagementWindow Make() => new(() => work, () => displays, () => { }, _ => { }, _ => { }, _ => { }, (_, _) => { }, () => { }, () => { }, () => { }, () => false);
            var empty = Make(); Render(empty, output + ".empty.png"); empty.Finish();
            var group = work.AddGroup("开发工具", "preview"); group.Entries.Add("preview");
            var paused = work.AddGroup("常用工具", "previous"); paused.DesktopVisible = false;
            var list = Make(); Render(list, output + ".list.png"); list.Refresh(); list.Refresh();
            foreach (var page in new[] { "外观", "关于" })
            {
                var navigation = Descendants(list).OfType<Button>().Single(button => button.Content is StackPanel panel && panel.Children.OfType<TextBlock>().Any(text => text.Text == page));
                navigation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Render(list, output + (page == "外观" ? ".appearance.png" : ".about.png"));
            }
            var groupsNavigation = Descendants(list).OfType<Button>().Single(button => button.Content is StackPanel panel && panel.Children.OfType<TextBlock>().Any(text => text.Text == "分组"));
            group.Name = new string('长', 40);
            groupsNavigation.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            list.Width = list.MinWidth; list.Height = list.MinHeight;
            Render(list, output + ".small.png"); list.Finish();
            group.Name = "开发工具";
            var editor = new GroupEditor(group, () => { }); Render(editor, output + ".editor.png"); editor.Close();
            var display = Native.Displays().First(d => d.Primary); var catalog = new DesktopCatalog();
            var previewEntries = catalog.Scan().Take(6).ToArray();
            var widgetGroup = new Group { Name = "开发工具", DisplayId = display.Id, X = 36, Y = 96, Width = 320, Height = 260 };
            var callbacks = new WidgetCallbacks { Open = _ => { }, Collect = (_, _) => false, Release = _ => { }, Edit = () => { },
                Remove = () => { }, Create = () => { }, ToggleLock = () => { }, Exit = () => { }, MoveBy = (_, _) => { }, ResizeBy = (_, _) => { }, CommitBounds = _ => { } };
            var widget = new GroupWidget(widgetGroup, display, previewEntries, catalog, false, false, callbacks);
            Render(widget, output + ".widget.png"); widget.Close();
            foreach (var radius in new[] { 0d, 48d })
            {
                widgetGroup.Radius = radius;
                var rounded = new GroupWidget(widgetGroup, display, previewEntries.Take(1).ToArray(), catalog, false, false, callbacks);
                Render(rounded, output + ".radius-" + radius + ".png"); rounded.Close();
            }
            var menu = AppTheme.CreateContextMenu();
            menu.Items.Add(AppTheme.MenuItem("名称与玻璃外观…", () => { }, "\uE790"));
            menu.Items.Add(AppTheme.MenuItem("新建分组", () => { }, "\uE710"));
            menu.Items.Add(AppTheme.MenuItem("锁定布局", () => { }, "\uE72E"));
            menu.Items.Add(new Separator());
            menu.Items.Add(AppTheme.MenuItem("移除分组（保留文件）", () => { }, "\uE74D", destructive: true));
            menu.Items.Add(new Separator());
            menu.Items.Add(AppTheme.MenuItem("退出桌面分组", () => { }, "\uE8BB"));
            // 直接渲染菜单视觉树，不打开用户桌面上下文菜单或执行其命令。
            menu.ApplyTemplate(); menu.Measure(new Size(360, 600)); menu.Arrange(new Rect(menu.DesiredSize)); menu.UpdateLayout();
            RenderElement(menu, output + ".menu.png");
            var logoFile = output + ".ico";
            using (var stream = File.Create(logoFile))
            using (var writer = new BinaryWriter(stream))
            {
                var images = new List<(int Size, byte[] Bytes)>();
                foreach (var size in new[] { 16, 24, 32, 48, 64, 128, 256 })
                {
                    var logo = AppTheme.Logo(size);
                    logo.Measure(new Size(size,size)); logo.Arrange(new Rect(0,0,size,size));
                    var bitmap = new RenderTargetBitmap(size,size,96,96,PixelFormats.Pbgra32); bitmap.Render(logo);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    using var data = new MemoryStream(); png.Save(data); images.Add((size,data.ToArray()));
                    if (size == 256) File.WriteAllBytes(output + ".logo.png", data.ToArray());
                }
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)images.Count);
                var offset = 6 + images.Count * 16;
                foreach (var image in images)
                {
                    writer.Write((byte)(image.Size == 256 ? 0 : image.Size)); writer.Write((byte)(image.Size == 256 ? 0 : image.Size));
                    writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
                    writer.Write(image.Bytes.Length); writer.Write(offset); offset += image.Bytes.Length;
                }
                foreach (var image in images) writer.Write(image.Bytes);
            }
            app.Shutdown();
            File.WriteAllText(output, JsonSerializer.Serialize(new { Passed = true, RealDesktopActions = false,
                EmptyGroups = 0, RepeatRefreshPassed = true, LogoSizes = new[]{16,24,32,48,64,128,256}, OfflineRenderOnly = true }));
            return 0;
        }
        catch (Exception error) { File.WriteAllText(output, JsonSerializer.Serialize(new { Passed = false, Error = error.ToString() })); return 1; }
    }
    private static void Render(Window window, string path)
    {
        window.ShowActivated = false; window.ShowInTaskbar = false; window.Show();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        var visual = (FrameworkElement)window.Content;
        var width = visual.ActualWidth + visual.Margin.Left + visual.Margin.Right;
        var height = visual.ActualHeight + visual.Margin.Top + visual.Margin.Bottom;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width),(int)Math.Ceiling(height),96,96,PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(0,0,width,height));
            // 显式取完整布局矩形，避免 VisualBrush 默认按可见内容边界裁掉表单留白。
            var offset = VisualTreeHelper.GetOffset(visual);
            var brush = new VisualBrush(visual) { ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(offset.X,offset.Y,visual.ActualWidth,visual.ActualHeight), Stretch = Stretch.Fill };
            dc.DrawRectangle(brush,null,new Rect(visual.Margin.Left,visual.Margin.Top,visual.ActualWidth,visual.ActualHeight));
        }
        bitmap.Render(drawing); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static void RenderElement(FrameworkElement visual, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
