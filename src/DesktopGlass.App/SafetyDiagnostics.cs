using System.Text.Json;
using DesktopGlass.App.Windows;

namespace DesktopGlass.App;

internal static class SafetyDiagnostics
{
    internal static int Run(string[] args)
    {
        var output=args[Array.IndexOf(args,"--output")+1];var root=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!,"safety-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var results=new List<object>();var failed=0;
        void Check(string name,Action test){try{test();results.Add(new{Name=name,Passed=true,Error=""});}catch(Exception e){failed++;results.Add(new{Name=name,Passed=false,Error=e.Message});}}
        void Assert(bool condition,string error){if(!condition)throw new InvalidOperationException(error);}
        Check("failed lease preserves corrupt journal",()=>{
            var dir=Path.Combine(root,"corrupt");Directory.CreateDirectory(dir);var journal=Path.Combine(dir,"recovery.json");File.WriteAllText(journal,"corrupt");
            try{using var lease=new DesktopLease(dir);lease.Acquire();}catch(JsonException){}
            Assert(File.Exists(journal),"未取得所有权的 Dispose 删除了恢复记录");
        });
        Check("failed installation is retryable",()=>{
            var source=Path.Combine(root,"source");var target=Path.Combine(root,"target");Directory.CreateDirectory(source);File.WriteAllText(Path.Combine(source,"DesktopGlass.exe"),"test fixture");File.WriteAllText(Path.Combine(source,"bundle-manifest.json"),"[\"DesktopGlass.exe\",\"missing.dll\"]");
            try{Installation.Install(Path.Combine(source,"DesktopGlass.exe"),target,false);}catch(IOException){}
            Assert(!Directory.Exists(target)||!Directory.EnumerateFileSystemEntries(target).Any(),"失败安装遗留了不可重试的无归属目录");
            File.WriteAllText(Path.Combine(source,"missing.dll"),"fixture");Installation.Install(Path.Combine(source,"DesktopGlass.exe"),target,false);Installation.EnsureOwned(target);
        });
        Check("rejected duplicate lease preserves active journal",()=>{
            var dir=Path.Combine(root,"active");using var first=new DesktopLease(dir);first.Acquire();var journal=Path.Combine(dir,"recovery.json");var original=File.ReadAllText(journal);
            var refused=false;try{using var second=new DesktopLease(dir);second.Acquire();}catch(InvalidOperationException){refused=true;}
            Assert(refused && File.Exists(journal)&&File.ReadAllText(journal)==original,"活动租约记录被其他对象删除");
        });
        Check("old guard cannot restore newer lease",()=>{
            var dir=Path.Combine(root,"handoff");var journal=Path.Combine(dir,"recovery.json");RecoveryRecord old;
            using(var first=new DesktopLease(dir)){first.Acquire();old=JsonSerializer.Deserialize<RecoveryRecord>(File.ReadAllText(journal))!;}
            using var current=new DesktopLease(dir);current.Acquire();current.HideOriginal();
            Assert(!DesktopLease.RestoreOwned(old,journal),"旧记录抢占新租约");Assert(!Native.IsWindowVisible((nint)old.ListWindow),"旧守护进程重新显示了原生图标");
        });
        Check("uninstall preflight preserves foreign data",()=>{
            var install=Path.Combine(root,"target");var foreign=Path.Combine(root,"foreign");Directory.CreateDirectory(foreign);var journal=Path.Combine(foreign,"recovery.json");File.WriteAllText(journal,"user data");
            var refused=false;try{Installation.PrepareUninstall(install,foreign);}catch(InvalidOperationException){refused=true;}
            Assert(refused&&File.ReadAllText(journal)=="user data"&&File.Exists(Path.Combine(install,"DesktopGlass.exe")),"预检前修改了外来数据");
        });
        Check("maintenance blocks concurrent startup",()=>{
            using var gate=Installation.EnterMaintenance();
            var refused=Task.Run(()=>{try{using var other=Installation.EnterMaintenance();return false;}catch(InvalidOperationException){return true;}}).GetAwaiter().GetResult();
            Assert(refused,"维护期间允许启动进入");
        });
        Check("uninstall rejects traversing manifest before touching data",()=>{
            var install=Path.Combine(root,"target");var manifest=Path.Combine(install,"bundle-manifest.json");var previous=File.ReadAllText(manifest);
            try{File.WriteAllText(manifest,"[\"DesktopGlass.exe\",\"../user-data.txt\"]");var refused=false;try{Installation.PrepareUninstall(install,Path.Combine(root,"absent"));}catch(InvalidDataException){refused=true;}Assert(refused,"越界清单未被拒绝");}
            finally{File.WriteAllText(manifest,previous);}
        });
        Check("interrupted uninstall can be repaired",()=>{
            var target=Path.Combine(root,"target");File.WriteAllText(Path.Combine(target,".maintenance"),"uninstall");
            Installation.Install(Path.Combine(root,"source","DesktopGlass.exe"),target,false);
            Assert(!File.Exists(Path.Combine(target,".maintenance")),"中断卸载无法重新安装修复");
        });
        Check("cross profile handoff retains old restore responsibility",()=>{
            var firstDirectory=Path.Combine(root,"old-profile");var journal=Path.Combine(firstDirectory,"recovery.json");RecoveryRecord old;
            using(var initial=new DesktopLease(firstDirectory)){initial.Acquire();old=JsonSerializer.Deserialize<RecoveryRecord>(File.ReadAllText(journal))!;}
            File.WriteAllText(journal,JsonSerializer.Serialize(old));Native.ShowWindow((nint)old.ListWindow,0);
            var active=new DesktopLease(Path.Combine(root,"new-profile"));Task<bool>? lateGuard=null;
            try
            {
                active.Acquire();active.HideOriginal();lateGuard=Task.Run(()=>DesktopLease.RestoreOwned(old,journal));
                Thread.Sleep(150);Assert(!lateGuard.IsCompleted,"旧守护进程提前丢弃了恢复责任");
            }
            finally{active.Dispose();}
            Assert(lateGuard!.Wait(3000)&&lateGuard.Result&&Native.IsWindowVisible((nint)old.ListWindow),"跨配置退出后没有恢复图标");
        });
        File.WriteAllText(output,JsonSerializer.Serialize(new{Passed=failed==0,Failed=failed,Results=results},new JsonSerializerOptions{WriteIndented=true}));return failed==0?0:1;
    }
}
