using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DesktopGlass.App.Windows;

namespace DesktopGlass.App.UI;

internal sealed class DesktopSurface : Window
{
    private const string EntryFormat="DesktopGlass.Entry";
    private readonly DesktopController controller;
    private readonly DisplayArea display;
    private readonly Canvas canvas=new();
    internal DesktopSurface(DesktopController controller,DisplayArea display)
    {
        this.controller=controller;this.display=display;
        Title="Desktop Glass";WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;
        AllowsTransparency=true;Background=Brushes.Transparent;FontFamily=new FontFamily("Microsoft YaHei UI");
        Width=display.Width/display.Scale;Height=display.Height/display.Scale;Left=display.Left/display.Scale;Top=display.Top/display.Scale;
        canvas.Background=Brushes.Transparent;Content=canvas;
        var menu=new ContextMenu(); menu.Items.Add(Menu("新建分组",()=>controller.CreateGroup(display.Id)));menu.Items.Add(Menu("打开桌面文件夹",controller.OpenDesktop));menu.Items.Add(Menu("恢复桌面并退出",controller.Exit));canvas.ContextMenu=menu;
        Render();
    }
    internal void Render()
    {
        canvas.Children.Clear();
        var toolbar=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(9,3,9,3)};
        toolbar.Children.Add(new TextBlock{Text="桌面分组",FontSize=14,FontWeight=FontWeights.SemiBold,Foreground=new SolidColorBrush(Glass.Ink),VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(9,0,16,0)});
        toolbar.Children.Add(Glass.Button("＋ 新建分组",()=>controller.CreateGroup(display.Id)));
        toolbar.Children.Add(Glass.Button(controller.Workspace.Locked?"解锁布局":"锁定布局",()=>{controller.Workspace.Locked=!controller.Workspace.Locked;controller.Changed();}));
        toolbar.Children.Add(Glass.Button("刷新",controller.Refresh));
        toolbar.Children.Add(Glass.Button("退出",controller.Exit));
        var pill=Glass.Panel(toolbar);canvas.Children.Add(pill);Canvas.SetLeft(pill,28);Canvas.SetTop(pill,22);
        foreach(var group in controller.Workspace.Groups)
        {
            var placement=LayoutResolver.Resolve(group,controller.Displays);
            if(placement.Temporary || placement.Display.Id!=display.Id)continue;
            var card=GroupCard(group,false);
            card.Width=placement.Width/display.Scale;card.Height=placement.Height/display.Scale;
            canvas.Children.Add(card);Canvas.SetLeft(card,(placement.Left-display.Left)/display.Scale);Canvas.SetTop(card,(placement.Top-display.Top)/display.Scale);
        }
        var sidebarWidth=Math.Min(260,Width/3);
        var missing=display.Primary?controller.Workspace.Groups.Where(g=>LayoutResolver.Resolve(g,controller.Displays).Temporary).ToList():[];
        var side=new StackPanel{Margin=new Thickness(13)};
        side.Children.Add(Glass.Label("未分类",16,true));
        side.Children.Add(new TextBlock{Text="拖入分组即可收纳",FontSize=11,Foreground=new SolidColorBrush(Glass.Ink),Margin=new Thickness(0,5,0,12)});
        var assigned=controller.Workspace.Groups.SelectMany(g=>g.Entries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unclassified=controller.Entries.Where(e=>!assigned.Contains(e.Path)).ToList();
        var tray=Tiles(unclassified,null);
        tray.AllowDrop=true;tray.Drop+=(_,e)=>HandleDrop(e,null);
        side.Children.Add(new ScrollViewer{Content=tray,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Height=Math.Max(100,Height-215-(missing.Count>0?285:0))});
        if(missing.Count>0)
        {
            var stack=new StackPanel();stack.Children.Add(new TextBlock{Text="外屏暂离 · 接回后归位",Margin=new Thickness(4,12,4,6),FontWeight=FontWeights.SemiBold,Foreground=new SolidColorBrush(Glass.Ink)});
            foreach(var group in missing){var card=GroupCard(group,true);card.Height=200;card.Margin=new Thickness(1,4,1,8);stack.Children.Add(card);}
            side.Children.Add(new ScrollViewer{Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Height=280});
        }
        var sidebar=Glass.Panel(side,null,display,Width-sidebarWidth-28,96,sidebarWidth,Height-124);sidebar.Width=sidebarWidth;sidebar.Height=Math.Max(200,Height-124);
        sidebar.AllowDrop=true;sidebar.Drop+=(_,e)=>HandleDrop(e,null);
        canvas.Children.Add(sidebar);Canvas.SetLeft(sidebar,Width-sidebarWidth-28);Canvas.SetTop(sidebar,96);
    }
    private Border GroupCard(Group group,bool temporary)
    {
        var layout=new Grid{Margin=new Thickness(12)};layout.RowDefinitions.Add(new RowDefinition{Height=new GridLength(38)});layout.RowDefinitions.Add(new RowDefinition());
        var header=new Grid();header.ColumnDefinitions.Add(new ColumnDefinition());header.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(36)});
        var title=Glass.Label(group.Name,15,true); title.Margin=new Thickness(8,0,0,0);title.VerticalAlignment=VerticalAlignment.Center;
        header.Children.Add(title);
        var drag=new Thumb{Background=Brushes.Transparent,Cursor=controller.Workspace.Locked || temporary?Cursors.Arrow:Cursors.SizeAll};
        var template=new ControlTemplate(typeof(Thumb));var grip=new FrameworkElementFactory(typeof(Border));grip.SetValue(Border.BackgroundProperty,Brushes.Transparent);template.VisualTree=grip;drag.Template=template;
        header.Children.Add(drag);
        var menu=new ContextMenu();menu.Items.Add(Menu("名称与玻璃外观",()=>new GroupEditor(group,controller.Changed).Show()));
        if(temporary) menu.Items.Add(Menu("移到当前屏幕",()=>{group.DisplayId=display.Id;group.X=36;group.Y=96;controller.Changed();}));
        menu.Items.Add(Menu("移除分组（保留文件）",()=>{controller.Workspace.RemoveGroup(group.Id);controller.Changed();}));
        var more=Glass.Button("⋯",()=>{menu.IsOpen=true;});more.Padding=new Thickness(3);Grid.SetColumn(more,1);header.Children.Add(more);layout.Children.Add(header);
        var entries=group.Entries.Select(path=>controller.Entries.FirstOrDefault(e=>string.Equals(e.Path,path,StringComparison.OrdinalIgnoreCase)) ?? new DesktopEntry(path,Path.GetFileNameWithoutExtension(path))).ToList();
        var scroll=new ScrollViewer{Content=Tiles(entries,group.Id),VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(0,8,0,0)};Grid.SetRow(scroll,1);layout.Children.Add(scroll);
        if(entries.Count==0){var hint=Glass.Label("将桌面项目拖到这里",12);hint.HorizontalAlignment=HorizontalAlignment.Center;hint.VerticalAlignment=VerticalAlignment.Center;hint.IsHitTestVisible=false;Grid.SetRow(hint,1);layout.Children.Add(hint);}
        var card=Glass.Panel(layout,group,display,group.X,group.Y,group.Width,group.Height);
        card.AllowDrop=true;card.Drop+=(_,e)=>HandleDrop(e,group.Id);
        card.DragOver+=(_,e)=>{e.Effects=e.Data.GetDataPresent(EntryFormat)||e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Link:DragDropEffects.None;e.Handled=true;};
        drag.DragDelta+=(_,e)=>{if(controller.Workspace.Locked||temporary)return;Canvas.SetLeft(card,Math.Max(0,Canvas.GetLeft(card)+e.HorizontalChange));Canvas.SetTop(card,Math.Max(76,Canvas.GetTop(card)+e.VerticalChange));};
        drag.DragCompleted+=(_,e)=>{
            if(!controller.CanCommit(e.Canceled,temporary))return;
            var cursor=System.Windows.Forms.Cursor.Position;
            var target=controller.Displays.FirstOrDefault(d=>cursor.X>=d.Left && cursor.X<d.Left+d.Width && cursor.Y>=d.Top && cursor.Y<d.Top+d.Height) ?? display;
            if(target.Id!=display.Id){group.DisplayId=target.Id;group.X=Math.Max(0,(cursor.X-target.Left)/target.Scale-60);group.Y=Math.Max(76,(cursor.Y-target.Top)/target.Scale-15);}
            else{group.X=Canvas.GetLeft(card);group.Y=Canvas.GetTop(card);}
            controller.Changed();
        };
        if(!temporary)
        {
            var resize=new Thumb{Width=18,Height=18,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Bottom,Cursor=Cursors.SizeNWSE,Opacity=.3};
            Grid.SetRowSpan(resize,2);layout.Children.Add(resize);
            resize.DragDelta+=(_,e)=>{if(controller.Workspace.Locked)return;card.Width=Math.Clamp(card.ActualWidth+e.HorizontalChange,240,Math.Min(2000,Width));card.Height=Math.Clamp(card.ActualHeight+e.VerticalChange,160,Math.Min(1600,Height));};
            resize.DragCompleted+=(_,e)=>{if(!controller.CanCommit(e.Canceled,temporary))return;group.Width=card.Width;group.Height=card.Height;controller.Changed();};
        }
        return card;
    }
    private WrapPanel Tiles(IEnumerable<DesktopEntry> entries,string? groupId)
    {
        var panel=new WrapPanel();
        foreach(var entry in entries)
        {
            var contents=new StackPanel{Margin=new Thickness(4),Width=69};
            var icon=controller.Catalog.Icon(entry.Path);
            if(icon is not null)contents.Children.Add(new Image{Source=icon,Width=36,Height=36,Margin=new Thickness(0,4,0,6)});
            else contents.Children.Add(new TextBlock{Text="◇",FontSize=28,HorizontalAlignment=HorizontalAlignment.Center});
            contents.Children.Add(new TextBlock{Text=entry.Name,TextAlignment=TextAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis,FontSize=11,Foreground=new SolidColorBrush(Glass.Ink),Height=29,TextWrapping=TextWrapping.Wrap});
            var tile=new Border{Child=contents,CornerRadius=new CornerRadius(10),Background=Brushes.Transparent,ToolTip=entry.Name+"\n"+entry.Path,Focusable=true,AllowDrop=true};
            tile.MouseEnter+=(_,_)=>tile.Background=new SolidColorBrush(Color.FromArgb(70,255,255,255));tile.MouseLeave+=(_,_)=>tile.Background=Brushes.Transparent;
            Point down=default;
            tile.PreviewMouseLeftButtonDown+=(_,e)=>{down=e.GetPosition(tile);tile.Focus();if(e.ClickCount==2){controller.Open(entry.Path);e.Handled=true;}};
            tile.PreviewMouseMove+=(_,e)=>{if(e.LeftButton!=MouseButtonState.Pressed)return;var point=e.GetPosition(tile);if((point-down).Length<7)return;DragDrop.DoDragDrop(tile,new DataObject(EntryFormat,entry.Path),DragDropEffects.Link);e.Handled=true;};
            tile.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){controller.Open(entry.Path);e.Handled=true;}};
            tile.Drop+=(_,e)=>HandleDrop(e,groupId,groupId is null?int.MaxValue:controller.Workspace.Groups.Single(g=>g.Id==groupId).Entries.IndexOf(entry.Path));
            var menu=new ContextMenu();menu.Items.Add(Menu("打开",()=>controller.Open(entry.Path)));
            if(groupId is not null)menu.Items.Add(Menu("移出分组",()=>{controller.Workspace.MoveEntry(entry.Path,null);controller.Changed();}));
            foreach(var g in controller.Workspace.Groups){var target=g;menu.Items.Add(Menu("移到「"+g.Name+"」",()=>{controller.Workspace.MoveEntry(entry.Path,target.Id);controller.Changed();}));}
            tile.ContextMenu=menu;panel.Children.Add(tile);
        }
        return panel;
    }
    private void HandleDrop(DragEventArgs e,string? groupId,int index=int.MaxValue)
    {
        if(e.Handled)return;
        try
        {
            if(e.Data.GetDataPresent(EntryFormat) && e.Data.GetData(EntryFormat) is string path)controller.Workspace.MoveEntry(path,groupId,index);
            else if(e.Data.GetData(DataFormats.FileDrop) is string[] files)foreach(var file in files){controller.Track(file);controller.Workspace.MoveEntry(file,groupId,index);}
            else return;
            e.Handled=true;e.Effects=DragDropEffects.Link;controller.Changed();
        }
        catch(Exception ex) {controller.Report(ex);}
    }
    private static MenuItem Menu(string title,Action action){var item=new MenuItem{Header=title};item.Click+=(_,_)=>action();return item;}
}
