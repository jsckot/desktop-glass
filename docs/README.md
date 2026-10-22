# Desktop Glass

Windows 桌面玻璃分组工具，当前为 0.1.0 本地体验版。首轮验证环境为 Windows 11 26H2 / 26300.9711 x64。

> **2026-10-22 体验反馈：0.1.0 已知显示异常，且全屏接管方式不符合保留原生桌面交互的要求。请先使用托盘或当前右键菜单的“恢复桌面并退出”。以下是旧版操作记录；修正目标见 [设计](mvp-design.md)，新版尚未交付。**

## 0.1.0 安装和使用记录

1. 完整解压 `DesktopGlass-0.1.0-win-x64.zip`。
2. 双击解压目录里的 `DesktopGlass.exe`，选择“是”安装到当前用户，选择“否”仅运行。
3. 安装后从开始菜单打开“Desktop Glass”。无需另外安装 .NET，无需管理员权限，不自动开机启动。
4. 将右侧“未分类”里的项目拖到分组；双击项目打开。分组内可拖拽排序，右键可移出或移到其他分组。
5. 拖动分组标题移动位置，拖动右下角调整大小；“⋯”可修改名称、玻璃浓度和圆角。
6. “锁定布局”可防止意外移动。通过工具栏或任务栏托盘“恢复桌面并退出”结束整理。

卸载入口位于 Windows“设置 → 应用 → 安装的应用 → Desktop Glass”及开始菜单的“卸载 Desktop Glass”。卸载时恢复桌面，清理本产品程序和配置，保留实际文件。安装/卸载若中断，可从原解压包重新运行安装修复；清理错误记录位于安装目录 `.cleanup-error.txt`。

当前安装包未签名，Windows 的信任提示由用户自行处理。

## 当前功能与边界

- 分组创建、命名、移除、排序、移动、缩放、锁定、玻璃外观设置。
- 保留原图标和文件路径；软件启用时接管桌面呈现，收录项目不重复显示。
- 原生桌面全部入口先通过 Shell 枚举，未分类项保持可见。新增/移除桌面项目会触发刷新，也可手动刷新。
- 原生桌面右键菜单目前由简化菜单替代；需要原文件操作时选择“打开桌面文件夹”。
- 玻璃使用静态壁纸采样模糊和高光，不是实时窗口背景折射；不同屏幕独立壁纸、跨屏壁纸暂未完整验证。
- 外屏离线时，相关分组进入主屏侧栏收纳，原位置不回写；接回后按设备身份恢复。真实双屏、换接口、复制模式、混合缩放与睡眠唤醒仍需实机验收。
- Explorer 重建或恢复保护失效时退出整理，保留原生桌面，用户可重新启动。
- 本版不包含任务栏美化、文件夹图标重绘、应用图标美化和搜索。

## 构建与验证

需要 .NET 10 SDK 和 Windows x64。发布包固定带上 .NET Core / WindowsDesktop 10.0.13 运行库。

```powershell
./scripts/Build.ps1
./tests/Verify-Windows.ps1 -Executable "$PWD/.codex/artifacts/desktop-glass-mvp-win-x64/DesktopGlass.exe" -EvidenceDirectory "$PWD/.codex/tmp/desktop-glass/my-check"
```

`Build.ps1 -Dotnet <dotnet.exe 路径>` 可使用项目内 SDK。领域测试使用返回退出码的测试执行器：

```powershell
dotnet run --project tests/DesktopGlass.Tests -c Release
```

验证脚本会短时接管桌面、终止测试子进程检验恢复，并在隔离目录验证安装和卸载清理；运行前应退出正常整理实例。证据与产物仅放 `.codex/`，不提交。

详情见 [设计](mvp-design.md)、[实施计划](implementation-plan.md)、[验收记录](acceptance.md)。
