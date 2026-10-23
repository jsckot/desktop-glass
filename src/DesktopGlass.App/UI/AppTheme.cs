using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Effects;

namespace DesktopGlass.App.UI;

/// <summary>面板、编辑器与品牌图形共享的轻量视觉语言；交互状态不依赖持续动画。</summary>
internal static class AppTheme
{
    internal static readonly Brush Ink = Brush("#172D3D"), Muted = Brush("#5F7482"), Accent = Brush("#357FA1");
    internal static SolidColorBrush Brush(string color) { var value = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!; value.Freeze(); return value; }
    internal static TextBlock Text(string text, double size = 13, Brush? color = null) => new()
    { Text = text, FontSize = size, Foreground = color ?? Ink, TextWrapping = TextWrapping.Wrap };
    internal static Button Button(string text, bool primary = false)
    {
        var button = new Button { Content = text, Padding = new Thickness(17, 10, 17, 10), FontSize = 12,
            Background = primary ? Accent : Brushes.White, Foreground = primary ? Brushes.White : Ink,
            BorderBrush = Brush("#CADDE6"), BorderThickness = new Thickness(primary ? 0 : 1),
            Cursor = Cursors.Hand, VerticalContentAlignment = VerticalAlignment.Center };
        var border = new FrameworkElementFactory(typeof(Border)); border.Name = "Frame";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(17));
        foreach (var name in new[] { "Background", "BorderBrush", "BorderThickness", "Padding" })
            border.SetBinding((DependencyProperty)typeof(Border).GetField(name + "Property")!.GetValue(null)!, new Binding(name) { RelativeSource = RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetBinding(FrameworkElement.HorizontalAlignmentProperty, new Binding("HorizontalContentAlignment") { RelativeSource = RelativeSource.TemplatedParent });
        presenter.SetBinding(FrameworkElement.VerticalAlignmentProperty, new Binding("VerticalContentAlignment") { RelativeSource = RelativeSource.TemplatedParent });
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, .82, "Frame")); template.Triggers.Add(hover);
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2), "Frame"));
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, Brush("#66AEC9"), "Frame")); template.Triggers.Add(focus);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, .42, "Frame")); template.Triggers.Add(disabled);
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.Template = template; return button;
    }
    internal static void StyleSlider(Slider slider)
    {
        if (SystemParameters.HighContrast) return;
        slider.MinHeight = 30;
        slider.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Slider">
              <Grid Height="30" Margin="1,0">
                <Track Name="PART_Track" Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{Binding Value, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                  <Track.DecreaseRepeatButton><RepeatButton Command="Slider.DecreaseLarge" Focusable="False"><RepeatButton.Template><ControlTemplate><Border Height="4" CornerRadius="2" Background="#5F9DB5"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
                  <Track.IncreaseRepeatButton><RepeatButton Command="Slider.IncreaseLarge" Focusable="False"><RepeatButton.Template><ControlTemplate><Border Height="4" CornerRadius="2" Background="#D3E1E8"/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
                  <Track.Thumb><Thumb Width="22" Height="22"><Thumb.Template><ControlTemplate><Border CornerRadius="11" Background="White" BorderBrush="#9DBECC" BorderThickness="1"><Border.Effect><DropShadowEffect BlurRadius="5" ShadowDepth="1" Opacity="0.14"/></Border.Effect></Border></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
                </Track>
              </Grid>
              <ControlTemplate.Triggers><Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter Property="Background" Value="#E0F0F7"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate>
            """);
    }
    internal static ScrollViewer Scroll(UIElement child)
    {
        var scroll = new ScrollViewer { Content = child, Focusable = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, PanningMode = PanningMode.VerticalOnly };
        if (SystemParameters.HighContrast) return scroll;
        var style = new Style(typeof(System.Windows.Controls.Primitives.ScrollBar));
        style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 9d));
        style.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ScrollBar">
              <Grid Background="Transparent" Margin="2,4">
                <Track Name="PART_Track" Orientation="Vertical" IsDirectionReversed="True">
                  <Track.DecreaseRepeatButton><RepeatButton Command="ScrollBar.PageUpCommand" Opacity="0" Focusable="False"/></Track.DecreaseRepeatButton>
                  <Track.IncreaseRepeatButton><RepeatButton Command="ScrollBar.PageDownCommand" Opacity="0" Focusable="False"/></Track.IncreaseRepeatButton>
                  <Track.Thumb><Thumb MinHeight="20"><Thumb.Template><ControlTemplate><Border CornerRadius="3" Background="#7092AAB7"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
                </Track>
              </Grid>
            </ControlTemplate>
            """)));
        scroll.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), style); return scroll;
    }
    internal static Border Card(UIElement child, double radius = 20) => new()
    { Background = Brush("#F8FCFE"), BorderBrush = Brushes.White, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(radius), Child = child };

    /// <summary>使用相同矢量几何生成窗口插画和多尺寸程序图标，避免不同入口品牌不一致。</summary>
    /// <param name="size">显示边长，单位 DIP。</param><returns>可缩放的自有玻璃叠片标志。</returns>
    internal static Image Logo(double size)
    {
        var drawing = new DrawingGroup();
        var rear = new RectangleGeometry(new Rect(13, 15, 65, 67), 20, 20) { Transform = new RotateTransform(-12, 46, 48) };
        drawing.Children.Add(new GeometryDrawing(new LinearGradientBrush(Color.FromRgb(60, 153, 193), Color.FromRgb(137, 217, 235), 40), null, rear));
        var front = new RectangleGeometry(new Rect(29, 27, 63, 64), 20, 20);
        drawing.Children.Add(new GeometryDrawing(new LinearGradientBrush(Color.FromArgb(242, 246, 253, 255), Color.FromArgb(205, 123, 196, 222), 85),
            new Pen(Brushes.White, 1.4), front));
        foreach (var r in new[] { new Rect(43, 43, 12, 12), new Rect(63, 43, 12, 12), new Rect(43, 63, 12, 12), new Rect(63, 63, 12, 12) })
            drawing.Children.Add(new GeometryDrawing(new LinearGradientBrush(Colors.White, Color.FromRgb(122, 184, 206), 80),
                new Pen(Brush("#C8E8F1"), .7), new RectangleGeometry(r, 4, 4)));
        drawing.Freeze();
        return new Image { Source = new DrawingImage(drawing), Width = size, Height = size, Stretch = Stretch.Uniform };
    }
    internal static void Apply(Window window)
    {
        window.FontFamily = new FontFamily("Microsoft YaHei UI"); window.Foreground = Ink;
        window.Background = new LinearGradientBrush(Color.FromRgb(238, 245, 249), Color.FromRgb(225, 238, 245), 105);
        window.Icon = Logo(32).Source; window.UseLayoutRounding = true;
    }
}
