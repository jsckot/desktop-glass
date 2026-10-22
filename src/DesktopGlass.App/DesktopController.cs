using Microsoft.Win32;
using System.Windows.Threading;
using DesktopGlass.App.UI;
using DesktopGlass.App.Windows;

namespace DesktopGlass.App;

internal sealed class DesktopController : IDisposable
{
    private readonly Application app;
    private readonly WorkspaceStore store;
    private readonly DesktopLease lease;
    private readonly List<DesktopSurface> surfaces=[];
    private readonly List<FileSystemWatcher> watchers=[];
    private readonly DispatcherTimer refreshTimer;
    private readonly DispatcherTimer guardTimer;
    private System.Windows.Forms.NotifyIcon? tray;
    private bool stopped;
    private bool rebuilding;
    private bool topologyDirty;
    internal bool CanCommit(bool canceled,bool temporary)=>LayoutEditPolicy.CanCommit(canceled,rebuilding,stopped,Workspace.Locked,temporary);
    internal Workspace Workspace {get;}
    internal DesktopCatalog Catalog {get;}=new();
    internal List<DesktopEntry> Entries {get;private set;}=[];
    internal List<DisplayArea> Displays {get;private set;}=[];
    internal DesktopController(Application app,string dataDirectory)
    {
        this.app=app;
        Directory.CreateDirectory(dataDirectory);var marker=Path.Combine(dataDirectory,".desktop-glass-owner");
        if(!File.Exists(marker))
        {
            if(Directory.EnumerateFileSystemEntries(dataDirectory).Any())throw new InvalidOperationException("配置目录已有未识别内容，已保留原数据并停止启动。");
            File.WriteAllText(marker,Installation.ProductId);
        }
        Installation.EnsureOwned(dataDirectory);
        store=new WorkspaceStore(dataDirectory);Workspace=store.Load();lease=new DesktopLease(dataDirectory);
        refreshTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(450)};refreshTimer.Tick+=(_,_)=>{refreshTimer.Stop();Refresh();};
        guardTimer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(2)};guardTimer.Tick+=(_,_)=>{if(!lease.Alive){MessageBox.Show("Windows 桌面或恢复进程已重建。已退出整理，请重新启动。","Desktop Glass");Exit();}};
    }
    internal void Start()
    {
        Displays=Native.Displays();Entries=Catalog.Scan();
        if(Displays.Count==0 || Entries.Count==0)throw new InvalidOperationException("未获取到完整桌面，保持原生桌面。");
        if(Workspace.Groups.Count==0){Workspace.AddGroup("常用工具",Displays.First(d=>d.Primary).Id);store.Save(Workspace);}
        lease.Acquire();BuildSurfaces();lease.HideOriginal();
        var menu=new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("新建分组",null,(_,_)=>Dispatch(()=>CreateGroup(Displays.First(d=>d.Primary).Id)));
        menu.Items.Add("刷新桌面",null,(_,_)=>Dispatch(Refresh));
        menu.Items.Add("打开桌面文件夹",null,(_,_)=>OpenDesktop());
        menu.Items.Add("恢复桌面并退出",null,(_,_)=>Dispatch(Exit));
        tray=new System.Windows.Forms.NotifyIcon{Icon=System.Drawing.SystemIcons.Application,Text="Desktop Glass · 桌面分组",Visible=true,ContextMenuStrip=menu};
        tray.DoubleClick+=(_,_)=>Dispatch(()=>CreateGroup(Displays.First(d=>d.Primary).Id));
        SystemEvents.DisplaySettingsChanged+=DisplayChanged;
        SystemEvents.PowerModeChanged+=PowerChanged;
        foreach(var directory in new[]{Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)}.Distinct())
        {
            if(!Directory.Exists(directory))continue;
            var watcher=new FileSystemWatcher(directory){NotifyFilter=NotifyFilters.FileName|NotifyFilters.DirectoryName,EnableRaisingEvents=true};
            watcher.Created+=FilesChanged;watcher.Deleted+=FilesChanged;watcher.Renamed+=FilesChanged;watcher.Error+=(_,_)=>Dispatch(QueueRefresh);watchers.Add(watcher);
        }
        guardTimer.Start();
    }
    internal void Changed()
    {
        try {store.Save(Workspace);Dispatch(()=>{foreach(var surface in surfaces)surface.Render();});}
        catch(Exception e) {Report(e);}
    }
    internal void Refresh()
    {
        if(stopped)return;
        if(topologyDirty)
        {
            topologyDirty=false;
            try{Displays=Native.Displays();BuildSurfaces();}
            catch(Exception e){Exit();Report(e);return;}
        }
        try {Entries=Catalog.Scan();foreach(var path in Workspace.Groups.SelectMany(g=>g.Entries))Track(path);foreach(var surface in surfaces)surface.Render();}
        catch(Exception e){Report(e);}
    }
    internal void CreateGroup(string displayId)
    {
        var group=Workspace.AddGroup("新分组",displayId);Changed();new GroupEditor(group,Changed).Show();
    }
    internal void Track(string path) {if(!Entries.Any(e=>string.Equals(e.Path,path,StringComparison.OrdinalIgnoreCase)))Entries.Add(new DesktopEntry(path,Path.GetFileNameWithoutExtension(path)));}
    internal void Open(string path){try{DesktopCatalog.Open(path);}catch(Exception e){Report(e);}}
    internal void OpenDesktop()=>Open(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
    internal void Report(Exception error)=>MessageBox.Show(error.Message,"Desktop Glass",MessageBoxButton.OK,MessageBoxImage.Warning);
    internal void Exit(){Dispose();app.Shutdown();}
    private void QueueRefresh(){refreshTimer.Stop();refreshTimer.Start();}
    private void Dispatch(Action action)=>app.Dispatcher.BeginInvoke(()=>{if(!stopped)action();});
    private void FilesChanged(object sender,FileSystemEventArgs e)=>Dispatch(QueueRefresh);
    private void DisplayChanged(object? sender,EventArgs e)=>Dispatch(()=>{topologyDirty=true;QueueRefresh();});
    private void PowerChanged(object sender,PowerModeChangedEventArgs e){if(e.Mode==PowerModes.Resume)DisplayChanged(sender,EventArgs.Empty);}
    private void BuildSurfaces()
    {
        rebuilding=true;
        try
        {
            foreach(var surface in surfaces)surface.Close();surfaces.Clear();
            foreach(var display in Displays){var surface=new DesktopSurface(this,display);surface.Closed+=(_,_)=>{if(!rebuilding&&!stopped)Dispatch(Exit);};surfaces.Add(surface);surface.Show();lease.Attach(surface,display);}
        }
        finally{rebuilding=false;}
    }
    public void Dispose()
    {
        if(stopped)return;stopped=true;guardTimer.Stop();refreshTimer.Stop();
        SystemEvents.DisplaySettingsChanged-=DisplayChanged;SystemEvents.PowerModeChanged-=PowerChanged;
        foreach(var watcher in watchers)watcher.Dispose();
        lease.Dispose();
        foreach(var surface in surfaces)surface.Close();surfaces.Clear();
        if(tray is not null){tray.Visible=false;tray.Dispose();tray=null;}
    }
}
