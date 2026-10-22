namespace DesktopGlass.App.UI;

internal sealed class GroupEditor : Window
{
    internal GroupEditor(Group group,Action save)
    {
        Title="分组外观";Width=390;Height=350;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterScreen;Background=new SolidColorBrush(Color.FromRgb(240,245,248));FontFamily=new FontFamily("Microsoft YaHei UI");
        var body=new StackPanel{Margin=new Thickness(25)};
        body.Children.Add(Glass.Label("让桌面更合你的习惯",20,true));
        body.Children.Add(new TextBlock{Text="名称",Margin=new Thickness(0,20,0,6)});
        var name=new TextBox{Text=group.Name,MaxLength=40,Padding=new Thickness(8),FontSize=14};body.Children.Add(name);
        body.Children.Add(new TextBlock{Text="玻璃底色浓度",Margin=new Thickness(0,15,0,6)});
        var tint=new Slider{Minimum=.12,Maximum=.85,Value=group.Tint};body.Children.Add(tint);
        body.Children.Add(new TextBlock{Text="圆角",Margin=new Thickness(0,15,0,6)});
        var radius=new Slider{Minimum=0,Maximum=48,Value=group.Radius};body.Children.Add(radius);
        body.Children.Add(Glass.Button("保存",()=>{if(string.IsNullOrWhiteSpace(name.Text)){name.Focus();return;}group.Name=name.Text.Trim();group.Tint=tint.Value;group.Radius=radius.Value;save();Close();}));
        Content=body;
    }
}
