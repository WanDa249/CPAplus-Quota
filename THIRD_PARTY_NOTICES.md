# 第三方声明

## 内嵌字体

Source Han Sans CN / 思源黑体，Adobe 官方未修改的 Regular、Medium 静态 OTF，版本来源固定于提交 `a4f7cf94edfb9d7ffbdfc4841de276358bd7e0f2`。

- 官方项目：https://github.com/adobe-fonts/source-han-sans
- 原始路径：`SubsetOTF/CN/SourceHanSansCN-Regular.otf` 与 `SubsetOTF/CN/SourceHanSansCN-Medium.otf`
- 许可：SIL Open Font License 1.1，完整版权及条款见 `licenses/SourceHanSans-OFL.txt`；EXE内嵌同一完整许可，并在首次运行时与字体一同释放到本地用户目录。
- 字体从 `%LOCALAPPDATA%\CPAplusQuota\Fonts\source-han-sans-a4f7cf94edfb` 私有加载，不安装到系统；字体继续按 OFL 许可，不能用本客户端的MIT条款替代。

## 参考项目

以下固定版本曾用于研究任务栏几何、非激活窗口及窗口状态跟踪。分发包不含这些项目的应用、媒体依赖或图形素材。为完整保留可能适用的参考声明，附上其 MIT 许可。

| 项目 | 参考提交 | 许可文件 |
| --- | --- | --- |
| [abailanli/echomusic-taskbar-lyrics](https://github.com/abailanli/echomusic-taskbar-lyrics) | `ad87608b9abb83e47662931c179693246e08c590` | `licenses/echo-watcher-MIT.txt` |
| [ysheng920/LyricBar](https://github.com/ysheng920/LyricBar) | `8d21e853ff2ada701e25a3a362904a5f4ea5be3a` | `licenses/lyricbar-MIT.txt` |
| [CarsonGame/EchoMusic-TaskbarLyrics](https://github.com/CarsonGame/EchoMusic-TaskbarLyrics) | `47d42ee9b5f194b8ce7c9a3e931282a8fa502602` | `licenses/echo-native-MIT.txt` |

[Barline](https://github.com/madeitdisfar/Barline) 的提交 `237a6b5e1ae6a14e9dda1aefa4f116d83dfd46e8` 按 GPL-3.0-or-later 许可，仅作架构阅读；本产品未纳入其代码、组件或素材，不把它描述为本包依赖。若后续纳入 GPL 覆盖的代码，须在分发前重新处理相应源码提供与 GPL 许可义务。

## 系统组件与自有图标

程序使用 Windows 自带的 .NET Framework、WPF、Windows Forms、Win32 与 UI Automation；本包不再分发 Windows DLL、Segoe 系统字体、第三方 NuGet 或浏览器运行时。少量控件图标按系统字体名称调用 Windows 11 自带 Segoe Fluent Icons，未提取或捆绑该字体。

应用额度卡片图标为项目自行绘制的几何图形，不含 Microsoft、OpenAI 标志或第三方图标。Windows、Codex 等名称仅用于说明兼容性，不表示官方关系。
