using System.Text.Json;
using DesktopGlass.App.Windows;

namespace DesktopGlass.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if(args.Length>=3 && args[0]=="--watch") return DesktopLease.Watch(args[1],args[2]);
            if(args.Contains("--ui-test")) return UiDiagnostics.Run(args);
            if(args.Contains("--diagnose") || args.Contains("--smoke") || args.Contains("--crash-test") || args.Contains("--crash-child")) return Diagnostics.Run(args);
            using var instance=new Mutex(true,"Local\\DesktopGlass-main",out var first);
            if(!first){MessageBox.Show("Desktop Glass 已在运行，请使用任务栏托盘菜单。","Desktop Glass");return 0;}
            var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
            app.DispatcherUnhandledException+=(_,e)=>{MessageBox.Show("整理遇到异常，即将恢复桌面并退出。\n"+e.Exception.Message,"Desktop Glass");e.Handled=true;app.Shutdown(1);};
            using var controller=new DesktopController(app,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DesktopGlass"));
            app.Exit+=(_,_)=>controller.Dispose();controller.Start();app.Run();return 0;
        }
        catch(Exception e) {System.Windows.MessageBox.Show(e.Message,"Desktop Glass",MessageBoxButton.OK,MessageBoxImage.Error);return 1;}
    }
}

internal static class Diagnostics
{
    internal static int Run(string[] args)
    {
        var outputIndex=Array.IndexOf(args,"--output");
        var output=outputIndex>=0 && outputIndex+1<args.Length ? args[outputIndex+1] : null;
        var evidence=output is null ? null : Path.GetDirectoryName(Path.GetFullPath(output));
        try
        {
            var displays=Native.Displays(); var catalog=new DesktopCatalog(); var entries=catalog.Scan(); var list=Native.DesktopListView();
            if(displays.Count==0 || list==0 || entries.Count==0) throw new InvalidOperationException("桌面能力检查失败。");
            if(args.Contains("--diagnose") && entries.Any(e=>e.Path.StartsWith("::",StringComparison.Ordinal) && catalog.Icon(e.Path) is null))throw new InvalidOperationException("虚拟桌面入口图标读取失败。");
            if(args.Contains("--crash-child"))
            {
                var data=args[Array.IndexOf(args,"--data")+1];
                using var childLease=new DesktopLease(data); childLease.Acquire();childLease.HideOriginal();
                File.WriteAllText(Path.Combine(data,"hidden"),"ready"); Thread.Sleep(30000); return 0;
            }
            var initiallyVisible=Native.IsWindowVisible(list);
            if(args.Contains("--smoke"))
            {
                if(evidence is null) throw new ArgumentException("需要 --output。");
                using(var lease=new DesktopLease(Path.Combine(evidence,"lease-smoke")))
                {
                    lease.Acquire();
                    var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
                    var d=displays[0]; var window=new Window{Title="Desktop Glass 自检",Width=300,Height=200,WindowStyle=WindowStyle.None,ShowInTaskbar=false,Background=Brushes.Transparent,AllowsTransparency=true};
                    window.Show(); lease.Attach(window,d);lease.HideOriginal();
                    if(Native.IsWindowVisible(list)) throw new InvalidOperationException("隐藏失败。");
                    window.Close();app.Shutdown();
                }
                if(Native.IsWindowVisible(list)!=initiallyVisible) throw new InvalidOperationException("正常恢复失败。");
            }
            if(args.Contains("--crash-test"))
            {
                if(evidence is null) throw new ArgumentException("需要 --output。");
                var data=Path.Combine(evidence,"lease-crash");Directory.CreateDirectory(data);var ready=Path.Combine(data,"hidden"); if(File.Exists(ready))File.Delete(ready);
                using var child=Self.Start("--crash-child","--data",data);
                var timeout=Stopwatch.StartNew();
                while(!File.Exists(ready) && !child.HasExited && timeout.Elapsed<TimeSpan.FromSeconds(20)) Thread.Sleep(100);
                if(!File.Exists(ready)) {if(!child.HasExited)child.Kill();throw new InvalidOperationException("故障测试未能启用桌面接管。");}
                child.Kill();child.WaitForExit();
                timeout.Restart();while(Native.IsWindowVisible(list)!=initiallyVisible && timeout.Elapsed<TimeSpan.FromSeconds(10))Thread.Sleep(100);
                if(Native.IsWindowVisible(list)!=initiallyVisible) {DesktopLease.Recover(Path.Combine(data,"recovery.json")); throw new InvalidOperationException("守护进程恢复失败。");}
            }
            Write(new{Passed=true,OS=Environment.OSVersion.Version.ToString(),Displays=displays,EntryCount=entries.Count,OriginalVisible=initiallyVisible,RestoredVisible=Native.IsWindowVisible(list)});return 0;
        }
        catch(Exception e) {Write(new{Passed=false,Error=e.ToString()});return 1;}
        void Write(object value) {if(output is not null){Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);File.WriteAllText(output,JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));}}
    }
}
