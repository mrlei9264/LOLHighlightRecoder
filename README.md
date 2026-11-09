# Rift Replay

Windows 上的英雄联盟录制工具。支持按击杀、多杀、目标物等事件保存高光，也能录制整局。对局结束后可预览并选择保留的片段，视频库支持播放、调色和导出对局数据。

## 一键构建

需要 Windows 10 2004 或更新版本的 x64 系统和 [.NET 8 SDK（x64）](https://dotnet.microsoft.com/download/dotnet/8.0)。克隆仓库后，在命令提示符（CMD）中进入仓库根目录并运行以下命令。

```cmd
build.cmd
```

首次构建需要联网下载 NuGet 依赖、WiX Toolset 和固定版本的 FFmpeg。脚本校验 FFmpeg 压缩包的 SHA-256，并自包含发布 .NET 和 Windows App SDK。两种成品均随附 `ffmpeg.exe` 和 `ffprobe.exe`，无需设置 PATH 或手动配置编码器。

需要下载 FFmpeg 时，CMD 会提示选择线路。`1` 使用 GitHub 官方下载（默认），`2` 使用面向中国大陆网络的 [GH-Proxy 第三方加速](https://gh-proxy.com/docs/github-accelerator)。下载进度条会显示百分比、已下载大小和实时速度，完成时显示平均速度。

也可以运行 `build.cmd global` 或 `build.cmd cn` 直接选定线路。无交互构建默认使用 GitHub 官方下载，有效的本地缓存会直接复用。

| 产物 | 使用方法 |
| --- | --- |
| `build/RiftReplay-Setup-x64.msi` | 双击进入安装向导，可更改安装目录。按系统提示授权，安装后从开始菜单打开 **Rift Replay**。 |
| `build/RiftReplay-Portable-x64.zip` | 解压完整 ZIP 到可写目录，打开 `LolHighlight.Desktop.exe`。 |

## 开始录制

打开程序后点击「启动监听」，进入英雄联盟对局即可录制。在设置页可切换录制模式、选择触发事件、开启麦克风或更改录像目录。游戏进程音频采集需要 Windows Build 20348 或更新版本，旧系统仍可录制画面。

安装版默认将配置、录像和日志保存在 `%LOCALAPPDATA%\RiftReplay`。免安装版将这些数据保存在程序目录。默认录像目录为 `recordings`，日志位于 `.runtime`。

## 架构与第三方组件

- [软件架构与录制原理](ARCHITECTURE.md)
- FFmpeg 的许可证、构建信息与来源记录位于成品的 `tools/ffmpeg`。该发行版启用了 GPL 组件，发布二进制时应同时提供对应源码。[FFmpeg 源码](https://github.com/FFmpeg/FFmpeg/tree/31ba826726) · [FFmpeg 许可说明](https://ffmpeg.org/legal.html)
