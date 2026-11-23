# DeskMosaic

`DeskMosaic` 是本仓库的 Windows 原生托盘应用。它把完整音频控制中心与原有截图模块合并在同一个托盘、统一设置和配置生命周期中。

## 音频中心

- 输入/输出设备枚举与默认设备切换（Console、Multimedia、Communications 同步）。
- 保存多个“输出 + 输入”音频配置组、整体切换、每设备音量记忆及输入/输出锁定恢复。
- 当前音量动态托盘图标；输入静音时显示红色边框。
- 登录启动期间持续发现延迟就绪的 Windows Audio、USB 和蓝牙端点，并自动恢复失败的系统通知监听。
- 上一个/下一个配置组全局快捷键、浅色/深色、中英文、登录与静默启动。
- 音频与截图共用统一设置窗口和 `%LOCALAPPDATA%\WinAssistant\configuration-v1.json` 配置。

## 截图功能

- 多显示器截图和跨屏自由选区
- 悬停自动识别最上层窗口
- 八个控制点调整选区，移动选区时显示对应鼠标光标
- 矩形、箭头、画笔标注；笔触默认 `3 px`，在紧凑的颜色或粗细胶囊上按住并上下滑动、松开，即可从短纵向轨道选择颜色或 `1 / 2 / 3 / 5 / 8 / 12 px` 粗细
- 工具栏“文字↕”本身是快速字号工具，默认字号为 `18`，按住上下滑动可选 `14 / 18 / 24 / 32 / 44 / 60`，松开后直接进入使用系统输入法的文字模式
- 已有标注支持路径命中后拖动；矩形只有边框可命中，镂空内部不会误选，箭头和画笔也只响应实际路径
- 四格紧凑调色盘，以及 5×2 更多颜色面板
- 点击“像素”后依次输入宽度、回车、高度、回车，得到精确像素尺寸
- 点击“保存”通过系统文件面板将编辑结果保存为 PNG；取消保存会返回当前编辑状态
- `Ctrl+Z` 撤销，双击或 Enter 完成，Esc 或鼠标右键取消
- 完成后自动复制到 Windows 剪贴板
- 可选全局快捷键，默认不占用任何按键
- 可选登录 Windows 时静默启动

## 本地构建

需要 Windows 10/11 和 .NET 8 SDK：

```powershell
dotnet publish WinAssistant/WinAssistant.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -o dist/windows
```

生成的 `DeskMosaic.exe` 是 x64 自包含单文件，不要求目标电脑预装 .NET。

当前自动构建产物未进行 Authenticode 代码签名，下载后可能触发 Microsoft Defender SmartScreen 提示；正式公开分发时应配置 Windows 代码签名证书。
