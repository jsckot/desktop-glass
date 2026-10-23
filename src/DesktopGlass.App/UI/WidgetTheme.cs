using System.Text;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using DesktopGlass.App.Windows;

namespace DesktopGlass.App.UI;

/// <summary>独立组件的静态玻璃材质；始终提供不透明底层，不依赖 layered 窗口合成。</summary>
internal static class WidgetTheme
{
    internal static readonly SolidColorBrush Ink = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Frozen(Color.FromRgb(29, 48, 63));
    internal static readonly SolidColorBrush SecondaryInk = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Frozen(Color.FromRgb(77, 101, 118));
    internal static readonly SolidColorBrush Focus = SystemParameters.HighContrast ? SystemColors.HighlightBrush : Frozen(Color.FromRgb(64, 114, 151));
    private static string wallpaperPath = "";
    private static BitmapImage? wallpaper;

    internal sealed record Material(Grid Root, Grid Desktop, Border Frost, Border Outer, Border Inner)
    {
        internal void ApplyAppearance(Group group)
        {
            var frost = (byte)Math.Round(255 * Math.Clamp(.015 + group.Tint * .42, .045, .55));
            Frost.Background = Frozen(SystemParameters.HighContrast ? SystemColors.WindowColor : Color.FromArgb(frost, 224, 242, 250));
            Outer.CornerRadius = new CornerRadius(Math.Max(0, group.Radius - 1));
            Inner.CornerRadius = new CornerRadius(Math.Max(0, group.Radius - 3));
        }
        internal void SetForeground(bool live)
        {
            Desktop.Visibility = Visibility.Collapsed;
            Root.Background = live ? Brushes.Transparent : Frozen(Color.FromRgb(219, 233, 240));
        }
    }

    internal static Grid Surface(Group group, DisplayArea display, FrameworkElement content) => CreateMaterial(group, display, content).Root;

    internal static Material CreateMaterial(Group group, DisplayArea display, FrameworkElement content)
    {
        // 圆角由宿主裁剪原生窗口区域；这里每个像素都有底色，避免子窗口透明合成空白。
        var surface = new Grid { Background = Frozen(Color.FromRgb(173, 194, 206)), ClipToBounds = true };
        var desktop = new Grid { IsHitTestVisible = false }; surface.Children.Add(desktop);
        var image = SystemParameters.HighContrast ? null : ReadWallpaper();
        if (image is not null)
        {
            var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s =>
                s.WorkingArea.Left == display.Left && s.WorkingArea.Top == display.Top);
            var bounds = screen?.Bounds ?? new System.Drawing.Rectangle((int)display.Left, (int)display.Top, (int)display.Width, (int)display.Height);
            var sample = new ImageBrush(image) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.RelativeToBoundingBox };
            var backdrop = new Border { Background = sample, Margin = new Thickness(-18), IsHitTestVisible = false,
                Effect = new BlurEffect { Radius = 8, RenderingBias = RenderingBias.Performance } };
            desktop.Children.Add(backdrop);
            // 仅采样静态壁纸；移动或缩放时更新采样区域，不截图、不读取其他窗口内容。
            void UpdateSample()
            {
                var window = Window.GetWindow(surface);
                var handle = window is null ? 0 : new WindowInteropHelper(window).Handle;
                var actual = handle == 0 ? new Rect(display.Left + group.X * display.Scale, display.Top + group.Y * display.Scale, 1, 1) : WidgetHost.ReadWindowBounds(handle);
                var x = actual.Left / display.Scale;
                var y = actual.Top / display.Scale;
                var width = Math.Max(1, surface.ActualWidth);
                var height = Math.Max(1, surface.ActualHeight);
                sample.Viewbox = new Rect((x * display.Scale - bounds.Left - 18 * display.Scale) / bounds.Width,
                    (y * display.Scale - bounds.Top - 18 * display.Scale) / bounds.Height,
                    (width + 36) * display.Scale / bounds.Width, (height + 36) * display.Scale / bounds.Height);
            }
            surface.Loaded += (_, _) =>
            {
                UpdateSample();
                if (Window.GetWindow(surface) is { } window) window.LocationChanged += (_, _) => UpdateSample();
            };
            surface.SizeChanged += (_, _) => UpdateSample();
        }
        var frost = (byte)Math.Round(255 * Math.Clamp(.015 + group.Tint * .42, .045, .55));
        var frostLayer = new Border { Background = Frozen(SystemParameters.HighContrast ? SystemColors.WindowColor : Color.FromArgb(frost, 224, 242, 250)), IsHitTestVisible = false }; surface.Children.Add(frostLayer);
        surface.Children.Add(new Border { Background = new LinearGradientBrush(
            Color.FromArgb(35, 255, 255, 255), Color.FromArgb(3, 207, 232, 247), 105), IsHitTestVisible = false });
        var outer = new Border { Margin = new Thickness(1), CornerRadius = new CornerRadius(Math.Max(0, group.Radius - 1)),
            BorderThickness = new Thickness(1), BorderBrush = new LinearGradientBrush(
                Color.FromArgb(235, 255, 255, 255), Color.FromArgb(72, 255, 255, 255), 110), IsHitTestVisible = false }; surface.Children.Add(outer);
        var inner = new Border { Margin = new Thickness(3), CornerRadius = new CornerRadius(Math.Max(0, group.Radius - 3)),
            BorderThickness = new Thickness(.7), BorderBrush = new LinearGradientBrush(
                Color.FromArgb(12, 24, 72, 95), Color.FromArgb(68, 255, 255, 255), 125), IsHitTestVisible = false }; surface.Children.Add(inner);
        surface.Children.Add(content);
        return new Material(surface, desktop, frostLayer, outer, inner);
    }

    internal static ControlTemplate TileTemplate()
    {
        var frame = new FrameworkElementFactory(typeof(Border));
        frame.Name = "Frame";
        frame.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
        frame.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        frame.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        frame.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.MarginProperty, new Thickness(3));
        content.SetBinding(FrameworkElement.HorizontalAlignmentProperty, new System.Windows.Data.Binding("HorizontalContentAlignment")
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        content.SetBinding(FrameworkElement.VerticalAlignmentProperty, new System.Windows.Data.Binding("VerticalContentAlignment")
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        frame.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = frame };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, Frozen(Color.FromArgb(98, 255, 255, 255)), "Frame"));
        template.Triggers.Add(hover);
        var keyboard = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        keyboard.Setters.Add(new Setter(Border.BorderBrushProperty, Focus, "Frame"));
        keyboard.Setters.Add(new Setter(Border.BackgroundProperty, Frozen(Color.FromArgb(130, 255, 255, 255)), "Frame"));
        template.Triggers.Add(keyboard);
        return template;
    }

    internal static Button MoreButton()
    {
        var button = new Button { Content = "⋯", Width = 32, Height = 32, FontSize = 24, Foreground = Ink,
            Cursor = System.Windows.Input.Cursors.Hand, Template = TileTemplate(), Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center,
            ToolTip = "分组菜单" };
        System.Windows.Automation.AutomationProperties.SetName(button, "分组菜单");
        return button;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color); brush.Freeze(); return brush;
    }

    private static BitmapImage? ReadWallpaper()
    {
        var path = new StringBuilder(32768);
        Native.SystemParametersInfo(0x73, (uint)path.Capacity, path, 0);
        if (path.ToString() == wallpaperPath) return wallpaper;
        wallpaperPath = path.ToString(); wallpaper = null;
        if (!File.Exists(wallpaperPath)) return null;
        try
        {
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 2560; bitmap.UriSource = new Uri(wallpaperPath); bitmap.EndInit(); bitmap.Freeze();
            wallpaper = bitmap;
        }
        // 壁纸文件可在主题切换时消失或暂不可读；此时保留不透明霜色底层。
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        { wallpaper = null; }
        return wallpaper;
    }
}
