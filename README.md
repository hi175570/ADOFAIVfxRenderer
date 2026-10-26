# ADOFAI VFX Renderer

ADOFAI VFX Renderer 是一个 Windows 离线渲染工具，用来把《冰与火之舞》谱面的视觉内容直接计算成视频背景。它不会启动 `A Dance of Fire and Ice.exe`，也不需要真人游玩、录屏或占用前台；渲染期间仍会使用 CPU、GPU、内存与硬盘，并显示当前帧数、百分比、渲染帧率和每帧耗时。

程序需要用户选择现有的游戏安装目录。只要选到包含 `A Dance of Fire and Ice.exe` 的目录即可，不需要手动寻找 `Managed`。程序会从所选安装中读取兼容的原版程序集、场景、滤镜、资源索引与 FFmpeg，但发布包不会重新分发游戏代码、游戏资源或 DLC。

## 四种输出版本

| 版本 | 视频内容 | 派生谱面实时内容 | 适用场景 |
| --- | --- | --- | --- |
| Video | 特效、装饰物 | 轨道、星球 | 标准视频背景体验 |
| Video Pro | 轨道、星球、特效、装饰物 | 不再实时绘制视觉元素 | 运行时性能消耗最低 |
| Video VFX | 装饰物 | 轨道、星球、全部特效 | 平衡画面与性能 |
| Video VFX Pro | 深度大于等于 0 的背景装饰物 | 轨道、星球、特效、深度小于 0 的前景装饰物 | 保留前景遮挡关系，硬件要求最高 |

深度小于 0 的装饰物位于轨道上方，能够遮挡轨道；深度大于等于 0 的装饰物位于轨道下方。

Video Pro 使用所选游戏安装中的原版 `scnGame` 场景、事件组件、轨道与对象渲染器、玩家星球、滤镜、相机栈和共享 RenderTexture，并在独立 Unity Worker 中逐帧推进时间。游戏可执行文件和游戏窗口都不会启动。其余三个需要拆分图层的版本仍使用独立的选择性图层渲染器；无法安全烤入视频的元素会保留在派生谱面中，不会从视频和谱面两边同时消失。

## 主要功能

- 支持 144p、360p、720p、1080p、2160p。
- 支持 15、30、60、90、120、144 FPS。
- 支持质量预设与自定义码率。
- 支持结束后继续渲染秒数或帧数，两个输入会自动换算。
- 支持一次生成多个版本、分辨率和帧率，并按任务队列顺序执行。
- 支持自动、CPU/WARP、GPU 光栅和 NVIDIA RTX/NVENC 后端；不静默降级。
- 界面为深色模式，包含大尺寸实时预览、可滚动任务列表、进度、速度和输出路径。
- UI 永远不会进入正式输出；Video Pro 可以包含轨道和星球。
- 自动计算第一格可游玩砖块的真实时间，并生成正确的正负 `vidOffset`，不修改源谱。
- 每个谱面旁只维护一个 `Video VFX` 输出目录；同一种版本只有一个规范视频和一个规范派生谱面，成功重渲染后原子替换旧文件，不堆积时间戳备份。
- 支持装饰贴图黑名单与白名单、引用顺序和深度排序、透明网格大图预览。
- 支持生成十组同帧有图／无图对照，共二十张诊断图，并直接在程序中比较。
- 支持按基本／高级滤镜、平面、目标类型和标签分组，分别选择保留实时、烤入视频或忽略。
- 能识别当前选择性渲染器尚不能可靠烤入的滤镜组，并在开始前阻止错误配置；不会用猜测参数冒充原版效果。
- 支持游戏自身的谱面版本转换路径。当前验证基线为 3.x、Unity 6000.3.10f1；未知二进制版本会明确拒绝，不会套用未经验证的假设。

## 输出与同步

正式输出位于源谱同目录下的 `Video VFX` 文件夹。视频从第一格可游玩砖块对应的绝对谱面时间开始计算；派生谱面的 `bgVideo` 指向生成的视频，`vidOffset` 则根据游戏从第一格砖块触发视频的语义自动计算。因此正常谱面通常可以得到 `0 ms`，特殊第一格角度、零号格速度事件或其他前置时序也可以得到正确的正数或负数。

只有真正烤入所选版本的视频元素才会从派生谱面关闭或删除。贴图载入失败、选择保留实时或当前实现无法可靠处理的元素都会继续留在派生谱面中。源 `.adofai` 文件始终保持不变。

## 下载与运行

发布页同时提供两种 Windows x64 软件包：

- `portable.zip`：解压后直接运行 `ADOFAIVfxRenderer.exe`。
- `setup.exe`：安装到当前用户目录，提供开始菜单快捷方式与卸载程序。

两个软件包都包含自包含的 .NET 运行时、独立 Unity Worker、FFmpeg 调用逻辑和独立 Worker 资源准备器；终端用户不需要另装 .NET、Python、Unity 或 UnityPy。仍然必须提供一份兼容的《冰与火之舞》安装目录，因为程序不会分发游戏文件。

## 从源码构建

源码构建需要 Windows x64、.NET 8 SDK、Unity 6000.3.10f1、Python 3、UnityPy 和 PyInstaller。PowerShell 命令如下：

```powershell
pwsh -File .\scripts\Build-UnityWorker.ps1
pwsh -File .\scripts\Build-ResourcePatcher.ps1
pwsh -File .\scripts\Publish-TestPackage.ps1 -SelfContained
pwsh -File .\scripts\Build-ReleasePackages.ps1 -Version 0.1.0
dotnet run --project .\tests\ADOFAIVfxRenderer.Core.Tests\ADOFAIVfxRenderer.Core.Tests.csproj
```

`Build-ReleasePackages.ps1` 会生成 Portable ZIP、Inno Setup 安装程序和 SHA-256 校验文件，并检查独立 Worker 中没有混入 `Assembly-CSharp.dll`。

## 已验证谱面

当前原版运行时 Video Pro 路径已用以下类型的谱面进行短片段实测：

- INVERSION：大量 `AddObject`、对象动画、轨道、滤镜与玩家星球。
- Friday：高密度逐帧贴图与图形动画。
- Camellia - Seagull：横向宽屏滤镜、遮罩、开场聚合线条与运镜。
- THE STAR SYSTEM'S CALLING：轨道、粒子、复杂高级滤镜和相机效果。
- 996：Hall of Mirrors、`SetObject` 与遮罩组合。
- Our Life：高密度遮罩与多组 Hall of Mirrors。

构建成功和自动测试通过只代表工程门槛通过；复杂谱面仍应将生成片段与同一游戏安装的原版画面对照确认。

## 兼容与边界

Video Pro 的目标是直接复用原版运行时，而不是手写猜测轨道样式、滤镜强度、缓动或对象参数。Video、Video VFX 和 Video VFX Pro 因为必须把同一原版场景拆成不同归属层，仍可能遇到原版滤镜跨层、遮罩依赖、历史帧反馈或模组 UI 无法单独拆分的情况。程序会尽量识别并提示这些组合；需要完整压平时优先使用 Video Pro，需要游戏实时轨道与滤镜时再使用 Video VFX 系列并进行对照。

旧版 2.9.x 使用不同 Unity 运行时，目前没有宣称兼容。若用户反馈真实旧版安装问题，应使用对应安装重新验证并制作独立适配器，而不是假定新版程序集可以直接套用。

## 开源与游戏文件

本仓库只包含本项目自行编写的源代码、构建脚本和 Worker 工程。发布包不会包含游戏的 `Assembly-CSharp.dll`、场景、贴图、音频或 DLC；运行时准备出的缓存只存在于用户本机。项目许可证会在首个稳定版前单独确定。
