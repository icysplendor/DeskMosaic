# DeskMosaic 图标

四块大小不一的蓝、青、紫色拼片像彼此错开的桌面窗口。内置 ImageGen 先生成透明拼片，再用该图生成白色圆角底的应用图标。可查看[应用图标](../MACAssistant/Assets.xcassets/AppIcon.appiconset/AppIcon-256.png)和[透明标记](../MACAssistant/Assets.xcassets/AppEmblem.imageset/AppEmblem.png)。

`MACAssistant/Resources/Generated/` 中的 `AppIcon-source.png` 用于 Finder、Windows EXE 和网页安装图标。`AppEmblem-source.png` 用于设置、笔记窗口的标题栏，以及关闭音频中心后的 Windows 托盘。启用音频中心时，Windows 托盘显示音量和静音。macOS 菜单栏使用透明拼片的单色轮廓，由系统随主题着色。Windows 笔记窗口沿用应用的任务栏身份，并用多尺寸应用图标处理原生窗口消息。

使用 `python3 scripts/process_generated_assets.py` 从原图更新 Xcode 图标资源、ICNS、Windows ICO 和共用的透明 PNG。缩小使用按 alpha 加权的面积采样。安装 Notebook 的依赖后，用 `node Notebook/build.mjs` 更新笔记本网页资源。
