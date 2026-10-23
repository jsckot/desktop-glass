using System.Text.Json;
using System.Windows.Threading;
using DesktopGlass.App.UI;
using DesktopGlass.App.Windows;

namespace DesktopGlass.App;

/// <summary>使用两扇自建窗口验证系统材质可见性，不读取其他应用内容或修改桌面配置。</summary>
internal static class BackdropDiagnostics
{
    internal static int Run(string[] args)
    {
        var index=Array.IndexOf(args,"--output");if(index<0||index+1>=args.Length)return 1;
        var output=WidgetDiagnostics.CheckedOutput(args[index+1]);
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        Window? back=null,front=null;
        try
        {
            var scene=new Grid();
            for(int column=0;column<4;column++)scene.ColumnDefinitions.Add(new ColumnDefinition());
            var colors=new[]{"#E46264","#3DAFC1","#E1BC54","#548FE0"};
            for(int column=0;column<4;column++)
            {
                var band=new Border{Background=AppTheme.Brush(colors[column])};Grid.SetColumn(band,column);scene.Children.Add(band);
                var label=AppTheme.Text("实时背景\n"+(column+1),28,Brushes.White);label.Margin=new Thickness(20,35,0,0);Grid.SetColumn(label,column);scene.Children.Add(label);
            }
            back=new Window{Title="Desktop Glass 玻璃验证背景",Width=900,Height=560,Left=240,Top=180,Content=scene,Topmost=true};
            back.Show();
            var contents=new StackPanel{Margin=new Thickness(28)};
            contents.Children.Add(AppTheme.Text("实时玻璃验证",22));
            contents.Children.Add(AppTheme.Text("后面的色块应透过本窗口，而不是显示桌面壁纸。",13));
            contents.Children.Add(new Border{Height=130,Margin=new Thickness(0,20,0,0),CornerRadius=new CornerRadius(20),
                BorderBrush=Brushes.White,BorderThickness=new Thickness(1),Background=new SolidColorBrush(Color.FromArgb(20,255,255,255))});
            front=new Window{Title="Desktop Glass 实时玻璃验证",Width=460,Height=340,Left=460,Top=280,
                Content=contents,Background=Brushes.Transparent,Owner=back,Topmost=true};
            front.Show(); front.Activate(); var result=WindowBackdrop.Enable(front);
            File.WriteAllText(output+".ready.json",JsonSerializer.Serialize(result));
            var timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(180)};
            timer.Tick+=(_,_)=>{timer.Stop();front.Close();back.Close();app.Shutdown();};timer.Start();
            front.Closed+=(_,_)=>{back.Close();app.Shutdown();};
            app.Run();
            File.WriteAllText(output,JsonSerializer.Serialize(new{ApiAccepted=result.Enabled,Api=result,VisualConfirmed=false,VisualConfirmationRequired=true,OwnedWindowsOnly=true}));
            return result.Enabled?0:1;
        }
        catch(Exception error){front?.Close();back?.Close();app.Shutdown();File.WriteAllText(output,JsonSerializer.Serialize(new{Passed=false,Error=error.ToString()}));return 1;}
    }
}
