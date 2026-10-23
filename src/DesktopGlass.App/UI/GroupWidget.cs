using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using DesktopGlass.App.Windows;

namespace DesktopGlass.App.UI;

/// <summary>仅占据分组矩形的独立桌面组件；所有数据和桌面变更均交由宿主回调处理。</summary>
internal sealed class GroupWidget : Window
{
    internal const string EntryFormat = "DesktopGlass.Entry";
    private readonly WidgetCallbacks callbacks;
    private readonly bool temporary;
    private readonly bool layoutMutable;
    private readonly int entryCount;
    private readonly Border dropHighlight;
    private readonly WidgetTheme.Material material;
    private readonly Grid expandedContent;
    private readonly Grid compactContent;
    private readonly TextBlock compactHeading;
    private readonly DispatcherTimer hoverTimer = new();
    private bool expanded, closed, dragging, sourceDragging, keyboardNavigation;
    private int openMenus;
    internal event Action<bool>? ExpansionRequested;
    internal DisplayArea Display { get; }
    internal bool Expanded => expanded;
    private TextBlock heading = null!;
    private Thumb resizeGrip = null!;

    /// <summary>建立一个分组快照；入口或锁定状态变化后由宿主完整重建组件。</summary>
    /// <param name="group">分组外观和 DIP 尺寸；不会由界面直接修改。</param>
    /// <param name="display">实际呈现屏幕，包含有效 DPI 缩放。</param>
    /// <param name="entries">已按组内顺序解析的入口引用。</param>
    /// <param name="catalog">原生图标来源。</param>
    /// <param name="locked">仅锁定布局，打开和内容管理仍可使用。</param>
    /// <param name="temporary">外屏缺失的临时呈现；禁止布局和分组内容变更。</param>
    /// <param name="callbacks">由宿主提供的完整命令集合。</param>
    internal GroupWidget(Group group, DisplayArea display, IReadOnlyList<DesktopEntry> entries,
        DesktopCatalog catalog, bool locked, bool temporary, WidgetCallbacks callbacks)
    {
        this.callbacks = callbacks; this.temporary = temporary; Display = display;
        layoutMutable = !locked && !temporary; entryCount = entries.Count;
        Title = group.Name; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false; ShowInTaskbar = false; ShowActivated = false; Focusable = true;
        Background = new SolidColorBrush(Color.FromRgb(173, 194, 206));
        FontFamily = new FontFamily("Microsoft YaHei UI"); UseLayoutRounding = true; SnapsToDevicePixels = true;
        Width = Math.Min(group.Width, display.Width / display.Scale);
        Height = Math.Min(group.Height, display.Height / display.Scale);
        Left = display.Left / display.Scale + Math.Clamp(temporary ? 24 : group.X, 0, Math.Max(0, display.Width / display.Scale - Width));
        Top = display.Top / display.Scale + Math.Clamp(temporary ? 24 : group.Y, 0, Math.Max(0, display.Height / display.Scale - Height));
        var layout = expandedContent = new Grid { Margin = new Thickness(20, 15, 20, 17) };
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(Header(group, locked));
        var items = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        var tileTemplate = WidgetTheme.TileTemplate();
        for (var index = 0; index < entries.Count; index++) items.Children.Add(Tile(entries[index], index, catalog, tileTemplate));
        var body = new Grid();
        var scroll = AppTheme.Scroll(items); scroll.Padding = new Thickness(0, 4, 0, 0); body.Children.Add(scroll);
        if (entries.Count == 0)
        {
            var empty = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                IsHitTestVisible = false, Margin = new Thickness(0, 0, 0, 10) };
            empty.Children.Add(AppTheme.Logo(38));
            empty.Children.Add(new TextBlock { Text = temporary ? "接回显示器后继续整理" : "拖入快捷方式或文件", FontSize = 12,
                Foreground = WidgetTheme.SecondaryInk, Margin = new Thickness(0, 7, 0, 0), TextAlignment = TextAlignment.Center });
            body.Children.Add(empty);
        }
        Grid.SetRow(body, 1); layout.Children.Add(body);
        var layers = new Grid(); layers.Children.Add(layout);
        compactContent = new Grid { Margin = new Thickness(14, 12, 14, 12) };
        compactContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(27) });
        compactContent.RowDefinitions.Add(new RowDefinition());
        compactContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(15) });
        compactHeading = new TextBlock { Text = group.Name, Foreground = WidgetTheme.Ink, FontSize = 12,
            FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        compactContent.Children.Add(compactHeading);
        var previews = new UniformGrid { Rows = 2, Columns = 2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (var entry in entries.Take(4))
        {
            var icon = catalog.Icon(entry.Path);
            FrameworkElement glyph = icon is null ? new TextBlock { Text = "◇", FontSize = 26, Foreground = WidgetTheme.SecondaryInk } :
                new Image { Source = icon, Width = 27, Height = 27, Stretch = Stretch.Uniform };
            previews.Children.Add(new Border { Width = 42, Height = 31, Child = glyph, ToolTip = entry.Name });
        }
        if (entries.Count == 0) { var mark = AppTheme.GroupMark(42); Grid.SetRow(mark, 1); compactContent.Children.Add(mark); }
        else { Grid.SetRow(previews, 1); compactContent.Children.Add(previews); }
        var count = new TextBlock { Text = temporary ? "显示器暂离" : entries.Count + " 个项目", FontSize = 9.5,
            Foreground = WidgetTheme.SecondaryInk, VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetRow(count, 2); compactContent.Children.Add(count); layers.Children.Add(compactContent);
        dropHighlight = new Border { Margin = new Thickness(5), CornerRadius = new CornerRadius(WidgetGeometry.Radius - 5),
            BorderThickness = new Thickness(1.5), BorderBrush = WidgetTheme.Focus, Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
            IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        layers.Children.Add(dropHighlight); layers.Children.Add(resizeGrip = ResizeGrip());
        material = WidgetTheme.CreateMaterial(WidgetGeometry.Appearance(group), display, layers); Content = material.Root; ContextMenu = GroupMenu(locked); AllowDrop = !temporary;
        hoverTimer.Tick += (_, _) => { hoverTimer.Stop(); EvaluateHover(); };
        MouseEnter += (_, _) => ScheduleHover(); MouseLeave += (_, _) => ScheduleHover();
        Loaded += (_, _) => ScheduleHover();
        PreviewKeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space or Key.Tab or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            { keyboardNavigation = true; ExpansionRequested?.Invoke(true); ScheduleHover(); } };
        // 桌面层禁止悬停、恢复时抢焦点；仅用户点击组件后主动激活，才允许后续键盘导航。
        PreviewMouseDown += (_, _) => { keyboardNavigation = false; Activate(); if (!expanded) Focus(); };
        IsKeyboardFocusWithinChanged += (_, _) => { if (!IsKeyboardFocusWithin) keyboardNavigation = false; ScheduleHover(); };
        LostMouseCapture += (_, _) => ScheduleHover(); Deactivated += (_, _) => ScheduleHover();
        Closed += (_, _) => { closed = true; hoverTimer.Stop(); ExpansionRequested = null; };
        DragEnter += (_, _) => { dragging = true; ExpansionRequested?.Invoke(true); };
        DragOver += (_, e) => { dragging = true; ExpansionRequested?.Invoke(true); ShowDrop(e); };
        DragLeave += (_, _) => { dragging = false; dropHighlight.Visibility = Visibility.Collapsed; ScheduleHover(); };
        Drop += (_, e) => ReceiveDrop(e, entryCount);
        ApplyAppearance(group); SetExpanded(false);
    }

    /// <summary>顶层浮动时切换为系统实时材质；系统不可用时使用中性色底，不伪装为壁纸透视。</summary>
    internal void SetForegroundMaterial(WindowBackdrop.Result result) => material.SetForeground(result.Enabled);

    /// <summary>只更新外观与裁剪提示，不重建窗口、入口或正在进行的几何手势。</summary>
    internal void ApplyAppearance(Group group)
    {
        Title = group.Name; heading.Text = group.Name; compactHeading.Text = group.Name; material.ApplyAppearance(WidgetGeometry.Appearance(group));
        dropHighlight.CornerRadius = new CornerRadius(WidgetGeometry.Radius - 5);
        // 标记整体内移到圆弧内部，避免线段跨过右下圆弧造成不对称错觉。
        var inset = Math.Max(10, WidgetGeometry.Radius * (1 - 1 / Math.Sqrt(2)) + 5);
        resizeGrip.Margin = new Thickness(0, 0, inset, inset);
    }

    /// <summary>原位切换紧凑预览和完整网格，保留入口控件及命令回调。</summary>
    internal void SetExpanded(bool value)
    {
        expanded = value;
        expandedContent.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        compactContent.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        resizeGrip.Visibility = value && layoutMutable ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool PointerInside => WidgetHost.PointerInside(new System.Windows.Interop.WindowInteropHelper(this).Handle);
    private bool KeepExpanded => openMenus > 0 || dragging || sourceDragging || IsMouseCaptureWithin || (keyboardNavigation && IsKeyboardFocusWithin);
    private void ScheduleHover()
    {
        if (closed) return;
        hoverTimer.Stop();
        hoverTimer.Interval = TimeSpan.FromMilliseconds(PointerInside ? 120 : 450); hoverTimer.Start();
    }
    private void EvaluateHover()
    {
        if (closed) return;
        if (PointerInside || KeepExpanded) ExpansionRequested?.Invoke(true);
        else ExpansionRequested?.Invoke(false);
    }
    private void TrackMenu(ContextMenu menu)
    {
        menu.Opened += (_, _) => { openMenus++; hoverTimer.Stop(); ExpansionRequested?.Invoke(true); };
        menu.Closed += (_, _) => { openMenus = Math.Max(0, openMenus - 1); ScheduleHover(); };
    }

    private Grid Header(Group group, bool locked)
    {
        var header = new Grid(); header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        labels.Children.Add(heading = new TextBlock { Text = group.Name, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = WidgetTheme.Ink, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(1, 0, 8, 2) });
        labels.Children.Add(new TextBlock { Text = temporary ? "显示器暂离" : entryCount + " 个项目" + (locked ? " · 布局已锁定" : ""),
            FontSize = 10.5, Foreground = WidgetTheme.SecondaryInk, Margin = new Thickness(1, 0, 0, 0) });
        header.Children.Add(labels);
        var move = TransparentThumb(layoutMutable ? Cursors.SizeAll : Cursors.Arrow); move.IsEnabled = layoutMutable;
        move.ToolTip = temporary ? "接回显示器后可调整布局" : locked ? "布局已锁定" : "拖动分组";
        move.DragStarted += (_, _) => { dragging = true; ExpansionRequested?.Invoke(true); };
        move.DragDelta += (_, e) => { if (layoutMutable) callbacks.MoveBy(e.HorizontalChange, e.VerticalChange); };
        move.DragCompleted += (_, e) => { if (layoutMutable) callbacks.CommitBounds(e.Canceled); dragging = false; ScheduleHover(); };
        header.Children.Add(move);
        var more = WidgetTheme.MoreButton(); more.HorizontalAlignment = HorizontalAlignment.Right; more.VerticalAlignment = VerticalAlignment.Top;
        more.Click += (_, _) =>
        {
            var menu = GroupMenu(locked); menu.PlacementTarget = more; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
        };
        Grid.SetColumn(more, 1); header.Children.Add(more); return header;
    }

    private Button Tile(DesktopEntry entry, int index, DesktopCatalog catalog, ControlTemplate template)
    {
        var contents = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        var icon = catalog.Icon(entry.Path);
        if (icon is not null)
            contents.Children.Add(new Image { Source = icon, Width = 40, Height = 40, Margin = new Thickness(0, 5, 0, 7), Stretch = Stretch.Uniform });
        else
            contents.Children.Add(new TextBlock { Text = "◇", FontSize = 34, Height = 52, TextAlignment = TextAlignment.Center, Foreground = WidgetTheme.SecondaryInk });
        contents.Children.Add(new TextBlock { Text = entry.Name, FontSize = 11, LineHeight = 14, Height = 30,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = WidgetTheme.Ink, Margin = new Thickness(1, 0, 1, 0) });
        var tile = new Button { Content = contents, Width = 74, Height = 94, Padding = new Thickness(0), Template = template,
            Cursor = Cursors.Hand, ToolTip = entry.Name + "\n" + entry.Path, AllowDrop = !temporary,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        System.Windows.Automation.AutomationProperties.SetName(tile, entry.Name);
        Point press = default; var dragArmed = false;
        tile.PreviewMouseLeftButtonDown += (_, e) =>
        {
            press = e.GetPosition(tile); dragArmed = !temporary;
            if (e.ClickCount != 2) return;
            dragArmed = false; callbacks.Open(entry.Path); e.Handled = true;
        };
        tile.PreviewMouseLeftButtonUp += (_, _) => dragArmed = false;
        tile.PreviewMouseMove += (_, e) =>
        {
            if (!dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
            var distance = e.GetPosition(tile) - press;
            if (Math.Abs(distance.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(distance.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            dragArmed = false;
            // 内部拖拽只传引用；成功或取消均不在源组件中移除真实文件或列表项。
            sourceDragging = true;
            try { DragDrop.DoDragDrop(tile, new DataObject(EntryFormat, entry.Path), DragDropEffects.Link); }
            finally { sourceDragging = false; ScheduleHover(); }
            e.Handled = true;
        };
        tile.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            callbacks.Open(entry.Path); e.Handled = true;
        };
        tile.Drop += (_, e) => ReceiveDrop(e, index);
        var menu = AppTheme.CreateContextMenu(); menu.Items.Add(Menu("打开", () => callbacks.Open(entry.Path)));
        menu.Items.Add(Menu("移出分组", () => callbacks.Release(entry.Path), !temporary));
        TrackMenu(menu); tile.ContextMenu = menu; return tile;
    }

    private Thumb ResizeGrip()
    {
        var thumb = TransparentThumb(Cursors.SizeNWSE); thumb.Width = 22; thumb.Height = 22;
        thumb.HorizontalAlignment = HorizontalAlignment.Right; thumb.VerticalAlignment = VerticalAlignment.Bottom;
        thumb.IsEnabled = layoutMutable; thumb.Visibility = layoutMutable ? Visibility.Visible : Visibility.Collapsed;
        thumb.Margin = new Thickness(0, 0, 10, 10); thumb.ToolTip = "调整分组大小";
        var drawing = new GeometryDrawing(null, new Pen(WidgetTheme.SecondaryInk, 1.4), Geometry.Parse("M 10,19 L 19,10 M 15,19 L 19,15"));
        thumb.Background = new DrawingBrush(drawing) { Stretch = Stretch.None, AlignmentX = AlignmentX.Right, AlignmentY = AlignmentY.Bottom, Opacity = .60 };
        thumb.DragStarted += (_, _) => { dragging = true; ExpansionRequested?.Invoke(true); };
        thumb.DragDelta += (_, e) => { if (layoutMutable) callbacks.ResizeBy(e.HorizontalChange, e.VerticalChange); };
        thumb.DragCompleted += (_, e) => { if (layoutMutable) callbacks.CommitBounds(e.Canceled); dragging = false; ScheduleHover(); }; return thumb;
    }

    private ContextMenu GroupMenu(bool locked)
    {
        var menu = AppTheme.CreateContextMenu(); menu.Items.Add(Menu("名称与玻璃外观…", callbacks.Edit, !temporary));
        menu.Items.Add(Menu("新建分组", callbacks.Create)); menu.Items.Add(Menu(locked ? "解锁布局" : "锁定布局", callbacks.ToggleLock));
        menu.Items.Add(new Separator()); menu.Items.Add(Menu("移除分组（保留文件）", callbacks.Remove, !temporary));
        menu.Items.Add(new Separator()); menu.Items.Add(Menu("退出桌面分组", callbacks.Exit)); TrackMenu(menu); return menu;
    }

    private void ShowDrop(DragEventArgs e)
    {
        if (e.Handled) return;
        e.Effects = !temporary && ReadPaths(e.Data).Count > 0 ? AllowedEffect(e.AllowedEffects) : DragDropEffects.None;
        dropHighlight.Visibility = e.Effects == DragDropEffects.None ? Visibility.Collapsed : Visibility.Visible; e.Handled = true;
    }

    private void ReceiveDrop(DragEventArgs e, int index)
    {
        if (e.Handled) return;
        dragging = false; ScheduleHover();
        dropHighlight.Visibility = Visibility.Collapsed; var paths = ReadPaths(e.Data); var effect = AllowedEffect(e.AllowedEffects);
        e.Handled = true; e.Effects = DragDropEffects.None;
        if (temporary || paths.Count == 0 || effect == DragDropEffects.None) return;
        if (callbacks.Collect(paths, index)) e.Effects = effect;
    }

    private static IReadOnlyList<string> ReadPaths(IDataObject data)
    {
        if (data.GetDataPresent(EntryFormat) && data.GetData(EntryFormat) is string entry && !string.IsNullOrWhiteSpace(entry)) return [entry];
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths)
            return paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        // 虚拟 Shell 数据由宿主另行集成；不得误把未知格式当作真实文件路径。
        return [];
    }

    private static DragDropEffects AllowedEffect(DragDropEffects allowed)
    {
        if ((allowed & DragDropEffects.Link) != 0) return DragDropEffects.Link;
        if ((allowed & DragDropEffects.Copy) != 0) return DragDropEffects.Copy;
        return DragDropEffects.None;
    }

    private static Thumb TransparentThumb(Cursor cursor)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
            { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        return new Thumb { Cursor = cursor, Background = Brushes.Transparent, Template = new ControlTemplate(typeof(Thumb)) { VisualTree = border } };
    }

    private static MenuItem Menu(string text, Action action, bool enabled = true)
    {
        var glyph = text switch { "打开" => "\uE8E5", "移出分组" => "\uE8B5", "新建分组" => "\uE710", "退出桌面分组" => "\uE8AC", _ => text.StartsWith("移除", StringComparison.Ordinal) ? "\uE74D" : text.Contains("锁", StringComparison.Ordinal) ? "\uE72E" : "\uE790" };
        return AppTheme.MenuItem(text, action, glyph, enabled, text.StartsWith("移除", StringComparison.Ordinal));
    }
}
