namespace DesktopGlass.Core;

/// <summary>按用户显示意愿与当前屏幕快照筛选真正可呈现的分组，不修改保存布局。</summary>
public static class WorkspaceVisibility
{
    /// <summary>列出启用且屏幕身份唯一匹配的分组；缺屏及重复身份不回退到主屏。</summary>
    /// <param name="workspace">已校验的工作区。</param><param name="displays">当前活动显示器快照。</param>
    /// <returns>保持原分组顺序的可呈现分组。</returns>
    public static IEnumerable<Group> VisibleGroups(Workspace workspace, IReadOnlyList<DisplayArea> displays)
        => workspace.Groups.Where(g => g.DesktopVisible && displays.Count(d => d.Id == g.DisplayId) == 1);

    /// <summary>仅返回可呈现分组引用，用于建立当前收纳责任。</summary>
    /// <param name="workspace">已校验工作区。</param><param name="displays">当前显示器快照。</param>
    /// <returns>不含暂停或缺屏分组内容的入口序列。</returns>
    public static IEnumerable<string> VisibleEntries(Workspace workspace, IReadOnlyList<DisplayArea> displays)
        => VisibleGroups(workspace, displays).SelectMany(g => g.Entries);
}
