using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Effects;

namespace DesktopGlass.App.UI;

/// <summary>面板、编辑器与品牌图形共享的轻量视觉语言；交互状态不依赖持续动画。</summary>
internal static class AppTheme
{
    internal static readonly Brush Ink = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brush("#122C49"),
        Muted = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brush("#667991"),
        Accent = SystemParameters.HighContrast ? SystemColors.HighlightBrush : Brush("#397FC4");
    internal static SolidColorBrush Brush(string color) { var value = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!; value.Freeze(); return value; }
    internal static TextBlock Text(string text, double size = 13, Brush? color = null) => new()
    { Text = text, FontSize = size, Foreground = color ?? Ink, TextWrapping = TextWrapping.Wrap };
    internal static Button Button(string text, bool primary = false)
    {
        var button = new Button { Content = text, Padding = new Thickness(15, 9, 15, 9), FontSize = 12,
            Background = primary ? Accent : Brushes.White, Foreground = primary ? Brushes.White : Ink,
            BorderBrush = Brush("#CADDE6"), BorderThickness = new Thickness(primary ? 0 : 1),
            Cursor = Cursors.Hand, VerticalContentAlignment = VerticalAlignment.Center };
        if (SystemParameters.HighContrast) { button.Background = SystemColors.ControlBrush; button.Foreground = SystemColors.ControlTextBrush; button.BorderBrush = SystemColors.ControlTextBrush; button.BorderThickness = new Thickness(1); return button; }
        var border = new FrameworkElementFactory(typeof(Border)); border.Name = "Frame";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
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
    /// <summary>创建带独立图标列与键盘高亮的轻透命令浮层；高对比度沿用系统菜单。</summary>
    internal static ContextMenu CreateContextMenu()
    {
        var menu = new ContextMenu { FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 12, MinWidth = 218 };
        if (SystemParameters.HighContrast) return menu;
        menu.Foreground = Ink;
        menu.Background = Brush("#F5F8FC");
        menu.BorderBrush = Brush("#E1E9F2");
        menu.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ContextMenu">
              <Border Padding="10" Background="Transparent">
                <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="13" Padding="5">
                  <Border.Effect><DropShadowEffect Color="#193653" BlurRadius="16" ShadowDepth="4" Opacity="0.18"/></Border.Effect>
                  <StackPanel IsItemsHost="True" KeyboardNavigation.DirectionalNavigation="Cycle"/>
                </Border>
              </Border>
            </ControlTemplate>
            """);
        var separator = new Style(typeof(Separator));
        separator.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Separator">
              <Border Height="1" Margin="34,5,10,5" Background="#DCE5EF"/>
            </ControlTemplate>
            """)));
        menu.Resources.Add(typeof(Separator), separator);
        return menu;
    }

    /// <summary>创建统一菜单命令；glyph 使用 Segoe MDL2 Assets 字形，危险操作保留文字含义。</summary>
    internal static MenuItem MenuItem(string text, Action action, string glyph, bool enabled = true, bool destructive = false)
    {
        var item = new MenuItem { Header = text, IsEnabled = enabled };
        item.Icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        item.Click += (_, _) => action();
        if (SystemParameters.HighContrast) return item;
        item.Foreground = destructive ? Brush("#AA3C48") : Ink;
        item.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="MenuItem">
              <Border Name="Frame" Background="Transparent" CornerRadius="8" Padding="8,9" BorderThickness="1" BorderBrush="Transparent" Margin="1">
                <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="24"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                  <ContentPresenter Content="{TemplateBinding Icon}" VerticalAlignment="Center"/>
                  <ContentPresenter Grid.Column="1" Margin="8,0,8,0" ContentSource="Header" RecognizesAccessKey="True" VerticalAlignment="Center"/>
                </Grid>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Frame" Property="Background" Value="#E2EDF8"/></Trigger>
                <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="#659ACA"/><Setter TargetName="Frame" Property="Background" Value="#E2EDF8"/></Trigger>
                <Trigger Property="IsEnabled" Value="False"><Setter Property="Foreground" Value="#94A2B3"/></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        return item;
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
    { Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : Brush("#EAF8FCFE"), BorderBrush = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Brushes.White, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(radius), Child = child };

    /// <summary>使用相同矢量几何生成窗口插画和多尺寸程序图标，避免不同入口品牌不一致。</summary>
    /// <param name="size">显示边长，单位 DIP。</param><returns>可缩放的自有玻璃叠片标志。</returns>
    internal static Image Logo(double size)
    {
        var drawing = new DrawingGroup();
        LinearGradientBrush Gradient(string start, string end) => new(((SolidColorBrush)Brush(start)).Color, ((SolidColorBrush)Brush(end)).Color, new Point(0, 0), new Point(1, 1));
        drawing.Children.Add(new GeometryDrawing(Gradient("#173C65", "#091B34"), new Pen(Brush("#395B7B"), 1), new RectangleGeometry(new Rect(5, 5, 90, 90), 23, 23)));
        var rear = new RectangleGeometry(new Rect(26, 22, 48, 58), 12, 12) { Transform = new RotateTransform(-12, 50, 51) };
        drawing.Children.Add(new GeometryDrawing(Gradient("#843CA7CE", "#478EDDE7"), new Pen(Brush("#868EDDE7"), 1), rear));
        // 一条连续折光带构成 G，保留足够大的负空间，托盘尺寸仍可识别。
        var ribbon = Geometry.Parse("M 73,32 L 63,42 C 59,37 55,35 49,35 C 40,35 34,42 34,51 C 34,61 40,67 50,67 C 55,67 59,66 62,63 L 62,57 L 50,57 L 50,46 L 75,46 L 75,69 C 68,77 60,81 49,81 C 31,81 20,68 20,51 C 20,33 32,21 49,21 C 59,21 67,25 73,32 Z");
        drawing.Children.Add(new GeometryDrawing(Gradient("#FAFFFFFF", "#9E8EDDE7"), new Pen(Brush("#B3E5F6FF"), .8), ribbon));
        drawing.Children.Add(new GeometryDrawing(Gradient("#D4EDFAFF", "#4C82BFE7"), null, Geometry.Parse("M 50,46 L 75,46 L 62,57 L 50,57 Z")));
        drawing.Children.Add(new GeometryDrawing(null, new Pen(Brush("#658EDDE7"), .8), Geometry.Parse("M 18,24 C 18,16 23,12 31,12 L 68,12")));
        drawing.Freeze();
        return new Image { Source = new DrawingImage(drawing), Width = size, Height = size, Stretch = Stretch.Uniform };
    }
    /// <summary>以统一轻圆角的四格玻璃图形提示分组容器，不替换文件自身图标。</summary>
    /// <param name="size">图形边长，单位 DIP。</param><returns>可随布局缩放的分组标记。</returns>
    internal static Image GroupMark(double size)
    {
        var drawing = new DrawingGroup();
        var highContrast = SystemParameters.HighContrast;
        drawing.Children.Add(new GeometryDrawing(highContrast ? SystemColors.WindowBrush :
            new LinearGradientBrush(Color.FromRgb(246,250,254), Color.FromRgb(210,227,239), 70),
            new Pen(highContrast ? SystemColors.WindowTextBrush : Brush("#B6CDDF"), 1), new RectangleGeometry(new Rect(1,1,46,46), 7,7)));
        foreach (var position in new[] { new Point(10,10), new Point(27,10), new Point(10,27), new Point(27,27) })
            drawing.Children.Add(new GeometryDrawing(highContrast ? SystemColors.WindowTextBrush :
                new LinearGradientBrush(Color.FromRgb(128,173,206), Color.FromRgb(65,116,155), 80),
                null, new RectangleGeometry(new Rect(position,new Size(11,11)),3,3)));
        drawing.Freeze();
        return new Image { Source = new DrawingImage(drawing), Width = size, Height = size, Stretch = Stretch.Uniform };
    }

    internal static void Apply(Window window)
    {
        window.FontFamily = new FontFamily("Microsoft YaHei UI"); window.Foreground = Ink;
        window.Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : new LinearGradientBrush(Color.FromRgb(239, 244, 250), Color.FromRgb(224, 236, 246), 105);
        window.Icon = Logo(32).Source; window.UseLayoutRounding = true;
    }
}
