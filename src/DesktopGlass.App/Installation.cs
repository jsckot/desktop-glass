using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DesktopGlass.App;

internal static class Installation
{
    internal const string ProductId="DesktopGlass-52ed77bc-5636-48d9-98f9-548648e153bc";
    internal static string Root=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","DesktopGlass");
    internal static string Data=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DesktopGlass");
    internal const string StopEvent="Local\\DesktopGlass-stop";
    private const string RegistryPath="Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\DesktopGlass";
    internal static bool IsInstalled=>string.Equals(Path.GetDirectoryName(Environment.ProcessPath),Root,StringComparison.OrdinalIgnoreCase);
    internal static void StopRunning()
    {
        try {using var signal=EventWaitHandle.OpenExisting(StopEvent);signal.Set();}
        catch(WaitHandleCannotBeOpenedException) { /* 程序没有运行，无需退出。 */ }
        using var main=new Mutex(false,"Local\\DesktopGlass-main");
        var acquired=false;
        try{try{acquired=main.WaitOne(TimeSpan.FromSeconds(15));}catch(AbandonedMutexException){acquired=true;}
            if(!acquired)throw new InvalidOperationException("桌面整理尚未退出，请从托盘退出后重试。");}
        finally{if(acquired)main.ReleaseMutex();}
    }
    internal static void Install(string source,string root,bool register)
    {
        using var maintenance=EnterMaintenance();
        var sourceDirectory=Path.GetDirectoryName(source)!;var packageFiles=PackageFiles(sourceDirectory);
        foreach(var relative in packageFiles)if(!File.Exists(SafePath(sourceDirectory,relative)))throw new FileNotFoundException("安装包不完整，请重新完整解压。",relative);
        var marker=Path.Combine(root,".desktop-glass-owner");
        if(Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() && (!File.Exists(marker) || File.ReadAllText(marker)!=ProductId))throw new InvalidOperationException("安装目录已有其他内容，未执行覆盖。");
        if(Directory.Exists(root))
        {
            if((File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("安装目录是链接。");
            if(File.Exists(Path.Combine(root,".maintenance")) && File.ReadAllText(Path.Combine(root,".maintenance"))=="uninstall" && CleanupRunning(root))throw new InvalidOperationException("卸载尚未结束，请稍后重试。");
        }
        StopRunning();Directory.CreateDirectory(root);
        File.WriteAllText(marker,ProductId);File.WriteAllText(Path.Combine(root,".maintenance"),"install");
        var target=Path.Combine(root,"DesktopGlass.exe");
        if(!string.Equals(source,target,StringComparison.OrdinalIgnoreCase))
        {
            foreach(var relative in packageFiles.Append("bundle-manifest.json"))
            {
                var origin=SafePath(sourceDirectory,relative);var destination=SafePath(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var watch=Stopwatch.StartNew();
                while(true)
                {
                    try{File.Copy(origin,destination,true);break;}
                    catch(IOException) when(watch.Elapsed<TimeSpan.FromSeconds(12)){Thread.Sleep(200);}
                }
            }
        }
        if(register)
        {
            using var key=Registry.CurrentUser.CreateSubKey(RegistryPath);
            key.SetValue("DisplayName","Desktop Glass 桌面分组");key.SetValue("DisplayVersion","0.1.0");key.SetValue("Publisher","Desktop Glass");
            key.SetValue("InstallLocation",root);key.SetValue("DisplayIcon",target);key.SetValue("UninstallString","\""+target+"\" --uninstall");key.SetValue("NoModify",1);key.SetValue("NoRepair",1);
            Shortcut(target,"Desktop Glass.lnk","");Shortcut(target,"卸载 Desktop Glass.lnk","--uninstall");
        }
        File.Delete(Path.Combine(root,".maintenance"));
        foreach(var name in new[]{".cleanup-process.json",".cleanup-error.txt"}){var file=Path.Combine(root,name);if(File.Exists(file))File.Delete(file);}
    }
    internal static void Uninstall()
    {
        using var maintenance=EnterMaintenance();
        PrepareUninstall(Root,Data);
        File.WriteAllText(Path.Combine(Root,".maintenance"),"uninstall");
        StopRunning();
        Windows.DesktopLease.Recover(Path.Combine(Data,"recovery.json"));
        Registry.CurrentUser.DeleteSubKeyTree(RegistryPath,false);
        var menu=MenuDirectory();foreach(var name in new[]{"Desktop Glass.lnk","卸载 Desktop Glass.lnk"}){var file=Path.Combine(menu,name);if(File.Exists(file))File.Delete(file);}
        if(Directory.Exists(menu) && !Directory.EnumerateFileSystemEntries(menu).Any())Directory.Delete(menu);
        // 不递归清理：只删除本产品明确拥有的配置与文件，保留用户放入的其他内容。
        if(Directory.Exists(Data))
        {
            EnsureOwned(Data);
            foreach(var file in Directory.EnumerateFiles(Data))
            {
                var name=Path.GetFileName(file);
                if(name is "workspace.json" or "workspace.json.bak" or "workspace.json.new" or "recovery.json" or ".desktop-glass-owner" || name.StartsWith("workspace.json.corrupt-",StringComparison.Ordinal))File.Delete(file);
            }
            if(!Directory.EnumerateFileSystemEntries(Data).Any())Directory.Delete(Data);
        }
        StartCleanup(Root,Environment.ProcessId);
    }
    internal static Process StartCleanup(string root,int waitPid)
    {
        OwnedFiles(root);
        // 清单从自有文件读取，避免长安装路径与大量运行库使命令行超过 Windows 长度上限。
        var script=$$$"""
            $ErrorActionPreference='Stop'
            Wait-Process -Id {{{waitPid}}} -ErrorAction SilentlyContinue
            $root={{{Quote(root)}}}
            $gate=New-Object Threading.Mutex($false,'Local\DesktopGlass-maintenance')
            $acquired=$false
            $owned=$false
            $manifestContent=$null
            try {
            try {$acquired=$gate.WaitOne(30000)} catch [Threading.AbandonedMutexException] {$acquired=$true}
            if(-not $acquired){throw 'Maintenance lock timeout'}
            Start-Sleep -Milliseconds 600
            if((Get-Content -LiteralPath (Join-Path $root '.desktop-glass-owner') -Raw) -ne {{{Quote(ProductId)}}}){throw 'Product ownership mismatch'}
            $manifestContent=Get-Content -LiteralPath (Join-Path $root 'bundle-manifest.json') -Raw -Encoding UTF8
            $owned=$true
            $prefix=[IO.Path]::GetFullPath($root).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
            $relative=Get-Content -LiteralPath (Join-Path $root 'bundle-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
            $paths=@(foreach($name in $relative){
                if([IO.Path]::IsPathRooted($name)){throw 'Invalid manifest path'}
                $full=[IO.Path]::GetFullPath((Join-Path $root $name))
                if(-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Path escapes product directory'}
                $full
            })
            $directories=@($paths | ForEach-Object {[IO.Path]::GetDirectoryName($_)} | Select-Object -Unique | Sort-Object Length -Descending)
            foreach($file in $paths){if(Test-Path -LiteralPath $file){
                for($attempt=0;$attempt -lt 15;$attempt++){
                    try {Remove-Item -LiteralPath $file; break} catch {if($attempt -eq 14){throw};Start-Sleep -Milliseconds 200}
                }
            }}
            foreach($name in @('bundle-manifest.json','.cleanup-process.json','.cleanup-error.txt','.desktop-glass-owner','.maintenance')){ $file=Join-Path $root $name; if(Test-Path -LiteralPath $file){Remove-Item -LiteralPath $file} }
            foreach($directory in $directories){if((Test-Path -LiteralPath $directory) -and -not (Get-ChildItem -LiteralPath $directory -Force)){Remove-Item -LiteralPath $directory}}
            if((Test-Path -LiteralPath $root) -and -not (Get-ChildItem -LiteralPath $root -Force)){Remove-Item -LiteralPath $root}
            } catch {
                $failure=$_.Exception.ToString()
                if((Test-Path -LiteralPath $root) -and $owned -and -not ((Get-Item -LiteralPath $root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){
                    [IO.File]::WriteAllText((Join-Path $root '.desktop-glass-owner'),{{{Quote(ProductId)}}})
                    [IO.File]::WriteAllText((Join-Path $root 'bundle-manifest.json'),$manifestContent)
                    [IO.File]::WriteAllText((Join-Path $root '.maintenance'),'uninstall')
                    $failure | Set-Content -LiteralPath (Join-Path $root '.cleanup-error.txt') -Encoding UTF8
                }
                exit 1
            }
            finally {if($acquired){$gate.ReleaseMutex()};$gate.Dispose()}
            """;
        var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,CreateNoWindow=true};
        start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-NonInteractive");start.ArgumentList.Add("-EncodedCommand");start.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
        var process=Process.Start(start)??throw new InvalidOperationException("无法启动卸载清理进程。");
        File.WriteAllText(Path.Combine(root,".cleanup-process.json"),JsonSerializer.Serialize(new CleanupProcess(process.Id,process.StartTime.ToUniversalTime().Ticks)));
        return process;
    }
    private sealed record CleanupProcess(int Pid,long Started);
    private static bool CleanupRunning(string root)
    {
        var record=Path.Combine(root,".cleanup-process.json");if(!File.Exists(record))return false;
        try
        {
            var data=JsonSerializer.Deserialize<CleanupProcess>(File.ReadAllText(record));if(data is null)return false;
            using var process=Process.GetProcessById(data.Pid);return !process.HasExited && process.ProcessName.Equals("powershell",StringComparison.OrdinalIgnoreCase) && process.StartTime.ToUniversalTime().Ticks==data.Started;
        }
        catch(ArgumentException){return false;}
    }
    internal static string[] PrepareUninstall(string root,string data)
    {
        var files=OwnedFiles(root);
        if(Directory.Exists(data))EnsureOwned(data);
        return files;
    }
    internal static IDisposable EnterMaintenance()=>new MaintenanceGate();
    internal static void CheckStartupAllowed()
    {
        if(File.Exists(Path.Combine(Root,".maintenance")))throw new InvalidOperationException("正在安装或卸载 Desktop Glass，请完成后再启动。安装中断时可重新运行安装包修复。");
    }
    private sealed class MaintenanceGate : IDisposable
    {
        private readonly Mutex mutex=new(false,"Local\\DesktopGlass-maintenance");
        internal MaintenanceGate()
        {
            bool acquired;try{acquired=mutex.WaitOne(0);}catch(AbandonedMutexException){acquired=true;}
            if(!acquired){mutex.Dispose();throw new InvalidOperationException("正在安装、卸载或启动 Desktop Glass，请稍后重试。");}
        }
        public void Dispose(){mutex.ReleaseMutex();mutex.Dispose();}
    }
    internal static void EnsureOwned(string root)
    {
        var marker=Path.Combine(root,".desktop-glass-owner");
        if(!File.Exists(marker)||File.ReadAllText(marker)!=ProductId)throw new InvalidOperationException("安装目录归属校验失败，已停止清理。");
        if((File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("安装目录是链接，已停止清理。");
    }
    internal static string[] OwnedFiles(string root)
    {
        EnsureOwned(root);return PackageFiles(root).Append("bundle-manifest.json").Append(".desktop-glass-owner").Select(p=>SafePath(root,p)).ToArray();
    }
    private static string[] PackageFiles(string root)
    {
        var manifest=Path.Combine(root,"bundle-manifest.json");
        if(!File.Exists(manifest))throw new InvalidOperationException("请先完整解压体验包，再运行 DesktopGlass.exe。");
        var files=JsonSerializer.Deserialize<string[]>(File.ReadAllText(manifest)) ?? throw new InvalidDataException("安装清单无效。");
        if(files.Length>2000 || !files.Contains("DesktopGlass.exe",StringComparer.OrdinalIgnoreCase))throw new InvalidDataException("安装清单缺少主程序。");
        foreach(var file in files)SafePath(root,file);return files;
    }
    private static string SafePath(string root,string relative)
    {
        if(string.IsNullOrWhiteSpace(relative)||Path.IsPathRooted(relative))throw new InvalidDataException("安装清单路径无效。");
        var full=Path.GetFullPath(Path.Combine(root,relative));var prefix=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("安装清单越界。");
        for(var directory=Path.GetDirectoryName(full);directory is not null && directory.StartsWith(prefix,StringComparison.OrdinalIgnoreCase);directory=Path.GetDirectoryName(directory))
            if(Directory.Exists(directory)&&(File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("安装目录包含链接。");
        return full;
    }
    private static string Quote(string value)=>"'"+value.Replace("'","''")+"'";
    private static string MenuDirectory()=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),"Programs","Desktop Glass");
    private static void Shortcut(string target,string name,string args)
    {
        Directory.CreateDirectory(MenuDirectory());object? shell=null,shortcut=null;
        try
        {
            shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);shortcut=((dynamic)shell!).CreateShortcut(Path.Combine(MenuDirectory(),name));
            ((dynamic)shortcut).TargetPath=target;((dynamic)shortcut).Arguments=args;((dynamic)shortcut).WorkingDirectory=Root;((dynamic)shortcut).Save();
        }
        finally{if(shortcut is not null)Marshal.FinalReleaseComObject(shortcut);if(shell is not null)Marshal.FinalReleaseComObject(shell);}
    }
}
