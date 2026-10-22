namespace DesktopGlass.App.UI;

/// <summary>分组界面的命令边界；宿主负责持久化、Shell 交互及错误处理，界面不直接修改桌面。</summary>
internal sealed class WidgetCallbacks
{
    /// <summary>打开原始 Shell 路径，不复制或移动目标。</summary>
    internal required Action<string> Open { get; init; }
    /// <summary>按目标索引收录或排序入口引用；成功保存返回 true，失败返回 false，末尾插入使用当前项目数。</summary>
    internal required Func<IReadOnlyList<string>, int, bool> Collect { get; init; }
    /// <summary>移出入口引用，并由宿主处理原桌面恢复。</summary>
    internal required Action<string> Release { get; init; }
    /// <summary>编辑当前分组名称及玻璃外观。</summary>
    internal required Action Edit { get; init; }
    /// <summary>移除当前分组，保留真实文件。</summary>
    internal required Action Remove { get; init; }
    /// <summary>创建一个新分组。</summary>
    internal required Action Create { get; init; }
    /// <summary>切换工作区布局锁定状态。</summary>
    internal required Action ToggleLock { get; init; }
    /// <summary>请求宿主恢复桌面并正常退出。</summary>
    internal required Action Exit { get; init; }
    /// <summary>预览位置增量，两个参数均为 DIP；不在界面中回写分组偏好。</summary>
    internal required Action<double, double> MoveBy { get; init; }
    /// <summary>预览尺寸增量，两个参数均为 DIP；宿主负责边界限制。</summary>
    internal required Action<double, double> ResizeBy { get; init; }
    /// <summary>结束几何手势；true 表示取消，宿主应恢复手势前的矩形。</summary>
    internal required Action<bool> CommitBounds { get; init; }
}
