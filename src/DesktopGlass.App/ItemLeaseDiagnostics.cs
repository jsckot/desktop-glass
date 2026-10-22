using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using DesktopGlass.App.Windows;

namespace DesktopGlass.App;

/// <summary>仅在源项目 .codex 自有 fixture 上验证项目租约；坐标替身不连接真实桌面。</summary>
internal static class ItemLeaseDiagnostics
{
    /// <summary>执行租约诊断或强退子进程；所有创建和属性操作均限制在带标记的自有 fixture。</summary>
    /// <param name="args">--item-lease-tests --output 路径，或 --item-lease-crash-child 的 data/fixture/ready 参数。</param>
    /// <returns>全部检查通过为 0，失败为 1；报告明确记录未通过项。</returns>
    internal static int Run(string[] args)
    {
        if (args.Contains("--item-lease-crash-child")) return Child(args);
        string? output = null;
        var results = new List<object>();
        try
        {
            output = CheckedOutput(Argument(args, "--output") ?? throw new ArgumentException("需要 --output。"));
            string work = Path.Combine(Path.GetDirectoryName(output)!, "item-lease-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            Test("属性仅合并Hidden位", root =>
            {
                string path = NewFile(root, "attributes.txt");
                using var item = ItemFileObject.Open(path);
                item.SetHidden(true); Assert(Hidden(path), "应添加Hidden位");
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                item.SetHidden(false); Assert(!Hidden(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0, "后增ReadOnly必须保留");
            });
            Test("日志先落盘与正常恢复", root =>
            {
                string path = NewFile(root, "normal.txt"); bool persisted = false;
                var positions = new ItemFixturePositions();
                using var lease = ItemVisibilityLease.Diagnostic(Profile(root, "normal"), root, positions, journal =>
                {
                    var record = ItemLeaseStorage.Read<ItemLeaseJournal>(journal);
                    Assert(record.Items.Count == 1 && record.Items[0].Identity.FileId.Length == 32 && !Hidden(path), "属性修改前日志必须已有完整身份且文件仍可见");
                    persisted = true;
                });
                lease.Acquire(); Assert(lease.Alive, "guard应握手就绪");
                lease.Collect(path); lease.ValidateReference(path); lease.Collect(path);
                Assert(persisted && Hidden(path) && lease.Contains(path), "收纳应隐藏且保留责任");
                Assert(ItemLeaseStorage.Read<ItemLeaseJournal>(lease.JournalPathForDiagnostics).Items.Count == 1, "重复收纳应幂等");
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                lease.Release(path); lease.Release(path);
                Assert(!Hidden(path) && !lease.Contains(path) && positions.Restored == 1, "移出应恢复且幂等");
                Assert((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0, "恢复不应覆盖新增ReadOnly位");
            });
            Test("跨配置互斥", root =>
            {
                using var first = ItemVisibilityLease.Diagnostic(Profile(root, "one"), root); first.Acquire();
                using var second = ItemVisibilityLease.Diagnostic(Profile(root, "two"), root);
                Expect<InvalidOperationException>(() => second.Acquire()); Assert(first.Alive, "第二配置不得破坏第一租约");
            });
            Test("生产路径拒绝fixture", root =>
            {
                string path = NewFile(root, "not-desktop.txt");
                Expect<InvalidOperationException>(() => ItemPathPolicy.Desktop().Validate(path)); Assert(!Hidden(path), "路径拒绝不得修改属性");
            });
            Test("原隐藏项拒绝", root =>
            {
                string path = NewFile(root, "already-hidden.txt"); File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
                using var lease = ItemVisibilityLease.Diagnostic(Profile(root, "hidden"), root); lease.Acquire();
                Expect<InvalidOperationException>(() => lease.Collect(path)); Assert(Hidden(path) && !lease.Contains(path), "不得接管原隐藏项");
            });
            Test("改名及同名替身", root =>
            {
                string original = NewFile(root, "rename.txt"), renamed = Path.Combine(root, "renamed.txt");
                using var lease = ItemVisibilityLease.Diagnostic(Profile(root, "rename"), root); lease.Acquire(); lease.Collect(original);
                File.Move(original, renamed); File.WriteAllText(original, "replacement");
                File.SetAttributes(original, File.GetAttributes(original) | FileAttributes.Hidden);
                Expect<InvalidOperationException>(() => lease.ValidateReference(original));
                lease.Release(original);
                Assert(!Hidden(renamed) && Hidden(original), "应恢复原对象并保持同名替身属性不变");
            });
            Test("同卷按128位ID重开", root =>
            {
                string original = NewFile(root, "by-id.txt"), renamed = Path.Combine(root, "by-id-renamed.txt");
                ItemFileIdentity identity;
                using (var item = ItemFileObject.Open(original)) { identity = item.Identity; item.SetHidden(true); }
                File.Move(original, renamed); File.WriteAllText(original, "unrelated replacement");
                using var reopened = ItemFileObject.OpenIdentity(identity, root) ?? throw new IOException("按ID未找到已改名fixture");
                Assert(reopened.Identity == identity && string.Equals(reopened.CurrentPath, renamed, StringComparison.OrdinalIgnoreCase), "按ID必须定位原改名对象");
                reopened.SetHidden(false); Assert(!Hidden(renamed), "按ID对象应恢复Hidden");
            });
            Test("坐标失败保留责任并重试", root =>
            {
                string path = NewFile(root, "retry.txt"); var positions = new ItemFixturePositions();
                using var lease = ItemVisibilityLease.Diagnostic(Profile(root, "retry"), root, positions); lease.Acquire(); lease.Collect(path);
                positions.FailRestore = true; Expect<AggregateException>(() => lease.Release(path));
                Assert(!Hidden(path) && lease.Contains(path) && File.Exists(lease.JournalPathForDiagnostics), "先恢复属性且不能丢弃待恢复坐标");
                positions.FailRestore = false; lease.Release(path); Assert(!lease.Contains(path), "可重试完成恢复");
            });
            Test("Dispose失败日志支持显式恢复", root =>
            {
                string path = NewFile(root, "dispose-retry.txt"); var positions = new ItemFixturePositions();
                var lease = ItemVisibilityLease.Diagnostic(Profile(root, "dispose-retry"), root, positions); lease.Acquire(); lease.Collect(path);
                positions.FailRestore = true; Expect<AggregateException>(() => lease.Dispose());
                Assert(!Hidden(path) && File.Exists(lease.JournalPathForDiagnostics), "Dispose失败应保留记录但先恢复Hidden");
                ItemVisibilityLease.Recover(lease.JournalPathForDiagnostics); ItemVisibilityLease.Recover(lease.JournalPathForDiagnostics);
                Assert(!File.Exists(lease.JournalPathForDiagnostics), "Released日志可幂等重试");
            });
            Test("后置故障回滚", root =>
            {
                string path = NewFile(root, "rollback.txt");
                using var lease = ItemVisibilityLease.Diagnostic(Profile(root, "rollback"), root, persisted: _ => throw new IOException("诊断注入日志提交后异常"));
                lease.Acquire(); Expect<IOException>(() => lease.Collect(path));
                Assert(!Hidden(path) && !lease.Contains(path), "属性修改前故障也应结束恢复责任");
            });
            Test("未知活动目录不覆盖", root =>
            {
                string state = Path.Combine(Path.GetDirectoryName(root)!, "item-lease-state"); Directory.CreateDirectory(state);
                string unknown = Path.Combine(state, "not-owned.txt"); File.WriteAllText(unknown, "user content");
                using var lease = ItemVisibilityLease.Diagnostic(Profile(root, "unknown"), root);
                Expect<InvalidOperationException>(() => lease.Acquire()); Assert(File.ReadAllText(unknown) == "user content", "未知目录内容应保留");
            });
            Test("删除或跨卷边界保留日志", root =>
            {
                string path = NewFile(root, "deleted.txt"); var lease = ItemVisibilityLease.Diagnostic(Profile(root, "deleted"), root);
                lease.Acquire(); lease.Collect(path); File.Delete(path);
                Expect<AggregateException>(() => lease.Dispose()); Assert(File.Exists(lease.JournalPathForDiagnostics), "不能把无法定位对象静默视为已恢复");
            });
            Test("文件夹和快捷方式仅修改自身Hidden", root =>
            {
                string folder = Path.Combine(root, "owned-folder"); Directory.CreateDirectory(folder);
                string child = Path.Combine(folder, "inside.txt"); File.WriteAllText(child, "child remains untouched");
                string shortcut = NewFile(root, "owned-shortcut.lnk");
                using var lease = ItemVisibilityLease.Diagnostic(Profile(root, "kinds"), root); lease.Acquire();
                lease.Collect(folder); lease.Collect(shortcut);
                Assert(Hidden(folder) && Hidden(shortcut) && !Hidden(child), "不递归修改目录内容");
                lease.Release(folder); lease.Release(shortcut); Assert(!Hidden(folder) && !Hidden(shortcut), "目录和快捷方式恢复");
            });
            Test("无法取得属性句柄时不改文件", root =>
            {
                string path = NewFile(root, "locked.txt");
                using var lease = ItemVisibilityLease.Diagnostic(Profile(root, "locked"), root); lease.Acquire();
                var file = new FileInfo(path);
                var original = FileSystemAclExtensions.GetAccessControl(file);
                var denied = new FileSecurity(); denied.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
                denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.WriteAttributes, AccessControlType.Deny));
                try
                {
                    FileSystemAclExtensions.SetAccessControl(file, denied);
                    Expect<System.ComponentModel.Win32Exception>(() => lease.Collect(path));
                    Assert(!Hidden(path) && !lease.Contains(path), "属性写权限拒绝不得产生收纳状态");
                }
                finally
                {
                    var restore = new FileSecurity(); restore.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
                    FileSystemAclExtensions.SetAccessControl(file, restore);
                }
                var restored = FileSystemAclExtensions.GetAccessControl(file);
                bool sameRules = AclRules(restored).SequenceEqual(AclRules(original));
                bool sameProtection = restored.AreAccessRulesProtected == original.AreAccessRulesProtected;
                bool sameOwner = restored.GetOwner(typeof(SecurityIdentifier))!.Value == original.GetOwner(typeof(SecurityIdentifier))!.Value &&
                    restored.GetGroup(typeof(SecurityIdentifier))!.Value == original.GetGroup(typeof(SecurityIdentifier))!.Value;
                // SetSecurityInfo可能补写AutoInherited元数据；逐条ACE、保护状态和所有者必须精确一致，不能只按SDDL字符串比较。
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(root)!, "acl-restore-evidence.json"), JsonSerializer.Serialize(new
                { SameRules = sameRules, SameProtection = sameProtection, SameOwner = sameOwner,
                    Before = original.GetSecurityDescriptorSddlForm(AccessControlSections.Access), After = restored.GetSecurityDescriptorSddlForm(AccessControlSections.Access) }));
                Assert(sameRules && sameProtection && sameOwner, "fixture原ACL规则、保护及所有者必须恢复");
            });
            Test("未收纳引用检查不接管文件", root =>
            {
                string path = NewFile(root, "uncollected.txt");
                using var lease = ItemVisibilityLease.Diagnostic(Profile(root, "uncollected"), root); lease.Acquire();
                lease.ValidateReference(path); Assert(!lease.Contains("::virtual") && !lease.Contains("relative.txt"), "虚拟和相对旧引用不应阻止移除"); Assert(!Hidden(path) && !lease.Contains(path), "只读检查不得收纳未管理项");
            });
            Test("无默认日志仍恢复跨配置登记", root =>
            {
                string path = NewFile(root, "registered-other-profile.txt"); var positions = new ItemFixturePositions();
                var lease = ItemVisibilityLease.Diagnostic(Profile(root, "other-profile"), root, positions);
                lease.Acquire(); lease.Collect(path); positions.FailRestore = true;
                Expect<AggregateException>(() => lease.Dispose());
                using (var item = ItemFileObject.Open(path)) item.SetHidden(true);
                string emptyDefault = Profile(root, "default-without-journal"); ItemVisibilityLease.Recover(emptyDefault);
                Assert(Hidden(path) && File.Exists(lease.JournalPathForDiagnostics), "默认配置无日志不应假装已恢复全局责任");
                ItemVisibilityLease.RecoverRegisteredDiagnostic(root);
                Assert(!Hidden(path) && !File.Exists(lease.JournalPathForDiagnostics), "隔离全局指针必须定位并恢复另一profile");
                ItemVisibilityLease.RecoverRegisteredDiagnostic(root);
            });
            Test("日志写读共享数量和UTF8字节上限", root =>
            {
                string path = NewFile(root, "bounded.txt");
                using var lease = ItemVisibilityLease.Diagnostic(Profile(root, "limits"), root); lease.Acquire(); lease.Collect(path);
                string journal = lease.JournalPathForDiagnostics;
                var valid = ItemLeaseStorage.Read<ItemLeaseJournal>(journal);
                byte[] original = File.ReadAllBytes(journal);
                var entry = valid.Items[0];
                var maxItems = valid with { Items = Enumerable.Repeat(entry, ItemLeaseLimits.MaxItemCount).ToList() };
                ItemLeaseStorage.Prepare(maxItems);
                var tooMany = valid with { Items = Enumerable.Repeat(entry, ItemLeaseLimits.MaxItemCount + 1).ToList() };
                Expect<InvalidDataException>(() => ItemLeaseStorage.Prepare(tooMany));
                Expect<InvalidDataException>(() => ItemLeaseStorage.Write(journal, tooMany));
                var tooWide = valid with { Items = [entry with { Path = new string('a', ItemLeaseLimits.MaxUtf8Bytes) }] };
                Expect<InvalidDataException>(() => ItemLeaseStorage.Write(journal, tooWide));
                Assert(ItemLeaseStorage.Prepare(new string('a', ItemLeaseLimits.MaxUtf8Bytes - 2)).Length == ItemLeaseLimits.MaxUtf8Bytes, "UTF8边界应按实际编码字节计算");
                Expect<InvalidDataException>(() => ItemLeaseStorage.Prepare(new string('测', ItemLeaseLimits.MaxUtf8Bytes / 2)));
                Assert(File.ReadAllBytes(journal).SequenceEqual(original) && Hidden(path) && lease.Contains(path), "限额拒绝不得损伤已有日志或责任");
                Assert(Directory.GetFiles(Path.GetDirectoryName(journal)!, Path.GetFileName(journal) + ".new-*").Length == 0, "限额拒绝不得留下半写日志");
                lease.Release(path); Assert(!Hidden(path), "拒绝后既有责任仍能恢复");
            });
            Test("视图重排仍解析并返回同一PIDL", root =>
            {
                int reads = 0, freed = 0, resolved = 0;
                nint result = DesktopItemPositionsView.FindMatchingPidl(1, _ => ++reads == 1 ? (nint)101 : (nint)202,
                    pidl => { resolved++; return pidl == 101 ? "fixture-target" : "unrelated-item"; }, _ => freed++, "fixture-target");
                Assert(result == 101 && reads == 1 && resolved == 1 && freed == 0, "不能在核对路径后按变化下标重新取另一PIDL");
                int index = 0;
                result = DesktopItemPositionsView.FindMatchingPidl(2, _ => ++index == 1 ? (nint)303 : (nint)404,
                    pidl => pidl == 404 ? "fixture-target" : "unrelated-item", _ => freed++, "fixture-target");
                Assert(result == 404 && freed == 1, "非目标PIDL释放、目标PIDL所有权移交调用者");
            });
            Test("启动不受收纳设置限制", root =>
            {
                string path = NewFile(root, "blocked-collect.txt");
                var positions = new ItemFixturePositions { CollectionAllowed = false };
                using var lease = ItemVisibilityLease.Diagnostic(Profile(root, "collect-settings"), root, positions);
                lease.Acquire(); Assert(lease.Alive, "显示隐藏项或自动排列不兼容时仍应取得展示租约");
                Expect<InvalidOperationException>(() => lease.Collect(path));
                Assert(!Hidden(path) && !lease.Contains(path) && ItemLeaseStorage.Read<ItemLeaseJournal>(lease.JournalPathForDiagnostics).Items.Count == 0,
                    "仅收纳应拒绝且不得修改属性或责任");
                positions.CollectionAllowed = true; lease.Collect(path); Assert(Hidden(path), "条件恢复后收纳仍严格工作"); lease.Release(path);
            });
            Test("全局关闭但实际视图显示隐藏时拒绝收纳", root =>
            {
                var invalidViews = new[] { new DesktopItemAttributeRead(0, 139), new DesktopItemAttributeRead(unchecked((int)0x80004005), null),
                    new DesktopItemAttributeRead(0, null), new DesktopItemAttributeRead(1, 0) };
                int index = 0;
                foreach (var viewOptions in invalidViews)
                {
                    string path = NewFile(root, "view-settings-" + ++index + ".txt");
                    var positions = new ItemFixturePositions { ViewOptions = viewOptions, ProcessShowHidden = false };
                    using var lease = ItemVisibilityLease.Diagnostic(Profile(root, "view-settings-" + index), root, positions);
                    lease.Acquire(); Assert(lease.Alive, "实际视图设置不兼容仍允许启动空分组");
                    Expect<InvalidOperationException>(() => lease.Collect(path));
                    Assert(!Hidden(path) && !lease.Contains(path) && ItemLeaseStorage.Read<ItemLeaseJournal>(lease.JournalPathForDiagnostics).Items.Count == 0,
                        "不兼容/不可读视图必须在任何文件属性和责任写入前拒绝");
                    positions.ViewOptions = new(0, 138); lease.Collect(path); Assert(Hidden(path), "视图明确关闭后可收纳"); lease.Release(path);
                    positions.ProcessShowHidden = true;
                    lease.Collect(path); Assert(Hidden(path), "实际视图明确关闭时不受过期全局缓存阻塞"); lease.Release(path);
                    positions.ViewOptions = viewOptions;
                    Expect<InvalidOperationException>(() => lease.Collect(path));
                    Assert(!Hidden(path) && !lease.Contains(path), "全局值无论为何，实际视图不兼容或不可读都必须拒绝");
                }
            });
            Test("实际视图关闭时不被全局缓存误拒", root =>
            {
                string path = NewFile(root, "actual-off-cache-on.txt");
                var positions = new ItemFixturePositions { ProcessShowHidden = true, ViewOptions = new(0, 170) };
                using var lease = ItemVisibilityLease.Diagnostic(Profile(root, "actual-off-cache-on"), root, positions);
                lease.Acquire(); Assert(lease.Alive, "实际视图关闭时应正常取得租约");
                lease.Collect(path); Assert(Hidden(path) && lease.Contains(path), "实际视图可靠关闭时不得被过期全局缓存阻塞");
                lease.Release(path); Assert(!Hidden(path) && !lease.Contains(path), "原属性与责任应正常恢复");
                positions.ViewOptions = new(0, 171);
                Expect<InvalidOperationException>(() => lease.Collect(path));
                Assert(!Hidden(path) && !lease.Contains(path), "实际视图开启时必须拒绝，不能因更换权威来源放宽门禁");
            });
            Test("视图缩减只重启完整枚举且不误报消失", root =>
            {
                int pass = 0, freed = 0, reads = 0;
                var bounds = new COMException("fixture view shrank", unchecked((int)0x8000000B));
                nint found = DesktopItemPositionsView.FindStableMatchingPidl(() => ++pass == 1 ? 2 : 1,
                    index => { reads++; if (pass == 1 && index == 1) throw bounds; return pass == 1 ? (nint)101 : (nint)202; },
                    pidl => pidl == 202 ? "target" : "other", _ => freed++, "target");
                Assert(found == 202 && reads == 3 && freed == 1, "E_BOUNDS后应从新计数和index0完整重启，释放旧PIDL并找到真目标");
                int attempts = 0;
                Expect<IOException>(() => DesktopItemPositionsView.FindStableMatchingPidl(() => { attempts++; return 1; },
                    _ => throw bounds, _ => null, _ => { }, "target"));
                Assert(attempts == 3, "连续变化必须有界失败，不能返回不存在或无限重试");
                var other = new COMException("access denied", unchecked((int)0x80070005));
                try { DesktopItemPositionsView.FindStableMatchingPidl(() => 1, _ => throw other, _ => null, _ => { }, "target"); throw new IOException("其他错误被吞掉"); }
                catch (COMException error) { Assert(ReferenceEquals(error, other), "其他HRESULT原样透传"); }
                attempts = 0;
                try { DesktopItemPositionsView.FindStableMatchingPidl(() => { attempts++; return 1; }, _ => (nint)303, _ => throw bounds, _ => { }, "target"); throw new IOException("解析错误被吞掉"); }
                catch (COMException error) { Assert(ReferenceEquals(error, bounds) && attempts == 1, "只有索引读取E_BOUNDS可重启，路径解析错误不得吞掉"); }
                int counts = 0; reads = 0;
                found = DesktopItemPositionsView.FindStableMatchingPidl(() => ++counts == 1 ? 1 : 2, _ => { reads++; return (nint)404; }, _ => "other", _ => { }, "target");
                Assert(found == 0 && counts == 4 && reads == 3, "无匹配时结束计数发生变化也应完整重启，不能用不完整轮次判定消失");
            });
            Test("父进程强退读取最新日志", Crash);
            if (results.Count == 0) throw new ArgumentException("没有匹配的诊断用例。");
            var failures = results.Count(result => !(bool)result.GetType().GetProperty("Passed")!.GetValue(result)!);
            Write(output, new { Passed = failures == 0, FailureCount = failures, Work = work, FixtureOnly = true, RealDesktopModified = false, Cases = results });
            return failures == 0 ? 0 : 1;

            void Test(string name, Action<string> action)
            {
                string? selected = Argument(args, "--case");
                if (selected is not null && selected != name) return;
                string caseRoot = Path.Combine(work, "case-" + (results.Count + 1));
                string fixture = Path.Combine(caseRoot, "fixtures"); Directory.CreateDirectory(fixture);
                File.WriteAllText(Path.Combine(fixture, ".item-lease-fixture-owner"), "DesktopGlass-item-fixture-v1");
                try { action(fixture); results.Add(new { Name = name, Passed = true, Error = (string?)null, Fixture = fixture }); }
                catch (Exception error) { results.Add(new { Name = name, Passed = false, Error = error.ToString(), Fixture = fixture }); }
            }
        }
        catch (Exception error)
        {
            if (output is not null) Write(output, new { Passed = false, Error = error.ToString(), FixtureOnly = true, Cases = results });
            else Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static void Crash(string fixture)
    {
        string first = NewFile(fixture, "crash-a.txt"), second = NewFile(fixture, "crash-b.txt");
        string directory = Profile(fixture, "crash"), ready = Path.Combine(Path.GetDirectoryName(fixture)!, "child-ready");
        string journal = Path.Combine(directory, ItemVisibilityLease.JournalName);
        using var child = Self.Start("--item-lease-crash-child", "--data", directory, "--fixture", fixture, "--ready", ready);
        try
        {
            Wait(() => File.Exists(ready) || child.HasExited, TimeSpan.FromSeconds(25));
            if (!File.Exists(ready)) throw new IOException("强退子进程未完成两项收纳。");
            var latest = ItemLeaseStorage.Read<ItemLeaseJournal>(journal);
            Assert(latest.Items.Count == 2, "guard握手后新增的两个项目必须在最新日志中");
            string renamed = Path.Combine(fixture, "crash-a-renamed.txt"); File.Move(first, renamed);
            File.WriteAllText(first, "replacement must remain hidden"); File.SetAttributes(first, File.GetAttributes(first) | FileAttributes.Hidden);
            child.Kill(); child.WaitForExit();
            Wait(() => !File.Exists(journal), TimeSpan.FromSeconds(18));
            Assert(!File.Exists(journal), "guard必须恢复最新责任并清理已完成日志");
            Assert(!Hidden(renamed) && !Hidden(second) && Hidden(first), "guard必须按ID恢复改名原件、不碰同名替身");
        }
        finally
        {
            if (!child.HasExited) { child.Kill(); child.WaitForExit(); }
            if (File.Exists(journal))
            {
                try { ItemVisibilityLease.Recover(journal); }
                catch (Exception recovery) { File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture)!, "crash-cleanup-error.txt"), recovery.ToString()); }
            }
        }
    }
    private static int Child(string[] args)
    {
        string fixture = Argument(args, "--fixture") ?? throw new ArgumentException("缺少fixture");
        ItemPathPolicy.Diagnostic(fixture);
        string data = CheckedUnderFixtureParent(Argument(args, "--data") ?? throw new ArgumentException("缺少data"), fixture);
        string ready = CheckedUnderFixtureParent(Argument(args, "--ready") ?? throw new ArgumentException("缺少ready"), fixture);
        using var lease = ItemVisibilityLease.Diagnostic(data, fixture); lease.Acquire();
        lease.Collect(Path.Combine(fixture, "crash-a.txt")); lease.Collect(Path.Combine(fixture, "crash-b.txt"));
        File.WriteAllText(ready, "ready"); Thread.Sleep(Timeout.Infinite); return 0;
    }
    private static string CheckedUnderFixtureParent(string path, string fixture)
    {
        string full = Path.GetFullPath(path), root = Path.GetDirectoryName(Path.GetFullPath(fixture))! + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("诊断子进程输出越出自有fixture父目录。");
        return full;
    }
    private static string CheckedOutput(string path)
    {
        string full = Path.GetFullPath(path);
        DirectoryInfo? current = new(Path.GetDirectoryName(full)!);
        while (current is not null && !current.Name.Equals(".codex", StringComparison.OrdinalIgnoreCase)) current = current.Parent;
        if (current?.Parent is null || !File.Exists(Path.Combine(current.Parent.FullName, "DesktopGlass.slnx"))) throw new InvalidOperationException("诊断输出必须位于源项目 .codex 内。");
        string? existing = Path.GetDirectoryName(full);
        while (existing is not null && !Directory.Exists(existing)) existing = Path.GetDirectoryName(existing);
        if (existing is not null) ItemPathPolicy.EnsureNoReparse(existing);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!); return full;
    }
    private static string[] AclRules(FileSecurity security) => security.GetAccessRules(true, true, typeof(SecurityIdentifier))
        .Cast<FileSystemAccessRule>().Select(rule => string.Join("|", rule.IdentityReference.Value, (int)rule.FileSystemRights,
            rule.AccessControlType, rule.IsInherited, rule.InheritanceFlags, rule.PropagationFlags)).Order().ToArray();    private static string NewFile(string fixture, string name) { string path = Path.Combine(fixture, name); File.WriteAllText(path, "owned item lease fixture"); return path; }
    private static string Profile(string fixture, string name) => Path.Combine(Path.GetDirectoryName(fixture)!, "profiles", name);
    private static bool Hidden(string path) => (File.GetAttributes(path) & FileAttributes.Hidden) != 0;
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("预期异常未发生：" + typeof(T).Name); }
    private static void Wait(Func<bool> predicate, TimeSpan limit)
    { var clock = Stopwatch.StartNew(); while (!predicate() && clock.Elapsed < limit) Thread.Sleep(80); }
    private static string? Argument(string[] args, string name) { int index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
    private static void Write(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}
