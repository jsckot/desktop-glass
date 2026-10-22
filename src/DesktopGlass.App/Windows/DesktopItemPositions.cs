using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace DesktopGlass.App.Windows;

/// <summary>原生桌面图标的物理屏幕像素位置，不包含选择状态。</summary>
/// <param name="X">虚拟屏幕物理像素横坐标，可为负数。</param>
/// <param name="Y">虚拟屏幕物理像素纵坐标，可为负数。</param>
internal sealed record DesktopItemPosition(int X, int Y);

/// <summary>将恢复事务与真实 Shell 坐标访问分离；诊断实现仅作用于自有 fixture。</summary>
internal interface IItemDesktopPositions
{
    bool Alive { get; }
    void EnsureAvailable();
    void EnsureCanCollect();
    DesktopItemPosition Capture(string path);
    void Restore(string path, DesktopItemPosition position);
}

/// <summary>通过已核对官方 ABI 的 IFolderView 读取和恢复单个图标位置，不隐藏桌面、不改变排序或选择。</summary>
internal sealed class DesktopItemPositions : IItemDesktopPositions
{
    private nint originalView;
    private int explorerPid;
    private long explorerStart;
    /// <summary>原 Shell 视图和 Explorer 进程身份仍有效时为真；首次能力检查前为假。</summary>
    public bool Alive
    {
        get
        {
            if (originalView == 0 || !DesktopItemPositionsNative.IsWindow(originalView)) return false;
            DesktopItemPositionsNative.GetWindowThreadProcessId(originalView, out uint pid);
            if (pid != explorerPid) return false;
            try { using var process = Process.GetProcessById(explorerPid); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == explorerStart; }
            catch (ArgumentException) { return false; }
        }
    }
    /// <summary>建立原 Explorer 身份及可见桌面能力，仅用于启动展示，不限制用户的收纳相关设置。</summary>
    /// <exception cref="InvalidOperationException">Explorer 已重启或桌面视图不可用。</exception>
    public void EnsureAvailable()
    {
        if (originalView != 0 && !Alive) throw new InvalidOperationException("Explorer 已重建，需要先恢复已收纳项目再重新启动。");
        using var desktop = new DesktopItemPositionsView();
        if (!DesktopItemPositionsNative.IsWindowVisible(desktop.Hwnd)) throw new InvalidOperationException("原生桌面图标视图当前不可见，无法建立桌面能力。");
        if (originalView == 0)
        {
            originalView = desktop.Hwnd;
            DesktopItemPositionsNative.GetWindowThreadProcessId(originalView, out uint pid);
            explorerPid = checked((int)pid);
            using var process = Process.GetProcessById(explorerPid);
            explorerStart = process.StartTime.ToUniversalTime().Ticks;
        }
    }
    /// <summary>仅在收纳或捕获收纳坐标前要求显示隐藏项及自动排列关闭，不修改用户设置。</summary>
    /// <exception cref="InvalidOperationException">收纳设置不兼容或原 Explorer 失效；启动展示本身不调用此检查。</exception>
    public void EnsureCanCollect()
    {
        EnsureAvailable();
        DesktopItemPositionsNative.SHGetSettings(out uint settings, 1);
        var processShowHidden = (settings & 1) != 0;
        using var desktop = new DesktopItemPositionsView();
        ValidateCollectionVisibility(processShowHidden, desktop.ReadViewOptions());
        desktop.RequireManualArrange();
    }
    /// <summary>仅按实际视图选项核对收纳条件；共享纯判断供隔离诊断覆盖全局缓存滞后边界。</summary>
    /// <param name="processShowHidden">调用进程 SHGetSettings 的诊断上下文；该缓存可能滞后，不参与当前视图判定。</param>
    /// <param name="viewOptions">当前桌面背景 automation 的 ViewOptions 查询结果。</param>
    /// <exception cref="InvalidOperationException">显示隐藏项开启或无法可靠确认当前桌面选项。</exception>
    internal static void ValidateCollectionVisibility(bool processShowHidden, DesktopItemAttributeRead viewOptions)
    {
        // SHGetSettings 与现有 Explorer 视图可双向不同步；其缓存值只作诊断，不能覆盖当前视图的权威状态。
        if (viewOptions.HResult != 0 || viewOptions.Value is null)
            throw new InvalidOperationException("无法可靠确认原生桌面的隐藏项目显示状态，未执行收纳。请在资源管理器“查看→显示”中关闭“隐藏的项目”，确认桌面已应用后重试；软件不会修改此设置。");
        if ((viewOptions.Value.Value & 1) != 0)
            throw new InvalidOperationException("原生桌面仍在显示隐藏项目，未执行收纳。请在资源管理器“查看→显示”中关闭“隐藏的项目”，确认桌面已应用后重试；软件不会修改此设置。");
    }
    /// <summary>读取指定可见桌面项的位置，仅在收纳时扫描当前视图一次。</summary>
    /// <param name="path">已通过桌面路径及稳定身份校验的普通文件系统路径。</param>
    /// <returns>物理屏幕像素位置。</returns>
    /// <exception cref="IOException">目标尚未出现在桌面视图或 Shell 无法读取坐标。</exception>
    public DesktopItemPosition Capture(string path)
    {
        EnsureCanCollect();
        using var desktop = new DesktopItemPositionsView();
        using var dpi = new DesktopItemPositionsDpi();
        nint pidl = desktop.Find(path);
        if (pidl == 0) throw new IOException("该项目当前不在可见桌面视图中，未执行收纳。");
        try
        {
            var point = desktop.Position(pidl);
            if (!DesktopItemPositionsNative.ClientToScreen(desktop.Hwnd, ref point)) throw new Win32Exception(Marshal.GetLastWin32Error(), "转换桌面物理坐标失败。");
            return new(point.X, point.Y);
        }
        finally { Marshal.FreeCoTaskMem(pidl); }
    }
    /// <summary>在恢复 Hidden 位后等待目标重新出现在当前 Shell，并仅恢复该项的位置。</summary>
    /// <param name="path">稳定身份对象的当前直接桌面子项路径。</param>
    /// <param name="position">收纳前保存的物理屏幕坐标。</param>
    /// <exception cref="IOException">目标两秒内仍未出现、坐标不一致或 Shell 失败，调用方应保留恢复日志。</exception>
    /// <exception cref="InvalidOperationException">用户已启用自动排列，无法保证原位恢复。</exception>
    public void Restore(string path, DesktopItemPosition position)
    {
        using var dpi = new DesktopItemPositionsDpi();
        for (int attempt = 0; attempt < 25; attempt++)
        {
            using var desktop = new DesktopItemPositionsView();
            desktop.RequireManualArrange();
            nint pidl = desktop.Find(path);
            if (pidl == 0) { Thread.Sleep(100); continue; }
            try
            {
                var point = new DesktopItemPositionsNative.Point { X = position.X, Y = position.Y };
                if (!DesktopItemPositionsNative.ScreenToClient(desktop.Hwnd, ref point)) throw new Win32Exception(Marshal.GetLastWin32Error(), "转换恢复坐标失败。");
                desktop.SetPosition(pidl, point);
                var actual = desktop.Position(pidl);
                if (actual.X != point.X || actual.Y != point.Y) throw new IOException("Shell 未保持请求的图标位置；恢复记录已保留。");
                return;
            }
            finally { Marshal.FreeCoTaskMem(pidl); }
        }
        string terminal;
        try
        {
            // 仅在原恢复条件已经失败后取证，不刷新视图、不验证或使缓存失效，不把观测当成功。
            using var observed = new DesktopItemPositionsView();
            terminal = System.Text.Json.JsonSerializer.Serialize(new { Hwnd = observed.Hwnd.ToInt64(), Count = observed.Count,
                FileAttributes = (uint)File.GetAttributes(path), Visibility = observed.ReadVisibilityState(path) });
        }
        catch (Exception observationError) { terminal = "只读终态查询失败：" + observationError; }
        throw new IOException("恢复属性后图标尚未重新出现在桌面，恢复记录已保留以供重试。只读终态：" + terminal);
    }
}

internal sealed class DesktopItemPositionsDpi : IDisposable
{
    private readonly nint previous;
    internal DesktopItemPositionsDpi()
    {
        previous = DesktopItemPositionsNative.SetThreadDpiAwarenessContext((nint)(-4));
        if (previous == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法取得物理像素DPI上下文。");
    }
    /// <summary>恢复本线程先前的 DPI 上下文，不改变系统设置。</summary>
    public void Dispose() { if (previous != 0) DesktopItemPositionsNative.SetThreadDpiAwarenessContext(previous); }
}

internal sealed class DesktopItemPositionsView : IDisposable
{
    private readonly nint windows, dispatch, provider, browser, view, folder;
    private readonly bool initialized;
    internal nint Hwnd { get; }
    internal int Count
    {
        get
        {
            Check(Call<DesktopItemPositionsNative.ItemCount>(folder, 7)(folder, 2, out int count));
            if (count < 0 || count > 100000) throw new IOException("Shell 返回无效的桌面项数。");
            return count;
        }
    }
    internal DesktopItemPositionsView()
    {
        var initializedHr = DesktopItemPositionsNative.CoInitializeEx(0, 2);
        if (initializedHr < 0) Marshal.ThrowExceptionForHR(initializedHr);
        initialized = true;
        try
        {
            Guid clsid = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), iid = new("85CB6900-4D95-11CF-960C-0080C7F4EE85");
            Check(DesktopItemPositionsNative.CoCreateInstance(ref clsid, 0, 4, ref iid, out windows));
            var location = new DesktopItemPositionsNative.Variant { Type = 3 };
            var root = new DesktopItemPositionsNative.Variant();
            Check(Call<DesktopItemPositionsNative.FindWindow>(windows, 15)(windows, ref location, ref root, 8, out _, 1, out dispatch));
            iid = new("6D5140C1-7436-11CE-8034-00AA006009FA"); Check(Marshal.QueryInterface(dispatch, in iid, out provider));
            Guid service = new("4C96BE40-915C-11CF-99D3-00AA004AE837"); iid = new("000214E2-0000-0000-C000-000000000046");
            Check(Call<DesktopItemPositionsNative.QueryService>(provider, 3)(provider, ref service, ref iid, out browser));
            Check(Call<DesktopItemPositionsNative.OutPointer>(browser, 15)(browser, out view));
            Check(Call<DesktopItemPositionsNative.OutPointer>(view, 3)(view, out nint hwnd)); Hwnd = hwnd;
            iid = new("1AF3A467-214F-4298-908E-06B03E0B39F9"); Check(Marshal.QueryInterface(view, in iid, out folder));
        }
        catch { Dispose(); throw; }
    }
    /// <summary>读取实际桌面背景 automation 的选项及同一视图 PIDL 的 Hidden 属性，仅供现场取证；不刷新、验证缓存或改变视图。</summary>
    /// <param name="path">要核对的已知文件路径；不会读取或输出其他项目的名称。</param>
    /// <returns>各 COM 查询的原始 HRESULT 与值；失败查询的值为空，未出现在视图中的项目没有 PIDL 属性结果。</returns>
    /// <exception cref="IOException">当前视图枚举返回非法计数或 PIDL。</exception>
    internal DesktopItemVisibilityState ReadVisibilityState(string path)
    {
        var options = ReadViewOptions();
        int flagsHr = Call<DesktopItemPositionsNative.OutFlags>(folder, 25)(folder, out uint flags);
        nint pidl = Find(path);
        DesktopItemAttributeRead? viewAttributes = null;
        try
        {
            if (pidl != 0)
            {
                Guid parentId = new("000214E6-0000-0000-C000-000000000046");
                int parentHr = Call<DesktopItemPositionsNative.GetFolder>(folder, 5)(folder, ref parentId, out nint parent);
                if (parentHr < 0) viewAttributes = new(parentHr, null);
                else
                {
                    try
                    {
                        Guid itemId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
                        int itemHr = DesktopItemPositionsNative.SHCreateItemWithParent(0, parent, pidl, ref itemId, out nint item);
                        viewAttributes = ReadHiddenAttributes(itemHr, item);
                    }
                    finally { if (parent != 0) Marshal.Release(parent); }
                }
            }
        }
        finally { if (pidl != 0) Marshal.FreeCoTaskMem(pidl); }
        Guid freshId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
        int freshHr = DesktopItemPositionsNative.SHCreateItemFromParsingName(path, 0, ref freshId, out nint fresh);
        var freshAttributes = ReadHiddenAttributes(freshHr, fresh);
        DesktopItemPositionsNative.SHGetSettings(out uint settings, 1);
        return new(options, new(flagsHr, flagsHr >= 0 ? flags : null), pidl != 0,
            viewAttributes, freshAttributes, (settings & 1) != 0);
    }
    internal DesktopItemAttributeRead ReadViewOptions()
    {
        Guid dispatchId = new("00020400-0000-0000-C000-000000000046");
        nint background = 0, dual = 0;
        try
        {
            int hr = Call<DesktopItemPositionsNative.GetItemObject>(view, 15)(view, 0, ref dispatchId, out background);
            if (hr < 0) return new(hr, null);
            Guid dualId = new("29EC8E6C-46D3-411F-BAAA-611A6C9CAC66");
            hr = Marshal.QueryInterface(background, in dualId, out dual);
            if (hr < 0) return new(hr, null);
            // ShlDisp.h: IUnknown + IDispatch 后第九个方法 get_ViewOptions，返回32位 long。
            hr = Call<DesktopItemPositionsNative.OutFlags>(dual, 15)(dual, out uint flags);
            return new(hr, hr >= 0 ? flags : null);
        }
        finally { if (dual != 0) Marshal.Release(dual); if (background != 0) Marshal.Release(background); }
    }
    private static DesktopItemAttributeRead ReadHiddenAttributes(int createHr, nint item)
    {
        try
        {
            if (createHr < 0) return new(createHr, null);
            // 只请求 SFGAO_HIDDEN；不带 SFGAO_VALIDATE，避免诊断本身使缓存失效。
            int hr = Call<DesktopItemPositionsNative.GetAttributes>(item, 6)(item, 0x00080000, out uint attributes);
            return new(hr, hr >= 0 ? attributes : null); // S_FALSE 也是有效结果，表示当前没有全部请求位。
        }
        finally { if (item != 0) Marshal.Release(item); }
    }
    internal void RequireManualArrange()
    {
        int hr = Call<DesktopItemPositionsNative.NoArguments>(folder, 14)(folder);
        if (hr == 0) throw new InvalidOperationException("请先关闭桌面自动排列图标；软件不会更改此设置。");
        if (hr != 1) { Check(hr); throw new IOException("无法确认桌面自动排列状态。"); }
    }
    internal nint Find(string path)
    {

        Guid folderId = new("000214E6-0000-0000-C000-000000000046");
        Check(Call<DesktopItemPositionsNative.GetFolder>(folder, 5)(folder, ref folderId, out nint parent));
        try
        {
            return FindStableMatchingPidl(() => Count, index =>
            {
                Check(Call<DesktopItemPositionsNative.ItemPidl>(folder, 6)(folder, index, out nint pidl));
                return pidl;
            }, pidl => PathFromPidl(parent, pidl), Marshal.FreeCoTaskMem, path);
        }
        finally { Marshal.Release(parent); }
    }
    internal static nint FindStableMatchingPidl(Func<int> readCount, Func<int, nint> readPidl, Func<nint, string?> pathFromPidl, Action<nint> freePidl, string expected)
    {
        Exception? change = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int count = readCount(); bool indexChanged = false;
            try
            {
                nint result = FindMatchingPidl(count, index =>
                {
                    try { return readPidl(index); }
                    catch (COMException error) when (error.HResult == unchecked((int)0x8000000B))
                    { indexChanged = true; throw; }
                }, pathFromPidl, freePidl, expected);
                if (result != 0) return result;
                if (readCount() == count) return 0;
                change = new IOException("枚举结束时桌面项目计数已改变。");
            }
            catch (COMException error) when (indexChanged && error.HResult == unchecked((int)0x8000000B))
            { change = error; } // 只重启索引读取的E_BOUNDS；已分配PIDL由内层finally释放，其他异常不截获。
        }
        throw new IOException("桌面项目列表持续变化，无法完成可靠枚举；未将残缺结果判定为项目不存在，请重试。", change);
    }
    internal static nint FindMatchingPidl(int count, Func<int, nint> readPidl, Func<nint, string?> pathFromPidl, Action<nint> freePidl, string expected)
    {
        for (int index = 0; index < count; index++)
        {
            // PIDL在取得时即固定对象；后续解析和定位使用同一PIDL，不再按可能变化的下标二次取项。
            nint pidl = readPidl(index);
            if (pidl == 0) throw new IOException("Shell返回了空的项目标识。");
            bool retained = false;
            try
            {
                if (!string.Equals(pathFromPidl(pidl), expected, StringComparison.OrdinalIgnoreCase)) continue;
                retained = true; return pidl;
            }
            finally { if (!retained) freePidl(pidl); }
        }
        return 0;
    }
    private static string? PathFromPidl(nint parent, nint pidl)
    {
        Guid itemId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
        Check(DesktopItemPositionsNative.SHCreateItemWithParent(0, parent, pidl, ref itemId, out nint item));
        nint name = 0;
        try
        {
            int hr = Call<DesktopItemPositionsNative.GetName>(item, 5)(item, 0x80058000, out name);
            return hr >= 0 ? Marshal.PtrToStringUni(name) : null; // 虚拟桌面项没有文件系统路径。
        }
        finally { if (name != 0) Marshal.FreeCoTaskMem(name); Marshal.Release(item); }
    }
    internal DesktopItemPositionsNative.Point Position(nint pidl)
    {
        Check(Call<DesktopItemPositionsNative.GetPosition>(folder, 11)(folder, pidl, out var point)); return point;
    }
    internal void SetPosition(nint pidl, DesktopItemPositionsNative.Point point)
    {
        nint items = Marshal.AllocHGlobal(IntPtr.Size), points = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteIntPtr(items, pidl); Marshal.StructureToPtr(point, points, false);
            Check(Call<DesktopItemPositionsNative.SetPositions>(folder, 16)(folder, 1, items, points, 0x40000080));
        }
        finally { Marshal.FreeHGlobal(points); Marshal.FreeHGlobal(items); }
    }
    private static T Call<T>(nint instance, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    /// <summary>释放本次 Shell 查询的 COM 引用并平衡 COM 初始化。</summary>
    public void Dispose()
    {
        foreach (nint item in new[] { folder, view, browser, provider, dispatch, windows }) if (item != 0) Marshal.Release(item);
        if (initialized) DesktopItemPositionsNative.CoUninitialize();
    }
}

/// <summary>单次只读 COM 属性查询；Value 仅在 HRESULT 非负时有效，不将失败误作属性关闭。</summary>
internal sealed record DesktopItemAttributeRead(int HResult, uint? Value);
/// <summary>实际视图选项、视图 PIDL 和重新解析项目的属性快照；ProcessShowAllObjects 仅代表调用进程的全局设置读取。</summary>
internal sealed record DesktopItemVisibilityState(DesktopItemAttributeRead ViewOptions, DesktopItemAttributeRead FolderFlags,
    bool FoundInView, DesktopItemAttributeRead? ViewPidlHiddenAttributes, DesktopItemAttributeRead FreshParsedHiddenAttributes,
    bool ProcessShowAllObjects);
internal static class DesktopItemPositionsNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Explicit, Size=24)] internal struct Variant { [FieldOffset(0)] internal ushort Type; [FieldOffset(8)] internal int Value; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int FindWindow(nint self,ref Variant location,ref Variant root,int kind,out int hwnd,int flags,out nint dispatch);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int QueryService(nint self,ref Guid service,ref Guid iid,out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int OutPointer(nint self,out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int OutFlags(nint self,out uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetItemObject(nint self,uint kind,ref Guid iid,out nint item);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetAttributes(nint self,uint mask,out uint attributes);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int NoArguments(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int ItemCount(nint self,uint flags,out int count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetFolder(nint self,ref Guid iid,out nint folder);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetName(nint self,uint kind,out nint name);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int ItemPidl(nint self,int index,out nint pidl);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetPosition(nint self,nint pidl,out Point point);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetPositions(nint self,uint count,nint pidls,nint points,uint flags);
    [DllImport("ole32.dll")] internal static extern int CoInitializeEx(nint reserved,uint flags);
    [DllImport("ole32.dll")] internal static extern void CoUninitialize();
    [DllImport("ole32.dll")] internal static extern int CoCreateInstance(ref Guid clsid,nint outer,uint context,ref Guid iid,out nint result);
    [DllImport("shell32.dll")] internal static extern int SHCreateItemWithParent(nint parentPidl,nint parentFolder,nint childPidl,ref Guid iid,out nint item);
    [DllImport("shell32.dll", CharSet=CharSet.Unicode)] internal static extern int SHCreateItemFromParsingName(string path,nint bindContext,ref Guid iid,out nint item);
    [DllImport("shell32.dll")] internal static extern void SHGetSettings(out uint flags,uint mask);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd,out uint pid);
    [DllImport("user32.dll",SetLastError=true)] internal static extern bool ClientToScreen(nint hwnd,ref Point point);
    [DllImport("user32.dll",SetLastError=true)] internal static extern bool ScreenToClient(nint hwnd,ref Point point);
    [DllImport("user32.dll",SetLastError=true)] internal static extern nint SetThreadDpiAwarenessContext(nint context);
}
