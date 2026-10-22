namespace DesktopGlass.Core;

/// <summary>仅将用户正常完成的编辑写回偏好，系统中断与缺屏呈现不能改变原布局。</summary>
public static class LayoutEditPolicy
{
    /// <summary>判断编辑是否可提交；任何取消、重建、停止、锁定或临时回退条件都拒绝提交。</summary>
    /// <param name="canceled">拖动是否取消。</param><param name="rebuilding">是否正在重建屏幕窗口。</param>
    /// <param name="stopped">是否正在退出。</param><param name="locked">布局是否锁定。</param><param name="temporary">是否临时缺屏收纳。</param><returns>可提交时为 true。</returns>
    public static bool CanCommit(bool canceled,bool rebuilding,bool stopped,bool locked,bool temporary)=>!canceled&&!rebuilding&&!stopped&&!locked&&!temporary;
}
