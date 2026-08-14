# 前台应用 FPS 与岛内冗余功能移除 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在折叠岛状态栏显示当前前台图形应用的真实 FPS，并移除 Windows 通知收纳和折叠歌曲信息的设置及运行时入口。

**Architecture:** 使用随程序部署的 PresentMon 2.4.0 控制台，按前台窗口 PID 采集 `MsBetweenPresents`，在独立 `ForegroundFpsService` 中维护最近一秒样本并发布 `double?` FPS。`LifeIslandWindow` 只负责显示，原有 `SystemTelemetryService` 不作改动。通知与音乐功能通过移除启动、订阅和用户入口停用；历史偏好字段保留，以便旧 JSON 可继续反序列化。

**Tech Stack:** .NET 8、WPF、Windows user32 前台窗口 API、PresentMon 2.4.0 x64、xUnit。

---

## 文件结构

- 创建 `src/ChronoIsle.App/Services/ForegroundFpsService.cs`：PresentMon 子进程生命周期、CSV 行解析、滚动 FPS 计算、快照事件。
- 创建 `src/ChronoIsle.App/Assets/PresentMon/PresentMon-2.4.0-x64.exe`：固定官方 v2.4.0 x64 二进制，SHA-256 为 `efe55aa91d381f425e686c87696965dd6b148e130e34985ef03733980a7480c4`。
- 创建 `src/ChronoIsle.App/Assets/PresentMon/LICENSE.txt`：PresentMon 的 MIT 许可文本。
- 修改 `src/ChronoIsle.App/ChronoIsle.App.csproj`：编译新服务并将二进制和许可证复制到发布目录。
- 修改 `src/ChronoIsle.App/App.xaml.cs`：注册、随遥测启停 FPS 服务，停止通知收纳和媒体会话启动。
- 修改 `src/ChronoIsle.App/Views/LifeIslandWindow.xaml`：在 CPU、内存之间新增 `FpsSummary`。
- 修改 `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs`：显示 FPS；移除通知收纳订阅、歌曲摘要和右键音乐入口。
- 修改 `src/ChronoIsle.App/Views/LifeSettingsWindow.xaml` 与 `.xaml.cs`：移除两个复选框和保存/加载行。
- 创建 `tests/ChronoIsle.Tests/ForegroundFpsServiceTests.cs`：测试 CSV 解析与一秒窗口 FPS 计算。
- 创建 `tests/ChronoIsle.UiTests/ForegroundFpsStatusContractTests.cs`：锁定服务注册、状态栏顺序、动态显示与发布资产。
- 修改 `tests/ChronoIsle.UiTests/SystemStatusUiContractTests.cs`、`MusicLineLyricsContractTests.cs`、`IslandContextMenuContractTests.cs`：将旧通知/音乐展示断言改为移除断言。保留其中用户已有的 `HttpMethod.Get` 未暂存改动。

### Task 1: 固定 PresentMon 资产并建立 FPS 单元测试

**Files:**
- Create: `tests/ChronoIsle.Tests/ForegroundFpsServiceTests.cs`
- Create: `src/ChronoIsle.App/Assets/PresentMon/PresentMon-2.4.0-x64.exe`
- Create: `src/ChronoIsle.App/Assets/PresentMon/LICENSE.txt`
- Modify: `src/ChronoIsle.App/ChronoIsle.App.csproj:37,54-62`

- [ ] **Step 1: 写入失败的 FPS 解析与计算测试**

```csharp
using ChronoIsle.App.Services;

namespace ChronoIsle.Tests;

public sealed class ForegroundFpsServiceTests
{
    [Fact]
    public void Parser_ReadsMsBetweenPresentsFromPresentMonCsv()
    {
        var parser = new PresentMonOutputParser();

        Assert.Null(parser.TryReadMillisecondsBetweenPresents("Application,ProcessID,MsBetweenPresents"));
        Assert.Equal(16.67d, parser.TryReadMillisecondsBetweenPresents("game.exe,42,16.67"));
        Assert.Null(parser.TryReadMillisecondsBetweenPresents("game.exe,42,NA"));
    }

    [Fact]
    public void FrameRate_UsesOnlySamplesFromTheLastSecond()
    {
        var now = DateTimeOffset.UtcNow;
        var samples = new[]
        {
            new PresentMonFrameSample(now.AddMilliseconds(-800), 16.67),
            new PresentMonFrameSample(now.AddMilliseconds(-400), 16.67),
            new PresentMonFrameSample(now.AddMilliseconds(-1200), 8.33)
        };

        Assert.InRange(PresentMonFrameRate.Calculate(samples, now)!.Value, 59.8, 60.2);
    }

    [Fact]
    public void FrameRate_ReturnsNullWhenThereIsNoRecentValidFrame()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.Null(PresentMonFrameRate.Calculate(
            [new PresentMonFrameSample(now.AddSeconds(-2), 16.67)], now));
    }
}
```

- [ ] **Step 2: 运行定向测试并确认红灯**

Run: `dotnet test tests/ChronoIsle.Tests/ChronoIsle.Tests.csproj --filter "FullyQualifiedName~ForegroundFpsServiceTests" -m:1 -nodeReuse:false`

Expected: 编译失败，指出 `PresentMonOutputParser`、`PresentMonFrameSample`、`PresentMonFrameRate` 尚不存在。

- [ ] **Step 3: 下载并验证固定官方二进制与许可**

Run:

```powershell
curl.exe --fail --location --output src\ChronoIsle.App\Assets\PresentMon\PresentMon-2.4.0-x64.exe https://github.com/GameTechDev/PresentMon/releases/download/v2.4.0/PresentMon-2.4.0-x64.exe
curl.exe --fail --location --output src\ChronoIsle.App\Assets\PresentMon\LICENSE.txt https://raw.githubusercontent.com/GameTechDev/PresentMon/v2.4.0/LICENSE.txt
(Get-FileHash src\ChronoIsle.App\Assets\PresentMon\PresentMon-2.4.0-x64.exe -Algorithm SHA256).Hash
```

Expected: 哈希输出为 `EFE55AA91D381F425E686C87696965DD6B148E130E34985EF03733980A7480C4`，且许可证文件包含 `Permission is hereby granted`。

- [ ] **Step 4: 将两个资产作为发布内容复制**

在 `ChronoIsle.App.csproj` 的资源项之后加入：

```xml
<ItemGroup>
  <Content Include="Assets\PresentMon\PresentMon-2.4.0-x64.exe">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
  </Content>
  <Content Include="Assets\PresentMon\LICENSE.txt">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
  </Content>
</ItemGroup>
```

- [ ] **Step 5: 保持红灯测试未提交，进入最小实现**

红灯测试故意无法编译，不能单独提交到 `main`。保留资产、项目文件和测试的工作区改动，待 Task 2 的服务实现使测试转绿后一起提交。

### Task 2: 实现独立的前台 FPS 采集服务

**Files:**
- Create: `src/ChronoIsle.App/Services/ForegroundFpsService.cs`
- Modify: `src/ChronoIsle.App/ChronoIsle.App.csproj:37`
- Test: `tests/ChronoIsle.Tests/ForegroundFpsServiceTests.cs`

- [ ] **Step 1: 实现可独立测试的 CSV 解析与 FPS 计算**

```csharp
public readonly record struct PresentMonFrameSample(
    DateTimeOffset ObservedAtUtc,
    double MillisecondsBetweenPresents);

public sealed class PresentMonOutputParser
{
    int intervalColumn = -1;

    public double? TryReadMillisecondsBetweenPresents(string line)
    {
        var values = line.Split(',');
        if (intervalColumn < 0)
        {
            intervalColumn = Array.FindIndex(values, value =>
                string.Equals(value, "MsBetweenPresents", StringComparison.Ordinal));
            return null;
        }

        return intervalColumn < values.Length &&
               double.TryParse(values[intervalColumn], CultureInfo.InvariantCulture, out var milliseconds) &&
               milliseconds > 0 && double.IsFinite(milliseconds)
            ? milliseconds
            : null;
    }
}

public static class PresentMonFrameRate
{
    public static double? Calculate(IEnumerable<PresentMonFrameSample> samples, DateTimeOffset now)
    {
        var recent = samples.Where(sample =>
            sample.ObservedAtUtc >= now.AddSeconds(-1) &&
            sample.MillisecondsBetweenPresents > 0).ToArray();
        return recent.Length == 0 ? null : 1000d / recent.Average(sample => sample.MillisecondsBetweenPresents);
    }
}
```

- [ ] **Step 2: 添加 `ForegroundFpsService` 的进程与焦点生命周期**

在同一文件定义 `ForegroundFpsSnapshot(int? ProcessId, double? FramesPerSecond)` 和 `ForegroundFpsService : IDisposable`，遵循如下骨架：

```csharp
public void Start() => focusTimer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(250));

async Task SampleForegroundAsync()
{
    var hwnd = GetForegroundWindow();
    GetWindowThreadProcessId(hwnd, out var processId);
    if (processId == Environment.ProcessId || processId == 0)
    {
        StopCapture();
        Publish(null, null);
        return;
    }
    if (processId != capturedProcessId) await StartCaptureAsync((int)processId);
    Publish(capturedProcessId, PresentMonFrameRate.Calculate(frameSamples, DateTimeOffset.UtcNow));
}
```

`StartCaptureAsync` 必须以 `UseShellExecute = false`、`CreateNoWindow = true`、`RedirectStandardOutput = true` 启动 `Path.Combine(AppContext.BaseDirectory, "Assets", "PresentMon", "PresentMon-2.4.0-x64.exe")`，参数为：

```text
--process_id <pid> --output_stdout --no_csv --no_console_stats --session_name ChronoIsleFps-<pid> --stop_existing_session
```

每次切换 PID 先调用 `StopCapture()`：取消读取任务、关闭标准输出、等待短暂退出并仅终止自己启动的 PresentMon 子进程。`SampleForegroundAsync` 必须用 `Interlocked.Exchange` 防止 250 毫秒轮询重入。读取任务对每行调用 `PresentMonOutputParser`，将有效样本加入受锁保护的队列；队列只保留最近一秒。所有启动、读取、终止异常都调用 `Publish(processId, null)`，不向 UI 抛出异常。

- [ ] **Step 3: 把新服务加入显式编译清单**

将 `Services\ForegroundFpsService.cs` 追加到 `ChronoIsle.App.csproj` 现有的显式服务 `Compile Include` 行；不得启用默认编译项。

- [ ] **Step 4: 运行定向单元测试并确认绿灯**

Run: `dotnet test tests/ChronoIsle.Tests/ChronoIsle.Tests.csproj --filter "FullyQualifiedName~ForegroundFpsServiceTests" -m:1 -nodeReuse:false`

Expected: 3 passed, 0 failed。

- [ ] **Step 5: 提交服务实现**

```powershell
git add -- src/ChronoIsle.App/Assets/PresentMon/PresentMon-2.4.0-x64.exe src/ChronoIsle.App/Assets/PresentMon/LICENSE.txt src/ChronoIsle.App/Services/ForegroundFpsService.cs src/ChronoIsle.App/ChronoIsle.App.csproj tests/ChronoIsle.Tests/ForegroundFpsServiceTests.cs
git commit -m "feat: sample foreground application FPS"
```

### Task 3: 显示 FPS 并移除设置和运行时入口

**Files:**
- Create: `tests/ChronoIsle.UiTests/ForegroundFpsStatusContractTests.cs`
- Modify: `src/ChronoIsle.App/App.xaml.cs:39-45,134-175`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml:278-292`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs:45-61,180-260,758-780,1324-1498,812-860`
- Modify: `src/ChronoIsle.App/Views/LifeSettingsWindow.xaml:187-191`
- Modify: `src/ChronoIsle.App/Views/LifeSettingsWindow.xaml.cs:58-63,132-141`
- Modify: `tests/ChronoIsle.UiTests/SystemStatusUiContractTests.cs`
- Modify: `tests/ChronoIsle.UiTests/MusicLineLyricsContractTests.cs`
- Modify: `tests/ChronoIsle.UiTests/IslandContextMenuContractTests.cs`

- [ ] **Step 1: 写入失败的 UI 与移除契约**

创建 `ForegroundFpsStatusContractTests.cs`，读取源码并断言：

```csharp
Assert.Contains("x:Name=\"FpsSummary\"", xaml, StringComparison.Ordinal);
Assert.True(xaml.IndexOf("x:Name=\"CpuUsageSummary\"", StringComparison.Ordinal) <
            xaml.IndexOf("x:Name=\"FpsSummary\"", StringComparison.Ordinal));
Assert.True(xaml.IndexOf("x:Name=\"FpsSummary\"", StringComparison.Ordinal) <
            xaml.IndexOf("x:Name=\"MemoryUsageSummary\"", StringComparison.Ordinal));
Assert.Contains("collection.AddSingleton<ForegroundFpsService>();", app, StringComparison.Ordinal);
Assert.Contains("foregroundFps.Start();", app, StringComparison.Ordinal);
Assert.Contains("FpsSummary.Text = snapshot.FramesPerSecond is { } fps ? $\"FPS {fps:0}\" : \"FPS --\";", island, StringComparison.Ordinal);
Assert.DoesNotContain("x:Name=\"ToastInboxEnabled\"", settingsXaml, StringComparison.Ordinal);
Assert.DoesNotContain("x:Name=\"MediaAutoTakeover\"", settingsXaml, StringComparison.Ordinal);
Assert.DoesNotContain("ToggleMusicMode", island, StringComparison.Ordinal);
Assert.DoesNotContain("StartToastInboxAsync", app, StringComparison.Ordinal);
```

在三个现有契约测试中，逐一把“存在通知窗口/订阅/启动”“存在歌曲卡片/音乐模式”“右键存在 `ToggleMusicMode`”的断言替换为上述入口不存在的断言。不要改动 `SystemStatusUiContractTests.cs` 中现有的 `HttpMethod.Get` 用户未提交改动。

- [ ] **Step 2: 运行 UI 契约测试并确认红灯**

Run: `dotnet test tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj --filter "FullyQualifiedName~ForegroundFpsStatusContractTests|FullyQualifiedName~SystemStatusUiContractTests|FullyQualifiedName~MusicLineLyricsContractTests|FullyQualifiedName~IslandContextMenuContractTests" -m:1 -nodeReuse:false`

Expected: `ForegroundFpsStatusContractTests` 因缺少 `FpsSummary` 和新服务而失败；三个旧契约仍包含已废弃入口而失败。

- [ ] **Step 3: 接入服务并显示 FPS**

在 `App.xaml.cs` 注册服务，并在两个现有遥测启动位置紧邻 `SystemTelemetryService().Start()` 调用 `ForegroundFpsService().Start()`；偏好变更时若 `TelemetryEnabled` 为 `false` 则调用 `ForegroundFpsService().Stop()`。删除 `SystemToastInboxService` 注册、`StartToastInboxAsync()`、岛加载时调用以及偏好变更后的调用；删除 `MediaSessionService.StartAsync()` 和 `AudioSpectrumService.Start()` 的启动块。

在状态栏中按如下顺序定义右侧 DockPanel 子项，确保视觉顺序为“网速、CPU、FPS、内存”：

```xml
<TextBlock x:Name="MemoryUsageSummary" DockPanel.Dock="Right" ... />
<TextBlock x:Name="FpsSummary" DockPanel.Dock="Right"
           Foreground="{DynamicResource Brush.TextSecondary}" FontSize="11"
           Margin="10,0,0,0" VerticalAlignment="Center" TextTrimming="CharacterEllipsis" />
<TextBlock x:Name="CpuUsageSummary" DockPanel.Dock="Right" ... />
```

在 `LifeIslandWindow` 中以一个保存的 `Action<ForegroundFpsSnapshot>` 订阅服务；关闭窗口时先取消订阅，再调用 `foregroundFps.Stop()`。在 `SetIdleSummaryWidgetVisibility` 中以 `TelemetryEnabled` 控制 `FpsSummary.Visibility`，并实现：

```csharp
void UpdateForegroundFpsSummary(ForegroundFpsSnapshot snapshot)
{
    FpsSummary.Text = snapshot.FramesPerSecond is { } fps ? $"FPS {fps:0}" : "FPS --";
}
```

删除 `ShowSystemToast`、`PositionNotificationWindow`、`notificationWindow` 和 `toastInbox` 的字段、构造函数参数、订阅、位置事件与关闭调用。`Refresh()` 删除媒体优先摘要分支，始终调用 `UpdateCollapsedMediaView(null, currentPreferences)`；移除 `IslandQuickAction.ToggleMusicMode` 枚举值、上下文菜单条目、标签、分支和 `ToggleMusicMode()` 方法。保留旧 `IslandShowMusicMode` 偏好字段但不读取它。

- [ ] **Step 4: 删除设置项的加载与保存路径**

从 `LifeSettingsWindow.xaml` 删除这两行：

```xml
<CheckBox x:Name="ToastInboxEnabled" Content="收纳新的 Windows 通知，并在灵动岛显示 5 秒" Margin="0,12,0,0"/>
<CheckBox x:Name="MediaAutoTakeover" Content="播放音乐时在折叠岛显示歌曲信息" Margin="0,14,0,0"/>
```

从构造函数和 `Save_Click` 的 `with` 表达式删除对应的 `IsChecked` 读写。保留 `LifePreferences` 的同名属性，防止旧 JSON 配置读取失败。

- [ ] **Step 5: 运行完整 UI 契约与单元测试**

Run:

```powershell
dotnet test tests/ChronoIsle.Tests/ChronoIsle.Tests.csproj -m:1 -nodeReuse:false
dotnet test tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj -m:1 -nodeReuse:false
git diff --check
```

Expected: 两个测试项目均为 0 failed，`git diff --check` 无输出。若 `SystemStatusUiContractTests.cs` 的用户 `HttpMethod.Get` hunk 仍未提交，确认它仍在工作区且没有被加入暂存区。

- [ ] **Step 6: 仅提交本任务的代码和测试**

暂存前先检查：

```powershell
git diff -- tests/ChronoIsle.UiTests/SystemStatusUiContractTests.cs
git add -p -- tests/ChronoIsle.UiTests/SystemStatusUiContractTests.cs
git diff --cached -- tests/ChronoIsle.UiTests/SystemStatusUiContractTests.cs
```

只选择本任务新增的移除契约 hunks，不选择用户的 `HttpMethod.Head` 到 `HttpMethod.Get` hunk。随后：

```powershell
git add -- src/ChronoIsle.App/App.xaml.cs src/ChronoIsle.App/Views/LifeIslandWindow.xaml src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs src/ChronoIsle.App/Views/LifeSettingsWindow.xaml src/ChronoIsle.App/Views/LifeSettingsWindow.xaml.cs tests/ChronoIsle.UiTests/ForegroundFpsStatusContractTests.cs tests/ChronoIsle.UiTests/MusicLineLyricsContractTests.cs tests/ChronoIsle.UiTests/IslandContextMenuContractTests.cs
git commit -m "feat: show foreground FPS in island status"
```

再单独用 `git add -p` 暂存 `SystemStatusUiContractTests.cs` 的本任务 hunks，并提交：

```powershell
git commit -m "test: retire island notification contracts"
```

### Task 4: 发布替换与真实窗口验收

**Files:**
- Verify: `src/ChronoIsle.App/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/Assets/PresentMon/PresentMon-2.4.0-x64.exe`
- Verify: `src/ChronoIsle.App/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/Assets/PresentMon/LICENSE.txt`

- [ ] **Step 1: 构建、重启并验证发布资产**

Run:

```powershell
scripts\dev-restart.ps1
Get-FileHash src\ChronoIsle.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\Assets\PresentMon\PresentMon-2.4.0-x64.exe -Algorithm SHA256
Get-Process ChronoIsle | Select-Object Id, Path
```

Expected: 发布目录中的哈希为 `EFE55AA91D381F425E686C87696965DD6B148E130E34985EF03733980A7480C4`，且仅有新启动的 `ChronoIsle` 发布程序在运行。

- [ ] **Step 2: 执行真实前台应用验收**

1. 打开持续动画、游戏或 GPU 渲染程序并使其前台，确认状态栏在一秒内从 `FPS --` 更新为整数 FPS。
2. 改变该程序的渲染负载或限帧设置，确认状态栏数值随之变化。
3. 切换到桌面或静态窗口，确认一秒后显示 `FPS --`，没有保留旧应用数值。
4. 打开设置，确认两个指定复选框不再显示；右键折叠岛确认没有“显示音乐模式”；播放音乐或收到 Windows 通知时，折叠岛不再被歌曲卡片或通知卡片替换。

- [ ] **Step 3: 记录验证边界**

若当前账号不在 `Performance Log Users` 组、目标是受保护进程或其渲染 API 无可用事件，记录状态栏的 `FPS --` 结果；这是正确的不可用表示，而不是 FPS 计算失败。

## 计划自检

- 规格中的真实 FPS、不可用显示、状态栏顺序、遥测开关、两项设置和运行时入口移除、旧偏好兼容、测试和发布验收均有对应任务。
- 所有二进制版本、哈希、文件路径、命令和测试类名已固定；没有未定的实现步骤。
- `ForegroundFpsService`、`PresentMonOutputParser`、`PresentMonFrameSample`、`PresentMonFrameRate` 和 `ForegroundFpsSnapshot` 的名称在所有任务中一致。
