namespace DesktopGlass.Core;

/// <summary>已识别屏幕工作区。位置及大小使用物理像素；Scale 为 DPI/96。</summary>
/// <param name="Id">持久设备身份。</param><param name="Name">可读名称。</param><param name="Left">工作区左坐标。</param><param name="Top">工作区顶坐标。</param>
/// <param name="Width">工作区像素宽度。</param><param name="Height">工作区像素高度。</param><param name="Scale">有效缩放倍率。</param><param name="Primary">是否主屏。</param>
public sealed record DisplayArea(string Id, string Name, double Left, double Top, double Width, double Height, double Scale, bool Primary);

/// <summary>临时解析出的物理像素位置；Temporary 表示缺屏收纳，不得回写到分组偏好。</summary>
public sealed record Placement(DisplayArea Display, double Left, double Top, double Width, double Height, bool Temporary);

/// <summary>将保存的 DIP 偏好映射到当前屏幕，始终保持输入对象不变。</summary>
public static class LayoutResolver
{
    /// <summary>计算可见位置。显示器缺失时标记临时回退；缩小工作区只限制呈现边界。</summary>
    /// <param name="group">保存的分组偏好。</param><param name="displays">当前非空显示器快照。</param><returns>物理像素呈现矩形。</returns>
    /// <exception cref="ArgumentException">几何无效、屏幕为空或缩放无效。</exception>
    public static Placement Resolve(Group group, IReadOnlyList<DisplayArea> displays)
    {
        if (!group.HasValidGeometry() || displays.Count == 0 || displays.Any(d => !double.IsFinite(d.Scale) || d.Scale <= 0 || d.Width <= 0 || d.Height <= 0)) throw new ArgumentException("分组或显示器几何无效。");
        var matches = displays.Where(d => d.Id == group.DisplayId).ToArray();
        var d = matches.Length == 1 ? matches[0] : displays.FirstOrDefault(d => d.Primary) ?? displays[0];
        var temporary = matches.Length != 1;
        var width = Math.Min(group.Width * d.Scale, d.Width); var height = Math.Min(group.Height * d.Scale, d.Height);
        return new Placement(d, d.Left + Math.Clamp(temporary ? 24 : group.X * d.Scale, 0, d.Width - width), d.Top + Math.Clamp(temporary ? 24 : group.Y * d.Scale, 0, d.Height - height), width, height, temporary);
    }
}
