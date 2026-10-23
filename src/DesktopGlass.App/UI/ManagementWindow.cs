using System.ComponentModel;

namespace DesktopGlass.App.UI;

/// <summary>桌面分组的唯一管理入口；显示快照和发出命令，不直接操作文件、租约或持久配置。</summary>
internal sealed class ManagementWindow : Window
{
    private readonly Func<Workspace> snapshot;
    private readonly Func<IReadOnlyList<DisplayArea>> screens;
    private readonly Action create, exit, lockLayout;
    private readonly Action<string> edit, remove, move;
    private readonly Action<string, bool> toggle;
    private readonly Action? foreground;
    private readonly Func<bool> isForeground;
    private readonly Dictionary<string, Button> navigation = [];
    private readonly StackPanel rows = new();
    private readonly TextBox search = new();
    private readonly TextBlock count = AppTheme.Text("", 12, AppTheme.Muted);
    private readonly ContentControl content = new();
    private bool allowClose;
    private TextBlock? searchHint;
    private string page = "分组";

    internal ManagementWindow(Func<Workspace> snapshot, Func<IReadOnlyList<DisplayArea>> screens,
        Action create, Action<string> edit, Action<string> remove, Action<string> move, Action<string, bool> toggle,
        Action lockLayout, Action exit, Action? foreground = null, Func<bool>? isForeground = null)
    {
        this.snapshot = snapshot; this.screens = screens; this.create = create; this.edit = edit;
        this.remove = remove; this.move = move; this.toggle = toggle; this.exit = exit; this.lockLayout = lockLayout;
        this.foreground = foreground; this.isForeground = isForeground ?? (() => false);
        Title = "Desktop Glass"; Width = 960; Height = 680; MinWidth = 820; MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterScreen; AppTheme.Apply(this);
        var root = new Grid { Margin = new Thickness(20) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(184) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var sidebar = new DockPanel { Margin = new Thickness(0, 4, 20, 0) };
        var brand = new StackPanel { Margin = new Thickness(12, 10, 0, 32) };
        var logo = AppTheme.Logo(50); logo.HorizontalAlignment = HorizontalAlignment.Left; brand.Children.Add(logo);
        var title = AppTheme.Text("Desktop Glass", 17); title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0, 13, 0, 3);
        brand.Children.Add(title); brand.Children.Add(AppTheme.Text("有序，仍然通透", 11, AppTheme.Muted));
        DockPanel.SetDock(brand, Dock.Top); sidebar.Children.Add(brand);
        var footer = new StackPanel { Margin = new Thickness(3,12,3,4) };
        footer.Children.Add(AppTheme.Text("本地运行 · 随桌面常驻", 10, AppTheme.Muted));
        var quit = AppTheme.Button("恢复项目并退出"); quit.Margin = new Thickness(0, 15, 0, 0); quit.Click += (_, _) => exit();
        footer.Children.Add(quit); DockPanel.SetDock(footer, Dock.Bottom); sidebar.Children.Add(footer);
        var nav = new StackPanel();
        foreach (var entry in new[] { "分组", "外观", "关于" })
        {
            var button = AppTheme.Button(entry); button.Margin = new Thickness(0, 0, 0, 6);
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new TextBlock { Text = entry == "分组" ? "\uE8F1" : entry == "外观" ? "\uE790" : "\uE946", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,12,0) });
            label.Children.Add(AppTheme.Text(entry, 13)); button.Content = label;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            navigation.Add(entry, button);
            button.Click += (_, _) => { page = entry; Refresh(); }; nav.Children.Add(button);
        }
        sidebar.Children.Add(nav); root.Children.Add(sidebar);
        var well = AppTheme.Card(content, 22); Grid.SetColumn(well, 1); root.Children.Add(well); Content = root;
        search.TextChanged += (_, _) => { if (searchHint is not null) searchHint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; if (page == "分组") RenderRows(); };
        Closing += OnClosing; Refresh();
    }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!allowClose) { e.Cancel = true; Hide(); } }
    internal void Finish() { allowClose = true; Close(); }

    /// <summary>重新读取控制器快照并刷新当前页面；不改变任何分组状态。</summary>
    internal void Refresh()
    {
        foreach (var (name, button) in navigation)
        {
            button.Background = SystemParameters.HighContrast ? SystemColors.ControlBrush : name == page ? AppTheme.Brush("#DBE9F6") : Brushes.Transparent;
            button.BorderBrush = SystemParameters.HighContrast ? SystemColors.ControlTextBrush : name == page ? AppTheme.Brush("#C9DBED") : Brushes.Transparent;
            button.FontWeight = name == page ? FontWeights.SemiBold : FontWeights.Normal;
        }
        if (page == "分组") Groups();
        else if (page == "外观") Appearance();
        else About();
    }
    private StackPanel Page(string title, string subtitle)
    {
        var body = new StackPanel { Margin = new Thickness(30, 28, 30, 24) };
        var heading = AppTheme.Text(title, 25); heading.FontWeight = FontWeights.SemiBold;
        body.Children.Add(heading);
        var description = AppTheme.Text(subtitle, 12, AppTheme.Muted); description.Margin = new Thickness(0, 8, 0, 24);
        body.Children.Add(description); return body;
    }
    private void Groups()
    {
        var body = new DockPanel { Margin = new Thickness(28) };
        var header = new DockPanel();
        var add = AppTheme.Button("＋ 新建分组", true); add.Click += (_, _) => create(); DockPanel.SetDock(add, Dock.Right); header.Children.Add(add);
        var title = AppTheme.Text("桌面分组", 25); title.FontWeight = FontWeights.SemiBold; header.Children.Add(title);
        DockPanel.SetDock(header, Dock.Top); body.Children.Add(header);
        var toolbar = new StackPanel { Margin = new Thickness(0, 12, 0, 8) };
        if (count.Parent is Panel oldCount) oldCount.Children.Remove(count); count.Margin = new Thickness(0, 0, 0, 20); toolbar.Children.Add(count);
        // 同一搜索框会被重新挂接；刷新列表保留用户输入。
        if (search.Parent is Panel old) old.Children.Remove(search);
        search.Padding = new Thickness(12, 10, 12, 10); search.FontSize = 12; search.Background = Brushes.Transparent;
        search.BorderThickness = new Thickness(0); search.ToolTip = "按分组名称查找";
        System.Windows.Automation.AutomationProperties.SetName(search, "查找分组");
        var searchBox = new Grid();
        searchBox.Children.Add(search);
        searchHint = AppTheme.Text("查找分组",12,AppTheme.Muted); searchHint.Margin = new Thickness(12,10,0,0); searchHint.IsHitTestVisible = false;
        searchHint.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        searchBox.Children.Add(searchHint);
        var tools = new Grid(); tools.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tools.Children.Add(new Border { Child = searchBox, CornerRadius = new CornerRadius(9), Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : AppTheme.Brush("#EDF2F8"), BorderBrush = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : AppTheme.Brush("#E0E8F1"), BorderThickness = new Thickness(1) });
        if (foreground is not null)
        {
            var mode = AppTheme.Button(isForeground() ? "放回桌面" : "临时浮到应用前面");
            mode.HorizontalAlignment = HorizontalAlignment.Left; mode.Margin = new Thickness(10,0,0,0);
            mode.ToolTip = "仅本次运行有效，下次启动仍贴在桌面";
            mode.Click += (_, _) => foreground(); Grid.SetColumn(mode, 1); tools.Children.Add(mode);
        }
        toolbar.Children.Add(tools);
        var caption = AppTheme.Text("分组 / 显示状态", 11, AppTheme.Muted); caption.Margin = new Thickness(2,22,0,2); toolbar.Children.Add(caption);
        DockPanel.SetDock(toolbar, Dock.Top); body.Children.Add(toolbar);
        if (rows.Parent is ScrollViewer previous) previous.Content = null;
        body.Children.Add(AppTheme.Scroll(rows));
        content.Content = body; RenderRows();
    }
    private void RenderRows()
    {
        rows.Children.Clear();
        var workspace = snapshot(); var displays = screens();
        count.Text = workspace.Groups.Count == 0 ? "手动创建分组，按自己的习惯整理。" :
            WorkspaceVisibility.VisibleGroups(workspace, displays).Count() + " 个正在桌面显示 · 共 " + workspace.Groups.Count + " 个分组";
        var filtered = workspace.Groups.Where(g => g.Name.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (filtered.Length == 0)
        {
            var empty = new StackPanel { Margin = new Thickness(10, 46, 10, 30), HorizontalAlignment = HorizontalAlignment.Center };
            empty.Children.Add(AppTheme.Logo(105));
            var title = AppTheme.Text(workspace.Groups.Count == 0 ? "桌面，从留白开始" : "没有找到分组", 20);
            title.FontWeight = FontWeights.SemiBold; title.HorizontalAlignment = HorizontalAlignment.Center; title.Margin = new Thickness(0, 24, 0, 10); empty.Children.Add(title);
            var note = AppTheme.Text(workspace.Groups.Count == 0 ? "创建一个分组，再把桌面的快捷方式或文件拖进去。" : "试试其他名称。", 12, AppTheme.Muted);
            note.MaxWidth = 310; note.TextAlignment = TextAlignment.Center; empty.Children.Add(note);
            if (workspace.Groups.Count == 0) { var add = AppTheme.Button("创建第一个分组", true); add.HorizontalAlignment = HorizontalAlignment.Center; add.Margin = new Thickness(0, 25, 0, 0); add.Click += (_, _) => create(); empty.Children.Add(add); }
            rows.Children.Add(empty); return;
        }
        foreach (var group in filtered)
        {
            var connected = displays.Count(d => d.Id == group.DisplayId) == 1;
            var box = new Grid { Margin = new Thickness(2, 17, 2, 17) };
            box.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            box.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,12,0) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var toggleButton = AppTheme.Button(group.DesktopVisible ? "隐藏" : "显示", !group.DesktopVisible);
            toggleButton.IsEnabled = connected || group.DesktopVisible;
            toggleButton.ToolTip = connected ? "显示或隐藏此分组" : "先移到当前屏幕，或接回原显示器";
            toggleButton.Click += (_, _) => toggle(group.Id, !group.DesktopVisible);
            toggleButton.Padding = new Thickness(12,7,12,7);
            var title = AppTheme.Text(group.Name, 15); title.FontWeight = FontWeights.SemiBold;
            title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; title.ToolTip = group.Name;
            details.Children.Add(title);
            var status = AppTheme.Text(group.Entries.Count + " 个项目 · " + (!connected ? "原显示器未连接" : group.DesktopVisible ? "正在显示" : "已暂停"), 11, AppTheme.Muted);
            status.Margin = new Thickness(0, 6, 0, 0); details.Children.Add(status); box.Children.Add(details);
            var appearance = AppTheme.Button("外观"); appearance.Padding = new Thickness(12,7,12,7); appearance.Margin = new Thickness(0,0,7,0); appearance.Click += (_, _) => edit(group.Id);
            actions.Children.Add(appearance); actions.Children.Add(toggleButton);
            var more = AppTheme.Button("⋯"); more.FontSize = 18; more.Padding = new Thickness(11,3,11,3); more.Margin = new Thickness(7,0,0,0); more.ToolTip = "更多分组操作";
            System.Windows.Automation.AutomationProperties.SetName(more, "更多分组操作：" + group.Name);
            var menu = AppTheme.CreateContextMenu(); menu.PlacementTarget = more; menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.Items.Add(AppTheme.MenuItem("移到当前屏幕", () => move(group.Id), "\uE7F4")); menu.Items.Add(new Separator());
            menu.Items.Add(AppTheme.MenuItem("移除分组…", () => remove(group.Id), "\uE74D", destructive: true));
            more.Click += (_, _) => menu.IsOpen = true; actions.Children.Add(more);
            Grid.SetColumn(actions, 1); box.Children.Add(actions);
            rows.Children.Add(new Border { BorderBrush = SystemParameters.HighContrast ? SystemColors.ControlTextBrush : AppTheme.Brush("#E0E8F1"), BorderThickness = new Thickness(0,0,0,1), Child = box });
        }
    }
    private void Appearance()
    {
        var body = Page("外观与布局", "每个分组各有边界，桌面保留自己的底色。");
        var title = AppTheme.Text("按分组调整玻璃", 18); title.Margin = new Thickness(0, 8, 0, 10); body.Children.Add(title);
        body.Children.Add(AppTheme.Text("在分组的“外观”中调整玻璃浓度、圆角与尺寸。拖动滑块即可在桌面预览，保存后保留设置。", 13, AppTheme.Muted));
        var configure = AppTheme.Button("选择要调整的分组"); configure.HorizontalAlignment = HorizontalAlignment.Left; configure.Margin = new Thickness(0,18,0,26); configure.Click += (_, _) => { page = "分组"; Refresh(); }; body.Children.Add(configure);
        body.Children.Add(new Border { Height = 1, Background = AppTheme.Brush("#DCE5EF"), Margin = new Thickness(0,0,0,24) });
        body.Children.Add(AppTheme.Text("固定桌面布局", 18));
        var layoutNote = AppTheme.Text(snapshot().Locked ? "布局已锁定，避免误拖或调整分组大小。" : "布局未锁定，可以拖动分组及调整尺寸。", 13, AppTheme.Muted); layoutNote.Margin = new Thickness(0,10,0,0); body.Children.Add(layoutNote);
        var locking = AppTheme.Button(snapshot().Locked ? "解锁桌面布局" : "锁定桌面布局", true); locking.HorizontalAlignment = HorizontalAlignment.Left;
        locking.Margin = new Thickness(0, 24, 0, 24); locking.Click += (_, _) => lockLayout(); body.Children.Add(locking);
        body.Children.Add(AppTheme.Text("关闭面板后，分组继续留在桌面；从托盘再次打开面板。启用 Windows 高对比度时，组件会采用实色背景。", 12, AppTheme.Muted));
        content.Content = AppTheme.Scroll(body);
    }
    private void About()
    {
        var version = typeof(ManagementWindow).Assembly.GetName().Version?.ToString(3) ?? "";
        var body = Page("关于 Desktop Glass", "版本 " + version + " · Windows 桌面分组");
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,4,0,26) }; brand.Children.Add(AppTheme.Logo(76));
        var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18,0,0,0) }; identity.Children.Add(AppTheme.Text("Desktop Glass",22)); identity.Children.Add(AppTheme.Text("把常用入口放在触手可及的位置。",12,AppTheme.Muted)); brand.Children.Add(identity); body.Children.Add(brand);
        body.Children.Add(AppTheme.Text("收纳与恢复", 17));
        var note = AppTheme.Text("收纳保留原文件路径；移除分组或选择“恢复项目并退出”时，项目图标回到桌面。关闭管理面板会保留桌面分组，可从托盘重新打开。",13,AppTheme.Muted); note.Margin = new Thickness(0,10,0,24); body.Children.Add(note);
        body.Children.Add(AppTheme.Text("使用前检查", 17));
        var tips = AppTheme.Text("支持普通文件、文件夹和快捷方式，回收站等系统入口暂不支持。\n\n请关闭资源管理器的“显示隐藏的项目”和桌面的“自动排列图标”，以免影响收纳和位置恢复。",13,AppTheme.Muted); tips.Margin = new Thickness(0,10,0,0); body.Children.Add(tips);
        content.Content = AppTheme.Scroll(body);
    }
}
