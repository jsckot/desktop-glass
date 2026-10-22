using System.Text.Json;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopGlass.App.UI;
using DesktopGlass.App.Windows;

namespace DesktopGlass.App;

internal static class UiDiagnostics
{
    internal static int Run(string[] args)
    {
        var output=args[Array.IndexOf(args,"--output")+1];var root=Path.GetDirectoryName(Path.GetFullPath(output))!;Directory.CreateDirectory(root);
        var holdIndex=Array.IndexOf(args,"--hold-seconds");var hold=holdIndex>=0?Math.Clamp(int.Parse(args[holdIndex+1]),0,600):0;
        var visible=Native.IsWindowVisible(Native.DesktopListView());
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        using var controller=new DesktopController(app,Path.Combine(root,"ui-profile"));
        try
        {
            controller.Start();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};
            Exception? failure=null;
            timer.Tick+=(_,_)=>{
                timer.Stop();
                try
                {
                    var screen=controller.Displays.First(d=>d.Primary);
                    controller.Workspace.Groups.Clear();
                    var a=controller.Workspace.AddGroup("开发工具",screen.Id);a.X=30;a.Y=105;a.Width=380;a.Height=310;a.Tint=.55;
                    var b=controller.Workspace.AddGroup("工作文件",screen.Id);b.X=440;b.Y=105;b.Width=340;b.Height=260;b.Tint=.55;
                    var chosen=controller.Entries.Take(9).ToArray();
                    for(var i=0;i<chosen.Length;i++)controller.Workspace.MoveEntry(chosen[i].Path,i<6?a.Id:b.Id);
                    controller.Changed();
                    var surface=app.Windows.OfType<DesktopSurface>().First();surface.Render();surface.UpdateLayout();
                    var width=(int)Math.Ceiling(surface.ActualWidth);var height=(int)Math.Ceiling(surface.ActualHeight);
                    var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
                    var visual=new DrawingVisual();using(var dc=visual.RenderOpen()){
                        if(Glass.Wallpaper() is {} image)dc.DrawImage(image,new Rect(0,0,width,height));
                        dc.DrawRectangle(new VisualBrush(surface),null,new Rect(0,0,width,height));
                    }
                    bitmap.Render(visual);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(Path.Combine(root,"ui-preview.png")))encoder.Save(stream);
                    if(width<=0||height<=0||!File.Exists(Path.Combine(root,"ui-profile","workspace.json")))throw new InvalidOperationException("界面或保存未通过。");
                }
                catch(Exception e){failure=e;}
                finally
                {
                    if(failure is null && hold>0)
                    {
                        File.WriteAllText(Path.Combine(root,"ready.json"),JsonSerializer.Serialize(new{Pid=Environment.ProcessId,EntryCount=controller.Entries.Count,GroupCount=controller.Workspace.Groups.Count}));
                        var finish=new DispatcherTimer{Interval=TimeSpan.FromSeconds(hold)};finish.Tick+=(_,_)=>{finish.Stop();controller.Exit();};finish.Start();
                    }
                    else controller.Exit();
                }
            };
            timer.Start();app.Run();
            if(failure is not null)throw failure;
            var restored=Native.IsWindowVisible(Native.DesktopListView())==visible;
            File.WriteAllText(output,JsonSerializer.Serialize(new{Passed=restored,Preview="ui-preview.png",Restored=restored}));return restored?0:1;
        }
        catch(Exception e){controller.Dispose();File.WriteAllText(output,JsonSerializer.Serialize(new{Passed=false,Error=e.ToString()}));return 1;}
    }
}
