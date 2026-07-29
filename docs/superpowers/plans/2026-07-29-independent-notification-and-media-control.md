# Independent Notification and Media Control Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Windows notifications independent from music takeover, fix bidirectional NetEase play/pause, and finish the five-bar waveform behavior.

**Architecture:** Add one focused WPF notification window owned operationally by `LifeIslandWindow` but rendered independently. Keep media routing in `DesktopMusicSessionDetector`, and keep waveform height calculation in `LifeIslandWindow`.

**Tech Stack:** .NET 8, WPF, xUnit, Windows media virtual keys.

---

### Task 1: Fix bidirectional NetEase play/pause

**Files:**
- Modify: `tests/ChronoIsle.Tests/IslandMediaAndStreamingTests.cs`
- Modify: `tests/ChronoIsle.UiTests/MusicLineLyricsContractTests.cs`
- Modify: `src/ChronoIsle.App/Services/DesktopMusicSessionDetector.cs`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs`

- [ ] Add tests asserting NetEase shortcuts are used only for previous/next and play/pause routes to `MediaPlayPause`.
- [ ] Add a UI contract asserting `MediaPlayPause_Click` does not call `UpdateMediaPlayPauseIcons(!current.IsPlaying)`.
- [ ] Run the focused tests and verify they fail for the missing routing behavior.
- [ ] Change `Control` so NetEase previous/next use `SendNetEaseShortcut`, while toggle uses `SendMediaKey(MediaPlayPause)`.
- [ ] Remove the optimistic icon inversion from the click handler.
- [ ] Run the focused tests and verify they pass.

### Task 2: Set five-bar waveform bounds

**Files:**
- Modify: `tests/ChronoIsle.Tests/AudioSpectrumDisplayTests.cs`
- Modify: `tests/ChronoIsle.UiTests/MusicLineLyricsContractTests.cs`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs`

- [ ] Add tests requiring silent heights to equal 4 and all active heights to remain in the range 4–20.
- [ ] Run the waveform tests and verify the current 2px minimum fails.
- [ ] Change XAML initial heights to 4 and keep `Width="4" CornerRadius="2"`.
- [ ] Change `SpectrumDisplayHeights` to produce 4–20px.
- [ ] Run focused waveform tests and verify they pass.

### Task 3: Add the independent notification window

**Files:**
- Create: `src/ChronoIsle.App/Views/IslandNotificationWindow.xaml`
- Create: `src/ChronoIsle.App/Views/IslandNotificationWindow.xaml.cs`
- Modify: `src/ChronoIsle.App/ChronoIsle.App.csproj`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs`
- Modify: `tests/ChronoIsle.UiTests/SystemStatusUiContractTests.cs`

- [ ] Replace old header-toast contracts with contracts for a separate non-activating window, five-second timer, latest-message replacement, and above/below placement.
- [ ] Run the notification contract tests and verify they fail because the window does not exist.
- [ ] Add `IslandNotificationWindow` with a 340px themed card and independent fade/slide timer.
- [ ] Add both new WPF files to the explicit project item lists.
- [ ] Route `ToastReceived` to the window and remove `SystemToastHeader` plus its width/height takeover logic from `LifeIslandWindow`.
- [ ] Reposition the notification window when the island moves or resizes and close it with the island.
- [ ] Run notification contracts and verify they pass.

### Task 4: Verify and deploy

**Files:**
- Verify only: all modified source and tests.

- [ ] Run focused media, waveform, and notification tests.
- [ ] Run `dotnet test tests\ChronoIsle.Tests\ChronoIsle.Tests.csproj -c Release --no-restore -m:1`.
- [ ] Run `dotnet test tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj -c Release --no-restore -m:1` and report unrelated existing failures separately.
- [ ] Stop the current published `ChronoIsle.exe`.
- [ ] Publish with `dotnet publish src\ChronoIsle.App\ChronoIsle.App.csproj -c Release -r win-x64 --self-contained true --no-restore -m:1`.
- [ ] Start the published executable and verify its process path.
- [ ] Validate one real Windows notification while music is playing.

