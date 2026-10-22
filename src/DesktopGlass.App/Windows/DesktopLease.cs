using System.Text;
using System.Text.Json;

namespace DesktopGlass.App.Windows;

internal sealed record RecoveryRecord(int OwnerPid,long OwnerStart,long ListWindow,int ExplorerPid,long ExplorerStart,bool WasVisible);

internal sealed class DesktopLease : IDisposable
{
    private readonly string journal;
    private RecoveryRecord? record;
    private Process? guard;
    internal DesktopLease(string directory) { Directory.CreateDirectory(directory); journal=Path.Combine(directory,"recovery.json"); }
    internal nint Parent {get;private set;}
    internal bool Alive => record is not null && (guard is null || !guard.HasExited) && Native.IsWindow((nint)record.ListWindow) && Matches(record);
    internal void Acquire()
    {
        Recover(journal);
        var list=Native.DesktopListView(); if(list==0) throw new InvalidOperationException("没有找到兼容的 Windows 桌面视图。");
        Native.GetWindowThreadProcessId(list,out var explorerPid);
        using var owner=Process.GetCurrentProcess(); using var explorer=Process.GetProcessById((int)explorerPid);
        record=new(owner.Id,owner.StartTime.ToUniversalTime().Ticks,list,(int)explorerPid,explorer.StartTime.ToUniversalTime().Ticks,Native.IsWindowVisible(list));
        Parent=Native.GetParent(Native.GetParent(list));
        if(Parent==0) throw new InvalidOperationException("桌面宿主不存在。");
        File.WriteAllText(journal,JsonSerializer.Serialize(record));
        var eventName="Local\\DesktopGlass-ready-"+Guid.NewGuid().ToString("N");
        using var ready=new EventWaitHandle(false,EventResetMode.ManualReset,eventName);
        guard=Self.Start("--watch",journal,eventName);
        if(!ready.WaitOne(TimeSpan.FromSeconds(12)) || guard.HasExited) { Dispose(); throw new InvalidOperationException("桌面恢复进程未就绪，已取消启用。"); }
    }
    internal void HideOriginal()
    {
        if(record is null || !Alive || guard is null || guard.HasExited) throw new InvalidOperationException("桌面恢复保护不可用。");
        Native.ShowWindow((nint)record.ListWindow,0);
        if(Native.IsWindowVisible((nint)record.ListWindow)) throw new InvalidOperationException("原生图标视图无法隐藏。");
    }
    internal void Attach(Window window,DisplayArea display)
    {
        if(!Alive) throw new InvalidOperationException("桌面已重建，请重新启动整理。");
        var hwnd=new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        var style=(long)Native.GetWindowLongPtr(hwnd,-16);
        Native.SetWindowLongPtr(hwnd,-16,(nint)((style & ~0x80000000L)|0x40000000L));
        Native.SetParent(hwnd,Parent);
        if(Native.GetParent(hwnd)!=Parent) throw new InvalidOperationException("无法附加桌面宿主。");
        var point=new Native.Point{X=(int)display.Left,Y=(int)display.Top}; Native.ScreenToClient(Parent,ref point);
        if(!Native.SetWindowPos(hwnd,0,point.X,point.Y,(int)display.Width,(int)display.Height,0x54)) throw new InvalidOperationException("桌面宿主定位失败。");
    }
    public void Dispose()
    {
        if(record is not null) {Restore(record);record=null;}
        if(File.Exists(journal)) File.Delete(journal);
        guard?.Dispose(); guard=null;
    }
    internal static int Watch(string journal,string eventName)
    {
        var r=Read(journal);
        using var parent=Process.GetProcessById(r.OwnerPid);
        if(parent.StartTime.ToUniversalTime().Ticks!=r.OwnerStart) return 2;
        using(var ready=EventWaitHandle.OpenExisting(eventName)) ready.Set();
        parent.WaitForExit(); Restore(r);
        // 主进程可能已正常恢复并移除记录；不移除新实例拥有的记录。
        if(File.Exists(journal) && Read(journal)==r) File.Delete(journal);
        return 0;
    }
    internal static void Recover(string journal)
    {
        if(!File.Exists(journal)) return;
        var r=Read(journal);
        try {using var p=Process.GetProcessById(r.OwnerPid); if(p.StartTime.ToUniversalTime().Ticks==r.OwnerStart && !p.HasExited) throw new InvalidOperationException("已有桌面整理实例正在运行。");}
        catch(ArgumentException) { /* 原进程已退出，执行幂等恢复。 */ }
        Restore(r); File.Delete(journal);
    }
    private static RecoveryRecord Read(string path) => JsonSerializer.Deserialize<RecoveryRecord>(File.ReadAllText(path)) ?? throw new InvalidDataException("恢复记录损坏，请保留记录并检查桌面图标可见性。");
    private static bool Matches(RecoveryRecord r)
    {
        if(!Native.IsWindow((nint)r.ListWindow)) return false;
        Native.GetWindowThreadProcessId((nint)r.ListWindow,out var pid); if(pid!=r.ExplorerPid) return false;
        var name=new StringBuilder(80); Native.GetClassName((nint)r.ListWindow,name,name.Capacity); if(name.ToString()!="SysListView32") return false;
        try {using var p=Process.GetProcessById(r.ExplorerPid); return p.ProcessName.Equals("explorer",StringComparison.OrdinalIgnoreCase) && p.StartTime.ToUniversalTime().Ticks==r.ExplorerStart;}
        catch(ArgumentException) {return false;}
    }
    private static void Restore(RecoveryRecord r) {if(r.WasVisible && Matches(r)) Native.ShowWindow((nint)r.ListWindow,5);}
}

internal static class Self
{
    internal static Process Start(params string[] args)
    {
        var executable=Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径。");
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true};
        if(Path.GetFileNameWithoutExtension(executable).Equals("dotnet",StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"DesktopGlass.dll"));
        foreach(var arg in args) start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new InvalidOperationException("无法启动恢复进程。");
    }
}
