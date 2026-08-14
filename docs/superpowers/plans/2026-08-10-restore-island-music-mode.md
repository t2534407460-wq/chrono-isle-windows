# Restore Island Music Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore collapsed-island music mode without restoring lyric settings.

**Architecture:** Reconnect the existing media services and restore their original island branches. Keep lyrics untouched because this correction changes only music-mode removal.

**Tech Stack:** WPF, .NET 8, xUnit static UI contracts.

---

### Task 1: Lock the intended boundary with UI contracts

**Files:**
- Modify: `tests/ChronoIsle.UiTests/ForegroundFpsStatusContractTests.cs`
- Modify: `tests/ChronoIsle.UiTests/MusicLineLyricsContractTests.cs`
- Modify: `tests/ChronoIsle.UiTests/IslandContextMenuContractTests.cs`

- [ ] **Step 1: Require music mode and keep lyrics settings absent**

```csharp
Assert.Contains("x:Name=\"MediaAutoTakeover\"", settings, StringComparison.Ordinal);
Assert.Contains("ToggleMusicMode", island, StringComparison.Ordinal);
Assert.Contains("media.StartAsync();", app, StringComparison.Ordinal);
Assert.DoesNotContain("x:Name=\"LyricsEnabled\"", settings, StringComparison.Ordinal);
```

- [ ] **Step 2: Run the contracts and verify they fail before production changes**

Run: `dotnet test tests\\ChronoIsle.UiTests\\ChronoIsle.UiTests.csproj --no-restore --filter "FullyQualifiedName~ForegroundFpsStatusContractTests|FullyQualifiedName~MusicLineLyricsContractTests|FullyQualifiedName~IslandContextMenuContractTests" -m:1`

Expected: FAIL because the current code omits `MediaAutoTakeover`, `ToggleMusicMode`, and `media.StartAsync()`.

### Task 2: Restore only the existing music-mode path

**Files:**
- Modify: `src/ChronoIsle.App/App.xaml.cs`
- Modify: `src/ChronoIsle.App/Views/LifeSettingsWindow.xaml`
- Modify: `src/ChronoIsle.App/Views/LifeSettingsWindow.xaml.cs`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs`

- [ ] **Step 1: Restore the exact pre-removal integration points**

```csharp
MediaAutoTakeover.IsChecked = savedPreferences.MediaAutoTakeover;
MediaAutoTakeover = MediaAutoTakeover.IsChecked == true,
media.StartAsync();
IslandQuickAction.ToggleMusicMode,
UpdateCollapsedMediaView(collapsedMedia, currentPreferences);
```

- [ ] **Step 2: Keep lyric Settings controls absent**

```csharp
// Do not add LyricsEnabled, LyricsOffsetMs, LyricsService, or lyrics.Refresh() to LifeSettingsWindow.
```

- [ ] **Step 3: Run focused contracts and build**

Run: `dotnet test tests\\ChronoIsle.UiTests\\ChronoIsle.UiTests.csproj --no-build --no-restore --filter "FullyQualifiedName~ForegroundFpsStatusContractTests|FullyQualifiedName~MusicLineLyricsContractTests|FullyQualifiedName~IslandContextMenuContractTests" -m:1`

Expected: PASS.

Run: `dotnet build src\\ChronoIsle.App\\ChronoIsle.App.csproj --no-restore -m:1`

Expected: Build succeeded.

- [ ] **Step 4: Commit the correction**

```powershell
git add -- src/ChronoIsle.App/App.xaml.cs src/ChronoIsle.App/Views/LifeSettingsWindow.xaml src/ChronoIsle.App/Views/LifeSettingsWindow.xaml.cs src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs tests/ChronoIsle.UiTests/ForegroundFpsStatusContractTests.cs tests/ChronoIsle.UiTests/MusicLineLyricsContractTests.cs tests/ChronoIsle.UiTests/IslandContextMenuContractTests.cs
git commit -m "fix: restore island music mode"
```
