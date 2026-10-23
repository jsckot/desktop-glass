namespace DesktopGlass.App;

internal sealed partial class NativeDesktopController
{
    /// <summary>显式切换本次运行的展示层级；不改工作区偏好、桌面窗口或文件租约。</summary>
    private void SetForeground(bool enabled)
    {
        if (stopped || foreground == enabled) return;
        foreground = enabled;
        try
        {
            BuildWidgets(); InstallTray();
            if (manager is not null) { manager.Topmost = enabled; manager.Refresh(); }
            foreach (var editor in editors.Values.Concat(manager?.OwnedWindows.OfType<UI.GroupEditor>() ?? []).Distinct())
                editor.Topmost = enabled;
        }
        catch (Exception error) { StopAfterFailure(error); }
    }
}
