namespace DesktopGlass.Core;

/// <summary>仅保存入口引用和分组偏好，不对真实文件执行移动或删除。</summary>
public sealed class Workspace
{
    /// <summary>配置格式版本；版本 1 只供读取迁移，保存使用版本 2。</summary>
    public int Version { get; set; } = 2;
    /// <summary>用户定义的分组，顺序为创建顺序。</summary>
    public List<Group> Groups { get; set; } = [];
    /// <summary>是否锁定分组位置与大小。</summary>
    public bool Locked { get; set; }

    /// <summary>创建分组。名称为空时抛出参数异常；返回带唯一标识的新分组。</summary>
    /// <param name="name">1～40 字符名称。</param><param name="displayId">所属显示器持久身份。</param>
    /// <returns>已加入工作区的分组。</returns>
    public Group AddGroup(string name, string displayId)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 40) throw new ArgumentException("分组名称须为 1～40 个字符。", nameof(name));
        var group = new Group { Name = name.Trim(), DisplayId = displayId, DesktopVisible = true, X = 36 + Groups.Count % 3 * 380, Y = 96 + Groups.Count / 3 * 320 };
        Groups.Add(group); return group;
    }

    /// <summary>移除分组，入口恢复未分类状态；不会删除入口对应的文件。</summary>
    /// <param name="id">分组标识；不存在时无操作。</param>
    public void RemoveGroup(string id) => Groups.RemoveAll(g => g.Id == id);

    /// <summary>调整入口唯一归属或排序。目标不存在时保持原归属并抛出参数异常。</summary>
    /// <param name="entry">非空 Shell 路径。</param><param name="groupId">目标分组，null 表示取消收录。</param>
    /// <param name="index">目标插入位置，越界时限制到有效范围。</param>
    public void MoveEntry(string entry, string? groupId, int index = int.MaxValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entry);
        var target = groupId is null ? null : Groups.SingleOrDefault(g => g.Id == groupId) ?? throw new ArgumentException("分组不存在。", nameof(groupId));
        foreach (var group in Groups) group.Entries.RemoveAll(e => string.Equals(e, entry, StringComparison.OrdinalIgnoreCase));
        target?.Entries.Insert(Math.Clamp(index, 0, target.Entries.Count), entry);
    }

    /// <summary>校验反序列化数据，拒绝重复标识、无效数值及超大数据；无效时抛出 InvalidDataException。</summary>
    public void Validate()
    {
        if (Version is not (1 or 2) || Groups is null || Groups.Count > 200) throw new InvalidDataException("配置版本或分组数量无效。");
        var ids = new HashSet<string>(); var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in Groups)
        {
            if (g is null || string.IsNullOrWhiteSpace(g.Id) || !ids.Add(g.Id) || string.IsNullOrWhiteSpace(g.Name) || g.Name.Length > 40 || g.DisplayId is null || g.Entries is null || g.Entries.Count > 10000 || !g.HasValidGeometry())
                throw new InvalidDataException("分组配置损坏。");
            foreach (var e in g.Entries) if (string.IsNullOrWhiteSpace(e) || !entries.Add(e)) throw new InvalidDataException("入口重复或无效。");
        }
    }
}

/// <summary>分组偏好；几何数据均为所属屏幕工作区内的 DIP，不记录临时回退位置。</summary>
public sealed class Group
{
    /// <summary>分组唯一标识。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>用户可见名称，1～40 字符。</summary>
    public string Name { get; set; } = "新分组";
    /// <summary>用户希望在桌面显示该组；缺屏时保留意愿但不创建组件或隐藏项目。</summary>
    public bool DesktopVisible { get; set; }
    /// <summary>显示器设备身份；缺失显示器时保留原值。</summary>
    public string DisplayId { get; set; } = "";
    /// <summary>相对工作区左边的 DIP 距离。</summary>
    public double X { get; set; } = 36;
    /// <summary>相对工作区顶边的 DIP 距离。</summary>
    public double Y { get; set; } = 96;
    /// <summary>宽度 DIP，允许 240～2000。</summary>
    public double Width { get; set; } = 320;
    /// <summary>高度 DIP，允许 160～1600。</summary>
    public double Height { get; set; } = 240;
    /// <summary>玻璃底色不透明度，0.12～0.85。</summary>
    public double Tint { get; set; } = 0.18;
    /// <summary>外轮廓圆角 DIP，0～48。</summary>
    public double Radius { get; set; } = 20;
    /// <summary>Shell 入口路径，列表顺序即显示顺序。</summary>
    public List<string> Entries { get; set; } = [];
    internal bool HasValidGeometry() => double.IsFinite(X) && double.IsFinite(Y) && Math.Abs(X) <= 100000 && Math.Abs(Y) <= 100000 && Width is >= 240 and <= 2000 && Height is >= 160 and <= 1600 && Tint is >= .12 and <= .85 && Radius is >= 0 and <= 48;
}
