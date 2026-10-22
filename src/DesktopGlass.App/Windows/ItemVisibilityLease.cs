using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace DesktopGlass.App.Windows;

/// <summary>只租用普通桌面项的 Hidden 位；恢复日志和独立守护进程先于任何属性修改生效。</summary>
internal sealed class ItemVisibilityLease : IDisposable
{
    internal const string JournalName = "item-recovery.json";
    private const string MutexName = "Local\\DesktopGlass-item-lease";
    private readonly string journal;
    private readonly string active;
    private readonly ItemPathPolicy policy;
    private readonly IItemDesktopPositions positions;
    private readonly Action<string>? beforeHide;
    private readonly Mutex gate = new(false, MutexName);
    private readonly Dictionary<string, ItemFileObject> handles = new(StringComparer.OrdinalIgnoreCase);
    private ItemLeaseJournal? state;
    private Process? guard;
    private bool ownsGate, disposed;
    private int ownerThread;

    /// <summary>创建当前用户租约；实际取得保护前不会修改任何桌面项。</summary>
    /// <param name="directory">本软件配置目录，恢复日志保存在该目录。</param>
    internal ItemVisibilityLease(string directory) : this(directory, ItemPathPolicy.Desktop(), new DesktopItemPositions(), null) { }
    private ItemVisibilityLease(string directory, ItemPathPolicy pathPolicy, IItemDesktopPositions desktopPositions, Action<string>? persisted)
    {
        journal = JournalPath(directory); policy = pathPolicy; positions = desktopPositions; beforeHide = persisted;
        active = ActivePath(pathPolicy);
    }
    internal static ItemVisibilityLease Diagnostic(string directory, string fixture, IItemDesktopPositions? desktop = null, Action<string>? persisted = null)
        => new(directory, ItemPathPolicy.Diagnostic(fixture), desktop ?? new ItemFixturePositions(), persisted);

    /// <summary>守护进程与原 Explorer 身份仍有效时为真；为假不妨碍 Release 或 Dispose 执行恢复。</summary>
    internal bool Alive => !disposed && ownsGate && state is { Released: false } && guard is not null && !guard.HasExited && positions.Alive;
    internal string JournalPathForDiagnostics => journal;

    /// <summary>取得跨配置独占租约，先恢复旧责任，再启动守护进程并等待 PID/启动时间/租约标识握手。</summary>
    /// <exception cref="InvalidOperationException">已有租约、桌面能力不可用或守护进程未就绪；收纳设置不限制启动。</exception>
    /// <exception cref="IOException">旧日志不能可靠恢复或新日志不能持久化；不会放行收纳。</exception>
    internal void Acquire()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (state is not null) throw new InvalidOperationException("租约已取得。");
        ownerThread = Environment.CurrentManagedThreadId;
        ownsGate = Take(gate, TimeSpan.Zero);
        if (!ownsGate) throw new InvalidOperationException("已有实例正在管理桌面项目。");
        try
        {
            if (policy.FixtureRoot is null)
                ItemLeaseStorage.EnsureOwnedDirectory(Path.GetDirectoryName(Path.GetDirectoryName(active)!)!); // 新profile启动也先建立默认Data根所有权，拒绝未知目录。
            ItemLeaseStorage.EnsureOwnedDirectory(Path.GetDirectoryName(active)!);
            RecoverActive(active, policy.FixtureRoot);
            if (File.Exists(journal)) RecoverOne(journal);
            positions.EnsureAvailable();
            Directory.CreateDirectory(Path.GetDirectoryName(journal)!); ItemPathPolicy.EnsureNoReparse(Path.GetDirectoryName(journal)!);
            using var owner = Process.GetCurrentProcess();
            string leaseId = Guid.NewGuid().ToString("N");
            string readyName = "Local\\DesktopGlass-items-ready-" + leaseId;
            state = new(1, leaseId, owner.Id, owner.StartTime.ToUniversalTime().Ticks, 0, 0, readyName, false, policy.FixtureRoot, []);
            Save();
            ItemLeaseStorage.Write(active, new ItemLeasePointer(journal, leaseId, state.OwnerPid, state.OwnerStart));
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
            guard = Self.Start("--watch-items", journal, readyName);
            state = state with { GuardPid = guard.Id, GuardStart = guard.StartTime.ToUniversalTime().Ticks };
            Save();
            if (!ready.WaitOne(TimeSpan.FromSeconds(12)) || guard.HasExited)
                throw new InvalidOperationException("项目恢复守护进程未完成握手，已取消收纳。");
        }
        catch (Exception original)
        {
            try { Dispose(); }
            catch (Exception recovery) { throw new AggregateException("取得租约失败，恢复责任已保留。", original, recovery); }
            throw;
        }
    }

    /// <summary>收纳一个原本可见的直接桌面子项，先持久化身份、原 Hidden 位和物理坐标，再修改属性。</summary>
    /// <param name="path">普通文件、目录或快捷方式的完整桌面路径。</param>
    /// <exception cref="InvalidOperationException">路径、重解析链、原 Hidden 位、硬链接或 Shell 设置不符合第一版约束。</exception>
    /// <exception cref="IOException">日志、属性或回滚失败；Contains 可能仍为真，调用方不得把失败解释为没有恢复责任。</exception>
    internal void Collect(string path)
    {
        RequireThread();
        if (!Alive) throw new InvalidOperationException("恢复保护或原 Explorer 已失效，不能继续收纳。");
        string normalized = policy.Validate(path);
        if (Contains(normalized)) return;
        positions.EnsureCanCollect();
        var item = ItemFileObject.Open(normalized);
        bool journaled = false;
        try
        {
            if (!string.Equals(ItemPathPolicy.Normalize(item.CurrentPath), normalized, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("路径解析到其他文件对象，已拒绝收纳。");
            if ((item.Attributes & FileAttributes.Hidden) != 0) throw new InvalidOperationException("原本已隐藏的项目不能收纳。");
            if ((item.Attributes & FileAttributes.Directory) == 0 && item.Links != 1) throw new InvalidOperationException("暂不支持具有多个硬链接的文件，避免影响其他位置的同一文件。");
            var entry = new ItemLeaseEntry(normalized, item.Identity, false, positions.Capture(normalized));
            var candidate = state! with { Items = state.Items.Append(entry).ToList() };
            ItemLeaseStorage.Prepare(candidate); // 写端与恢复端共享条目数及实际UTF8字节限额，拒绝时不改原日志或属性。
            state.Items.Add(entry); handles.Add(normalized, item); journaled = true;
            Save();
            beforeHide?.Invoke(journal);
            if (!Alive) throw new InvalidOperationException("属性修改前恢复守护已失效。");
            item.SetHidden(true);
            Notify(item.CurrentPath);
        }
        catch (Exception original)
        {
            if (journaled)
            {
                try { RestoreEntries(state!, policy, positions, journal, handles, [normalized]); }
                catch (Exception rollback) { throw new AggregateException("收纳失败且回滚未完成；恢复日志已保留。", original, rollback); }
            }
            throw;
        }
        finally { if (!journaled) item.Dispose(); }
    }

    /// <summary>恢复指定收纳责任；即使 guard 或 Explorer 已失效，也先恢复 Hidden 位，再尝试当前 Shell 坐标。</summary>
    /// <param name="path">原收纳路径；即使对象已改名，仍使用此键识别其恢复责任。</param>
    /// <exception cref="AggregateException">属性或坐标恢复失败，责任及日志保留以供重试。</exception>
    internal void Release(string path)
    {
        RequireThread();
        if (state is null) return;
        string normalized = ItemPathPolicy.Normalize(path);
        if (!state.Items.Any(item => string.Equals(item.Path, normalized, StringComparison.OrdinalIgnoreCase))) return;
        RestoreEntries(state, policy, positions, journal, handles, [normalized]);
    }
    /// <summary>判断原路径是否仍有收纳或未完成的恢复责任；不扫描桌面。</summary>
    /// <param name="path">原完整收纳路径。</param>
    /// <returns>仍在日志中时为真，包含失败回滚和待恢复坐标；虚拟或其他不支持路径为 false。</returns>
    internal bool Contains(string path)
    {
        if (state is null) return false;
        string normalized;
        try { normalized = ItemPathPolicy.Normalize(path); }
        catch (InvalidOperationException) { return false; } // 纯路径策略拒绝表示不可能存在此租约；不捕获文件I/O失败。
        return state.Items.Any(item => string.Equals(item.Path, normalized, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>只读核对已收纳路径仍指向日志中的同一文件对象，不自动改写工作区引用。</summary>
    /// <param name="path">当前 UI 保存的完整收纳路径；未收纳路径直接返回。</param>
    /// <exception cref="InvalidOperationException">对象已改名、删除或被同名对象替换，应先移出旧引用再重新拖入。</exception>
    internal void ValidateReference(string path)
    {
        if (state is null) return;
        string normalized = ItemPathPolicy.Normalize(path);
        var entry = state.Items.FirstOrDefault(item => string.Equals(item.Path, normalized, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return;
        const string message = "该收纳引用已改名、删除或被替换，请先移出旧引用后重新拖入。";
        try
        {
            ItemPathPolicy.EnsureNoReparse(normalized);
            using var current = ItemFileObject.Open(normalized, false, false);
            if (current.Identity != entry.Identity || !string.Equals(ItemPathPolicy.Normalize(current.CurrentPath), normalized, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        { throw new InvalidOperationException(message, error); }
    }
    /// <summary>停止收纳并恢复全部责任；先恢复所有属性，再恢复位置。失败保留日志并释放独占锁供显式重试。</summary>
    /// <exception cref="AggregateException">存在未恢复项目或持久化失败；不得删除剩余日志。</exception>
    public void Dispose()
    {
        if (disposed) return;
        if (ownsGate && ownerThread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("租约必须在取得它的线程释放。");
        var failures = new List<Exception>();
        try
        {
            if (ownsGate && state is not null)
            {
                state = state with { Released = true };
                try { Save(); } catch (Exception error) { failures.Add(error); }
                try { RestoreEntries(state, policy, positions, journal, handles, null); } catch (Exception error) { failures.Add(error); }
                if (failures.Count == 0 && state.Items.Count == 0) Clear(journal, active, state.LeaseId);
            }
        }
        finally
        {
            foreach (var item in handles.Values) item.Dispose(); handles.Clear();
            var watcher = guard; guard = null;
            if (ownsGate) { gate.ReleaseMutex(); ownsGate = false; }
            if (watcher is not null)
            {
                // guard可能正等待本锁；必须先释放互斥，再在成功恢复时等待其观察到日志删除并退出。
                if (failures.Count == 0 && state?.Items.Count == 0 && !watcher.WaitForExit(3000))
                    failures.Add(new IOException("项目均已恢复，但恢复守护进程退出超时。"));
                watcher.Dispose();
            }
            gate.Dispose(); disposed = true;
        }
        if (failures.Count != 0) throw new AggregateException("项目恢复未全部完成；请保留 item-recovery.json 并重试恢复。", failures);
    }

    /// <summary>恢复指定陈旧日志；只处理死亡或已释放的租约，幂等且不误改同名替换对象。</summary>
    /// <param name="directoryOrJournal">配置目录或 item-recovery.json 完整路径。</param>
    /// <exception cref="InvalidOperationException">拥有者仍在运行或跨配置独占锁不可得。</exception>
    /// <exception cref="AggregateException">仍有无法恢复的属性或坐标，日志保留。</exception>
    internal static void Recover(string directoryOrJournal)
    {
        string path = JournalPath(directoryOrJournal);
        if (!File.Exists(path)) return;
        using var mutex = new Mutex(false, MutexName);
        bool owned = Take(mutex, TimeSpan.FromSeconds(15));
        if (!owned) throw new InvalidOperationException("另一个实例仍占用项目租约，无法恢复。");
        try { if (File.Exists(path)) RecoverOne(path); }
        finally { mutex.ReleaseMutex(); }
    }

    /// <summary>恢复当前用户跨配置登记的陈旧责任，用于卸载或显式恢复，不能因默认配置没有日志而跳过其他配置。</summary>
    /// <exception cref="InvalidOperationException">拥有者仍在运行、锁不可得或登记目录所有权无效。</exception>
    /// <exception cref="IOException">全局指针与日志不一致或恢复失败；所有证据保留。</exception>
    internal static void RecoverRegistered() => RecoverRegisteredCore(ItemPathPolicy.Desktop());

    internal static void RecoverRegisteredDiagnostic(string fixture) => RecoverRegisteredCore(ItemPathPolicy.Diagnostic(fixture));
    private static void RecoverRegisteredCore(ItemPathPolicy pathPolicy)
    {
        string registered = ActivePath(pathPolicy);
        if (!File.Exists(registered)) return;
        using var mutex = new Mutex(false, MutexName);
        bool owned = Take(mutex, TimeSpan.FromSeconds(15));
        if (!owned) throw new InvalidOperationException("其他实例仍占用项目租约，不能恢复登记责任。");
        try
        {
            if (!File.Exists(registered)) return;
            ItemLeaseStorage.EnsureOwnedDirectory(Path.GetDirectoryName(registered)!);
            var pointer = ItemLeaseStorage.Read<ItemLeasePointer>(registered);
            var value = ItemLeaseStorage.Read<ItemLeaseJournal>(JournalPath(pointer.Journal)); Validate(value);
            if (!string.Equals(value.FixtureRoot, pathPolicy.FixtureRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("登记责任的生产或诊断范围不匹配。");
            RecoverActive(registered, pathPolicy.FixtureRoot);
        }
        finally { mutex.ReleaseMutex(); }
    }
    /// <summary>独立守护入口；握手后等待原进程退出，再重新读取同一租约的最新日志恢复全部项目。</summary>
    /// <param name="journal">父进程创建的完整恢复日志路径。</param>
    /// <param name="readyEvent">日志内记录的本次随机握手事件名。</param>
    /// <returns>恢复成功或责任已处理为 0；身份验证或恢复失败为 1，错误证据保留。</returns>
    internal static int Watch(string journal, string readyEvent)
    {
        string path = JournalPath(journal);
        try
        {
            var initial = ItemLeaseStorage.Read<ItemLeaseJournal>(path); Validate(initial);
            if (initial.ReadyEvent != readyEvent) throw new InvalidDataException("守护握手事件与租约不匹配。");
            using var current = Process.GetCurrentProcess();
            var timer = Stopwatch.StartNew();
            ItemLeaseJournal latest = initial;
            while (timer.Elapsed < TimeSpan.FromSeconds(4))
            {
                if (!File.Exists(path)) return 0;
                latest = ItemLeaseStorage.Read<ItemLeaseJournal>(path);
                RequireSameOwner(initial, latest);
                if (latest.GuardPid == current.Id && latest.GuardStart == current.StartTime.ToUniversalTime().Ticks) break;
                if (!OwnerAlive(latest)) break;
                Thread.Sleep(40);
            }
            if (OwnerAlive(latest))
            {
                if (latest.GuardPid != current.Id || latest.GuardStart != current.StartTime.ToUniversalTime().Ticks)
                    throw new InvalidDataException("守护进程 PID 或启动时间未登记。");
                using (var ready = EventWaitHandle.OpenExisting(readyEvent)) ready.Set();
                try
                {
                    using var parent = Process.GetProcessById(latest.OwnerPid);
                    if (parent.StartTime.ToUniversalTime().Ticks == latest.OwnerStart)
                    {
                        while (!parent.WaitForExit(250))
                        {
                            if (!File.Exists(path)) return 0;
                            var pending = ItemLeaseStorage.Read<ItemLeaseJournal>(path); RequireSameOwner(initial, pending);
                            // Released只允许显式恢复重试；独立guard仍等待父进程死亡，避免与正常Dispose/后续租约抢锁。
                        }
                    }
                }
                catch (ArgumentException) { /* 父进程已退出，继续按最新日志恢复。 */ }
            }
            if (!File.Exists(path)) return 0;
            using var mutex = new Mutex(false, MutexName);
            bool owned = Take(mutex, TimeSpan.FromSeconds(15));
            if (!owned)
            {
                if (!File.Exists(path)) return 0;
                throw new InvalidOperationException("恢复锁等待超时，日志已保留。");
            }
            try
            {
                if (!File.Exists(path)) return 0;
                latest = ItemLeaseStorage.Read<ItemLeaseJournal>(path); RequireSameOwner(initial, latest);
                RecoverOne(path);
            }
            finally { mutex.ReleaseMutex(); }
            return 0;
        }
        catch (Exception error)
        {
            try { File.WriteAllText(path + ".guard-error.txt", error.ToString(), Encoding.UTF8); }
            catch (Exception logError) { Console.Error.WriteLine("守护错误证据写入失败：" + logError.Message); }
            Console.Error.WriteLine(error.Message); return 1;
        }
    }

    private void RequireThread()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!ownsGate || state is null || state.Released) throw new InvalidOperationException("尚未取得有效项目租约。");
        if (ownerThread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("租约操作必须在取得租约的线程执行。");
    }
    private void Save() => ItemLeaseStorage.Write(journal, state ?? throw new InvalidOperationException("没有租约记录。"));
    private static string JournalPath(string value)
    {
        value = Path.GetFullPath(value);
        if (Path.GetFileName(value).Equals(JournalName, StringComparison.OrdinalIgnoreCase)) return value;
        if (value.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("恢复路径必须是目录或 item-recovery.json。");
        return Path.Combine(value, JournalName);
    }
    private static string ActivePath(ItemPathPolicy policy) => Path.Combine(policy.FixtureRoot is null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopGlass", "item-lease-state")
        : Path.Combine(Path.GetDirectoryName(policy.FixtureRoot)!, "item-lease-state"), "item-active-lease.json");
    private static ItemPathPolicy Policy(ItemLeaseJournal value) => value.FixtureRoot is null ? ItemPathPolicy.Desktop() : ItemPathPolicy.Diagnostic(value.FixtureRoot);
    private static IItemDesktopPositions Positions(ItemLeaseJournal value) => value.FixtureRoot is null ? new DesktopItemPositions() : new ItemFixturePositions();
    private static bool Take(Mutex mutex, TimeSpan wait) { try { return mutex.WaitOne(wait); } catch (AbandonedMutexException) { return true; } }
    private static bool OwnerAlive(ItemLeaseJournal value)
    {
        if (value.Released) return false;
        try { using var process = Process.GetProcessById(value.OwnerPid); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == value.OwnerStart; }
        catch (ArgumentException) { return false; }
    }
    private static void RequireSameOwner(ItemLeaseJournal first, ItemLeaseJournal second)
    {
        if (first.LeaseId != second.LeaseId || first.OwnerPid != second.OwnerPid || first.OwnerStart != second.OwnerStart || first.FixtureRoot != second.FixtureRoot)
            throw new InvalidDataException("日志已属于其他租约，旧守护进程不会修改它。");
    }
    private static void Validate(ItemLeaseJournal value)
    {
        if (value.Version != 1 || !Guid.TryParseExact(value.LeaseId, "N", out _) || value.OwnerPid <= 0 || value.OwnerStart <= 0 ||
            value.ReadyEvent != "Local\\DesktopGlass-items-ready-" + value.LeaseId || value.Items is null || value.Items.Count > ItemLeaseLimits.MaxItemCount)
            throw new InvalidDataException("恢复日志格式或租约身份无效，请保留原文件。");
        var policy = Policy(value);
        if (value.Items.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Items.Count)
            throw new InvalidDataException("恢复日志包含重复路径。");
        foreach (var item in value.Items)
        {
            if (item.Identity is null || !Guid.TryParseExact(item.Identity.FileId, "N", out _) || item.Identity.CreationTime <= 0 || item.Position is null ||
                item.OriginalHidden || !policy.IsDirectChild(ItemPathPolicy.Normalize(item.Path))) throw new InvalidDataException("恢复日志项目身份或范围无效。");
        }
    }
    private static void RecoverActive(string active, string? fixtureRoot)
    {
        if (!File.Exists(active)) return;
        var pointer = ItemLeaseStorage.Read<ItemLeasePointer>(active);
        string target = JournalPath(pointer.Journal);
        if (!File.Exists(target)) throw new InvalidDataException("全局活动指针引用的恢复日志不存在；请保留指针检查，未启用新收纳。");
        var value = ItemLeaseStorage.Read<ItemLeaseJournal>(target); Validate(value);
        if (!string.Equals(value.FixtureRoot, fixtureRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("全局指针越过生产或诊断边界，未执行恢复。");
        if (value.LeaseId != pointer.LeaseId || value.OwnerPid != pointer.OwnerPid || value.OwnerStart != pointer.OwnerStart)
            throw new InvalidDataException("全局活动指针与日志身份不一致。");
        RecoverOne(target);
    }
    private static void RecoverOne(string journal)
    {
        var value = ItemLeaseStorage.Read<ItemLeaseJournal>(journal); Validate(value);
        if (OwnerAlive(value)) throw new InvalidOperationException("恢复日志属于仍在运行的租约，未修改任何项目。");
        var policy = Policy(value);
        RestoreEntries(value, policy, Positions(value), journal, null, null);
        if (value.Items.Count == 0) Clear(journal, ActivePath(policy), value.LeaseId);
    }
    private static void RestoreEntries(ItemLeaseJournal value, ItemPathPolicy policy, IItemDesktopPositions positions,
        string journal, Dictionary<string, ItemFileObject>? retained, string[]? only)
    {
        var failures = new List<Exception>();
        var ready = new List<(ItemLeaseEntry Entry, string? Current)>();
        foreach (var entry in value.Items.ToArray())
        {
            if (only is not null && !only.Contains(entry.Path, StringComparer.OrdinalIgnoreCase)) continue;
            ItemFileObject? item = null; bool borrowed = false;
            try
            {
                if (retained is not null && retained.TryGetValue(entry.Path, out item)) borrowed = true;
                else
                {
                    // 路径可能已被替身占用；直接按卷内ID重开原对象，不先申请替身的写属性权限。
                    item = ItemFileObject.OpenIdentity(entry.Identity, policy.RootFor(entry.Path));
                }
                if (item is null || item.DeletePending) throw new IOException("原对象已删除或跨卷移走，无法证明恢复完成；保留记录，不修改同名替身。");
                if (item.Identity != entry.Identity) throw new IOException("恢复对象的稳定身份不匹配，未改动同名新文件。");
                item.SetHidden(entry.OriginalHidden);
                string current = ItemPathPolicy.Normalize(item.CurrentPath);
                // 按ID打开的属性写句柄到关闭时才发布目录变更；先关闭再通知，避免Shell收到尚未发布的恢复状态。
                // retained为正常租约的路径句柄，生命周期和通知顺序维持原样；后续仍需按身份pin并验证坐标。
                if (!borrowed) { item.Dispose(); item = null; }
                Notify(current);
                ready.Add((entry, current));
            }
            catch (Exception error) { failures.Add(new IOException("恢复属性失败，记录已保留：" + entry.Path, error)); }
            finally { if (!borrowed) item?.Dispose(); }
        }
        // 所有属性均优先于任何坐标恢复；Explorer 重启时也先把真实文件重新变为可见。
        foreach (var result in ready)
        {
            try
            {
                if (result.Current is not null && policy.IsDirectChild(result.Current))
                {
                    ItemPathPolicy.EnsureNoReparse(result.Current);
                    using var pinned = ItemFileObject.Open(result.Current, false, false);
                    if (pinned.Identity != result.Entry.Identity) throw new IOException("恢复坐标前对象身份改变，未定位同名替身。");
                    positions.Restore(result.Current, result.Entry.Position);
                }
                value.Items.Remove(result.Entry);
                try { ItemLeaseStorage.Write(journal, value); }
                catch { value.Items.Add(result.Entry); throw; }
                if (retained is not null && retained.Remove(result.Entry.Path, out var held)) held.Dispose();
            }
            catch (Exception error) { failures.Add(new IOException("恢复位置或日志提交失败，记录已保留：" + result.Entry.Path, error)); }
        }
        if (failures.Count != 0) throw new AggregateException("部分项目仍待恢复。", failures);
    }
    private static void Notify(string path) => ItemFileNative.SHChangeNotify(0x800, 0x1005, path, 0);
    private static void Clear(string journal, string active, string leaseId)
    {
        // 先移除指针，再删除空责任日志；中途终止时日志仍可由原配置重试。
        if (File.Exists(active))
        {
            ItemLeaseStorage.EnsureOwnedDirectory(Path.GetDirectoryName(active)!);
            var pointer = ItemLeaseStorage.Read<ItemLeasePointer>(active);
            if (pointer.LeaseId == leaseId && string.Equals(Path.GetFullPath(pointer.Journal), Path.GetFullPath(journal), StringComparison.OrdinalIgnoreCase)) File.Delete(active);
        }
        File.Delete(journal);
    }
}

/// <summary>日志中的单项恢复责任，身份和坐标均在 Hidden 修改前持久化。</summary>
/// <param name="Path">原桌面直接子项绝对路径，是 UI 与租约交互的稳定键。</param>
/// <param name="Identity">卷、文件 ID 和创建时间，用于防止路径替换误恢复。</param>
/// <param name="OriginalHidden">原 Hidden 位，第一版只接受 false。</param>
/// <param name="Position">收纳前物理屏幕像素坐标。</param>
internal sealed record ItemLeaseEntry(string Path, ItemFileIdentity Identity, bool OriginalHidden, DesktopItemPosition Position);
/// <summary>原子持久化的租约和全部最新责任；guard 必须在父进程退出后重新读取。</summary>
/// <param name="Version">日志格式版本，目前为 1。</param><param name="LeaseId">随机 N 格式租约标识。</param>
/// <param name="OwnerPid">父进程 ID。</param><param name="OwnerStart">父进程 UTC 启动 ticks，用于拒绝 PID 复用。</param>
/// <param name="GuardPid">守护进程 ID，握手前可能为 0。</param><param name="GuardStart">守护进程 UTC 启动 ticks。</param>
/// <param name="ReadyEvent">包含租约标识的随机握手事件。</param><param name="Released">正常释放已开始，允许显式恢复重试。</param>
/// <param name="FixtureRoot">仅诊断存在，必须位于源项目 .codex；生产为 null。</param><param name="Items">尚未恢复完成的逐项责任。</param>
internal sealed record ItemLeaseJournal(int Version, string LeaseId, int OwnerPid, long OwnerStart, int GuardPid, long GuardStart,
    string ReadyEvent, bool Released, string? FixtureRoot, List<ItemLeaseEntry> Items);
/// <summary>跨配置活动日志指针，只有与原日志完整身份一致才可恢复。</summary>
/// <param name="Journal">恢复日志绝对路径。</param><param name="LeaseId">租约标识。</param><param name="OwnerPid">父进程 ID。</param><param name="OwnerStart">父进程 UTC 启动 ticks。</param>
internal sealed record ItemLeasePointer(string Journal, string LeaseId, int OwnerPid, long OwnerStart);

internal static class ItemLeaseLimits
{
    internal const int MaxItemCount = 4096;
    internal const int MaxUtf8Bytes = 4 * 1024 * 1024;
}

internal static class ItemLeaseStorage
{
    private const string Owner = "DesktopGlass-52ed77bc-5636-48d9-98f9-548648e153bc";
    internal static void EnsureOwnedDirectory(string directory)
    {
        string? existing = directory;
        while (existing is not null && !Directory.Exists(existing)) existing = Path.GetDirectoryName(existing);
        if (existing is not null) ItemPathPolicy.EnsureNoReparse(existing);
        if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
        ItemPathPolicy.EnsureNoReparse(directory);
        string marker = Path.Combine(directory, ".desktop-glass-owner");
        if (File.Exists(marker))
        {
            if (File.ReadAllText(marker) != Owner || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("全局租约目录所有权标记不匹配。");
        }
        else
        {
            if (Directory.EnumerateFileSystemEntries(directory).Any()) throw new InvalidOperationException("全局租约目录包含无法识别的内容，未覆盖。");
            using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            stream.Write(Encoding.UTF8.GetBytes(Owner)); stream.Flush(true);
        }
    }
    internal static T Read<T>(string path)
    {
        ItemPathPolicy.EnsureNoReparse(path);
        var info = new FileInfo(path);
        if (info.Length > ItemLeaseLimits.MaxUtf8Bytes) throw new InvalidDataException("恢复日志超出大小限制。");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? throw new InvalidDataException("恢复日志为空或无效。");
    }
    internal static byte[] Prepare<T>(T value)
    {
        if (value is ItemLeaseJournal journal && (journal.Items is null || journal.Items.Count > ItemLeaseLimits.MaxItemCount))
            throw new InvalidDataException("收纳项数量超过可恢复日志上限，未修改属性或已有日志。");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > ItemLeaseLimits.MaxUtf8Bytes) throw new InvalidDataException("恢复日志UTF8字节数超过上限，未修改属性或已有日志。");
        return bytes;
    }    internal static void Write<T>(string path, T value)
    {
        byte[] bytes = Prepare(value);
        string directory = Path.GetDirectoryName(path)!; ItemPathPolicy.EnsureNoReparse(directory);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("恢复文件不能是重解析链接。");
        string temporary = path + ".new-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            if (!ItemFileNative.MoveFileEx(temporary, path, 9)) throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), "原子提交恢复日志失败。");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>诊断坐标替身，只由严格 .codex fixture 工厂创建，不连接或修改真实桌面。</summary>
internal sealed class ItemFixturePositions : IItemDesktopPositions
{
    internal bool FailRestore { get; set; }
    internal bool CollectionAllowed { get; set; } = true;
    internal DesktopItemAttributeRead ViewOptions { get; set; } = new(0, 0);
    internal bool ProcessShowHidden { get; set; }
    internal int Restored { get; private set; }
    /// <summary>fixture 替身没有 Explorer 依赖。</summary>
    public bool Alive => true;
    /// <summary>建立fixture展示能力，不读取也不改变真实Shell。</summary>
    public void EnsureAvailable() { }
    /// <summary>仅检查诊断注入的收纳条件；正式路径工厂不能使用此实现。</summary>
    public void EnsureCanCollect() { if (!CollectionAllowed) throw new InvalidOperationException("诊断模拟：显示隐藏项或自动排列条件不允许收纳。"); DesktopItemPositions.ValidateCollectionVisibility(ProcessShowHidden, ViewOptions); }
    /// <summary>返回自有 fixture 的固定物理坐标以验证日志和恢复事务。</summary>
    /// <param name="path">已通过诊断路径边界的 fixture 路径。</param><returns>固定坐标。</returns>
    public DesktopItemPosition Capture(string path) { EnsureCanCollect(); return new(25, 50); }
    /// <summary>记录恢复请求或注入可重试失败，不操作真实桌面。</summary>
    /// <param name="path">fixture 当前路径。</param><param name="position">日志中的坐标。</param>
    /// <exception cref="IOException">诊断明确要求注入恢复失败。</exception>
    public void Restore(string path, DesktopItemPosition position)
    { if (FailRestore) throw new IOException("诊断注入的坐标恢复失败。"); Restored++; }
}
