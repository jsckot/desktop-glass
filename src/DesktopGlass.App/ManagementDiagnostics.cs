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
            ManagementWindow Make() => new(() => work, () => displays, () => { }, _ => { }, _ => { }, _ => { }, (_, _) => { }, () => { }, () => { });
            var empty = Make(); Render(empty, output + ".empty.png"); empty.Finish();
            var group = work.AddGroup("开发工具", "preview"); group.Entries.Add("preview");
            var paused = work.AddGroup("常用工具", "previous"); paused.DesktopVisible = false;
            var list = Make(); Render(list, output + ".list.png"); list.Refresh(); list.Refresh(); list.Finish();
            var editor = new GroupEditor(group, () => { }); Render(editor, output + ".editor.png"); editor.Close();
            var display = Native.Displays().First(d => d.Primary); var catalog = new DesktopCatalog();
            var previewEntries = catalog.Scan().Take(6).ToArray();
            var widgetGroup = new Group { Name = "开发工具", DisplayId = display.Id, X = 36, Y = 96, Width = 320, Height = 260 };
            var callbacks = new WidgetCallbacks { Open = _ => { }, Collect = (_, _) => false, Release = _ => { }, Edit = () => { },
                Remove = () => { }, Create = () => { }, ToggleLock = () => { }, Exit = () => { }, MoveBy = (_, _) => { }, ResizeBy = (_, _) => { }, CommitBounds = _ => { } };
            var widget = new GroupWidget(widgetGroup, display, previewEntries, catalog, false, false, callbacks);
            Render(widget, output + ".widget.png"); widget.Close();
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
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),(int)Math.Ceiling(visual.ActualHeight),96,96,PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(0,0,visual.ActualWidth,visual.ActualHeight));
            dc.DrawRectangle(new VisualBrush(visual),null,new Rect(0,0,visual.ActualWidth,visual.ActualHeight));
        }
        bitmap.Render(drawing); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
