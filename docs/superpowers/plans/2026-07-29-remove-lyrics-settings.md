# Remove Legacy Lyrics Settings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 删除设置窗口里已经废弃的歌词开关和歌词校准设置，同时保持现有偏好文件兼容。

**Architecture:** 只收缩设置窗口这一条 UI 与依赖链。偏好模型和歌词服务源码保持不动，以最小变更保留旧数据兼容性。

**Tech Stack:** C#、WPF XAML、xUnit、.NET

---

### Task 1: 建立失败契约

**Files:**
- Modify: `tests/ChronoIsle.UiTests/MusicLineLyricsContractTests.cs`
- Modify: `tests/ChronoIsle.UiTests/SettingsWindowDependencyContractTests.cs`

**Step 1: 修改契约**

- 断言设置 XAML 不包含 `LyricsEnabled`、`LyricsOffset`、`LRCLIB` 和“歌词校准”。
- 断言设置窗口代码不包含 `LyricsService` 构造参数、字段和刷新调用。
- 断言音乐模式开关仍然存在。

**Step 2: 验证 RED**

Run:

```powershell
dotnet test tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj -c Release --no-restore -p:NuGetAudit=false -m:1 -nr:false --filter "FullyQualifiedName~MusicLineLyricsContractTests|FullyQualifiedName~SettingsWindowDependencyContractTests" --verbosity minimal
```

Expected: 新契约因当前歌词设置仍存在而失败。

### Task 2: 删除歌词设置

**Files:**
- Modify: `src/ChronoIsle.App/Views/LifeSettingsWindow.xaml`
- Modify: `src/ChronoIsle.App/Views/LifeSettingsWindow.xaml.cs`

**Step 1: 删除 UI**

- 删除 LRCLIB 复选框。
- 删除歌词校准网格。
- 保留 `MediaAutoTakeover` 和 `MoveIslandDuringFullscreen`。

**Step 2: 删除设置窗口绑定**

- 删除 `LyricsEnabled`、`LyricsOffset` 的加载和保存。
- 删除 `LyricsService` 字段、构造参数、赋值和 `Refresh()`。
- 不修改 `LifePreferences` 旧字段及服务注册。

**Step 3: 验证 GREEN**

重复 Task 1 的定向测试，预期全部通过。

### Task 3: 全面验证与本机替换

**Step 1: 测试和构建**

Run:

```powershell
dotnet test tests/ChronoIsle.Tests/ChronoIsle.Tests.csproj -c Release --no-restore -p:NuGetAudit=false -m:1 -nr:false --verbosity minimal
dotnet test tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj -c Release --no-restore -p:NuGetAudit=false -m:1 -nr:false --verbosity minimal
dotnet build ChronoIsle.sln -c Release --no-restore -p:NuGetAudit=false -m:1 -nr:false --verbosity minimal
```

Expected: 全部成功。

**Step 2: 发布并重启**

Run:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/dev-restart.ps1
```

Expected: 新发布产物启动，进程路径指向仓库发布目录。

**Step 3: 检查差异**

- 确认只包含设计、计划、两份契约测试和设置窗口两份源文件。
- 不暂存已有的 `releases` 文件修改。
