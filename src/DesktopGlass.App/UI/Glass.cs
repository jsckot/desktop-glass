using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using DesktopGlass.App.Windows;

namespace DesktopGlass.App.UI;

internal static class Glass
{
    internal static readonly Color Ink=Color.FromRgb(27,43,55);
    private static BitmapImage? wallpaper;
    private static string wallpaperPath="";
    internal static Border Panel(FrameworkElement content,Group? style=null,DisplayArea? display=null,double x=0,double y=0,double width=350,double height=300)
    {
        var radius=style?.Radius ?? 24;
        var grid=new Grid{ClipToBounds=true};
        var image=Wallpaper();
        if(image is not null && display is not null)
        {
            var screen=System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s=>s.WorkingArea.Left==display.Left && s.WorkingArea.Top==display.Top);
            var full=screen?.Bounds ?? new System.Drawing.Rectangle((int)display.Left,(int)display.Top,(int)display.Width,(int)display.Height);
            // 从静态壁纸取对应区域，图像仅在重布局时更新，不捕获其他应用窗口。
            var sx=Math.Max(0,x*display.Scale+display.Left-full.Left)/full.Width;
            var sy=Math.Max(0,y*display.Scale+display.Top-full.Top)/full.Height;
            var brush=new ImageBrush(image){Stretch=Stretch.Fill,ViewboxUnits=BrushMappingMode.RelativeToBoundingBox,
                Viewbox=new Rect(sx,sy,Math.Min(1,width*display.Scale/full.Width),Math.Min(1,height*display.Scale/full.Height))};
            grid.Children.Add(new Border{Background=brush,Effect=new BlurEffect{Radius=22,RenderingBias=RenderingBias.Performance},Margin=new Thickness(-8)});
        }
        grid.Children.Add(new Border{Background=new SolidColorBrush(Color.FromArgb((byte)(255*(style?.Tint ?? .62)),239,247,250))});
        grid.Children.Add(new Border{Background=new LinearGradientBrush(Color.FromArgb(75,255,255,255),Color.FromArgb(12,255,255,255),90)});
        grid.Children.Add(content);
        var border=new Border{CornerRadius=new CornerRadius(radius),BorderThickness=new Thickness(1),BorderBrush=new LinearGradientBrush(Color.FromArgb(220,255,255,255),Color.FromArgb(75,255,255,255),90),Child=grid,
            Effect=new DropShadowEffect{Color=Color.FromRgb(12,30,45),BlurRadius=18,ShadowDepth=5,Opacity=.17}};
        border.SizeChanged+=(_,_)=>grid.Clip=new RectangleGeometry(new Rect(0,0,Math.Max(0,border.ActualWidth-2),Math.Max(0,border.ActualHeight-2)),radius,radius);
        return border;
    }
    internal static TextBlock Label(string text,double size=13,bool bold=false) => new(){Text=text,FontSize=size,FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,Foreground=new SolidColorBrush(Ink),TextTrimming=TextTrimming.CharacterEllipsis};
    internal static Button Button(string text,Action action)
    {
        var button=new Button{Content=text,Padding=new Thickness(13,7,13,7),Margin=new Thickness(3),Cursor=System.Windows.Input.Cursors.Hand,Background=new SolidColorBrush(Color.FromArgb(160,255,255,255)),Foreground=new SolidColorBrush(Ink),BorderThickness=new Thickness(0),FontSize=13};
        var shell=new FrameworkElementFactory(typeof(Border));shell.SetValue(Border.CornerRadiusProperty,new CornerRadius(9));shell.SetBinding(Border.BackgroundProperty,new System.Windows.Data.Binding("Background"){RelativeSource=System.Windows.Data.RelativeSource.TemplatedParent});shell.SetBinding(Border.PaddingProperty,new System.Windows.Data.Binding("Padding"){RelativeSource=System.Windows.Data.RelativeSource.TemplatedParent});
        var presenter=new FrameworkElementFactory(typeof(ContentPresenter));presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty,HorizontalAlignment.Center);shell.AppendChild(presenter);button.Template=new ControlTemplate(typeof(Button)){VisualTree=shell};
        button.Click+=(_,_)=>action();return button;
    }
    internal static BitmapImage? Wallpaper()
    {
        var path=new System.Text.StringBuilder(32768); Native.SystemParametersInfo(0x73,(uint)path.Capacity,path,0);
        if(path.ToString()==wallpaperPath) return wallpaper;
        wallpaperPath=path.ToString();wallpaper=null;
        if(!File.Exists(wallpaperPath))return null;
        try
        {
            var value=new BitmapImage(); value.BeginInit();value.CacheOption=BitmapCacheOption.OnLoad;value.DecodePixelWidth=2560;value.UriSource=new Uri(wallpaperPath);value.EndInit();value.Freeze();wallpaper=value;
        }
        catch(Exception e) when(e is IOException or NotSupportedException or ArgumentException) {wallpaper=null;}
        return wallpaper;
    }
}
