using System.Text.Json;
using DesktopGlass.App.Windows;
using Microsoft.Win32;

namespace DesktopGlass.App;

/// <summary>安装器调用的无界面维护入口；任何恢复失败都返回非零并保留安装文件和恢复责任。</summary>
internal static class InstallerBridge
{
    internal static int Run(string[] args)
    {
        string? report = null;
        try
        {
            report = Value(args, "--report");
            var sandbox = Value(args, "--sandbox-data");
            var data = sandbox is null ? Installation.Data : CheckedSandbox(sandbox);
            var previous = Value(args, "--previous");
            using var gate = Installation.EnterMaintenance();
            if (sandbox is null) Installation.StopRunning();
            var mode = args[0];
            if (mode == "--setup-prepare")
            {
                var target = Path.GetFullPath(Value(args, "--target") ?? throw new ArgumentException("安装目录不能为空。"));
                ValidateTarget(target, data);
                if (previous is not null && !string.Equals(target, Path.GetFullPath(previous), StringComparison.OrdinalIgnoreCase))
                {
                    Installation.EnsureOwned(previous);
                    if (File.Exists(Path.Combine(previous, ".previous-installations.json")))
                        throw new InvalidOperationException("上一次安装位置迁移尚未完成，请先安装到当前原位置修复，再更换目录。");
                }
                if (sandbox is null) Recover(data);
                Directory.CreateDirectory(target);
                ItemPathPolicy.EnsureNoReparse(target);
                File.WriteAllText(Path.Combine(target, ".desktop-glass-owner"), Installation.ProductId);
                File.WriteAllText(Path.Combine(target, ".maintenance"), "install");
                RegisterPreviousLocation(target, previous);
                if (previous is not null && Directory.Exists(previous))
                { Installation.EnsureOwned(previous); File.WriteAllText(Path.Combine(previous, ".maintenance"), "install"); }
            }
            else if (mode == "--setup-complete")
            {
                var root = Path.GetDirectoryName(Environment.ProcessPath)!; Installation.EnsureOwned(root);
                var manifestPath = Path.Combine(root, "bundle-manifest.json");
                var manifest = JsonSerializer.Deserialize<string[]>(File.ReadAllText(manifestPath)) ?? throw new InvalidDataException("安装清单无效。");
                var uninstallFiles = Directory.EnumerateFiles(root).Select(Path.GetFileName).Where(n =>
                    n is not null && System.Text.RegularExpressions.Regex.IsMatch(n, @"^unins[0-9]{3}\.(exe|dat)$")).Cast<string>();
                File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest.Concat(uninstallFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
                CompletePreviousLocations(root, previous);
                var marker = Path.Combine(root, ".maintenance"); if (File.Exists(marker)) File.Delete(marker);
                if (sandbox is null)
                {
                    using var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\DesktopGlass");
                    if (key?.GetValue("InstallLocation") is string old && previous is not null &&
                        string.Equals(Path.GetFullPath(old), Path.GetFullPath(previous), StringComparison.OrdinalIgnoreCase))
                    { key.Close(); Registry.CurrentUser.DeleteSubKeyTree("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\DesktopGlass", false); }
                }
            }
            else if (mode == "--setup-cleanup")
            {
                var root = Path.GetDirectoryName(Environment.ProcessPath)!; Installation.EnsureOwned(root);
                CompletePreviousLocations(root, null);
                if (sandbox is null) Recover(data);
                else if (File.Exists(Path.Combine(data,"recovery.json"))) throw new InvalidOperationException("隔离配置仍有恢复责任。");
                Installation.CleanupRecoveredItemState(data);
                File.WriteAllText(Path.Combine(root, ".maintenance"), "uninstall");
                CleanupData(data);
            }
            else throw new ArgumentException("未知安装维护模式。");
            Write(report, new { Passed = true, Mode = mode }); return 0;
        }
        catch (Exception error) { Write(report, new { Passed = false, Error = error.ToString() }); return 1; }
    }

    // 新安装器已接管登记后，旧目录失败责任必须留在新位置，重装和卸载都能再次处理。
    private static void CompletePreviousLocations(string root, string? previous)
    {
        RegisterPreviousLocation(root, previous);
        var pendingPath = Path.Combine(root, ".previous-installations.json");
        var pending = File.Exists(pendingPath)
            ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(pendingPath)) ?? throw new InvalidDataException("旧安装位置记录无效。") : [];
        if (pending.Length > 20) throw new InvalidDataException("旧安装位置记录过多，请先完成现有清理。");
        pending = pending.Where(p => !string.Equals(Path.GetFullPath(p), root, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (pending.Length == 0) { if (File.Exists(pendingPath)) File.Delete(pendingPath); return; }
        File.WriteAllText(pendingPath, JsonSerializer.Serialize(pending));
        foreach (var old in pending)
        {
            if (!Directory.Exists(old))
            {
                pending = pending.Where(p => !string.Equals(p, old, StringComparison.OrdinalIgnoreCase)).ToArray();
                WritePending(pendingPath, pending); continue;
            }
            ItemPathPolicy.EnsureNoReparse(old);
            if (File.Exists(Path.Combine(old, ".previous-installations.json")))
                throw new InvalidOperationException("旧位置仍有更早的清理责任，请先修复该位置，不能丢弃迁移记录。");
            var files = Installation.OwnedFiles(old);
            var marker = Path.Combine(old, ".desktop-glass-owner");
            var manifest = Path.Combine(old, "bundle-manifest.json");
            var manifestContent = File.ReadAllText(manifest);
            try
            {
            foreach (var file in files.Where(f => !string.Equals(f, marker, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(f, manifest, StringComparison.OrdinalIgnoreCase))) File.Delete(file);
            foreach (var directory in files.Select(Path.GetDirectoryName).OfType<string>().Where(d => d != old)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(d => d.Length))
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            foreach (var name in new[] { ".maintenance", ".cleanup-process.json", ".cleanup-error.txt" }) File.Delete(Path.Combine(old, name));
            File.Delete(manifest); File.Delete(marker);
            if (!Directory.EnumerateFileSystemEntries(old).Any()) Directory.Delete(old);
            }
            catch
            {
                if (Directory.Exists(old))
                {
                    File.WriteAllText(marker, Installation.ProductId); File.WriteAllText(manifest, manifestContent);
                    File.WriteAllText(Path.Combine(old, ".maintenance"), "install");
                }
                throw;
            }
            pending = pending.Where(p => !string.Equals(p, old, StringComparison.OrdinalIgnoreCase)).ToArray();
            WritePending(pendingPath, pending);
        }
        File.Delete(pendingPath);
    }
    private static void RegisterPreviousLocation(string root, string? previous)
    {
        if (previous is null || string.Equals(Path.GetFullPath(previous), root, StringComparison.OrdinalIgnoreCase)) return;
        var path = Path.Combine(root, ".previous-installations.json");
        var pending = File.Exists(path) ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ??
            throw new InvalidDataException("旧安装位置记录无效。") : [];
        if (pending.Length >= 20) throw new InvalidDataException("旧安装位置记录过多。");
        WritePending(path, pending.Append(Path.GetFullPath(previous)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }
    private static void WritePending(string path, string[] pending)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(pending);
        using (var file = new FileStream(path + ".new", FileMode.Create, FileAccess.Write, FileShare.None))
        { file.Write(bytes); file.Flush(true); }
        File.Move(path + ".new", path, true);
    }

    /// <summary>拒绝根目录、配置目录、链接及未知归属；验证在创建任何安装文件之前完成。</summary>
    internal static void ValidateTarget(string target, string data)
    {
        target = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
        data = Path.GetFullPath(data).TrimEnd(Path.DirectorySeparatorChar);
        if (!Path.IsPathFullyQualified(target) || target.Length < 4 || target.StartsWith(@"\\", StringComparison.Ordinal) ||
            target[2..].Contains(':') || target.Equals(data, StringComparison.OrdinalIgnoreCase) ||
            data.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择独立的本地程序目录，不要使用磁盘根目录或软件配置目录。");
        CheckExistingAncestors(target);
        if (File.Exists(target)) throw new IOException("所选位置是文件，不能作为安装目录。");
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) Installation.EnsureOwned(target);
    }
    private static void Recover(string data)
    {
        DesktopLease.Recover(Path.Combine(data, "recovery.json"));
        ItemVisibilityLease.RecoverRegistered(); ItemVisibilityLease.Recover(data);
    }
    private static void CleanupData(string data)
    {
        if (!Directory.Exists(data)) return;
        Installation.EnsureOwned(data); ItemPathPolicy.EnsureNoReparse(data);
        foreach (var file in Directory.EnumerateFiles(data))
        {
            var name = Path.GetFileName(file);
            if (name is "workspace.json" or "workspace.json.bak" or "workspace.json.new" or "workspace.v1.backup.json" or "recovery.json" ||
                name.StartsWith("workspace.json.corrupt-", StringComparison.Ordinal)) File.Delete(file);
        }
        var marker = Path.Combine(data, ".desktop-glass-owner");
        if (Directory.EnumerateFileSystemEntries(data).All(p => p.Equals(marker, StringComparison.OrdinalIgnoreCase)))
        { File.Delete(marker); Directory.Delete(data); }
    }
    private static string CheckedSandbox(string path)
    {
        path = Path.GetFullPath(path);
        var directory = new DirectoryInfo(path);
        while (directory is not null && directory.Name != ".codex") directory = directory.Parent;
        if (directory?.Parent is null || !File.Exists(Path.Combine(directory.Parent.FullName, "DesktopGlass.slnx")) ||
            !path.StartsWith(directory.FullName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("安装诊断配置必须在源项目 .codex 内。");
        CheckExistingAncestors(path); return path;
    }
    // 新安装目录尚不存在；先检查最近的已有祖先，创建后再校验完整路径，不能跳过祖先链接检查。
    private static void CheckExistingAncestors(string path)
    {
        while (!Directory.Exists(path) && !File.Exists(path))
            path = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("安装目录没有有效的本地父目录。");
        ItemPathPolicy.EnsureNoReparse(path);
    }
    private static string? Value(string[] args, string name)
    { var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length && args[index + 1].Length > 0 ? args[index + 1] : null; }
    private static void Write(string? path, object result)
    { if (path is not null) File.WriteAllText(path, JsonSerializer.Serialize(result)); }
}
