using System.Text.Json;

namespace DesktopGlass.App;

internal static class InstallDiagnostics
{
    internal static int Run(string[] args)
    {
        var output=args[Array.IndexOf(args,"--output")+1];var root=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!,"sandbox-install");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root,"user-file.txt"),"keep");
            var refused=false;
            try{Installation.Install(Environment.ProcessPath!,root,false);}catch(InvalidOperationException){refused=true;}
            if(!refused)throw new InvalidOperationException("没有拒绝非本产品目录。");
            File.Delete(Path.Combine(root,"user-file.txt"));
            Installation.Install(Environment.ProcessPath!,root,false);Installation.EnsureOwned(root);
            var destination=Path.Combine(root,"DesktopGlass.exe");
            var diagnose=Path.Combine(root,"installed-diagnose.json");
            var start=new ProcessStartInfo(destination){UseShellExecute=false,CreateNoWindow=true};start.ArgumentList.Add("--diagnose");start.ArgumentList.Add("--output");start.ArgumentList.Add(diagnose);
            using var process=Process.Start(start)!;if(!process.WaitForExit(30000)){process.Kill();throw new InvalidOperationException("安装后启动超时。");}
            if(process.ExitCode!=0)throw new InvalidOperationException("安装后诊断失败。");
            File.Delete(diagnose);
            var lockedFile=Installation.OwnedFiles(root).Last(f=>f.EndsWith(".dll",StringComparison.OrdinalIgnoreCase));
            using(var locked=new FileStream(lockedFile,FileMode.Open,FileAccess.Read,FileShare.None))
            {
                File.WriteAllText(Path.Combine(root,".maintenance"),"uninstall");
                using var interrupted=Installation.StartCleanup(root,int.MaxValue);
                if(!interrupted.WaitForExit(15000)){interrupted.Kill();throw new InvalidOperationException("清理失败测试超时。");}
                if(interrupted.ExitCode==0||!File.Exists(Path.Combine(root,".cleanup-error.txt"))||!File.Exists(Path.Combine(root,"bundle-manifest.json")))throw new InvalidOperationException("清理失败后未保留可恢复清单与诊断。");
            }
            Installation.Install(Environment.ProcessPath!,root,false);
            if(File.Exists(Path.Combine(root,".maintenance"))||!File.Exists(destination))throw new InvalidOperationException("清理中断后重装修复失败。");
            var holdReady=Path.Combine(Path.GetDirectoryName(root)!,"directory-held");if(File.Exists(holdReady))File.Delete(holdReady);
            using(var holder=Windows.Self.Start("--hold-directory",root,holdReady))
            {
                try
                {
                    var wait=Stopwatch.StartNew();while(!File.Exists(holdReady)&&wait.Elapsed<TimeSpan.FromSeconds(10))Thread.Sleep(100);
                    if(!File.Exists(holdReady))throw new InvalidOperationException("目录占用测试未就绪。");
                    using var blocked=Installation.StartCleanup(root,int.MaxValue);
                    if(!blocked.WaitForExit(15000)){blocked.Kill();throw new InvalidOperationException("目录清理测试超时。");}
                    if(blocked.ExitCode==0)throw new InvalidOperationException("目录占用测试没有触发删除失败。");
                    if(!File.Exists(Path.Combine(root,".desktop-glass-owner"))||!File.Exists(Path.Combine(root,"bundle-manifest.json")))throw new InvalidOperationException("目录删除失败后未保留恢复元数据。");
                }
                finally{if(!holder.HasExited)holder.Kill();holder.WaitForExit();}
            }
            Installation.Install(Environment.ProcessPath!,root,false);
            // 使用正式卸载的延迟清理器；外部验证脚本等待本进程退出后检查沙箱消失。
            File.Delete(diagnose);File.WriteAllText(Path.Combine(root,".maintenance"),"uninstall");Installation.StartCleanup(root,Environment.ProcessId);
            File.WriteAllText(output,JsonSerializer.Serialize(new{Passed=true,NonOwnedDirectoryRefused=refused,InstalledExecutableStarted=true,InterruptedCleanupRepaired=true,DirectoryCleanupFailureRepaired=true,CleanupScheduled=true,Sandbox=root}));return 0;
        }
        catch(Exception e){File.WriteAllText(output,JsonSerializer.Serialize(new{Passed=false,Error=e.ToString()}));return 1;}
    }
}
