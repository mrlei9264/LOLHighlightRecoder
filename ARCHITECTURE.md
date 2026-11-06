# Rift Replay 架构

## 模块

| 模块 | 职责 |
| --- | --- |
| `LolHighlight.Desktop` | WinUI 3 页面、ViewModel、依赖注入。负责设置、录制状态、实时预览、视频库、调色界面与对局结束确认。 |
| `LolHighlight.Recording` | 管理对局、窗口、音频和编码进程，负责高光规则、缓存、导出、磁盘配额和遥测。通过接口与会话快照向界面提供数据。 |
| FFmpeg / ffprobe | 独立子进程。FFmpeg 采集窗口、编码、封装和生成调色成片；ffprobe 读取媒体时长等信息。 |

## 从对局到录像

```text
Riot Live Client Data API → RecordingSessionService → 事件去重、规则判定、遥测
LOL 窗口 + 游戏声音 / 麦克风 → FFmpeg → H.264 / AAC MPEG-TS
                                         ├─ 高光模式 → 内存环 → 按时间窗导出 MP4
                                         └─ 整局模式 → 连续封装为 MP4
两种模式 → 对局元数据 → 直接保存或等待用户确认 → 视频库
```

### 监听与采集

`RecordingSessionService` 串行管理启动与停止。开始监听后，`RiotApiClient` 轮询本机 `127.0.0.1:2999` 的 `/liveclientdata/allgamedata`，读取玩家、队伍、对局状态和事件。事件游标只处理新事件；默认每 100 ms 尝试读取一次，请求串行执行。

`GameWindowLocator` 找到 LOL 对局窗口。FFmpeg 的 `gfxcapture` 使用窗口句柄和 Windows Graphics Capture 采集画面。窗口暂时没有新画面时复用上一帧。游戏声音由 WASAPI 进程回环采集，麦克风单独采集。两路音频通过有界队列送入 FFmpeg，可选混音或分轨。自动编码依次探测硬件编码器，最后回退到软件 x264。

### 两种保存模式

- **事件高光**。`TransportStreamSegmenter` 在关键帧处切分 188 字节 TS 包流，补齐 PAT / PMT。`MemoryRingBuffer` 按时长和字节限制滚动淘汰旧段。`HighlightEvaluator` 匹配事件，`ClipCoordinator` 根据时钟建立前后保留窗口，合并相邻事件并在长高光中分段。`ExportManager` 将所需内存段送入 FFmpeg，用 `-c copy` 封装成 MP4。
- **整局录制**。`ContinuousRecording` 将同一编码流送入另一个 FFmpeg，持续写入碎片 MP4。缓存只用于采集状态检查，整局长度由磁盘空间决定。录制中断后恢复会另建文件。

两种模式都先写 `.part.mp4`，完成封装和元数据后改名。开启「对局结束后确认保存」时，成片先进入 `.pending/<对局编号>`。确认后，选中项移入录像目录，未选项删除；待确认项可在重启后继续处理。磁盘配额只管理已完成的 `highlight_*.mp4` 和 `fullgame_*.mp4`。

### 遥测、预览与调色

- `TelemetryCollector` 将本机 API 返回的快照及增量事件写为 NDJSON。`MetadataFooterService` 将玩家、触发事件和录像实际范围写入 MP4 的 `LHRM` UUID 盒，带压缩和 SHA-256 校验。`TelemetryArchive` 将实际录像时间范围内的原始遥测按块压缩至 `LHT1` UUID 盒，视频库可流式导出为 NDJSON。
- 首页预览复用已编码的录制流。事件通知由桌面层独立显示，并使用 Windows 的窗口采集排除机制。视频库负责异步扫描和缩略图。调色用 Win2D 实时预览 3D LUT，生成时由 FFmpeg 编码为新文件并保留音轨与对局元数据。

## 发布与存储

`build.ps1` 下载并验证固定版本的 FFmpeg，发布自包含的 Windows x64 程序。相同的发布目录分别打成 WiX MSI 和免安装 ZIP。运行时通过 `FfmpegExecutable` 优先找到程序目录下的 `tools/ffmpeg/bin/ffmpeg.exe`，设置页可选择自定义程序。

`AppPaths` 统一解析可写目录。MSI 单独写入 `installed-mode` 标记，程序据此使用 `%LOCALAPPDATA%\RiftReplay`。免安装版使用 EXE 所在目录。两种方式都支持自定义录像目录，正在监听时暂存设置改动，停止后再应用。
