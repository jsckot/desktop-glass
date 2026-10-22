using Microsoft.Win32;
using System.Windows.Threading;
using DesktopGlass.App.UI;
using DesktopGlass.App.Windows;

namespace DesktopGlass.App;

/// <summary>协调独立分组、逐项恢复租约及工作区事务；组件之外始终由 Windows 处理原生桌面输入。</summary>
internal sealed class NativeDesktopController : IDisposable
{
    private readonly Application app;
    private readonly string profile;
    private readonly WorkspaceStore store;
    private readonly ItemVisibilityLease lease;
    private readonly DesktopCatalog catalog = new();
    private readonly Dictionary<string, DesktopEntry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Presentation> widgets = [];
    private readonly Dictionary<string, GroupEditor> editors = [];
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly DispatcherTimer refreshTimer;
    private readonly DispatcherTimer guardTimer;
    private Workspace workspace;
    private List<DisplayArea> displays = [];
    private System.Windows.Forms.NotifyIcon? tray;
    private bool stopped, rebuilding, topologyDirty, renderQueued;
    /// <summary>清理或恢复未全部成功时为 true，供启动入口返回非零退出码。</summary>
    internal bool CleanupFailed { get; private set; }

    /// <summary>读取自有配置；此时不隐藏文件、不创建全屏窗口。</summary>
    /// <param name="app">当前 STA WPF 应用。</param>
    /// <param name="profile">带产品所有权标记的配置目录；空目录可初始化。</param>
    /// <exception cref="InvalidOperationException">配置目录包含未识别数据。</exception>
    internal NativeDesktopController(Application app, string profile)
    {
        this.app = app; this.profile = Path.GetFullPath(profile);
        Directory.CreateDirectory(this.profile);
        var marker = Path.Combine(this.profile, ".desktop-glass-owner");
        if (!File.Exists(marker))
        {
            if (Directory.EnumerateFileSystemEntries(this.profile).Any()) throw new InvalidOperationException("配置目录已有未识别内容，已保留原数据并停止启动。");
            File.WriteAllText(marker, Installation.ProductId);
        }
        Installation.EnsureOwned(this.profile);
        store = new WorkspaceStore(this.profile); workspace = store.Load(); lease = new ItemVisibilityLease(this.profile);
        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        refreshTimer.Tick += (_, _) => { refreshTimer.Stop(); Execute(Refresh); };
        guardTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        guardTimer.Tick += (_, _) =>
        {
            if (!stopped && (!lease.Alive || widgets.Any(w => !w.Host!.Alive)))
                StopAfterFailure(new InvalidOperationException("Windows 桌面或恢复保护已失效，已停止整理并尝试恢复收纳项目。"));
        };
    }

    /// <summary>恢复历史责任、建立逐项恢复保护，并为每个分组建立独立组件。</summary>
    /// <exception cref="Exception">恢复、宿主或租约不可用时启动失败，调用方须释放控制器。</exception>
    internal void Start()
    {
        if (stopped) throw new ObjectDisposedException(nameof(NativeDesktopController));
        // 仅处理旧版留下的恢复日志；绝不取得旧桌面租约或隐藏整张图标列表。
        DesktopLease.Recover(Path.Combine(profile, "recovery.json"));
        displays = Native.Displays();
        if (displays.Count == 0) throw new InvalidOperationException("未找到可用显示器，保持原生桌面。");
        RefreshEntries();
        if (workspace.Groups.Count == 0)
        {
            var initial = Copy(workspace); initial.AddGroup("常用工具", Primary.Id); Save(initial);
        }
        lease.Acquire(); BuildWidgets();
        var warnings = new List<string>();
        foreach (var path in workspace.Groups.SelectMany(g => g.Entries))
        {
            if (path.StartsWith("::", StringComparison.Ordinal) || (!File.Exists(path) && !Directory.Exists(path)))
            { warnings.Add(DisplayName(path) + "：入口不存在或是虚拟桌面项目，保留引用但未隐藏原图标。"); continue; }
            try { lease.Collect(path); }
            catch (Exception error)
            {
                try { if (lease.Contains(path)) lease.Release(path); }
                catch (Exception recovery) { throw new MutationRecoveryException("启动收纳失败，部分项目仍需恢复。", [error, recovery]); }
                warnings.Add(DisplayName(path) + "：" + error.Message);
            }
        }
        InstallTray();
        SystemEvents.DisplaySettingsChanged += DisplayChanged; SystemEvents.PowerModeChanged += PowerChanged;
        foreach (var directory in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory)) continue;
            var watcher = new FileSystemWatcher(directory) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName };
            watcher.Created += FilesChanged; watcher.Deleted += FilesChanged; watcher.Renamed += FilesChanged;
            watcher.Error += (_, _) => Dispatch(QueueRefresh); watcher.EnableRaisingEvents = true; watchers.Add(watcher);
        }
        guardTimer.Start();
        if (warnings.Count > 0) Report(new InvalidOperationException("以下入口未自动收纳：\n" + string.Join("\n", warnings.Take(12)) +
            (warnings.Count > 12 ? "\n另有 " + (warnings.Count - 12) + " 项；原引用均保留。" : "")));
    }

    private DisplayArea Primary => displays.FirstOrDefault(d => d.Primary) ?? displays[0];

    private void InstallTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("新建分组", null, (_, _) => Dispatch(() => Execute(CreateGroup)));
        menu.Items.Add(workspace.Locked ? "解锁布局" : "锁定布局", null, (_, _) => Dispatch(() => Execute(ToggleLock)));
        menu.Items.Add("刷新分组", null, (_, _) => Dispatch(() => Execute(Refresh)));
        menu.Items.Add("打开桌面文件夹", null, (_, _) => Dispatch(() => Open(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory))));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("恢复项目并退出", null, (_, _) => Dispatch(Exit));
        if (tray is not null) { var previous = tray.ContextMenuStrip; tray.ContextMenuStrip = menu; previous?.Dispose(); return; }
        tray = new System.Windows.Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application,
            Text = "Desktop Glass · 桌面分组", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => Dispatch(() => Execute(CreateGroup));
    }

    private void RefreshEntries()
    {
        foreach (var entry in catalog.Scan()) entries[entry.Path] = entry;
        foreach (var path in workspace.Groups.SelectMany(g => g.Entries))
            entries.TryAdd(path, new DesktopEntry(path, DisplayName(path)));
    }

    private void Refresh()
    {
        if (stopped) return;
        if (!lease.Alive || widgets.Any(w => !w.Host!.Alive))
        { StopAfterFailure(new InvalidOperationException("桌面宿主或恢复保护已失效，停止刷新并恢复项目。")); return; }
        if (topologyDirty)
        {
            // 重建会取消在途几何手势；临时缺屏矩形绝不回写配置。
            var next = Native.Displays(); if (next.Count == 0) throw new InvalidOperationException("未找到可用显示器。");
            displays = next; topologyDirty = false;
        }
        RefreshEntries(); RebuildOrStop();
    }

    private void BuildWidgets()
    {
        rebuilding = true;
        try
        {
            foreach (var item in widgets) item.Host?.Dispose(); widgets.Clear();
            double temporaryX = 20, temporaryY = 20, rowHeight = 0;
            var temporaryIndex = 0;
            foreach (var group in workspace.Groups)
            {
                var placement = LayoutResolver.Resolve(group, displays);
                var bounds = new Rect(placement.Left, placement.Top, placement.Width, placement.Height);
                if (placement.Temporary)
                {
                    var d = placement.Display;
                    bounds.Width = Math.Min(bounds.Width, Math.Max(1, (d.Width - 60) / 2));
                    bounds.Height = Math.Min(bounds.Height, Math.Max(1, (d.Height - 60) / 2));
                    if (temporaryX + bounds.Width > d.Width - 20) { temporaryX = 20; temporaryY += rowHeight + 20; rowHeight = 0; }
                    if (temporaryY + bounds.Height > d.Height)
                    { temporaryX = 20 + temporaryIndex * 23 % Math.Max(1, d.Width - bounds.Width - 20); temporaryY = 20 + temporaryIndex * 29 % Math.Max(1, d.Height - bounds.Height - 20); }
                    bounds.X = d.Left + Math.Clamp(temporaryX, 0, d.Width - bounds.Width);
                    bounds.Y = d.Top + Math.Clamp(temporaryY, 0, d.Height - bounds.Height);
                    temporaryX += bounds.Width + 20; rowHeight = Math.Max(rowHeight, bounds.Height); temporaryIndex++;
                }
                var item = new Presentation(group.Id, placement.Display, placement.Temporary);
                var callbacks = new WidgetCallbacks
                {
                    Open = Open, Collect = (paths, index) => Collect(item, paths, index),
                    Release = path => Execute(() => Release(item, path)), Edit = () => Execute(() => EditGroup(item.GroupId)),
                    Remove = () => Execute(() => RemoveGroup(item)), Create = () => Execute(CreateGroup),
                    ToggleLock = () => Execute(ToggleLock), Exit = Exit,
                    MoveBy = (dx, dy) => Execute(() => PreviewBounds(item, dx, dy, false)),
                    ResizeBy = (dw, dh) => Execute(() => PreviewBounds(item, dw, dh, true)),
                    CommitBounds = canceled => Execute(() => CommitBounds(item, canceled))
                };
                var ordered = group.Entries.Select(path => entries.GetValueOrDefault(path) ?? new DesktopEntry(path, DisplayName(path))).ToArray();
                var window = new GroupWidget(group, placement.Display, ordered, catalog, workspace.Locked, placement.Temporary, callbacks);
                window.Closed += (_, _) => { if (!rebuilding && !stopped) Dispatch(Exit); };
                item.Host = new WidgetHost(window, bounds, group.Radius, placement.Display.Scale); widgets.Add(item);
            }
        }
        finally { rebuilding = false; }
    }

    private bool Collect(Presentation item, IReadOnlyList<string> paths, int index)
    {
        if (stopped || rebuilding || item.Temporary) return false;
        try
        {
            foreach (var path in paths) if (lease.Contains(path)) lease.ValidateReference(path);
            var candidate = CollectTransaction(workspace, item.GroupId, paths, index, lease.Contains, lease.Collect, lease.Release, store.Save, () => lease.Alive);
            workspace = candidate;
            foreach (var path in paths) entries.TryAdd(path, new DesktopEntry(path, DisplayName(path)));
            QueueRender(); return true;
        }
        catch (MutationRecoveryException error) { StopAfterFailure(error); return false; }
        catch (Exception error) { Report(error); return false; }
    }

    private void Release(Presentation item, string path)
    {
        if (stopped || rebuilding || item.Temporary) return;
        workspace = ReleaseTransaction(workspace, [path], candidate => candidate.MoveEntry(path, null),
            lease.Contains, lease.Collect, lease.Release, store.Save);
        QueueRender();
    }

    private void RemoveGroup(Presentation item)
    {
        if (stopped || rebuilding || item.Temporary) return;
        var group = workspace.Groups.Single(g => g.Id == item.GroupId);
        workspace = ReleaseTransaction(workspace, group.Entries.ToArray(), candidate => candidate.RemoveGroup(group.Id),
            lease.Contains, lease.Collect, lease.Release, store.Save);
        if (editors.Remove(group.Id, out var editor)) editor.Close();
        QueueRender();
    }

    private void CreateGroup()
    {
        var candidate = Copy(workspace); var group = candidate.AddGroup("新分组", Primary.Id); Save(candidate); QueueRender(); EditGroup(group.Id);
    }

    private void EditGroup(string id)
    {
        if (editors.TryGetValue(id, out var existing)) { existing.Activate(); return; }
        var draft = CopyGroup(workspace.Groups.Single(g => g.Id == id));
        var editor = new GroupEditor(draft, () =>
        {
            if (stopped) throw new InvalidOperationException("应用已停止，不能保存分组。");
            var candidate = Copy(workspace); var index = candidate.Groups.FindIndex(g => g.Id == id);
            if (index < 0) throw new InvalidOperationException("分组已被移除。");
            // 编辑器只负责外观，保留期间可能变化的入口和布局，避免覆盖新发生的收纳或拖动。
            candidate.Groups[index].Name = draft.Name; candidate.Groups[index].Tint = draft.Tint; candidate.Groups[index].Radius = draft.Radius;
            Save(candidate); QueueRender();
        });
        editors.Add(id, editor); editor.Closed += (_, _) => editors.Remove(id); editor.Show();
    }

    private void ToggleLock()
    { var candidate = Copy(workspace); candidate.Locked = !candidate.Locked; Save(candidate); InstallTray(); QueueRender(); }

    private void PreviewBounds(Presentation item, double dx, double dy, bool resize)
    {
        if (!LayoutEditPolicy.CanCommit(false, rebuilding, stopped, workspace.Locked, item.Temporary)) return;
        var host = item.Host!; var bounds = host.GetBounds(); if (item.GestureStart is null) { item.GestureStart = bounds; item.Resizing = resize; }
        if (resize)
        {
            bounds.Width = Math.Clamp(bounds.Width + dx * item.Display.Scale, Math.Min(240 * item.Display.Scale, item.Display.Width), Math.Min(2000 * item.Display.Scale, item.Display.Width));
            bounds.Height = Math.Clamp(bounds.Height + dy * item.Display.Scale, Math.Min(160 * item.Display.Scale, item.Display.Height), Math.Min(1600 * item.Display.Scale, item.Display.Height));
        }
        else bounds.Offset(dx * item.Display.Scale, dy * item.Display.Scale);
        host.SetBounds(bounds, workspace.Groups.Single(g => g.Id == item.GroupId).Radius, item.Display.Scale);
    }

    private void CommitBounds(Presentation item, bool canceled)
    {
        if (item.GestureStart is not { } original) return;
        var resized = item.Resizing; item.GestureStart = null; item.Resizing = false;
        var source = workspace.Groups.SingleOrDefault(g => g.Id == item.GroupId);
        if (source is null || item.Host is null || !item.Host.Alive) return;
        if (!LayoutEditPolicy.CanCommit(canceled, rebuilding, stopped, workspace.Locked, item.Temporary))
        { item.Host.SetBounds(original, source.Radius, item.Display.Scale); return; }
        try
        {
            var bounds = item.Host.GetBounds(); var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            var target = displays.FirstOrDefault(d => new Rect(d.Left, d.Top, d.Width, d.Height).Contains(center)) ?? item.Display;
            var candidate = Copy(workspace); var group = candidate.Groups.Single(g => g.Id == item.GroupId);
            group.DisplayId = target.Id; group.Width = resized ? Math.Clamp(bounds.Width / item.Display.Scale, 240, 2000) : source.Width;
            group.Height = resized ? Math.Clamp(bounds.Height / item.Display.Scale, 160, 1600) : source.Height;
            group.X = Math.Clamp((bounds.Left - target.Left) / target.Scale, 0, Math.Max(0, target.Width / target.Scale - group.Width));
            group.Y = Math.Clamp((bounds.Top - target.Top) / target.Scale, 0, Math.Max(0, target.Height / target.Scale - group.Height));
            Save(candidate); QueueRender();
        }
        catch
        { item.Host.SetBounds(original, source.Radius, item.Display.Scale); throw; }
    }

    private void Save(Workspace candidate) { store.Save(candidate); workspace = candidate; }
    private void QueueRender()
    {
        if (renderQueued || stopped) return; renderQueued = true;
        Dispatch(() => { renderQueued = false; RebuildOrStop(); });
    }
    private void RebuildOrStop() { try { BuildWidgets(); } catch (Exception error) { StopAfterFailure(error); } }
    private void Execute(Action action)
    {
        if (stopped) return;
        try { action(); }
        catch (MutationRecoveryException error) { StopAfterFailure(error); }
        catch (Exception error) { Report(error); }
    }
    private void Open(string path) => Execute(() =>
    {
        if (lease.Contains(path)) lease.ValidateReference(path);
        if (!path.StartsWith("::", StringComparison.Ordinal) && !File.Exists(path) && !Directory.Exists(path))
            throw new InvalidOperationException("该入口已不存在或已改名。请移出旧引用后重新拖入。");
        DesktopCatalog.Open(path);
    });
    private void Dispatch(Action action)
    { if (!stopped && !app.Dispatcher.HasShutdownStarted) app.Dispatcher.BeginInvoke(() => { if (!stopped) action(); }); }
    private void QueueRefresh() { if (stopped) return; refreshTimer.Stop(); refreshTimer.Start(); }
    private void FilesChanged(object sender, FileSystemEventArgs e) => Dispatch(QueueRefresh);
    private void DisplayChanged(object? sender, EventArgs e) => Dispatch(() => { topologyDirty = true; QueueRefresh(); });
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) DisplayChanged(sender, EventArgs.Empty); }
    private static string DisplayName(string path) => Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
        ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
    private void Report(Exception error)
    {
        var text = error is AggregateException aggregate ? string.Join("\n", aggregate.Flatten().InnerExceptions.Select(e => e.Message).Distinct()) : error.Message;
        MessageBox.Show(text, "Desktop Glass", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    private void StopAfterFailure(Exception error) { Dispose(); Report(error); app.Shutdown(1); }

    /// <summary>恢复收纳项目后退出；恢复失败保持恢复日志并明确提示其位置。</summary>
    internal void Exit() { Dispose(); app.Shutdown(CleanupFailed ? 1 : 0); }

    /// <summary>幂等停止监听、关闭自己的组件并释放逐项租约；恢复失败保留日志并显示原因。</summary>
    public void Dispose()
    {
        if (stopped) return; stopped = true; refreshTimer.Stop(); guardTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= DisplayChanged; SystemEvents.PowerModeChanged -= PowerChanged;
        var errors = new List<Exception>();
        foreach (var watcher in watchers) { try { watcher.Dispose(); } catch (Exception error) { errors.Add(error); } } watchers.Clear();
        foreach (var editor in editors.Values.ToArray()) { try { editor.Close(); } catch (Exception error) { errors.Add(error); } } editors.Clear();
        foreach (var item in widgets) { try { item.Host?.Dispose(); } catch (Exception error) { errors.Add(error); } } widgets.Clear();
        try { lease.Dispose(); } catch (Exception error) { errors.Add(error); }
        if (tray is not null)
        {
            var menu = tray.ContextMenuStrip;
            try { tray.Visible = false; } catch (Exception error) { errors.Add(error); }
            try { tray.Dispose(); } catch (Exception error) { errors.Add(error); }
            try { menu?.Dispose(); } catch (Exception error) { errors.Add(error); }
            tray = null;
        }
        CleanupFailed = errors.Count > 0;
        if (errors.Count > 0) Report(new AggregateException("部分项目未能恢复，已保留恢复日志：" + profile, errors.Prepend(
            new InvalidOperationException("部分项目未能恢复，请保留此目录中的恢复日志并重试恢复：" + profile))));
    }

    /// <summary>规划收纳顺序并执行隐藏—保存事务；失败恢复本次新建租约，不修改原工作区对象。</summary>
    internal static Workspace CollectTransaction(Workspace source, string groupId, IReadOnlyList<string> paths, int index,
        Func<string, bool> contains, Action<string> collect, Action<string> release, Action<Workspace> save, Func<bool> guardAlive)
    {
        var incoming = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (incoming.Length == 0) throw new ArgumentException("没有可收纳的项目。");
        var candidate = PlanCollection(source, groupId, incoming, index); var attempted = new List<string>();
        try
        {
            foreach (var path in incoming)
            {
                if (!guardAlive()) throw new InvalidOperationException("恢复保护未就绪，无法收纳项目。");
                if (contains(path)) continue;
                attempted.Add(path); collect(path);
            }
            if (!guardAlive()) throw new InvalidOperationException("恢复保护已停止，取消保存收纳状态。");
            save(candidate); return candidate;
        }
        catch (Exception error)
        {
            var failures = new List<Exception>();
            foreach (var path in attempted.AsEnumerable().Reverse())
            { try { if (contains(path)) release(path); } catch (Exception recovery) { failures.Add(recovery); } }
            if (failures.Count > 0) throw new MutationRecoveryException("收纳失败且部分新收纳项目未能恢复。", failures.Prepend(error));
            throw;
        }
    }

    /// <summary>先恢复入口再保存移出结果；保存失败重新建立原租约，回滚不完整时保留责任并停止应用。</summary>
    internal static Workspace ReleaseTransaction(Workspace source, IReadOnlyList<string> paths, Action<Workspace> mutate,
        Func<string, bool> contains, Action<string> collect, Action<string> release, Action<Workspace> save)
    {
        var candidate = Copy(source); mutate(candidate); var released = new List<string>(); var releaseFailed = false;
        try
        {
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!contains(path)) continue;
                try { release(path); released.Add(path); }
                catch { releaseFailed = true; throw; }
            }
            save(candidate); return candidate;
        }
        catch (Exception error)
        {
            var failures = new List<Exception>();
            foreach (var path in released)
            { try { collect(path); } catch (Exception recovery) { failures.Add(recovery); } }
            if (releaseFailed || failures.Count > 0) throw new MutationRecoveryException("移出失败，需恢复原工作区及项目。", failures.Prepend(error));
            throw;
        }
    }

    /// <summary>以投放前的目标索引规划排序；先扣除索引前的源项目，再按输入顺序插入。</summary>
    internal static Workspace PlanCollection(Workspace source, string groupId, IReadOnlyList<string> paths, int index)
    {
        var candidate = Copy(source); var target = candidate.Groups.Single(g => g.Id == groupId);
        var incoming = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); var set = incoming.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var raw = Math.Clamp(index, 0, target.Entries.Count); var insertion = raw - target.Entries.Take(raw).Count(set.Contains);
        foreach (var group in candidate.Groups) group.Entries.RemoveAll(set.Contains);
        target.Entries.InsertRange(insertion, incoming); candidate.Validate(); return candidate;
    }

    private static Workspace Copy(Workspace source) => new() { Version = source.Version, Locked = source.Locked, Groups = source.Groups.Select(CopyGroup).ToList() };
    private static Group CopyGroup(Group source) => new() { Id = source.Id, Name = source.Name, DisplayId = source.DisplayId,
        X = source.X, Y = source.Y, Width = source.Width, Height = source.Height, Tint = source.Tint, Radius = source.Radius, Entries = [.. source.Entries] };
    private sealed class Presentation(string groupId, DisplayArea display, bool temporary)
    {
        internal string GroupId { get; } = groupId;
        internal DisplayArea Display { get; } = display;
        internal bool Temporary { get; } = temporary;
        internal WidgetHost? Host { get; set; }
        internal Rect? GestureStart { get; set; }
        internal bool Resizing { get; set; }
    }
    private sealed class MutationRecoveryException(string message, IEnumerable<Exception> errors) : AggregateException(message, errors);
}
