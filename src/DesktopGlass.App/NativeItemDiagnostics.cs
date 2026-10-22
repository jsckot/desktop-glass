using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.Json;
using DesktopGlass.App.Windows;

namespace DesktopGlass.App;

/// <summary>仅对显式指定、具备完整所有权标记的桌面测试快捷方式执行真实收纳及强退恢复诊断。</summary>
internal static class NativeItemDiagnostics
{
    internal const string FixtureToken = "DesktopGlass-native-verification-v1";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>验证生产租约的真实 Shell 恢复；不创建、执行或删除桌面项目，不替代鼠标和最终合成验收。</summary>
    /// <param name="args">模式及必填 --fixture、--data、--output；强退子进程还需父进程生成的 --run-id。</param>
    /// <returns>指定项目、原生视图及可比较样本的结构检查通过时为 0，失败或恢复不完整时为 1。</returns>
    internal static int Run(string[] args)
    {
        if (args.Contains("--native-item-crash-child")) return Child(args);
        string? output = null;
        Options? options = null;
        FileState? before = null, after = null;
        ViewState? nativeBefore = null, nativeAfter = null;
        object? normal = null, crash = null;
        var normalAttempts = new List<AttemptObservation>();
        string normalStage = "not-started";
        string? normalAttemptError = null, normalDisposalError = null;
        SampleSelection? sampleSelection = null;
        var errors = new List<string>(); var recovery = new List<object>();
        var samples = new List<SampleState>(); var comparisons = new List<SampleComparison>();
        var runId = Guid.NewGuid().ToString("N");
        Process? child = null; long childStart = 0;
        string? normalProfile = null, crashProfile = null;
        try
        {
            output = CheckedPath(Required(args, "--output"), false);
            if (!args.Contains("--native-item-test")) throw new ArgumentException("需要 --native-item-test 模式。");
            options = Validate(args, output);
            // 测试不得借生产 Acquire 的陈旧恢复流程接手其他配置。已有登记须先由主任务显式处理。
            if (File.Exists(RegisteredPath())) throw new InvalidOperationException("生产租约已有活动登记；未执行真实测试，请先确认并恢复已有责任。");
            var positions = new DesktopItemPositions(); positions.EnsureCanCollect();
            before = Capture(options.Fixture, positions, true); nativeBefore = NativeView();
            Assert(!before.Hidden && before.Visible, "测试快捷方式必须原本可见且不含 Hidden 位。");
            (samples, sampleSelection) = CaptureSamples(options.Fixture, positions);
            normalProfile = Path.Combine(options.Data, "normal-" + runId);
            crashProfile = Path.Combine(options.Data, "crash-" + runId);
            FileState? hidden = null, released = null; ViewState? nativeHidden = null;
            Exception? normalFailure = null;
            var lease = new ItemVisibilityLease(normalProfile);
            try
            {
                normalStage = "acquire";
                lease.Acquire(); Assert(lease.Alive, "正常阶段的恢复保护未就绪。");
                normalAttempts.Add(ObserveFixture(options.Fixture, "before-collect"));
                normalStage = "collect";
                lease.Collect(options.Fixture);
                normalAttempts.Add(ObserveFixture(options.Fixture, "immediately-after-collect"));
                normalStage = "wait-hidden-and-find-absent";
                WaitFor(() => Hidden(options.Fixture) && !Visible(options.Fixture), TimeSpan.FromSeconds(5), "Hidden 已设置但目标未从当前 IFolderView 消失。");
                normalStage = "verify-hidden";
                hidden = Capture(options.Fixture, positions, false); nativeHidden = NativeView();
                Assert(hidden.Identity == before.Identity && hidden.Hidden && !hidden.Visible, "正常收纳的身份、Hidden 或 Shell 可见性不符。");
                Assert(SameView(nativeBefore, nativeHidden), "收纳期间原生图标视图的身份、可见性或样式改变。");
                normalStage = "release";
                lease.Release(options.Fixture); released = Capture(options.Fixture, positions, true);
                normalStage = "verify-restored";
                AssertRestored(before, released); Assert(!lease.Contains(options.Fixture), "移出后仍存在恢复责任。");
            }
            catch (Exception error)
            {
                // 先保留首个异常与恢复前现场，再释放租约；观测本身的错误只写入各字段，不替换首个异常。
                normalFailure = error; normalAttemptError = error.ToString();
                normalAttempts.Add(ObserveFixture(options.Fixture, "failure-before-dispose:" + normalStage));
            }
            finally
            {
                try { lease.Dispose(); }
                catch (Exception error)
                {
                    normalDisposalError = error.ToString();
                    normalFailure = normalFailure is null ? error : new AggregateException("正常阶段失败且租约释放也失败。", normalFailure, error);
                }
            }
            if (normalFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(normalFailure).Throw();
            normalStage = "complete";
            bool normalJournalRemoved = !File.Exists(Path.Combine(normalProfile, ItemVisibilityLease.JournalName));
            Assert(normalJournalRemoved, "正常释放后日志未清理。");
            normal = new { Hidden = hidden, Released = released, NativeDuring = nativeHidden, NativeUnchanged = true, JournalRemoved = normalJournalRemoved };

            Validate(args, output); // 两阶段之间再次核对快捷方式和目标，绝不执行或解析目标。
            Assert(Capture(options.Fixture, positions, true).Identity == before.Identity, "强退阶段开始前测试对象已被替换。");
            if (File.Exists(RegisteredPath())) throw new InvalidOperationException("强退阶段开始前出现其他租约登记，已停止测试。");
            var ready = Path.Combine(options.Data, "crash-ready-" + runId + ".json");
            var childOutput = Path.Combine(options.Data, "crash-child-" + runId + ".json");
            child = Self.Start("--native-item-crash-child", "--fixture", options.Fixture, "--data", options.Data,
                "--output", childOutput, "--run-id", runId);
            childStart = child.StartTime.ToUniversalTime().Ticks;
            WaitFor(() => File.Exists(ready) || child.HasExited, TimeSpan.FromSeconds(22), "真实恢复测试子进程未就绪。");
            if (child.HasExited || !File.Exists(ready)) throw new InvalidOperationException("测试子进程提前退出；请检查 " + childOutput);
            var readyState = ItemLeaseStorage.Read<ReadyState>(ready);
            Assert(readyState.RunId == runId && readyState.ChildPid == child.Id && readyState.ChildStart == childStart &&
                readyState.Fixture == options.Fixture && readyState.Identity == before.Identity &&
                Path.GetFullPath(readyState.Journal) == Path.Combine(crashProfile, ItemVisibilityLease.JournalName), "子进程就绪证据不匹配本次测试。");
            Assert(Hidden(options.Fixture), "终止子进程前目标并未隐藏。");
            WaitFor(() => !Visible(options.Fixture), TimeSpan.FromSeconds(5), "强退阶段目标仍在当前 IFolderView 中。");
            var crashHidden = Capture(options.Fixture, positions, false);
            Assert(crashHidden.Identity == before.Identity && SameView(nativeBefore, NativeView()), "强退前对象身份或原生视图状态改变。");
            // 仅终止 Self.Start 返回的这个测试子进程，不终止其 guard、不按进程名搜索或终止其他程序。
            Assert(!child.HasExited && child.StartTime.ToUniversalTime().Ticks == childStart, "测试子进程生命周期已改变。");
            int childPid = child.Id; child.Kill();
            Assert(child.WaitForExit(5000), "测试子进程未在终止后退出。");
            var crashJournal = Path.Combine(crashProfile, ItemVisibilityLease.JournalName);
            var guardWait = Stopwatch.StartNew();
            WaitFor(() => !Hidden(options.Fixture) && !File.Exists(crashJournal) && Visible(options.Fixture), TimeSpan.FromSeconds(15), "guard 未在15秒内恢复 Hidden、Shell 可见性及日志。");
            var restored = Capture(options.Fixture, positions, true); AssertRestored(before, restored);
            crash = new { ChildPid = childPid, ChildStart = childStart, ChildExitCode = child.ExitCode, ReadyPath = ready,
                Ready = readyState, HiddenBeforeKill = crashHidden, Restored = restored, GuardWaitMs = guardWait.ElapsedMilliseconds,
                JournalRemoved = !File.Exists(crashJournal), OnlyOwnedChildKilled = true };
        }
        catch (Exception error) { errors.Add(error.ToString()); }
        finally
        {
            if (child is not null)
            {
                try
                {
                    if (!child.HasExited)
                    {
                        Assert(child.StartTime.ToUniversalTime().Ticks == childStart, "清理时测试子进程身份不符，未执行终止。");
                        child.Kill(); Assert(child.WaitForExit(5000), "清理时子进程终止超时。");
                    }
                }
                catch (Exception error) { errors.Add("测试子进程清理：" + error); }
                child.Dispose();
            }
            if (options is not null && before is not null && normalProfile is not null && crashProfile is not null)
            {
                RecoverOwn(options.Fixture, before.Identity, [normalProfile, crashProfile], recovery, errors);
                try
                {
                    var positions = new DesktopItemPositions();
                    after = Capture(options.Fixture, positions, true); AssertRestored(before, after);
                    nativeAfter = NativeView(); Assert(SameView(nativeBefore!, nativeAfter), "测试后原生视图身份、可见性或样式不符。");
                    comparisons = CompareSamples(samples, positions);
                    if (comparisons.Any(sample => sample.PositionChangedObserved))
                        errors.Add("观察到未收纳样本的位置变化；无法排除并发用户操作，未将原因归于软件，结构检查按失败记录。");
                }
                catch (Exception error) { errors.Add("最终状态核验：" + error); }
            }
        }
        bool passed = errors.Count == 0 && normal is not null && crash is not null && after is not null;
        if (output is not null) Write(output, new { Passed = passed, RunId = runId, Fixture = options?.Fixture,
            Data = options?.Data, Before = before, After = after, NativeBefore = nativeBefore, NativeAfter = nativeAfter,
            Normal = normal, NormalAttempt = new { Stage = normalStage, PrimaryError = normalAttemptError,
                DisposalError = normalDisposalError, Observations = normalAttempts }, Crash = crash,
            SampleSelection = sampleSelection, SamplesBefore = samples, SampleComparisons = comparisons, Recovery = recovery,
            Errors = errors, FixtureCreated = false, FixtureDeleted = false, StructuralEvidenceOnly = true,
            MouseInteractionVerified = false, FinalDesktopCompositionVerified = false });
        return passed ? 0 : 1;
    }

    private static int Child(string[] args)
    {
        string? output = null; ItemVisibilityLease? lease = null; Exception? failure = null;
        try
        {
            output = CheckedPath(Required(args, "--output"), false); var options = Validate(args, output);
            var runId = Required(args, "--run-id");
            Assert(Guid.TryParseExact(runId, "N", out _), "强退子进程需要父进程生成的 N 格式 run-id。");
            Assert(!File.Exists(RegisteredPath()), "测试子进程发现已有租约登记，未接手其他责任。");
            var positions = new DesktopItemPositions(); var before = Capture(options.Fixture, positions, true);
            lease = new ItemVisibilityLease(Path.Combine(options.Data, "crash-" + runId)); lease.Acquire();
            Assert(lease.Alive, "强退子进程恢复保护未就绪。"); lease.Collect(options.Fixture);
            Assert(Hidden(options.Fixture), "测试子进程收纳后 Hidden 位不符。");
            var journal = ItemLeaseStorage.Read<ItemLeaseJournal>(lease.JournalPathForDiagnostics);
            using var self = Process.GetCurrentProcess();
            var ready = new ReadyState(runId, self.Id, self.StartTime.ToUniversalTime().Ticks, options.Fixture, before.Identity,
                lease.JournalPathForDiagnostics, journal.LeaseId, journal.GuardPid, journal.GuardStart, true, NativeView());
            Write(Path.Combine(options.Data, "crash-ready-" + runId + ".json"), ready);
            var wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(60)) Thread.Sleep(100);
            throw new TimeoutException("父进程未在60秒内终止测试子进程，已正常释放租约。");
        }
        catch (Exception error) { failure = error; }
        finally
        {
            try { lease?.Dispose(); } catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
            if (output is not null) Write(output, new { Passed = false, Child = true, Error = failure?.ToString(), FixtureDeleted = false });
        }
        return 1;
    }

    private static Options Validate(string[] args, string output)
    {
        var data = CheckedPath(Required(args, "--data"), true);
        Assert(Directory.Exists(data), "--data 必须是已存在的本次测试目录。"); ItemPathPolicy.EnsureNoReparse(data);
        var fixture = Path.GetFullPath(Required(args, "--fixture"));
        var desktop = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)).TrimEnd(Path.DirectorySeparatorChar);
        Assert(string.Equals(Path.GetDirectoryName(fixture), desktop, StringComparison.OrdinalIgnoreCase), "fixture 只能是当前用户桌面直接子项。");
        var name = Path.GetFileName(fixture); const string prefix = "DesktopGlass-验证用-";
        Assert(name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase), "fixture 名称不符合专用测试格式。");
        var guid = name[prefix.Length..^4]; Assert(Guid.TryParseExact(guid, "N", out _) || Guid.TryParseExact(guid, "D", out _), "fixture 名称必须包含 GUID。");
        Assert(File.Exists(fixture), "fixture 必须已经存在；诊断不会创建桌面项。"); ItemPathPolicy.EnsureNoReparse(fixture);
        var target = Path.Combine(data, "fixture-target.txt"); Assert(File.Exists(target), "测试目标文件不存在。"); ItemPathPolicy.EnsureNoReparse(target);
        Assert(new FileInfo(target).Length < 4096 && File.ReadAllText(target) == FixtureToken, "测试目标的固定内容标记不符。");
        var shortcut = ReadShortcut(fixture);
        Assert(shortcut.Description == Installation.ProductId + ": native verification" && string.IsNullOrEmpty(shortcut.Arguments), "快捷方式描述或空参数所有权标记不符。");
        Assert(Path.IsPathFullyQualified(shortcut.Target) && string.Equals(Path.GetFullPath(shortcut.Target), target, StringComparison.OrdinalIgnoreCase), "快捷方式目标必须精确指向 --data 下的 fixture-target.txt。");
        return new(fixture, data, output);
    }

    private static ShortcutState ReadShortcut(string path)
    {
        object? shell = null; nint unknown = 0, link = 0;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!);
            ((IPersistFile)shell!).Load(path, 0);
            unknown = Marshal.GetIUnknownForObject(shell!); var iid = new Guid("000214F9-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out link));
            // IShellLinkW 的 SDK 槽位：GetPath=3、GetDescription=6、GetArguments=10；只读 Load，不 Resolve/Save。
            var target = new StringBuilder(32768); var description = new StringBuilder(1024); var arguments = new StringBuilder(32768);
            Marshal.ThrowExceptionForHR(Call<GetPath>(link, 3)(link, target, target.Capacity, 0, 4));
            Marshal.ThrowExceptionForHR(Call<GetText>(link, 6)(link, description, description.Capacity));
            Marshal.ThrowExceptionForHR(Call<GetText>(link, 10)(link, arguments, arguments.Capacity));
            return new(target.ToString(), description.ToString(), arguments.ToString());
        }
        finally { if (link != 0) Marshal.Release(link); if (unknown != 0) Marshal.Release(unknown); if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell); }
    }

    private static FileState Capture(string path, DesktopItemPositions positions, bool position)
    {
        using var file = ItemFileObject.Open(path, false); var visible = Visible(path);
        return new(file.Identity, (uint)file.Attributes, (file.Attributes & FileAttributes.Hidden) != 0, visible,
            position ? positions.Capture(path) : null);
    }
    private static bool Hidden(string path) { using var item = ItemFileObject.Open(path, false); return (item.Attributes & FileAttributes.Hidden) != 0; }
    private static bool Visible(string path)
    {
        using var view = new DesktopItemPositionsView(); var pidl = view.Find(path);
        if (pidl == 0) return false; Marshal.FreeCoTaskMem(pidl); return true;
    }
    private static void AssertRestored(FileState before, FileState after)
    { Assert(before.Identity == after.Identity && before.Hidden == after.Hidden && after.Visible && before.Position == after.Position, "原对象的身份、Hidden、Shell 可见性或精确坐标未恢复。"); }

    private static (List<SampleState> Samples, SampleSelection Selection) CaptureSamples(string fixture, DesktopItemPositions positions)
    {
        var result = new List<SampleState>(); var desktop = Path.GetDirectoryName(fixture);
        var scanned = new DesktopCatalog().Scan(); var candidates = new List<DesktopEntry>();
        int virtualExcluded = 0, fixtureExcluded = 0, otherDirectoryExcluded = 0, notFileExcluded = 0, captureErrors = 0;
        foreach (var entry in scanned)
        {
            if (entry.Path.StartsWith("::", StringComparison.Ordinal)) { virtualExcluded++; continue; }
            if (string.Equals(entry.Path, fixture, StringComparison.OrdinalIgnoreCase)) { fixtureExcluded++; continue; }
            if (!string.Equals(Path.GetDirectoryName(entry.Path), desktop, StringComparison.OrdinalIgnoreCase)) { otherDirectoryExcluded++; continue; }
            if (!File.Exists(entry.Path)) { notFileExcluded++; continue; }
            candidates.Add(entry);
        }
        foreach (var entry in candidates.Take(4))
        {
            try { using var item = ItemFileObject.Open(entry.Path, false); result.Add(new(entry.Path, item.Identity, positions.Capture(entry.Path), null)); }
            catch (Exception error) { captureErrors++; result.Add(new(entry.Path, null, null, error.Message)); }
        }
        return (result, new SampleSelection(scanned.Count, virtualExcluded, fixtureExcluded, otherDirectoryExcluded,
            notFileExcluded, candidates.Count, result.Count, captureErrors, "当前用户桌面直接文件；不含目录、虚拟项目或测试fixture，最多4项"));
    }

    private static AttemptObservation ObserveFixture(string fixture, string stage)
    {
        ItemFileIdentity? identity = null; uint? attributes = null; bool? hidden = null, found = null;
        int? folderCount = null; DesktopItemPosition? client = null, screen = null; ViewState? native = null; DesktopItemVisibilityState? visibilityState = null;
        var errors = new List<string>();
        try
        {
            using var item = ItemFileObject.Open(fixture, false); identity = item.Identity;
            var current = item.Attributes; attributes = (uint)current; hidden = (current & FileAttributes.Hidden) != 0;
        }
        catch (Exception error) { errors.Add("读取fixture属性/身份：" + error); }
        try
        {
            using var dpi = new DesktopItemPositionsDpi(); using var view = new DesktopItemPositionsView();
            try { folderCount = view.Count; } catch (Exception error) { errors.Add("读取IFolderView.ItemCount：" + error); }
            try { visibilityState = view.ReadVisibilityState(fixture); } catch (Exception error) { errors.Add("读取Shell实际可见性设置：" + error); }
            nint pidl = 0;
            try
            {
                pidl = view.Find(fixture); found = pidl != 0;
                if (pidl != 0)
                {
                    var point = view.Position(pidl); client = new(point.X, point.Y);
                    if (!DesktopItemPositionsNative.ClientToScreen(view.Hwnd, ref point)) throw new Win32Exception(Marshal.GetLastWin32Error(), "现场坐标转换失败。");
                    screen = new(point.X, point.Y);
                }
            }
            catch (Exception error) { errors.Add("读取IFolderView.Find/坐标：" + error); }
            finally { if (pidl != 0) Marshal.FreeCoTaskMem(pidl); }
        }
        catch (Exception error) { errors.Add("取得IFolderView/DPI上下文：" + error); }
        try { native = NativeView(); } catch (Exception error) { errors.Add("读取原生列表状态：" + error); }
        return new(stage, DateTimeOffset.UtcNow, identity, attributes, hidden, found, folderCount,
            "SVGIO_ALLVIEW=2；Find存在性与最终屏幕绘制分开记录", client, screen, native, visibilityState, errors.ToArray());
    }
    private static List<SampleComparison> CompareSamples(List<SampleState> samples, DesktopItemPositions positions)
    {
        var result = new List<SampleComparison>();
        foreach (var sample in samples)
        {
            if (sample.Identity is null) { result.Add(new(sample.Path, false, false, null, "基线读取失败，未比较")); continue; }
            try
            {
                using var item = ItemFileObject.Open(sample.Path, false);
                if (item.Identity != sample.Identity) { result.Add(new(sample.Path, false, false, null, "样本身份改变，可能有并发用户操作，未比较")); continue; }
                var current = positions.Capture(sample.Path); bool changed = current != sample.Position;
                result.Add(new(sample.Path, true, changed, current, changed ? "观察到位置变化，原因未归因，不能排除并发用户操作" : "同一对象位置一致"));
            }
            catch (Exception error) { result.Add(new(sample.Path, false, false, null, "样本不可比较：" + error.Message)); }
        }
        return result;
    }

    private static ViewState NativeView()
    {
        var list = Native.DesktopListView(); Assert(list != 0 && Native.IsWindow(list), "原生图标视图不可用。");
        Native.GetWindowThreadProcessId(list, out var pid); using var explorer = Process.GetProcessById(checked((int)pid));
        Assert(SendMessageTimeoutW(list, 0x1004, 0, 0, 2, 1000, out var count) != 0, "原生图标数读取超时。");
        return new(list.ToInt64(), Native.GetParent(list).ToInt64(), Native.IsWindowVisible(list), (long)Native.GetWindowLongPtr(list, -16),
            (long)Native.GetWindowLongPtr(list, -20), count.ToInt64(), pid, explorer.StartTime.ToUniversalTime().Ticks);
    }
    private static bool SameView(ViewState before, ViewState after) => before with { ItemCount = 0 } == after with { ItemCount = 0 };
    private static void RecoverOwn(string fixture, ItemFileIdentity identity, string[] profiles, List<object> evidence, List<string> errors)
    {
        bool OwnJournal(string path)
        {
            if (!profiles.Any(profile => string.Equals(Path.Combine(profile, ItemVisibilityLease.JournalName), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))) return false;
            var value = ItemLeaseStorage.Read<ItemLeaseJournal>(path);
            return value.FixtureRoot is null && value.Items.All(item => string.Equals(item.Path, fixture, StringComparison.OrdinalIgnoreCase) && item.Identity == identity);
        }
        try
        {
            var active = RegisteredPath();
            if (File.Exists(active) && !OwnJournal(ItemLeaseStorage.Read<ItemLeasePointer>(active).Journal))
                throw new InvalidOperationException("全局登记不属于本轮测试，未恢复无关责任。");
            ItemVisibilityLease.RecoverRegistered(); evidence.Add(new { Operation = "RecoverRegistered", Passed = true });
        }
        catch (Exception error) { errors.Add("登记恢复：" + error); evidence.Add(new { Operation = "RecoverRegistered", Passed = false }); }
        foreach (var profile in profiles)
        {
            try
            {
                var journal = Path.Combine(profile, ItemVisibilityLease.JournalName);
                if (File.Exists(journal) && !OwnJournal(journal)) throw new InvalidOperationException("测试profile日志范围不符，未改动并保留日志。");
                ItemVisibilityLease.Recover(profile); evidence.Add(new { Operation = "Recover", Profile = profile, Passed = true, JournalRemaining = File.Exists(journal) });
            }
            catch (Exception error) { errors.Add("测试profile恢复：" + error); evidence.Add(new { Operation = "Recover", Profile = profile, Passed = false }); }
        }
    }
    private static string RegisteredPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopGlass", "item-lease-state", "item-active-lease.json");
    private static string CheckedPath(string path, bool directory)
    {
        var root = new DirectoryInfo(Environment.CurrentDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DesktopGlass.slnx"))) root = root.Parent;
        Assert(root is not null, "诊断必须从源项目内运行。");
        var allowed = Path.Combine(root!.FullName, ".codex", "tmp", "native-item") + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        Assert(full.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && (directory || Path.GetExtension(full).Equals(".json", StringComparison.OrdinalIgnoreCase)), "测试data及output只能位于项目.codex/tmp/native-item/子目录。");
        var existing = directory ? full : Path.GetDirectoryName(full)!;
        while (!Directory.Exists(existing)) existing = Path.GetDirectoryName(existing) ?? throw new IOException("无法确认测试目录。");
        ItemPathPolicy.EnsureNoReparse(existing);
        if (File.Exists(full)) ItemPathPolicy.EnsureNoReparse(full);
        return full;
    }
    private static string Required(string[] args, string key)
    { var index = Array.IndexOf(args, key); if (index < 0 || index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException(key + " 缺少参数。"); return args[index + 1]; }
    private static void WaitFor(Func<bool> condition, TimeSpan timeout, string message)
    { var watch = Stopwatch.StartNew(); while (watch.Elapsed < timeout) { if (condition()) return; Thread.Sleep(100); } throw new TimeoutException(message); }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Write(string path, object value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); ItemLeaseStorage.Write(path, value); }
    private static T Call<T>(nint instance, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    private sealed record Options(string Fixture, string Data, string Output);
    private sealed record ShortcutState(string Target, string Description, string Arguments);
    private sealed record FileState(ItemFileIdentity Identity, uint Attributes, bool Hidden, bool Visible, DesktopItemPosition? Position);
    private sealed record ViewState(long Handle, long Parent, bool Visible, long Style, long ExtendedStyle, long ItemCount, uint ExplorerPid, long ExplorerStart);
    private sealed record SampleState(string Path, ItemFileIdentity? Identity, DesktopItemPosition? Position, string? Error);
    private sealed record SampleSelection(int CatalogEntries, int VirtualExcluded, int FixtureExcluded, int OtherDirectoryExcluded,
        int NotExistingFileExcluded, int Eligible, int Selected, int CaptureErrors, string Scope);
    private sealed record AttemptObservation(string Stage, DateTimeOffset CapturedAt, ItemFileIdentity? Identity, uint? Attributes,
        bool? FixtureHidden, bool? FolderViewFindPresent, int? FolderViewItemCount, string FolderViewCountScope,
        DesktopItemPosition? FolderViewClientPosition, DesktopItemPosition? FolderViewScreenPosition, ViewState? NativeView, DesktopItemVisibilityState? VisibilityState, string[] ObservationErrors);
    private sealed record SampleComparison(string Path, bool Comparable, bool PositionChangedObserved, DesktopItemPosition? After, string Note);
    private sealed record ReadyState(string RunId, int ChildPid, long ChildStart, string Fixture, ItemFileIdentity Identity, string Journal, string LeaseId, int GuardPid, long GuardStart, bool Hidden, ViewState Native);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] private delegate int GetPath(nint self, [Out] StringBuilder path, int size, nint findData, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] private delegate int GetText(nint self, [Out] StringBuilder text, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SendMessageTimeoutW(nint hwnd, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
}
