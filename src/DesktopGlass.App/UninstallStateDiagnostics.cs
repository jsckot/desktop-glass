using System.Text.Json;

namespace DesktopGlass.App;

/// <summary>在项目临时目录检查卸载元数据清理边界；不访问真实安装、注册表或桌面。</summary>
internal static class UninstallStateDiagnostics
{
    /// <summary>验证有待恢复责任时拒绝清理，且成功清理不越过软件自有文件范围。</summary>
    /// <param name="args">包含 --output 和项目 .codex 中 JSON 路径的参数。</param>
    /// <returns>全部检查通过返回 0，否则 1 并保存失败证据。</returns>
    internal static int Run(string[] args)
    {
        string? output=null;var checks=new List<string>();
        try
        {
            var index=Array.IndexOf(args,"--output");
            if(index<0 || index+1>=args.Length)throw new ArgumentException("需要 --output。");
            output=WidgetDiagnostics.CheckedOutput(args[index+1]);
            var root=Path.Combine(Path.GetDirectoryName(output)!,"cleanup-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string Owned(string name)
            {var path=Path.Combine(root,name);Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,".desktop-glass-owner"),Installation.ProductId);return path;}
            void Assert(bool condition,string error){if(!condition)throw new InvalidOperationException(error);}
            void Refused(Action action){try{action();}catch(InvalidOperationException){return;}throw new Exception("预期拒绝未发生");}
            var pending=Owned("pending");var journal=Path.Combine(pending,"item-recovery.json");File.WriteAllText(journal,"pending");
            var evidence=Path.Combine(pending,"item-recovery.json.guard-error.txt");File.WriteAllText(evidence,"preserve");
            Refused(()=>Installation.CleanupRecoveredItemState(pending));Assert(File.ReadAllText(journal)=="pending" && File.Exists(evidence),"待恢复日志被清理");checks.Add("待恢复日志阻止清理且保留证据");
            var active=Owned("active");var activeState=Path.Combine(active,"item-lease-state");Directory.CreateDirectory(activeState);File.WriteAllText(Path.Combine(activeState,".desktop-glass-owner"),Installation.ProductId);
            File.WriteAllText(Path.Combine(activeState,"item-active-lease.json"),"pending");Refused(()=>Installation.CleanupRecoveredItemState(active));Assert(File.Exists(Path.Combine(activeState,"item-active-lease.json")),"活动指针丢失");checks.Add("跨配置活动指针阻止清理");
            var foreign=Owned("foreign");var foreignState=Path.Combine(foreign,"item-lease-state");Directory.CreateDirectory(foreignState);File.WriteAllText(Path.Combine(foreignState,"user.txt"),"keep");Refused(()=>Installation.CleanupRecoveredItemState(foreign));Assert(File.ReadAllText(Path.Combine(foreignState,"user.txt"))=="keep","未知目录被修改");checks.Add("未知状态目录不被覆盖或清理");
            var clean=Owned("clean");var cleanState=Path.Combine(clean,"item-lease-state");Directory.CreateDirectory(cleanState);File.WriteAllText(Path.Combine(cleanState,".desktop-glass-owner"),Installation.ProductId);
            File.WriteAllText(Path.Combine(cleanState,"item-active-lease.json.new-"+Guid.NewGuid().ToString("N")),"stale");File.WriteAllText(Path.Combine(clean,"item-recovery.json.guard-error.txt"),"stale");File.WriteAllText(Path.Combine(clean,"item-recovery.json.new-"+Guid.NewGuid().ToString("N")),"stale");
            File.WriteAllText(Path.Combine(clean,"user.txt"),"keep");File.WriteAllText(Path.Combine(clean,"item-recovery.json.new-user"),"keep");
            Installation.CleanupRecoveredItemState(clean);Assert(!Directory.Exists(cleanState),"已恢复空状态目录仍遗留");Assert(!File.Exists(Path.Combine(clean,"item-recovery.json.guard-error.txt")),"已恢复错误证据未清理");Assert(File.ReadAllText(Path.Combine(clean,"user.txt"))=="keep" && File.ReadAllText(Path.Combine(clean,"item-recovery.json.new-user"))=="keep","越界清理未知文件");
            Installation.CleanupRecoveredItemState(clean);checks.Add("完成恢复后仅清理已知元数据且可重复执行");
            File.WriteAllText(output,JsonSerializer.Serialize(new{Passed=true,Count=checks.Count,Checks=checks,RealInstallationChanged=false,RealDesktopChanged=false},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception error){if(output is not null)File.WriteAllText(output,JsonSerializer.Serialize(new{Passed=false,Checks=checks,Error=error.ToString()}));return 1;}
    }
}