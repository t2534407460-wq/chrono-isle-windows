# 音乐波形与播放控制设计

## 目标

- 将折叠态音乐波形从 9 根细白柱改为 5 根较粗柱。
- 移除专辑封面左上角的音乐状态灯。
- 波形颜色实时跟随设置中的主题强调色，并形成相近色的深浅层次。
- 修复网易云音乐上一首、下一首可用，但播放/暂停无法控制的问题。

## 波形设计

- 保留现有专辑封面、歌曲名、歌手和悬浮播放控件布局。
- 删除 `CollapsedMediaStatusLight`，不影响事项、网络等其他状态灯。
- 保留 `CollapsedSpectrum0` 至 `CollapsedSpectrum4`，删除其余四根。
- 每根柱宽为 `4px`，圆角为 `2px`，相邻柱间保持清晰间隔。
- 所有柱条使用动态资源 `Brush.Accent`，通过不透明度
  `0.68 / 0.84 / 1.0 / 0.84 / 0.68` 形成“深—浅—亮—浅—深”层次。
- 主题或强调色实时变化时，波形随动态资源自动更新。
- 保留当前音频驱动算法、最小高度 `2px` 和约 `15.68px` 的最大高度，
  仅把采样映射到 5 根柱，确保五根都会随不同频段变化。

## 播放/暂停控制设计

### 已验证根因

- 本机 `Ctrl+Alt+Left`、`Ctrl+Alt+P`、`Ctrl+Alt+Right` 均已被全局注册。
- 网易云当前未发布可用的 Windows GSMTC 媒体会话。
- 通用媒体播放键未能切换网易云播放状态。
- 现有 `DesktopMusicSessionDetector.Control` 只有在
  `cachedPlayerProcessName == "cloudmusic"` 时才发送 `Ctrl+Alt+P`。
- 当 `MediaSessionService.Current.SourceAppId` 已能识别网易云、但桌面检测器缓存为空时，
  控制会错误回退到无效的通用播放键。通用上一首/下一首键仍可被网易云接受，
  因而产生“只有播放/暂停失效”的现象。

### 修复方式

- `MediaSessionService` 调用桌面控制时把当前 `SourceAppId` 一并传入。
- `DesktopMusicSessionDetector.Control` 优先用传入来源识别网易云，
  再回退到已缓存进程名。
- 来源包含 `cloudmusic` 或“网易云音乐”时，继续使用现有
  `Ctrl+Alt+Left / P / Right` 组合键；QQ 音乐和其他播放器行为保持不变。
- 不新增设置项，不改变上一首/下一首的已工作行为。

## 测试与发布

- 单元测试覆盖 `cloudmusic.exe`、“网易云音乐”和缓存为空的网易云来源路由。
- UI 契约测试确认只有 5 根柱、柱宽和主题资源正确，并确认音乐状态灯已删除。
- 运行现有两个测试项目和 Release 发布。
- 覆盖当前测试版发布目录，结束旧进程并启动新 `ChronoIsle.exe`。
- 核对运行进程路径指向新发布目录；播放/暂停仍需在真实网易云播放状态下验证。

