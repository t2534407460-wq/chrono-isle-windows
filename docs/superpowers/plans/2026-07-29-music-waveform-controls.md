# Music Waveform and Playback Controls Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将折叠态音乐波形改为跟随主题色的五柱样式，并让网易云播放/暂停稳定走其已注册的快捷键。

**Architecture:** 保留现有音频频谱服务与音乐接管布局，只缩减 XAML 柱条数量并使用动态主题资源。控制层把当前媒体来源传入桌面兼容检测器，使网易云路由不再依赖可能为空的内部缓存。

**Tech Stack:** .NET 8、WPF、xUnit、Windows 媒体控制与 Win32 键盘事件。

---

### Task 1: 固定网易云来源路由

**Files:**
- Modify: `tests/ChronoIsle.Tests/IslandMediaAndStreamingTests.cs`
- Modify: `src/ChronoIsle.App/Services/DesktopMusicSessionDetector.cs`
- Modify: `src/ChronoIsle.App/Services/MediaSessionService.cs`

- [ ] **Step 1: 写入失败测试**

在 `NetEaseControl_UsesItsConfiguredGlobalShortcut` 的数据中加入中文来源，并增加来源优先于空缓存的契约：

```csharp
[Theory]
[InlineData("cloudmusic")]
[InlineData("cloudmusic.exe")]
[InlineData("网易云音乐")]
public void NetEaseControl_RecognizesProcessAndPublishedSource(string sourceAppId)
{
    Assert.True(InvokeShouldUseNetEaseShortcut(sourceAppId));
}
```

- [ ] **Step 2: 验证测试红灯**

运行：

```powershell
dotnet test tests/ChronoIsle.Tests/ChronoIsle.Tests.csproj -c Release --filter NetEaseControl_RecognizesProcessAndPublishedSource
```

预期：`网易云音乐` 用例失败，因为当前实现只接受完全等于 `cloudmusic`。

- [ ] **Step 3: 实现最小路由修复**

把控制签名改为接受来源，并优先识别来源：

```csharp
public void Control(DesktopMediaCommand command, string? sourceAppId = null)
{
    if (ShouldUseNetEaseShortcut(sourceAppId) ||
        ShouldUseNetEaseShortcut(cachedPlayerProcessName))
        SendNetEaseShortcut(command);
    else
        SendMediaKey(MediaKeyFor(command));
}

static bool ShouldUseNetEaseShortcut(string? sourceAppId) =>
    !string.IsNullOrWhiteSpace(sourceAppId) &&
    (sourceAppId.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase) ||
     sourceAppId.Contains(Profiles[0].DisplayName, StringComparison.OrdinalIgnoreCase));
```

`MediaSessionService.ControlAsync` 的所有桌面回退调用传入 `Current?.SourceAppId`。

- [ ] **Step 4: 验证路由测试转绿**

运行：

```powershell
dotnet test tests/ChronoIsle.Tests/ChronoIsle.Tests.csproj -c Release --filter "NetEaseControl"
```

预期：全部通过。

### Task 2: 五柱主题色波形

**Files:**
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs`
- Modify: `tests/ChronoIsle.Tests/AudioSpectrumDisplayTests.cs`
- Modify: `tests/ChronoIsle.UiTests/MusicLineLyricsContractTests.cs`

- [ ] **Step 1: 写入失败契约测试**

断言 XAML 只有 `CollapsedSpectrum0` 至 `CollapsedSpectrum4`，每根宽 `4`、
背景为动态 `Brush.Accent`，并且不再包含 `CollapsedMediaStatusLight`。

```csharp
Assert.DoesNotContain("CollapsedSpectrum5", xaml, StringComparison.Ordinal);
Assert.DoesNotContain("CollapsedMediaStatusLight", xaml, StringComparison.Ordinal);
Assert.Equal(5, Regex.Matches(xaml, "x:Name=\"CollapsedSpectrum").Count);
Assert.Contains("Width=\"4\"", xaml, StringComparison.Ordinal);
Assert.Contains("Background=\"{DynamicResource Brush.Accent}\"", xaml, StringComparison.Ordinal);
```

频谱测试把 `barCount` 改为 `5`，继续断言五根高度均会变化且最大值不超过 `15.68`。

- [ ] **Step 2: 验证波形测试红灯**

运行：

```powershell
dotnet test tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj -c Release --filter MusicLineLyricsContractTests
dotnet test tests/ChronoIsle.Tests/ChronoIsle.Tests.csproj -c Release --filter AudioSpectrumDisplayTests
```

预期：UI 契约因仍有 9 根白色细柱和状态灯而失败。

- [ ] **Step 3: 实现五柱 XAML 与映射**

删除状态灯和第 6 至第 9 根柱。五根柱统一：

```xml
<Border x:Name="CollapsedSpectrum0"
        Width="4" Height="2" CornerRadius="2" Margin="2,0"
        Background="{DynamicResource Brush.Accent}" Opacity="0.68"
        VerticalAlignment="Center"/>
```

其余不透明度依次为 `0.84 / 1 / 0.84 / 0.68`。
`CollapsedSpectrumBars()` 只返回五个元素，频谱高度算法保持不变。

- [ ] **Step 4: 验证波形测试转绿**

重复 Step 2 两条命令，预期全部通过。

### Task 3: 回归、发布与启动

**Files:**
- Build output: `src/ChronoIsle.App/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/`

- [ ] **Step 1: 运行完整测试**

```powershell
dotnet test tests/ChronoIsle.Tests/ChronoIsle.Tests.csproj -c Release
dotnet test tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj -c Release
```

预期：零失败。

- [ ] **Step 2: 覆盖生成测试版**

```powershell
dotnet publish src/ChronoIsle.App/ChronoIsle.App.csproj -c Release -r win-x64 --self-contained true
```

预期：发布目录生成新的 `ChronoIsle.exe`。

- [ ] **Step 3: 重启并核对进程**

停止旧测试版进程，启动发布目录中的 `ChronoIsle.exe`，再读取进程路径。

预期：唯一运行中的 `ChronoIsle` 进程路径指向上述发布目录。

- [ ] **Step 4: 手工验证**

- 播放网易云音乐时，播放/暂停按钮能连续切换两次并恢复原状态。
- 上一首、下一首保持可用。
- 波形为五根粗柱，全部随音频变化，颜色跟随主题强调色。
- 专辑封面左上角不再显示音乐状态灯。

