using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using DesktopGlass.App.UI;
using DesktopGlass.App.Windows;
namespace DesktopGlass.App;

internal sealed partial class NativeDesktopController
{
    /// <summary>仅构造空分组和自有 HWND 验证预览；不调用 Start、不取得全局租约、不扫描桌面入口。</summary>
    internal static int RunAppearanceDiagnostics(string[] args)
    {
        var output = WidgetDiagnostics.CheckedOutput(args[Array.IndexOf(args, "--output") + 1]);
        var passed = new List<string>(); var checks = new List<object>();
        Application? application = null; NativeDesktopController? c = null;
        void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        void Test(string name, Action body) { body(); passed.Add(name); }
        try
        {
            application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var profile = Path.Combine(Path.GetDirectoryName(output)!, "appearance-" + Guid.NewGuid().ToString("N"));
            c = new NativeDesktopController(application, profile);
            c.displays = Native.Displays(); var display = c.displays.First(d => d.Primary);
            c.workspace.AddGroup("第一组", display.Id); c.workspace.AddGroup("第二组", display.Id);
            c.store.Save(c.workspace); c.BuildWidgets();
            void Pump() => application.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Pump(); var id = c.workspace.Groups[0].Id; var second = c.workspace.Groups[1].Id;
            var saved = File.ReadAllText(Path.Combine(profile, "workspace.json"));
            var host = c.widgets[0].Host!; var hwnd = host.Handle;
            Test("实时预览不落盘且保留HWND与其他组", () => {
                c.PreviewAppearance(id, new Group { Name = "预览", Tint = .85, Radius = 48 }); Pump();
                Require(c.widgets[0].Host!.Handle == hwnd && host.Alive, "预览重建了HWND");
                Require(c.workspace.Groups[0].Name == "第一组" && c.EffectiveAppearance(c.workspace.Groups[0]).Name == "预览", "预览污染工作区或未生效");
                Require(c.EffectiveAppearance(c.workspace.Groups[1]).Name == "第二组", "预览污染其他分组");
                Require(File.ReadAllText(Path.Combine(profile, "workspace.json")) == saved, "预览写入配置");
                Require(!c.lease.Alive, "隔离诊断意外取得租约");
            });
            Test("编辑中移动及取消保留预览", () => {
                var item=c.widgets[0]; host.SetExpanded(true); var bounds=host.GetBounds(); c.PreviewBounds(item, 25, 10, false); c.CommitBounds(item, true);
                Require(host.GetBounds()==bounds && c.EffectiveAppearance(c.workspace.Groups[0]).Radius==48, "取消拖动丢失外观覆盖");
            });
            Test("重建与隐藏再显示保留独立覆盖", () => {
                c.PreviewAppearance(second, new Group { Name="第二预览", Tint=.12, Radius=0 });
                c.BuildWidgets(); Pump(); Require(c.EffectiveAppearance(c.workspace.Groups[0]).Radius==48, "重建丢失预览");
                c.SetVisible(id,false); Pump();
                c.SetVisible(id,true); Pump();
                Require(c.widgets.Count==2 && c.EffectiveAppearance(c.workspace.Groups[1]).Radius==0, "隐藏影响其他组预览");
            });
            Test("保存仅提交外观且失败保留源状态", () => {
                var source=c.workspace; source.Groups[0].X=101; source.Groups[0].Entries.Add("diagnostic-reference");
                var draft=new Group { Name="保存名称", Tint=.6, Radius=33, X=999, Entries=[] };
                var next=AppearanceTransaction(source,id,draft,_=>{});
                Require(next.Groups[0].X==101 && next.Groups[0].Entries.Count==1 && source.Groups[0].Name=="第一组", "外观保存覆盖布局或入口");
                try { AppearanceTransaction(source,id,draft,_=>throw new IOException("injected")); throw new InvalidOperationException("未传播保存失败"); }
                catch(IOException) { Require(source.Groups[0].Name=="第一组", "保存失败污染源状态"); }
                source.Groups[0].Entries.Clear();
                c.workspace=AppearanceTransaction(source,id,draft,c.store.Save);
                Require(new WorkspaceStore(profile).Load().Groups[0].Tint==.6 && new WorkspaceStore(profile).Load().Groups[0].Radius==source.Groups[0].Radius, "外观未持久化或覆盖历史圆角");
            });
            Test("关闭取消清理覆盖并恢复最新保存外观", () => {
                c.EditGroup(id); Pump(); var editor=c.editors[id];
                var sliders=Descendants(editor).OfType<Slider>().ToArray(); sliders[0].Value=.82; Pump();
                Require(c.EffectiveAppearance(c.workspace.Groups[0]).Tint==.82, "编辑器滑块未回调");
                editor.Close(); Pump(); Require(!c.appearancePreviews.ContainsKey(id) && c.EffectiveAppearance(c.workspace.Groups[0]).Tint==.6, "关闭未恢复保存外观");
            });
            Test("保存失败保留输入及一致预览且工作区不变", () => {
                var draft=CopyGroup(c.workspace.Groups[0]);
                var editor=new GroupEditor(draft,()=>throw new IOException("故障注入"),previewAppearance:value=>c.PreviewAppearance(id,value));
                editor.Show(); Pump(); var sliders=Descendants(editor).OfType<Slider>().ToArray(); sliders[0].Value=.81;
                Descendants(editor).OfType<Button>().Single(b=>Equals(b.Content,"保存更改")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                Require(sliders[0].Value==.81 && draft.Tint==.6 && c.EffectiveAppearance(c.workspace.Groups[0]).Tint==.81 && c.workspace.Groups[0].Tint==.6,"保存失败未保留输入对应的预览或污染工作区");
                Require(Descendants(editor).OfType<TextBlock>().Any(t=>t.Text.Contains("故障注入")),"保存失败未显示原因");
                editor.Close(); c.appearancePreviews.Remove(id); c.ApplyGroupAppearance(id);
            });
            Test("合成副屏工作区及DPI约束编辑器位置与限高", () => {
                var fit=typeof(GroupEditor).GetMethod("FitWorkArea",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic);
                Require(fit is not null,"编辑器缺少按实际副屏工作区限高和定位逻辑");
                var result=((double MaxHeight, Point Origin))fit!.Invoke(null,new object[]{new Rect(-1500,200,660,900),new Rect(-1600,80,1280,720),1.5})!;
                Require(result.MaxHeight==480 && result.Origin==new Point(-1500,80),"较矮负坐标副屏/DPI限高或夹紧错误");
                result=((double MaxHeight, Point Origin))fit.Invoke(null,new object[]{new Rect(1800,1000,440,600),new Rect(0,40,1920,1040),1.0})!;
                Require(result.MaxHeight==1040 && result.Origin==new Point(1480,480),"工作区边界夹紧错误");
            });
            Test("小工作区按钮可见且内容可滚动", () => {
                var editor=new GroupEditor(CopyGroup(c.workspace.Groups[0]),()=>{}) {MaxHeight=440}; editor.Show(); Pump();
                var confirm=Descendants(editor).OfType<Button>().Single(b=>Equals(b.Content,"保存更改"));
                var root=(FrameworkElement)editor.Content; var bounds=confirm.TransformToAncestor(root).TransformBounds(new Rect(0,0,confirm.ActualWidth,confirm.ActualHeight));
                Require(editor.ActualHeight<=440 && bounds.Bottom<=root.ActualHeight && bounds.Top>=0,"小工作区保存按钮超出窗口");
                Require(Descendants(root).OfType<ScrollViewer>().Any(v=>v.ScrollableHeight>0),"小工作区内容无法滚动"); editor.Close();
            });
            Test("保存按钮持久化并保留HWND，取消按钮恢复外观", () => {
                c.EditGroup(id); Pump(); var editor=c.editors[id]; var handle=c.widgets[0].Host!.Handle;
                var sliders=Descendants(editor).OfType<Slider>().ToArray(); sliders[0].Value=.7;
                Descendants(editor).OfType<Button>().Single(b=>Equals(b.Content,"保存更改")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                Require(!c.editors.ContainsKey(id) && !c.appearancePreviews.ContainsKey(id) && c.widgets[0].Host!.Handle==handle,"保存重建HWND或残留草稿");
                Require(new WorkspaceStore(profile).Load().Groups[0].Tint==.7 && c.workspace.Groups[0].X==101,"保存按钮未持久化或覆盖布局");
                c.EditGroup(id); Pump(); editor=c.editors[id]; Descendants(editor).OfType<Slider>().Last().Value=.8;
                Descendants(editor).OfType<Button>().Single(b=>Equals(b.Content,"取消")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                Require(c.EffectiveAppearance(c.workspace.Groups[0]).Tint==.7 && !c.appearancePreviews.ContainsKey(id),"取消按钮未恢复外观");
            });
            Test("材质边线与缩放标记位于圆弧内部", () => {
                c.PreviewAppearance(id,new Group{Name="材质验证",Tint=.85,Radius=48}); Pump();
                var visual=(GroupWidget)System.Windows.Interop.HwndSource.FromHwnd(c.widgets[0].Host!.Handle)!.RootVisual;
                c.widgets[0].Host!.SetExpanded(true); Pump();
                var root=(Grid)visual.Content;
                var borders=root.Children.OfType<Border>().Where(b=>b.BorderThickness.Left>0).ToArray();
                Require(borders.Length==2 && borders[0].CornerRadius.TopLeft==11 && borders[1].CornerRadius.BottomRight==9,"边线未随预览同步");
                Require(visual.Title=="材质验证","标题未原位更新");
                var frost=(SolidColorBrush)root.Children.OfType<Border>().First().Background;
                Require(frost.Color.A==(byte)Math.Round(255*(.035+.85*.32)),"玻璃浓度未原位更新");
                var grip=Descendants(root).OfType<Thumb>().Single(t=>t.Width==22);
                var rectangle=grip.TransformToAncestor(root).TransformBounds(new Rect(0,0,grip.ActualWidth,grip.ActualHeight));
                var clip=new RectangleGeometry(new Rect(0,0,root.ActualWidth,root.ActualHeight),12,12);
                Require(clip.FillContains(rectangle.BottomRight) && clip.FillContains(rectangle.TopRight) && clip.FillContains(rectangle.BottomLeft),"缩放标记穿过弧线");
            });
            Test("最大圆角原生区域与客户区几何", () => {
                var widget=c.widgets[0].Host!;
                foreach(var scale in new[]{1.0,1.25,1.5}) foreach(var radius in new[]{0.0,48.0})
                {
                    var bounds=new Rect(display.Left+40,display.Top+80,350*scale,300*scale);
                    widget.SetBounds(bounds,radius,scale); widget.ApplyAppearance(new Group{Name="几何",Tint=.5,Radius=radius},scale); Pump();
                    var actual=widget.GetBounds(); var client=WidgetHost.ReadClientScreenBounds(widget.Handle);
                    var visual=(GroupWidget)System.Windows.Interop.HwndSource.FromHwnd(widget.Handle)!.RootVisual;
                    var dpi=WidgetHost.WindowDpi(widget.Handle);
                    var region=CreateRectRgn(0,0,0,0);
                    try {
                        Require(GetWindowRgn(widget.Handle,region)!=0,"读取区域失败");
                        var mismatches=0; var w=(int)actual.Width; var h=(int)actual.Height;
                        for(var y=0;y<Math.Min(80,h);y++) for(var x=0;x<Math.Min(80,w);x++)
                        { var hit=PtInRegion(region,x,y); if(hit!=PtInRegion(region,w-1-x,y)||hit!=PtInRegion(region,x,h-1-y)) mismatches++; }
                        checks.Add(new {Scale=scale,Radius=radius,Window=actual.ToString(),Client=client.ToString(),Mismatches=mismatches, WpfWidth=visual.ActualWidth, WpfHeight=visual.ActualHeight, Dpi=dpi, RootWidth=((FrameworkElement)visual.Content).ActualWidth});
                        Require(mismatches==0,"原生圆角区域四角不对称，差异像素="+mismatches);
                        Require(client==actual,"客户区和窗口矩形不一致");
                        Require(Math.Abs(((FrameworkElement)visual.Content).ActualWidth*dpi/96-actual.Width)<1 && Math.Abs(((FrameworkElement)visual.Content).ActualHeight*dpi/96-actual.Height)<1,"WPF材质未覆盖实际客户区");
                    } finally { DeleteObject(region); }
                }
            });
            Test("删除后草稿不可复活且退出清理覆盖", () => {
                c.workspace.RemoveGroup(id); c.appearancePreviews.Remove(id);
                try { AppearanceTransaction(c.workspace,id,new Group(),_=>{}); throw new Exception("删除后仍可保存"); } catch(InvalidOperationException) {}
                c.Dispose(); Require(c.appearancePreviews.Count==0 && c.widgets.Count==0 && !c.CleanupFailed,"退出未清理");
            });
            File.WriteAllText(output,JsonSerializer.Serialize(new {Passed=true,Tests=passed,Geometry=checks,LeaseAcquired=false,RealDesktopItemsCollected=0})); return 0;
        }
        catch(Exception error) { File.WriteAllText(output,JsonSerializer.Serialize(new {Passed=false,Tests=passed,Geometry=checks,Error=error.ToString(),LeaseAcquired=false})); return 1; }
        finally { c?.Dispose(); application?.Shutdown(); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++) { var child=VisualTreeHelper.GetChild(parent,i); yield return child; foreach(var nested in Descendants(child)) yield return nested; }
    }
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left,int top,int right,int bottom);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint window,nint region);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(nint region,int x,int y);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint region);
}
