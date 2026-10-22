using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DesktopGlass.App.Windows;

/// <summary>同时记录卷、128 位文件标识和创建时间，避免路径替换及文件标识复用造成误恢复。</summary>
/// <param name="Volume">Windows 文件系统卷序列号。</param>
/// <param name="FileId">128 位文件标识，N 格式，不含文件名。</param>
/// <param name="CreationTime">文件创建时间，Windows FILETIME 的 100ns 单位。</param>
internal sealed record ItemFileIdentity(ulong Volume, string FileId, long CreationTime);

/// <summary>绑定文件对象而非路径的属性操作；打开期间允许用户重命名、移动和删除对象。</summary>
internal sealed class ItemFileObject : IDisposable
{
    private readonly SafeFileHandle handle;
    private ItemFileObject(SafeFileHandle value) => handle = value;
    internal ItemFileIdentity Identity
    {
        get
        {
            Check(ItemFileNative.GetFileInformationByHandleEx(handle, 18, out ItemFileNative.FileIdInfo info, 24), "读取文件稳定身份");
            var basic = Basic();
            return new(info.Volume, info.Id.ToString("N"), basic.CreationTime);
        }
    }
    internal FileAttributes Attributes => (FileAttributes)Basic().Attributes;
    internal bool DeletePending => Standard().DeletePending;
    internal uint Links => Standard().Links;
    internal string CurrentPath
    {
        get
        {
            var value = new StringBuilder(512);
            var size = ItemFileNative.GetFinalPathNameByHandle(handle, value, (uint)value.Capacity, 0);
            if (size == 0) throw Error("读取文件当前路径");
            if (size >= value.Capacity)
            {
                value = new StringBuilder(checked((int)size + 1));
                if (ItemFileNative.GetFinalPathNameByHandle(handle, value, (uint)value.Capacity, 0) == 0) throw Error("读取文件当前路径");
            }
            var path = value.ToString();
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
            return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        }
    }

    internal static ItemFileObject Open(string path, bool writable = true, bool allowRename = true)
    {
        var value = ItemFileNative.CreateFile(path, writable ? 0x180u : 0x80u, allowRename ? 7u : 3u, 0, 3, 0x02200000, 0);
        if (value.IsInvalid) { value.Dispose(); throw Error("打开文件属性句柄"); }
        var result = new ItemFileObject(value);
        try
        {
            if ((result.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("不支持重解析文件或目录。");
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal static ItemFileObject? OpenIdentity(ItemFileIdentity identity, string volumeHint)
    {
        ItemPathPolicy.EnsureNoReparse(volumeHint);
        using var hint = Open(volumeHint, false);
        if (hint.Identity.Volume != identity.Volume) throw new IOException("恢复卷已改变，不能把未找到文件解释为对象已删除。");
        var descriptor = new ItemFileNative.FileIdDescriptor { Size = 24, Type = 2, Id = Guid.ParseExact(identity.FileId, "N") };
        var value = ItemFileNative.OpenFileById(hint.handle, ref descriptor, 0x180, 7, 0, 0x02200000);
        if (value.IsInvalid)
        {
            var code = Marshal.GetLastWin32Error(); value.Dispose();
            if (code is 2 or 3 or 1168) return null;
            throw new Win32Exception(code, "无法按稳定身份定位恢复对象；恢复记录已保留。");
        }
        var item = new ItemFileObject(value);
        try
        {
            if (item.Identity != identity) throw new InvalidOperationException("恢复对象的稳定身份或创建时间不匹配。");
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("恢复对象已成为重解析对象。");
            return item;
        }
        catch { item.Dispose(); throw; }
    }

    internal void SetHidden(bool hidden)
    {
        // 重新读取当前属性，只合并本租约负责的 Hidden 位；不回写收纳时的属性快照或时间戳。
        var attributes = Attributes;
        var desired = hidden ? attributes | FileAttributes.Hidden : attributes & ~FileAttributes.Hidden;
        if (desired == attributes) return;
        if ((desired & ~FileAttributes.Normal) != 0) desired &= ~FileAttributes.Normal;
        if (desired == 0) desired = FileAttributes.Normal;
        var basic = new ItemFileNative.BasicInfo { Attributes = (uint)desired };
        Check(ItemFileNative.SetFileInformationByHandle(handle, 0, ref basic, (uint)Marshal.SizeOf<ItemFileNative.BasicInfo>()), "设置 Hidden 位");
        if (((Attributes & FileAttributes.Hidden) != 0) != hidden) throw new IOException("文件 Hidden 位写入后核对失败。");
    }

    private ItemFileNative.BasicInfo Basic()
    {
        Check(ItemFileNative.GetBasic(handle, 0, out ItemFileNative.BasicInfo value, (uint)Marshal.SizeOf<ItemFileNative.BasicInfo>()), "读取文件属性");
        return value;
    }
    private ItemFileNative.StandardInfo Standard()
    {
        Check(ItemFileNative.GetStandard(handle, 1, out ItemFileNative.StandardInfo value, (uint)Marshal.SizeOf<ItemFileNative.StandardInfo>()), "读取文件链接状态");
        return value;
    }
    private static Win32Exception Error(string operation) => new(Marshal.GetLastWin32Error(), operation);
    private static void Check(bool success, string operation) { if (!success) throw Error(operation); }
    /// <summary>释放元数据句柄；不会删除、移动或修改文件内容。</summary>
    public void Dispose() => handle.Dispose();
}

/// <summary>正式桌面路径策略；诊断工厂只能指向当前源项目 .codex 内的自有 fixture 根。</summary>
internal sealed class ItemPathPolicy
{
    private readonly string[] roots;
    internal string? FixtureRoot { get; }
    private ItemPathPolicy(string[] values, string? fixture) { roots = values; FixtureRoot = fixture; }
    internal static ItemPathPolicy Desktop() => new(new[] {
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
    }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), null);

    internal static ItemPathPolicy Diagnostic(string root)
    {
        root = Normalize(root);
        var directory = new DirectoryInfo(root);
        while (directory is not null && !directory.Name.Equals(".codex", StringComparison.OrdinalIgnoreCase)) directory = directory.Parent;
        if (directory?.Parent is null || !File.Exists(Path.Combine(directory.Parent.FullName, "DesktopGlass.slnx")) ||
            !root.StartsWith(directory.FullName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("诊断 fixture 必须位于源项目 .codex 的子目录中。");
        EnsureNoReparse(root);
        if (File.ReadAllText(Path.Combine(root, ".item-lease-fixture-owner")) != "DesktopGlass-item-fixture-v1")
            throw new InvalidOperationException("诊断目录缺少本次自有fixture标记。");
        return new([root], root);
    }

    internal string Validate(string path)
    {
        var value = Normalize(path);
        if (!IsDirectChild(value)) throw new InvalidOperationException("第一版仅支持当前用户或公共桌面的直接文件、文件夹及快捷方式；不支持虚拟入口或其他路径。");
        EnsureNoReparse(value);
        return value;
    }
    internal bool IsDirectChild(string path) => roots.Any(root => string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase));
    internal string RootFor(string original) => roots.FirstOrDefault(root => string.Equals(Path.GetDirectoryName(original), root, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidDataException("恢复记录不属于允许的直接子项。");
    internal static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("::", StringComparison.Ordinal) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException("必须使用普通文件系统绝对路径。");
        var value = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (value.StartsWith(@"\\", StringComparison.Ordinal) || value.Length < 3 || value[1] != ':' || value[2..].Contains(':'))
            throw new InvalidOperationException("暂不支持网络路径、设备路径或备用数据流。");
        return value;
    }
    internal static void EnsureNoReparse(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("路径包含重解析链接，已拒绝属性修改。");
            if (current == Path.GetPathRoot(current)) break;
        }
    }
}

internal static class ItemFileNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct FileIdInfo { internal ulong Volume; internal Guid Id; }
    [StructLayout(LayoutKind.Sequential)] internal struct FileIdDescriptor { internal uint Size; internal uint Type; internal Guid Id; }
    [StructLayout(LayoutKind.Sequential)] internal struct BasicInfo { internal long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; internal uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] internal struct StandardInfo
    {
        internal long AllocationSize, EndOfFile; internal uint Links;
        [MarshalAs(UnmanagedType.U1)] internal bool DeletePending;
        [MarshalAs(UnmanagedType.U1)] internal bool Directory;
    }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] internal static extern SafeFileHandle CreateFile(string path,uint access,uint share,nint security,uint disposition,uint flags,nint template);
    [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,int kind,out FileIdInfo info,uint size);
    [DllImport("kernel32.dll", EntryPoint="GetFileInformationByHandleEx",SetLastError=true)] internal static extern bool GetBasic(SafeFileHandle handle,int kind,out BasicInfo info,uint size);
    [DllImport("kernel32.dll", EntryPoint="GetFileInformationByHandleEx",SetLastError=true)] internal static extern bool GetStandard(SafeFileHandle handle,int kind,out StandardInfo info,uint size);
    [DllImport("kernel32.dll", SetLastError=true)] internal static extern bool SetFileInformationByHandle(SafeFileHandle handle,int kind,ref BasicInfo info,uint size);
    [DllImport("kernel32.dll", SetLastError=true)] internal static extern SafeFileHandle OpenFileById(SafeFileHandle hint,ref FileIdDescriptor descriptor,uint access,uint share,nint security,uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] internal static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,StringBuilder path,uint length,uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] internal static extern bool MoveFileEx(string source,string destination,uint flags);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] internal static extern void SHChangeNotify(uint action,uint flags,string path,nint extra);
}
