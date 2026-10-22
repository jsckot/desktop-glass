using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace DesktopGlass.App.Windows;

internal sealed record DesktopEntry(string Path,string Name);

internal sealed class DesktopCatalog
{
    private readonly Dictionary<string,ImageSource> icons=new(StringComparer.OrdinalIgnoreCase);
    internal List<DesktopEntry> Scan()
    {
        object? shell=null,folder=null,items=null;
        try
        {
            shell=Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("Windows Shell 不可用。"));
            folder=((dynamic)shell!).Namespace(0); items=((dynamic)folder!).Items();
            var result=new List<DesktopEntry>();
            for(var i=0;i<(int)((dynamic)items!).Count;i++)
            {
                object? item=null;
                try {item=((dynamic)items).Item(i); string path=((dynamic)item!).Path; string name=((dynamic)item).Name; if(!string.IsNullOrWhiteSpace(path)) result.Add(new(path,name));}
                finally {Release(item);}
            }
            return result.DistinctBy(e=>e.Path,StringComparer.OrdinalIgnoreCase).OrderBy(e=>e.Name,StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        finally {Release(items);Release(folder);Release(shell);}
    }
    internal ImageSource? Icon(string path)
    {
        if(icons.TryGetValue(path,out var cached)) return cached;
        var info=new Native.ShellFileInfo {DisplayName="",TypeName=""};
        Native.SHGetFileInfo(path,0,ref info,(uint)Marshal.SizeOf<Native.ShellFileInfo>(),0x100);
        if(info.Icon==0) return null;
        try
        {
            var source=Imaging.CreateBitmapSourceFromHIcon(info.Icon,Int32Rect.Empty,BitmapSizeOptions.FromWidthAndHeight(48,48)); source.Freeze();
            if(icons.Count>=512) icons.Clear(); icons[path]=source; return source;
        }
        finally {Native.DestroyIcon(info.Icon);}
    }
    internal static void Open(string path) => Process.Start(new ProcessStartInfo(path.StartsWith("::",StringComparison.Ordinal)?"shell:"+path:path){UseShellExecute=true});
    private static void Release(object? value) {if(value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);}
}
