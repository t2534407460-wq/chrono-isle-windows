# 网易云托盘控制修复 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 网易云音乐隐藏到托盘后，上一首和下一首始终走桌面媒体键，并且播放/暂停图标不再被 3 秒预测状态错误覆盖。

**Architecture:** 保留现有 `DesktopMusicSessionDetector` 标准媒体键发送器，只修正 `MediaSessionService` 的来源路由和状态发布策略。中文来源“网易云音乐”与 `cloudmusic` 一样强制走桌面媒体键；桌面媒体键发送后不发布乐观播放状态，交给随后的真实媒体刷新更新图标。

**Tech Stack:** .NET 8、WPF、xUnit、Windows 媒体键

---

## 文件范围

- 修改 `src/ChronoIsle.App/Services/MediaSessionService.cs`
  - 识别中文网易云来源。
  - 桌面媒体键不启用 3 秒播放状态预测。
- 修改 `tests/ChronoIsle.Tests/IslandMediaAndStreamingTests.cs`
  - 固定中文网易云来源的路由行为。
- 修改 `tests/ChronoIsle.Tests/MediaPlaybackStateTests.cs`
  - 固定桌面/原生媒体控制各自的状态预测行为。
- 不修改媒体键发送 API、XAML、封面、歌词、曲目信息和现有无关改动。
- 当前工作区已有用户改动，本任务不暂存、不提交。

### Task 1: 为两个回归场景建立 RED 测试

**Files:**
- Test: `tests/ChronoIsle.Tests/IslandMediaAndStreamingTests.cs`
- Test: `tests/ChronoIsle.Tests/MediaPlaybackStateTests.cs`

- [x] **Step 1: 增加中文网易云来源路由用例**

在 `MediaControl_PrefersGlobalMediaKeyForDesktopPlayers` 的数据中加入：

```csharp
[InlineData("网易云音乐", true)]
```

- [x] **Step 2: 把播放状态预测测试区分为桌面媒体键和原生媒体会话**

将测试改为：

```csharp
[Theory]
[InlineData(true, "TogglePlayPause", false)]
[InlineData(false, "TogglePlayPause", true)]
[InlineData(false, "Previous", false)]
[InlineData(false, "Next", false)]
public void OptimisticPlaybackState_IsLimitedToNativeToggleCommands(
    bool usesDesktopMediaKey,
    string commandName,
    bool expected)
{
    var command = Enum.Parse<DesktopMediaCommand>(commandName);
    Assert.Equal(
        expected,
        MediaSessionService.ShouldPublishOptimisticPlaybackToggle(
            usesDesktopMediaKey,
            command));
}
```

- [x] **Step 3: 运行定向测试并确认 RED**

```powershell
$env:MSBUILDDISABLENODEREUSE='1'
dotnet test tests/ChronoIsle.Tests/ChronoIsle.Tests.csproj -c Release --no-restore -p:NuGetAudit=false -p:BuildInParallel=false -m:1 -nodeReuse:false --filter "FullyQualifiedName~MediaControl_PrefersGlobalMediaKeyForDesktopPlayers|FullyQualifiedName~OptimisticPlaybackState_IsLimitedToNativeToggleCommands" --logger "console;verbosity=minimal"
```

预期：中文来源断言失败，并且新的两参数方法调用因当前签名不匹配而编译失败。先修复编译测试签名后，至少应看到桌面媒体键预测期望与当前行为不一致。

### Task 2: 实施最小路由和状态修复

**Files:**
- Modify: `src/ChronoIsle.App/Services/MediaSessionService.cs`

- [x] **Step 1: 给预测判断恢复桌面媒体键参数**

```csharp
internal static bool ShouldPublishOptimisticPlaybackToggle(
    bool usesDesktopMediaKey,
    DesktopMediaCommand command) =>
    !usesDesktopMediaKey &&
    command == DesktopMediaCommand.TogglePlayPause;
```

两处调用均传入现有局部变量 `usesDesktopMediaKey`。

- [x] **Step 2: 补齐中文网易云来源路由**

```csharp
static bool ShouldPreferDesktopMediaKey(string? sourceAppId) =>
    !string.IsNullOrWhiteSpace(sourceAppId) &&
    (sourceAppId.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase) ||
     sourceAppId.Contains("网易云音乐", StringComparison.OrdinalIgnoreCase) ||
     sourceAppId.Contains("qqmusic", StringComparison.OrdinalIgnoreCase));
```

- [x] **Step 3: 运行定向测试并确认 GREEN**

运行 Task 1 Step 3 的同一命令，预期两个测试组全部通过。

### Task 3: 回归、构建和运行程序验证

**Files:**
- Verify only

- [x] **Step 1: 运行核心测试**

```powershell
$env:MSBUILDDISABLENODEREUSE='1'
dotnet test tests/ChronoIsle.Tests/ChronoIsle.Tests.csproj -c Release --no-restore -p:NuGetAudit=false -p:BuildInParallel=false -m:1 -nodeReuse:false --logger "console;verbosity=minimal"
```

预期：全部通过，0 失败。

- [x] **Step 2: 运行 UI 契约测试**

```powershell
$env:MSBUILDDISABLENODEREUSE='1'
dotnet test tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj -c Release --no-restore -p:NuGetAudit=false -p:BuildInParallel=false -m:1 -nodeReuse:false --logger "console;verbosity=minimal"
```

预期：全部通过，0 失败。

- [x] **Step 3: 执行 Release 自包含发布并重启当前程序**

```powershell
& scripts\dev-restart.ps1
```

预期：发布成功，新的 `ChronoIsle.exe` 启动，进程路径指向 `src/ChronoIsle.App/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/ChronoIsle.exe`。

- [x] **Step 4: 核对变更范围**

```powershell
git diff -- src/ChronoIsle.App/Services/MediaSessionService.cs tests/ChronoIsle.Tests/IslandMediaAndStreamingTests.cs tests/ChronoIsle.Tests/MediaPlaybackStateTests.cs docs/superpowers/plans/2026-08-02-netease-tray-controls.md
git status --short
```

预期：产品修复只涉及媒体服务和两处测试；其余已有工作区改动保持不变。真实网易云托盘切歌与按钮视觉仍需在正在运行的网易云客户端中验收。
