using System.Windows.Data;

namespace DesktopGlass.App.UI;

/// <summary>编辑分组名称和静态玻璃外观；保存失败保留输入，取消不修改分组。</summary>
internal sealed class GroupEditor : Window
{
    /// <summary>创建外观编辑器；调用方可传入独立草稿，以事务方式持久化。</summary>
    /// <param name="group">待编辑分组或草稿。</param>
    /// <param name="save">保存回调；失败应抛异常，窗口会保留输入并显示原因。</param>
    internal GroupEditor(Group group, Action save, IReadOnlyList<DisplayArea>? displays = null)
    {
        AppTheme.Apply(this); Title = displays is null ? "分组外观" : "新建分组"; Width = 440; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(239, 246, 250)); Foreground = WidgetTheme.Ink;
        FontFamily = new FontFamily("Microsoft YaHei UI"); ShowInTaskbar = false;
        var body = new StackPanel { Margin = new Thickness(26, 24, 26, 23) };
        body.Children.Add(new TextBlock { Text = displays is null ? "分组外观" : "创建桌面分组", FontSize = 21, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = "调整名称、玻璃浓度和圆角", FontSize = 12,
            Foreground = WidgetTheme.SecondaryInk, Margin = new Thickness(0, 7, 0, 23) });
        body.Children.Add(Label("分组名称"));
        var name = new TextBox { Text = group.Name, MaxLength = 40, Padding = new Thickness(11, 9, 11, 9),
            FontSize = 14, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Center };
        body.Children.Add(new Border { Child = name, CornerRadius = new CornerRadius(9), Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(196, 214, 225)), BorderThickness = new Thickness(1) });
        ComboBox? screenChoice = null;
        if (displays is not null)
        {
            body.Children.Add(Label("显示到"));
            screenChoice = new ComboBox { ItemsSource = displays, DisplayMemberPath = "Name", SelectedValuePath = "Id",
                SelectedValue = group.DisplayId, Padding = new Thickness(10), Margin = new Thickness(0, 4, 0, 0) };
            body.Children.Add(screenChoice);
        }
        var previewName = new TextBlock { Text = group.Name, FontSize = 15, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(17, 0, 17, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var preview = new Border { Height = 62, Margin = new Thickness(0, 18, 0, 20), BorderThickness = new Thickness(1),
            BorderBrush = Brushes.White, Child = previewName };
        body.Children.Add(preview);
        var tint = new Slider { Minimum = .12, Maximum = .85, Value = group.Tint, TickFrequency = .01,
            Margin = new Thickness(0, 9, 0, 17), IsMoveToPointEnabled = true };
        var tintValue = new TextBlock { Foreground = WidgetTheme.SecondaryInk, FontSize = 12 };
        body.Children.Add(LabeledValue("玻璃浓度", tintValue)); body.Children.Add(tint);
        var radius = new Slider { Minimum = 0, Maximum = 48, Value = group.Radius, TickFrequency = 1,
            Margin = new Thickness(0, 9, 0, 22), IsMoveToPointEnabled = true };
        var radiusValue = new TextBlock { Foreground = WidgetTheme.SecondaryInk, FontSize = 12 };
        AppTheme.StyleSlider(tint); AppTheme.StyleSlider(radius);
        body.Children.Add(LabeledValue("圆角", radiusValue)); body.Children.Add(radius);
        void UpdatePreview()
        {
            previewName.Text = string.IsNullOrWhiteSpace(name.Text) ? "新分组" : name.Text;
            preview.CornerRadius = new CornerRadius(Math.Min(25, radius.Value));
            preview.Background = new LinearGradientBrush(Color.FromRgb(244, 251, 255),
                Color.FromRgb((byte)(179 + tint.Value * 65), (byte)(204 + tint.Value * 42), (byte)(218 + tint.Value * 30)), 100);
            tintValue.Text = Math.Round(tint.Value * 100) + "%"; radiusValue.Text = Math.Round(radius.Value) + " DIP";
        }
        name.TextChanged += (_, _) => UpdatePreview(); tint.ValueChanged += (_, _) => UpdatePreview(); radius.ValueChanged += (_, _) => UpdatePreview();
        UpdatePreview();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Button("取消", false); cancel.IsCancel = true; cancel.Click += (_, _) => Close(); buttons.Children.Add(cancel);
        var confirm = Button("保存更改", true); confirm.IsDefault = true;
        confirm.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { name.Focus(); return; }
            var previous = (group.Name, group.Tint, group.Radius);
            try { group.Name = name.Text.Trim(); group.Tint = tint.Value; group.Radius = radius.Value; if (screenChoice?.SelectedValue is string selected) group.DisplayId = selected; save(); Close(); }
            catch (Exception error)
            {
                (group.Name, group.Tint, group.Radius) = previous;
                MessageBox.Show(this, error.Message, "无法保存分组", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        buttons.Children.Add(confirm); body.Children.Add(buttons); Content = body;
        Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
    }

    private static TextBlock Label(string text) => new() { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 7) };
    private static Grid LabeledValue(string text, TextBlock value)
    {
        var grid = new Grid(); grid.Children.Add(Label(text)); value.HorizontalAlignment = HorizontalAlignment.Right; grid.Children.Add(value); return grid;
    }
    private static Button Button(string text, bool primary)
    {
        var button = AppTheme.Button(text, primary); button.Margin = new Thickness(8,0,0,0); return button;
    }
}
