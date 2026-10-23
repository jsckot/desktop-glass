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
        var root = new Grid { Margin = new Thickness(18) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(172) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var sidebar = new DockPanel { Margin = new Thickness(0, 8, 22, 0) };
        var brand = new StackPanel { Margin = new Thickness(13, 10, 0, 35) };
        var logo = AppTheme.Logo(54); logo.HorizontalAlignment = HorizontalAlignment.Left; brand.Children.Add(logo);
        var title = AppTheme.Text("Desktop Glass", 17); title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0, 13, 0, 3);
        brand.Children.Add(title); brand.Children.Add(AppTheme.Text("让桌面轻一点", 11, AppTheme.Muted));
        DockPanel.SetDock(brand, Dock.Top); sidebar.Children.Add(brand);
        var footer = new StackPanel { Margin = new Thickness(12) };
        footer.Children.Add(AppTheme.Text("0.3 · 本地桌面分组", 10, AppTheme.Muted));
        var quit = AppTheme.Button("恢复项目并退出"); quit.Margin = new Thickness(0, 15, 0, 0); quit.Click += (_, _) => exit();
        footer.Children.Add(quit); DockPanel.SetDock(footer, Dock.Bottom); sidebar.Children.Add(footer);
        var nav = new StackPanel();
        foreach (var entry in new[] { "分组", "外观", "关于" })
        {
            var button = AppTheme.Button(entry); button.Margin = new Thickness(0, 0, 0, 10);
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            navigation.Add(entry, button);
            button.Click += (_, _) => { page = entry; Refresh(); }; nav.Children.Add(button);
        }
        sidebar.Children.Add(nav); root.Children.Add(sidebar);
        var well = AppTheme.Card(content, 25); Grid.SetColumn(well, 1); root.Children.Add(well); Content = root;
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
            button.Background = name == page ? AppTheme.Brush("#D6EAF3") : Brushes.Transparent;
            button.BorderBrush = name == page ? Brushes.White : Brushes.Transparent;
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
        var toolbar = new StackPanel { Margin = new Thickness(0, 18, 0, 20) };
        if (count.Parent is Panel oldCount) oldCount.Children.Remove(count); count.Margin = new Thickness(0, 0, 0, 14); toolbar.Children.Add(count);
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
        toolbar.Children.Add(new Border { Child = searchBox, CornerRadius = new CornerRadius(12), Background = AppTheme.Brush("#EDF5F8") });
        if (foreground is not null)
        {
            var mode = AppTheme.Button(isForeground() ? "放回桌面" : "临时浮到应用前面");
            mode.HorizontalAlignment = HorizontalAlignment.Left; mode.Margin = new Thickness(0,12,0,0);
            mode.ToolTip = "仅本次运行有效，下次启动仍贴在桌面";
            mode.Click += (_, _) => foreground(); toolbar.Children.Add(mode);
        }
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
            var box = new StackPanel { Margin = new Thickness(17) };
            var header = new DockPanel();
            var toggleButton = AppTheme.Button(group.DesktopVisible ? "隐藏" : "显示", !group.DesktopVisible);
            toggleButton.IsEnabled = connected || group.DesktopVisible;
            toggleButton.ToolTip = connected ? "显示或隐藏此分组" : "先移到当前屏幕，或接回原显示器";
            toggleButton.Click += (_, _) => toggle(group.Id, !group.DesktopVisible);
            DockPanel.SetDock(toggleButton, Dock.Right); header.Children.Add(toggleButton);
            var title = AppTheme.Text(group.Name, 16); title.FontWeight = FontWeights.SemiBold; title.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(title); box.Children.Add(header);
            var status = AppTheme.Text(group.Entries.Count + " 个项目 · " + (!connected ? "原显示器未连接" : group.DesktopVisible ? "正在显示" : "已暂停"), 11, AppTheme.Muted);
            status.Margin = new Thickness(0, 7, 0, 14); box.Children.Add(status);
            var actions = new WrapPanel();
            foreach (var action in new (string Text, Action Execute)[] {
                ("外观", () => edit(group.Id)), ("移到当前屏幕", () => move(group.Id)), ("移除", () => remove(group.Id)) })
            { var button = AppTheme.Button(action.Text); button.Padding = new Thickness(12, 6, 12, 6); button.Margin = new Thickness(0, 0, 7, 0); button.Click += (_, _) => action.Execute(); actions.Children.Add(button); }
            box.Children.Add(actions);
            var card = AppTheme.Card(box, 17); card.Background = AppTheme.Brush("#EDF5F9"); card.Margin = new Thickness(0, 0, 0, 12); rows.Children.Add(card);
        }
    }
    private void Appearance()
    {
        var body = Page("轻盈，有边界", "通透材质留给桌面，清晰层次留给操作。");
        body.Children.Add(AppTheme.Logo(95));
        var title = AppTheme.Text("液态玻璃", 19); title.Margin = new Thickness(0, 24, 0, 12); body.Children.Add(title);
        body.Children.Add(AppTheme.Text("每个分组都可以独立调整玻璃浓度、圆角和尺寸。在分组列表点击“外观”预览并保存。", 13, AppTheme.Muted));
        var locking = AppTheme.Button(snapshot().Locked ? "解锁桌面布局" : "锁定桌面布局", true); locking.HorizontalAlignment = HorizontalAlignment.Left;
        locking.Margin = new Thickness(0, 24, 0, 24); locking.Click += (_, _) => lockLayout(); body.Children.Add(locking);
        body.Children.Add(AppTheme.Text("关闭面板后，分组继续留在桌面；从托盘再次打开面板。启用 Windows 高对比度时，组件会采用实色背景。", 12, AppTheme.Muted));
        content.Content = body;
    }
    private void About()
    {
        var body = Page("Desktop Glass", "0.3.0 · Windows 桌面分组");
        body.Children.Add(AppTheme.Logo(100));
        var note = AppTheme.Text("把常用入口放在触手可及的位置。\n\n收纳保留原文件路径，退出时恢复图标。普通文件、文件夹和快捷方式可拖入；回收站等系统入口暂不支持。\n\n收纳前请关闭“显示隐藏的项目”和桌面“自动排列图标”。软件不会替你修改这些设置。", 13, AppTheme.Muted);
        note.Margin = new Thickness(0, 28, 0, 0); body.Children.Add(note); content.Content = body;
    }
}
