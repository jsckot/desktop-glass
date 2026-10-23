using DesktopGlass.App.UI;
namespace DesktopGlass.App;

internal sealed partial class NativeDesktopController
{
    // 覆盖只存在于本进程；workspace 始终代表最后成功持久化的状态。
    private readonly Dictionary<string, Group> appearancePreviews = [];
    private Group EffectiveAppearance(Group source)
    {
        if (!appearancePreviews.TryGetValue(source.Id, out var value)) return source;
        var result = CopyGroup(source); result.Name = value.Name; result.Tint = value.Tint; result.Radius = value.Radius; return result;
    }
    private void PreviewAppearance(string id, Group value)
    {
        if (stopped || !workspace.Groups.Any(g => g.Id == id)) return;
        appearancePreviews[id] = new Group { Name = value.Name, Tint = value.Tint, Radius = value.Radius };
        ApplyGroupAppearance(id);
    }
    private void ApplyGroupAppearance(string id)
    {
        var source = workspace.Groups.SingleOrDefault(g => g.Id == id); if (source is null || stopped) return;
        var value = EffectiveAppearance(source);
        foreach (var presentation in widgets.Where(w => w.GroupId == id)) presentation.Host?.ApplyAppearance(value, presentation.Display.Scale);
    }

    /// <summary>仅提交名称及浓度，保留历史圆角字段；以最新工作区为基准保留编辑期间发生的布局和入口变化。</summary>
    /// <param name="source">最后成功保存的工作区。</param>
    /// <param name="id">仍然存在的目标分组标识。</param>
    /// <param name="draft">名称及浓度草稿，其余字段忽略。</param>
    /// <param name="save">持久化回调；失败异常原样传播，原工作区不变。</param>
    /// <returns>成功持久化的独立工作区。</returns>
    /// <exception cref="InvalidOperationException">分组已移除，拒绝复活旧草稿。</exception>
    internal static Workspace AppearanceTransaction(Workspace source, string id, Group draft, Action<Workspace> save)
    {
        var candidate = Copy(source); var target = candidate.Groups.SingleOrDefault(g => g.Id == id)
            ?? throw new InvalidOperationException("分组已被移除。");
        target.Name = draft.Name; target.Tint = draft.Tint;
        candidate.Validate(); save(candidate); return candidate;
    }
}
