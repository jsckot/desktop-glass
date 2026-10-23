using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopGlass.App.UI;
using DesktopGlass.App.Windows;
namespace DesktopGlass.App;

internal sealed partial class NativeDesktopController
{
    /// <summary>仅创建隔离空组与自有 HWND 验证悬停；不启动租约或改动真实桌面文件。</summary>
    internal static int RunHoverDiagnostics(string[] args)
    {
        var output = WidgetDiagnostics.CheckedOutput(args[Array.IndexOf(args, "--output") + 1]);
        var passed = new List<string>(); Application? app = null; NativeDesktopController? c = null;
        void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        void Test(string name, Action action) { action(); passed.Add(name); }
        try
        {
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var profile = Path.Combine(Path.GetDirectoryName(output)!, "hover-" + Guid.NewGuid().ToString("N"));
            c = new NativeDesktopController(app, profile); c.displays = Native.Displays(); var d = c.displays.First(x => x.Primary);
            var g = c.workspace.AddGroup("悬停测试", d.Id); g.X=60; g.Y=80; g.Width=420; g.Height=320; g.Radius=48;
            c.store.Save(c.workspace); c.BuildWidgets();
            void Pump() => app.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Pump(); var item=c.widgets.Single(); var host=item.Host!; var handle=host.Handle;
            var saved=File.ReadAllText(Path.Combine(profile,"workspace.json"));
            Test("默认真实HWND为144DIP紧凑方块",()=> {
                Require(Math.Abs(host.GetBounds().Width-144*d.Scale)<=1 && Math.Abs(host.GetBounds().Height-144*d.Scale)<=1,"默认窗口仍为完整尺寸，未释放桌面输入范围");
            });
            var visual=(GroupWidget)HwndSource.FromHwnd(handle)!.RootVisual;
            var expand=typeof(WidgetHost).GetMethod("SetExpanded",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
            Test("原位展开及收回不落盘",()=> {
                Require(expand is not null,"缺少原位展开入口"); var compact=host.GetBounds();
                expand!.Invoke(host,[true]); Pump();
                Require(host.Handle==handle && host.GetBounds().Width>compact.Width,"展开未保持HWND或完整尺寸");
                expand.Invoke(host,[false]); Pump(); Require(host.GetBounds()==compact,"未回到紧凑锚点");
                Require(File.ReadAllText(Path.Combine(profile,"workspace.json"))==saved && !c.lease.Alive,"悬停改写配置或获取租约");
            });
            Test("编辑器仅一条浓度滑块并保留历史Radius",()=> {
                c.EditGroup(g.Id); Pump(); var editor=c.editors[g.Id];
                Require(Descendants(editor).OfType<Slider>().Count()==1,"编辑器仍有圆角滑块"); editor.Close();
                var next=AppearanceTransaction(c.workspace,g.Id,new Group{Name="名称",Tint=.6,Radius=0},_=>{});
                Require(next.Groups[0].Radius==48,"编辑外观覆盖历史圆角字段");
            });
            Test("负坐标混合DPI屏边展开夹紧且收回锚点",()=> {
                foreach(var scale in new[]{1d,1.25,1.5,2})
                {
                    var area=new Rect(-2400,80,1800,1000); var anchor=new Point(area.Right-144*scale,area.Bottom-144*scale);
                    var size=new Size(420*scale,320*scale);
                    var compact=WidgetGeometry.Bounds(anchor,size,area,scale,false);
                    var full=WidgetGeometry.Bounds(anchor,size,area,scale,true);
                    Require(full.Right<=area.Right && full.Bottom<=area.Bottom,"展开越过工作区");
                    Require(compact.Location==anchor && compact.Width==144*scale,"紧凑锚点或DPI尺寸错误");
                }
            });
            Test("屏边展开移动只提交用户增量与取消保留锚点",()=> {
                var anchor=new Point(d.Left+d.Width-144*d.Scale,d.Top+d.Height-144*d.Scale);
                host.UpdatePresentation(new Size(420*d.Scale,320*d.Scale),anchor); host.SetExpanded(true);
                var full=host.GetBounds(); c.PreviewBounds(item,-20,-10,false); c.CommitBounds(item,true);
                Require(host.Anchor==anchor && host.GetBounds()==full,"取消手势改变紧凑锚点");
                c.PreviewBounds(item,-20,-10,false); c.CommitBounds(item,false);
                Require((host.Anchor-(anchor-new Vector(20*d.Scale,10*d.Scale))).Length<=.75,"提交混入展开夹紧偏移："+host.Anchor+" expected "+(anchor-new Vector(20*d.Scale,10*d.Scale)));
                Require(c.workspace.Groups[0].Width==420 && c.workspace.Groups[0].Height==320,"移动保存了紧凑尺寸");
            });
            if (!args.Contains("--keyboard-only")) Test("鼠标进入延迟展开离开延迟收起及菜单关闭后继续收起",()=> {
                // 仅移动指针、不注入点击；最后归还原指针位置。前景仅用于保证自有测试窗口可命中。
                GetCursorPos(out var pointer);
                try
                {
                    c.foreground=true; c.BuildWidgets(); Pump(); host=c.widgets.Single().Host!;
                    visual=(GroupWidget)HwndSource.FromHwnd(host.Handle)!.RootVisual;
                    var bounds=host.GetBounds();
                    SetCursorPos((int)(bounds.Left+40*d.Scale),(int)(bounds.Top+40*d.Scale)); Wait(600);
                    Require(host.Expanded && visual.Expanded,"鼠标进入未展开");
                    var menu=visual.ContextMenu!; menu.PlacementTarget=visual; menu.IsOpen=true; Pump();
                    SetCursorPos((int)d.Left+2,(int)d.Top+2); Wait(600);
                    Require(host.Expanded,"菜单弹出时提前收起");
                    menu.IsOpen=false; Wait(1100);
                    Require(!host.Expanded,"菜单关闭且指针在外后未收起: pointer="+WidgetHost.PointerInside(host.Handle)+" capture="+visual.IsMouseCaptureWithin+" fields="+string.Join(",",typeof(GroupWidget).GetFields(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Where(f=>f.FieldType==typeof(bool)||f.Name=="openMenus").Select(f=>f.Name+"="+f.GetValue(visual))));
                    c.workspace.Locked=true; c.BuildWidgets(); Pump(); host=c.widgets.Single().Host!;
                    bounds=host.GetBounds(); SetCursorPos((int)bounds.Left+20,(int)bounds.Top+20); Wait(600);
                    Require(host.Expanded,"锁定布局禁用了悬停");
                    SetCursorPos((int)d.Left+2,(int)d.Top+2); Wait(600); Require(!host.Expanded,"锁定组离开后未收起");
                }
                finally { SetCursorPos(pointer.X,pointer.Y); }
            });
            Test("桌面模式真实键盘焦点与方向键保持展开",()=> {
                c.foreground=false; c.BuildWidgets(); Pump(); host=c.widgets.Single().Host!;
                visual=(GroupWidget)HwndSource.FromHwnd(host.Handle)!.RootVisual;
                visual.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left)
                    { RoutedEvent=Mouse.PreviewMouseDownEvent });
                Pump();
                Require(visual.IsKeyboardFocusWithin,"点击路径后桌面组件无法获得真实键盘焦点");
                Require(GetFocus()==host.Handle,"原生键盘焦点不属于当前组件HWND");
                var source=PresentationSource.FromVisual(visual)!;
                visual.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,source,Environment.TickCount,Key.Right)
                    { RoutedEvent=Keyboard.PreviewKeyDownEvent });
                Wait(700);
                Require(host.Expanded && visual.IsKeyboardFocusWithin,"方向键导航期间提前收起");
                Keyboard.ClearFocus(); Wait(700);
                Require(!host.Expanded,"键盘焦点移出后没有收起");
            });
            c.Dispose(); Require(!c.CleanupFailed,"隔离窗口清理失败");
            File.WriteAllText(output,JsonSerializer.Serialize(new{Passed=true,Tests=passed,LeaseAcquired=false,RealDesktopItemsCollected=0})); return 0;
        }
        catch(Exception error) { File.WriteAllText(output,JsonSerializer.Serialize(new{Passed=false,Tests=passed,Error=error.ToString(),LeaseAcquired=false})); return 1; }
        finally { c?.Dispose(); app?.Shutdown(); }
    }
    private static void Wait(int milliseconds)
    {
        var frame=new DispatcherFrame(); var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);
    }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Native.Point point);
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x,int y);
}
