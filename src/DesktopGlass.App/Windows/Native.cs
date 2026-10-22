using System.Runtime.InteropServices;
using System.Text;

namespace DesktopGlass.App.Windows;

internal static class Native
{
    internal delegate bool EnumProc(nint hwnd, nint arg);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] internal struct DisplayDevice
    {
        internal int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] internal string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] internal string DeviceString;
        internal int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] internal string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] internal string DeviceKey;
    }
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] internal static extern nint FindWindow(string? name, string? title);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] internal static extern nint FindWindowEx(nint parent, nint after, string? name, string? title);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback,nint value);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint hwnd,int command);
    [DllImport("user32.dll")] internal static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd,out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] internal static extern int GetClassName(nint hwnd,StringBuilder name,int size);
    [DllImport("user32.dll", SetLastError=true)] internal static extern nint SetParent(nint child,nint parent);
    [DllImport("user32.dll")] internal static extern nint GetWindowLongPtr(nint hwnd,int index);
    [DllImport("user32.dll")] internal static extern nint SetWindowLongPtr(nint hwnd,int index,nint value);
    [DllImport("user32.dll", SetLastError=true)] internal static extern bool SetWindowPos(nint hwnd,nint after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] internal static extern bool ScreenToClient(nint hwnd,ref Point point);
    [DllImport("user32.dll")] internal static extern nint MonitorFromPoint(Point point,uint flags);
    [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(nint monitor,int kind,out uint x,out uint y);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] internal static extern bool EnumDisplayDevices(string name,uint number,ref DisplayDevice device,uint flags);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] internal static extern bool SystemParametersInfo(uint action,uint param,StringBuilder output,uint flags);
    [DllImport("user32.dll")] internal static extern bool DestroyIcon(nint icon);
    [DllImport("shell32.dll", CharSet=CharSet.Unicode)] internal static extern nint SHGetFileInfo(string path,uint attributes,ref ShellFileInfo info,uint size,uint flags);
    [DllImport("shell32.dll", EntryPoint="SHGetFileInfoW")] internal static extern nint SHGetFileInfoPidl(nint pidl,uint attributes,ref ShellFileInfo info,uint size,uint flags);
    [DllImport("shell32.dll", CharSet=CharSet.Unicode)] internal static extern int SHParseDisplayName(string name,nint bind,out nint pidl,uint mask,out uint attributes);
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] internal struct ShellFileInfo
    {
        internal nint Icon; internal int Index; internal uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] internal string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=80)] internal string TypeName;
    }
    internal static nint DesktopListView()
    {
        nint result=0;
        EnumWindows((window,_) => {
            var view=FindWindowEx(window,0,"SHELLDLL_DefView",null);
            if(view!=0) result=FindWindowEx(view,0,"SysListView32",null);
            return result==0;
        },0);
        return result;
    }
    internal static List<DisplayArea> Displays()
    {
        var result=new List<DisplayArea>();
        foreach(var s in System.Windows.Forms.Screen.AllScreens)
        {
            var device=new DisplayDevice { Size=Marshal.SizeOf<DisplayDevice>(), DeviceName="",DeviceString="",DeviceId="",DeviceKey="" };
            var identified=EnumDisplayDevices(s.DeviceName,0,ref device,1) && !string.IsNullOrEmpty(device.DeviceId);
            var monitor=MonitorFromPoint(new Point{X=s.Bounds.Left,Y=s.Bounds.Top},2);
            var hr=GetDpiForMonitor(monitor,0,out var dpi,out _);
            var area=s.WorkingArea;
            // 无稳定身份的设备只使用临时标识，避免将屏幕序号误当作持久身份。
            var id=identified ? device.DeviceId : "unresolved:"+s.DeviceName;
            result.Add(new DisplayArea(id,identified?device.DeviceString:s.DeviceName,area.Left,area.Top,area.Width,area.Height,hr==0?dpi/96d:1,s.Primary));
        }
        return result;
    }
}
