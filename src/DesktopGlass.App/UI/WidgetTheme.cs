
namespace DesktopGlass.App.UI;

/// <summary>独立组件的中性材质；仅在系统实时背景可用时透明，否则明确呈现兼容底色。</summary>
internal static class WidgetTheme
{
    internal static readonly SolidColorBrush Ink = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Frozen(Color.FromRgb(29, 48, 63));
    internal static readonly SolidColorBrush SecondaryInk = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Frozen(Color.FromRgb(77, 101, 118));
    internal static readonly SolidColorBrush Focus = SystemParameters.HighContrast ? SystemColors.HighlightBrush : Frozen(Color.FromRgb(64, 114, 151));

    internal sealed record Material(Grid Root, Grid Desktop, Border Frost, Border Outer, Border Inner)
    {
        internal void ApplyAppearance(Group group)
        {
            var frost = (byte)Math.Round(255 * Math.Clamp(.035 + group.Tint * .32, .035, .36));
            Frost.Background = Frozen(SystemParameters.HighContrast ? SystemColors.WindowColor : Color.FromArgb(frost, 237, 240, 242));
            Outer.CornerRadius = new CornerRadius(11);
            Inner.CornerRadius = new CornerRadius(9);
        }
        internal void SetForeground(bool live)
        {
            Desktop.Visibility = Visibility.Collapsed;
            Root.Background = live ? Brushes.Transparent : Frozen(Color.FromRgb(224, 230, 233));
        }
    }

    internal static Grid Surface(Group group, DisplayArea display, FrameworkElement content) => CreateMaterial(group, display, content).Root;

    internal static Material CreateMaterial(Group group, DisplayArea display, FrameworkElement content)
    {
        // 圆角由宿主裁剪原生窗口区域；这里每个像素都有底色，避免子窗口透明合成空白。
        var surface = new Grid { Background = Frozen(Color.FromRgb(224, 230, 233)), ClipToBounds = true };
        var desktop = new Grid { IsHitTestVisible = false, Visibility = Visibility.Collapsed }; surface.Children.Add(desktop);
        var frost = (byte)Math.Round(255 * Math.Clamp(.035 + group.Tint * .32, .035, .36));
        var frostLayer = new Border { Background = Frozen(SystemParameters.HighContrast ? SystemColors.WindowColor : Color.FromArgb(frost, 237, 240, 242)), IsHitTestVisible = false }; surface.Children.Add(frostLayer);
        surface.Children.Add(new Border { Background = new LinearGradientBrush(
            Color.FromArgb(20, 255, 255, 255), Color.FromArgb(2, 230, 236, 240), 105), IsHitTestVisible = false });
        var outer = new Border { Margin = new Thickness(1), CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(1), BorderBrush = new LinearGradientBrush(
                Color.FromArgb(170, 255, 255, 255), Color.FromArgb(55, 255, 255, 255), 110), IsHitTestVisible = false }; surface.Children.Add(outer);
        var inner = new Border { Margin = new Thickness(3), CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(.7), BorderBrush = new LinearGradientBrush(
                Color.FromArgb(18, 49, 62, 72), Color.FromArgb(42, 255, 255, 255), 125), IsHitTestVisible = false }; surface.Children.Add(inner);
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

}
