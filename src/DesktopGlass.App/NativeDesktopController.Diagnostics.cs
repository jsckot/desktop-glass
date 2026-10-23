using System.Text.Json;
using System.Windows.Threading;

namespace DesktopGlass.App;

internal sealed partial class NativeDesktopController
{
    /// <summary>仅用项目内自建空组验证真实 HWND 在移动、缩放和取消后的生命周期；不收纳任何桌面项。</summary>
    internal static int RunLayoutDiagnostics(string[] args)
    {
        if (args.Contains("--appearance-only")) return RunAppearanceDiagnostics(args);
        var index = Array.IndexOf(args, "--output");
        if (index < 0 || index + 1 >= args.Length) return 1;
        var output = WidgetDiagnostics.CheckedOutput(args[index + 1]);
        var checks = new List<object>();
        Application? application = null; NativeDesktopController? controller = null;
        try
        {
            var profile = Path.Combine(Path.GetDirectoryName(output)!, "layout-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, ".desktop-glass-owner"), Installation.ProductId);
            var workspace = new Workspace(); workspace.AddGroup("移动生命周期验证", Windows.Native.Displays().First(d => d.Primary).Id);
            new WorkspaceStore(profile).Save(workspace);
            application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            controller = new NativeDesktopController(application, profile); controller.Start(); controller.manager!.Hide();
            application.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            void Check(string name, Action operation)
            {
                var before = controller.widgets.Single(); var handle = before.Host!.Handle;
                operation();
                application.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var same = controller.widgets.Single().Host!.Handle == handle && before.Host.Alive;
                checks.Add(new { Name = name, SameWindowHandle = same });
                if (!same) throw new InvalidOperationException(name + "重建了组件 HWND，会造成松手闪烁。");
            }
            Check("同屏移动", () => { var item=controller.widgets.Single(); controller.PreviewBounds(item,32,17,false); controller.CommitBounds(item,false); });
            Check("同屏缩放", () => { var item=controller.widgets.Single(); controller.PreviewBounds(item,24,12,true); controller.CommitBounds(item,false); });
            var original=controller.widgets.Single().Host!.GetBounds();
            Check("取消手势", () => { var item=controller.widgets.Single(); controller.PreviewBounds(item,50,30,false); controller.CommitBounds(item,true); });
            if (controller.widgets.Single().Host!.GetBounds() != original) throw new InvalidOperationException("取消手势未恢复原矩形。");
            controller.SetForeground(true);
            var floating = controller.widgets.Single().Host!;
            var floatingStyle = (long)Windows.Native.GetWindowLongPtr(floating.Handle, -16);
            var floatingExtended = (long)Windows.Native.GetWindowLongPtr(floating.Handle, -20);
            if ((floatingStyle & 0x40000000L) != 0 || (floatingExtended & 8) == 0)
                throw new InvalidOperationException("临时浮层仍附着桌面，或未显示在普通应用前面。");
            checks.Add(new { Name = "临时前景层", DesktopChild = false, Topmost = true });
            controller.SetForeground(false);
            var restored = controller.widgets.Single().Host!;
            if (Windows.Native.GetParent(restored.Handle) != restored.Parent || restored.GetBounds() != original ||
                ((long)Windows.Native.GetWindowLongPtr(restored.Handle, -20) & 8) != 0)
                throw new InvalidOperationException("返回桌面后层级或原矩形发生变化。");
            checks.Add(new { Name = "返回桌面", OriginalBounds = true, Topmost = false });
            controller.Dispose(); if (controller.CleanupFailed) throw new InvalidOperationException("空组件测试清理失败。");
            application.Shutdown();
            File.WriteAllText(output, JsonSerializer.Serialize(new { Passed=true, Checks=checks, RealDesktopItemsCollected=0, Profile=profile }));
            return 0;
        }
        catch(Exception error)
        {
            controller?.Dispose(); application?.Shutdown();
            File.WriteAllText(output, JsonSerializer.Serialize(new { Passed=false, Checks=checks, Error=error.ToString(), RealDesktopItemsCollected=0 }));
            return 1;
        }
    }

}
