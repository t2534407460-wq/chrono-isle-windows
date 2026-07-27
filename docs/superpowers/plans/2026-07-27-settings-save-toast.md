# Settings Save Toast Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the native settings-save information box with a themed in-window success toast that closes the settings window automatically.

**Architecture:** Add one overlay `Border` to `LifeSettingsWindow.xaml` using existing dynamic theme resources. Convert only `Save_Click` to an asynchronous handler that reveals the toast, disables the footer actions, waits 900ms, and closes the window.

**Tech Stack:** .NET 8, WPF XAML, C#, xUnit UI contract tests

---

### Task 1: Lock the themed save confirmation contract

**Files:**
- Modify: `tests/ChronoIsle.UiTests/SettingsCustomizationContractTests.cs`

- [ ] **Step 1: Replace the native-dialog assertions with the desired toast contract**

```csharp
Assert.Contains("x:Name=\"SaveSuccessToast\"", xaml, StringComparison.Ordinal);
Assert.Contains("Background=\"{DynamicResource Brush.Surface}\"", xaml, StringComparison.Ordinal);
Assert.Contains("BorderBrush=\"{DynamicResource Brush.Accent}\"", xaml, StringComparison.Ordinal);
Assert.Contains("Text=\"设置已保存\"", xaml, StringComparison.Ordinal);
Assert.Contains("async void Save_Click", saveHandler, StringComparison.Ordinal);
Assert.DoesNotContain("System.Windows.MessageBox.Show(", saveHandler, StringComparison.Ordinal);
Assert.Contains("SaveSuccessToast.Visibility = Visibility.Visible;", saveHandler, StringComparison.Ordinal);
Assert.Contains("await Task.Delay(900);", saveHandler, StringComparison.Ordinal);
```

- [ ] **Step 2: Run the focused test and verify it fails on the old native dialog**

Run:

```powershell
$env:DOTNET_CLI_USE_MSBUILD_SERVER='0'
dotnet test tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj --no-restore --nologo -m:1 -p:UseSharedCompilation=false -nr:false --filter "FullyQualifiedName~SettingsCustomizationContractTests.Settings_SaveConfirmsSuccessAndClosesTheWindow"
```

Expected: FAIL because `SaveSuccessToast` and the asynchronous toast flow do not exist.

### Task 2: Add the themed toast and automatic close

**Files:**
- Modify: `src/ChronoIsle.App/Views/LifeSettingsWindow.xaml`
- Modify: `src/ChronoIsle.App/Views/LifeSettingsWindow.xaml.cs`

- [ ] **Step 1: Name the footer buttons and add the overlay card**

```xml
<Button x:Name="CancelButton" Content="取消" Click="Cancel_Click"/>
<Button x:Name="SaveButton" Content="保存" Click="Save_Click"
        Style="{StaticResource Button.Primary}"/>
<Border x:Name="SaveSuccessToast"
        Background="{DynamicResource Brush.Surface}"
        BorderBrush="{DynamicResource Brush.Accent}"
        Visibility="Collapsed" Opacity="0">
  <TextBlock Text="设置已保存"/>
</Border>
```

The final XAML also includes the approved accent-soft check icon, “更改已应用” subtitle, 14px corner radius, padding, placement above the footer, and the existing project shadow treatment.

- [ ] **Step 2: Replace the information box with the minimal asynchronous toast flow**

```csharp
async void Save_Click(object sender, RoutedEventArgs e)
{
    // Existing persistence and refresh operations remain unchanged.
    SaveButton.IsEnabled = false;
    CancelButton.IsEnabled = false;
    SaveSuccessToast.Visibility = Visibility.Visible;
    SaveSuccessToast.BeginAnimation(
        UIElement.OpacityProperty,
        new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    await Task.Delay(900);
    if (IsLoaded) Close();
}
```

- [ ] **Step 3: Run the focused test and verify it passes**

Run the focused command from Task 1.

Expected: PASS.

### Task 3: Verify, publish, and restart

**Files:**
- Verify all files modified above

- [ ] **Step 1: Run both complete test projects**

```powershell
dotnet test tests\ChronoIsle.Tests\ChronoIsle.Tests.csproj --no-restore --nologo -m:1 -p:UseSharedCompilation=false -nr:false
dotnet test tests\ChronoIsle.UiTests\ChronoIsle.UiTests.csproj --no-restore --nologo -m:1 -p:UseSharedCompilation=false -nr:false
```

Expected: all tests pass.

- [ ] **Step 2: Check the working diff**

```powershell
git diff --check
```

Expected: exit code 0.

- [ ] **Step 3: Stop the currently running worktree test build, overwrite the same publish directory, and restart**

```powershell
dotnet publish src\ChronoIsle.App\ChronoIsle.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:UseSharedCompilation=false --no-restore --nologo -m:1 -nr:false
```

Expected: publish succeeds and the restarted `ChronoIsle.exe` process path points to this worktree's `win-x64\publish` directory.
