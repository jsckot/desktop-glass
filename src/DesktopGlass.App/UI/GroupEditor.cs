using DesktopGlass.App.Windows;

namespace DesktopGlass.App.UI;

/// <summary>编辑独立外观草稿并同步预览；持久化及关闭回滚由控制器负责。</summary>
internal sealed class GroupEditor : Window
{
    private bool fittingWorkArea, workAreaQueued, editorClosed;
    private double initialHeightLimit = double.NaN;
    /// <summary>创建外观编辑器；调用方可传入独立草稿，以事务方式持久化。</summary>
    /// <param name="group">待编辑分组或草稿。</param>
    /// <param name="save">保存回调；失败应抛异常，窗口会保留输入并显示原因。</param>
    /// <param name="displays">创建分组时可选的屏幕；编辑外观时为 null。</param>
    /// <param name="previewAppearance">接收独立外观快照，不得持久化；关闭时调用方清理覆盖。</param>
    internal GroupEditor(Group group, Action save, IReadOnlyList<DisplayArea>? displays = null, Action<Group>? previewAppearance = null)
    {
        AppTheme.Apply(this); Title = displays is null ? "分组外观" : "新建分组"; Width = 440; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : new SolidColorBrush(Color.FromRgb(239, 246, 250)); Foreground = WidgetTheme.Ink;
        FontFamily = new FontFamily("Microsoft YaHei UI"); ShowInTaskbar = false;
        var body = new StackPanel { Margin = new Thickness(26, 24, 26, 23) };
        body.Children.Add(new TextBlock { Text = displays is null ? "分组外观" : "创建桌面分组", FontSize = 21, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = "调整名称与玻璃浓度", FontSize = 12,
            Foreground = WidgetTheme.SecondaryInk, Margin = new Thickness(0, 7, 0, 23) });
        body.Children.Add(Label("分组名称"));
        var name = new TextBox { Text = group.Name, MaxLength = 40, Padding = new Thickness(11, 9, 11, 9),
            FontSize = 14, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Center };
        body.Children.Add(new Border { Child = name, CornerRadius = new CornerRadius(9), Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : Brushes.White,
            BorderBrush = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromRgb(196, 214, 225)), BorderThickness = new Thickness(1) });
        ComboBox? screenChoice = null;
        if (displays is not null)
        {
            body.Children.Add(Label("显示到"));
            screenChoice = new ComboBox { ItemsSource = displays, DisplayMemberPath = "Name", SelectedValuePath = "Id",
                SelectedValue = group.DisplayId, Padding = new Thickness(10), Margin = new Thickness(0, 4, 0, 0) };
            body.Children.Add(screenChoice);
        }
        var previewName = new TextBlock { Text = group.Name, FontSize = 15, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(20, 18, 20, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var previewContent = new Grid(); previewContent.Children.Add(previewName);
        previewContent.Children.Add(new TextBlock { Text = "桌面分组", Foreground = WidgetTheme.SecondaryInk, FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 22, 0, 0) });
        var previewDisplay = (displays ?? Native.Displays()).FirstOrDefault(d => d.Id == group.DisplayId) ?? Native.Displays().First();
        var previewMaterial = WidgetTheme.CreateMaterial(WidgetGeometry.Appearance(group), previewDisplay, previewContent);
        var preview = new Border { Height = 154, Margin = new Thickness(0, 18, 0, 20), Child = previewMaterial.Root };
        body.Children.Add(preview);
        body.Children.Add(new TextBlock { Text = displays is null ? "显示中的桌面分组会同步预览" : "保存后显示到桌面", FontSize = 11, Foreground = WidgetTheme.SecondaryInk, Margin = new Thickness(0, -10, 0, 18) });
        var tint = new Slider { Minimum = .12, Maximum = .85, Value = group.Tint, TickFrequency = .01,
            Margin = new Thickness(0, 9, 0, 17), IsMoveToPointEnabled = true };
        var tintValue = new TextBlock { Foreground = WidgetTheme.SecondaryInk, FontSize = 12 };
        body.Children.Add(LabeledValue("玻璃浓度", tintValue)); body.Children.Add(tint);
        AppTheme.StyleSlider(tint);
        void UpdatePreview()
        {
            previewName.Text = string.IsNullOrWhiteSpace(name.Text) ? "新分组" : name.Text;
            preview.CornerRadius = new CornerRadius(WidgetGeometry.Radius);
            preview.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, preview.ActualWidth), preview.Height), WidgetGeometry.Radius, WidgetGeometry.Radius);
            previewMaterial.ApplyAppearance(new Group { Tint = tint.Value, Radius = WidgetGeometry.Radius });
            previewAppearance?.Invoke(new Group { Name = previewName.Text, Tint = tint.Value, Radius = WidgetGeometry.Radius });
            tintValue.Text = Math.Round(tint.Value * 100) + "%";
        }
        name.TextChanged += (_, _) => UpdatePreview(); tint.ValueChanged += (_, _) => UpdatePreview();
        preview.SizeChanged += (_, _) => UpdatePreview();
        UpdatePreview();
        var errorNotice = new TextBlock { Foreground = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : new SolidColorBrush(Color.FromRgb(155, 50, 50)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), Visibility = Visibility.Collapsed };
        body.Children.Add(errorNotice);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Button("取消", false); cancel.IsCancel = true; cancel.Click += (_, _) => Close(); buttons.Children.Add(cancel);
        var confirm = Button("保存更改", true); confirm.IsDefault = true;
        confirm.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { name.Focus(); return; }
            var previous = (group.Name, group.Tint, group.Radius, group.DisplayId);
            try { group.Name = name.Text.Trim(); group.Tint = tint.Value; if (screenChoice?.SelectedValue is string selected) group.DisplayId = selected; save(); Close(); }
            catch (Exception error)
            {
                (group.Name, group.Tint, group.Radius, group.DisplayId) = previous;
                UpdatePreview();
                errorNotice.Text = "无法保存：" + error.Message; errorNotice.Visibility = Visibility.Visible;
            }
        };
        buttons.Children.Add(confirm);
        // 操作区固定在底部；低分辨率工作区只滚动表单，保存和取消始终可达。
        buttons.Margin = new Thickness(26, 0, 26, 23);
        var shell = new DockPanel(); DockPanel.SetDock(buttons, Dock.Bottom); shell.Children.Add(buttons);
        shell.Children.Add(AppTheme.Scroll(body)); Content = shell;
        Loaded += (_, _) => { FitCurrentWorkArea(); name.Focus(); name.SelectAll(); };
        LocationChanged += (_, _) => QueueWorkAreaFit();
        SizeChanged += (_, _) => QueueWorkAreaFit();
        DpiChanged += (_, _) => QueueWorkAreaFit();
        Closed += (_, _) => editorClosed = true;
    }

    private void QueueWorkAreaFit()
    {
        if (!IsLoaded || editorClosed || fittingWorkArea || workAreaQueued) return;
        workAreaQueued = true;
        Dispatcher.BeginInvoke(() => { workAreaQueued = false; if (!editorClosed) FitCurrentWorkArea(); }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void FitCurrentWorkArea()
    {
        if (fittingWorkArea || editorClosed) return;
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == 0) return;
        fittingWorkArea = true;
        try
        {
            var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            var workArea = new Rect(area.Left, area.Top, area.Width, area.Height);
            var scale = WidgetHost.WindowDpi(handle) / 96.0;
            if (double.IsNaN(initialHeightLimit)) initialHeightLimit = MaxHeight;
            var fit = FitWorkArea(WidgetHost.ReadWindowBounds(handle), workArea, scale);
            MaxHeight = Math.Min(initialHeightLimit, fit.MaxHeight);
            UpdateLayout();
            // 非客户区和 HWND 均使用物理坐标；混合 DPI 下不以全局 DIP 坐标推断副屏原点。
            var bounds = WidgetHost.ReadWindowBounds(handle);
            fit = FitWorkArea(bounds, workArea, scale);
            if (bounds.Location != fit.Origin && !Native.SetWindowPos(handle, 0, (int)Math.Round(fit.Origin.X), (int)Math.Round(fit.Origin.Y), 0, 0, 0x1 | 0x4 | 0x10))
                throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError(), "编辑器定位到当前工作区失败。");
        }
        finally { fittingWorkArea = false; }
    }

    /// <summary>依据选定屏幕的物理工作区及实际窗口 DPI 计算逻辑限高和可见位置；支持负坐标副屏。</summary>
    internal static (double MaxHeight, Point Origin) FitWorkArea(Rect bounds, Rect workArea, double scale)
    {
        if (workArea.IsEmpty || workArea.Width <= 0 || workArea.Height <= 0 || !double.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(workArea));
        return (workArea.Height / scale, new Point(
            Math.Clamp(bounds.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - bounds.Width)),
            Math.Clamp(bounds.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - bounds.Height))));
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
