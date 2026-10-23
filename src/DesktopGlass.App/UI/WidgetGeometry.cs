namespace DesktopGlass.App.UI;

/// <summary>当前组件的统一轻圆角与紧凑尺寸；不改写历史配置字段。</summary>
internal static class WidgetGeometry
{
    internal const double Radius = 12;
    internal const double CompactSize = 144;
    internal static Rect Bounds(Point anchor, Size size, Rect area, double scale, bool expanded)
    {
        var width = Math.Min(expanded ? size.Width : CompactSize * scale, area.Width);
        var height = Math.Min(expanded ? size.Height : CompactSize * scale, area.Height);
        return new Rect(Math.Clamp(anchor.X, area.Left, area.Right - width),
            Math.Clamp(anchor.Y, area.Top, area.Bottom - height), width, height);
    }
    internal static Group Appearance(Group source) => new() { Name = source.Name, Tint = source.Tint, Radius = Radius };
}
