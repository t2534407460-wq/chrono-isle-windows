# Network Speed Gauge Polish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep the speed-test loading motion inside the semicircle, visibly update the needle from each rate snapshot, switch `Mbps`/`Mb/s` labels, and use bundled official platform icons.

**Architecture:** `NetworkSpeedTestService` continues publishing its 250ms snapshots. `LifeIslandWindow` owns display-unit state, the two-segment rate-to-angle mapping, and dash-offset animation. Five official favicons are fetched once, embedded as WPF resources, and rendered by Pack URI without runtime icon loading.

**Tech Stack:** .NET 8, WPF XAML, xUnit, WPF resource Pack URIs, `curl.exe`.

---

## File Structure

- `src/ChronoIsle.App/Assets/platform-*.ico` — five official favicon assets.
- `src/ChronoIsle.App/ChronoIsle.App.csproj` — explicit resource registrations.
- `src/ChronoIsle.App/Resources/Controls.xaml` — remove the five now-unused `Icon.Platform*` Geometry entries.
- `src/ChronoIsle.App/Views/LifeIslandWindow.xaml` — local icon images, dash flow Path, unit button.
- `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs` — unit state, angle helper, dash animations.
- `tests/ChronoIsle.UiTests/NetworkSpeedTestUiContractTests.cs` — visual behavior contracts.

### Task 1: Bundle official platform icon resources

**Files:**

- Create: `src/ChronoIsle.App/Assets/platform-league.ico`
- Create: `src/ChronoIsle.App/Assets/platform-douyin.ico`
- Create: `src/ChronoIsle.App/Assets/platform-jd.ico`
- Create: `src/ChronoIsle.App/Assets/platform-ctrip.ico`
- Create: `src/ChronoIsle.App/Assets/platform-toutiao.ico`
- Modify: `src/ChronoIsle.App/ChronoIsle.App.csproj:55-56`
- Modify: `src/ChronoIsle.App/Resources/Controls.xaml`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml:621-701`
- Test: `tests/ChronoIsle.UiTests/NetworkSpeedTestUiContractTests.cs`

- [ ] **Step 1: Write the failing icon contract**

In `Tools_ProvidesThemedNetworkSpeedTestStructure`, read the project file into `project`, then add:

```csharp
foreach (var platform in new[] { "league", "douyin", "jd", "ctrip", "toutiao" })
{
    Assert.Contains($"Assets/platform-{platform}.ico", island, StringComparison.Ordinal);
    Assert.Contains($"Assets\\platform-{platform}.ico", project, StringComparison.Ordinal);
}
Assert.DoesNotContain("Icon.PlatformLeague", panel, StringComparison.Ordinal);
Assert.DoesNotContain("Icon.PlatformDouyin", panel, StringComparison.Ordinal);
Assert.DoesNotContain("Icon.PlatformJd", panel, StringComparison.Ordinal);
Assert.DoesNotContain("Icon.PlatformCtrip", panel, StringComparison.Ordinal);
Assert.DoesNotContain("Icon.PlatformToutiao", panel, StringComparison.Ordinal);
Assert.DoesNotContain("Icon.PlatformLeague", controls, StringComparison.Ordinal);
```

- [ ] **Step 2: Verify the contract is red**

```powershell
dotnet test tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj --no-restore --filter "FullyQualifiedName~Tools_ProvidesThemedNetworkSpeedTestStructure" -m:1 -nodeReuse:false
```

Expected: FAIL because local resources and image elements do not exist.

- [ ] **Step 3: Retrieve each official favicon once**

```powershell
curl.exe -L --fail --retry 2 -o src/ChronoIsle.App/Assets/platform-league.ico https://lol.qq.com/favicon.ico
curl.exe -L --fail --retry 2 -o src/ChronoIsle.App/Assets/platform-douyin.ico https://douyin.com/favicon.ico
curl.exe -L --fail --retry 2 -o src/ChronoIsle.App/Assets/platform-jd.ico https://jd.com/favicon.ico
curl.exe -L --fail --retry 2 -o src/ChronoIsle.App/Assets/platform-ctrip.ico https://ctrip.com/favicon.ico
curl.exe -L --fail --retry 2 -o src/ChronoIsle.App/Assets/platform-toutiao.ico https://toutiao.com/favicon.ico
Get-Item src/ChronoIsle.App/Assets/platform-*.ico | Select-Object Name,Length
```

Expected: five nonzero-sized files.

- [ ] **Step 4: Embed and render local icon assets**

Add one `Resource` item per file in the project:

```xml
<Resource Include="Assets\platform-league.ico" />
<Resource Include="Assets\platform-douyin.ico" />
<Resource Include="Assets\platform-jd.ico" />
<Resource Include="Assets\platform-ctrip.ico" />
<Resource Include="Assets\platform-toutiao.ico" />
```

Replace every platform-card `Viewbox`/`Path` with its corresponding local image. The League card example is:

```xml
<Image Width="18" Height="18"
       Source="pack://application:,,,/ChronoIsle;component/Assets/platform-league.ico"
       Stretch="Uniform" VerticalAlignment="Center"/>
```

Remove the five `Icon.Platform*` Geometry resources from `Controls.xaml`.

- [ ] **Step 5: Verify the contract is green**

Run the Step 2 command. Expected: PASS.

- [ ] **Step 6: Commit the icon slice**

```powershell
git add src/ChronoIsle.App/Assets/platform-*.ico src/ChronoIsle.App/ChronoIsle.App.csproj src/ChronoIsle.App/Resources/Controls.xaml src/ChronoIsle.App/Views/LifeIslandWindow.xaml tests/ChronoIsle.UiTests/NetworkSpeedTestUiContractTests.cs
git commit -m "feat: bundle official platform icons"
```

### Task 2: Implement C-style semicircle flow and responsive pointer mapping

**Files:**

- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml:564-590`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs:1990-2090`
- Test: `tests/ChronoIsle.UiTests/NetworkSpeedTestUiContractTests.cs`

- [ ] **Step 1: Write the failing animation and mapping contract**

In `NetworkSpeedTest_WiresServiceTabsCancellationAndAnimations`, create `panel` with `NetworkSpeedTestPanel(island)` and add:

```csharp
Assert.Contains("x:Name=\"NetworkSpeedTestSpinnerPath\"", panel, StringComparison.Ordinal);
Assert.Contains("StrokeDashArray=\"2 5\"", panel, StringComparison.Ordinal);
Assert.DoesNotContain("NetworkSpeedTestSpinnerRotation", panel, StringComparison.Ordinal);
Assert.Contains("NetworkSpeedTestSpinnerPath.BeginAnimation(Shape.StrokeDashOffsetProperty", codeBehind, StringComparison.Ordinal);
Assert.Contains("static double GetNetworkSpeedTestGaugeAngle(double rate)", codeBehind, StringComparison.Ordinal);
Assert.Contains("rate <= 100", codeBehind, StringComparison.Ordinal);
Assert.Contains("Math.Log(1 + rate - 100)", codeBehind, StringComparison.Ordinal);
```

- [ ] **Step 2: Verify the contract is red**

```powershell
dotnet test tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj --no-restore --filter "FullyQualifiedName~NetworkSpeedTest_WiresServiceTabsCancellationAndAnimations" -m:1 -nodeReuse:false
```

Expected: FAIL because the current code rotates a transformed path and has one logarithmic scale.

- [ ] **Step 3: Replace transformed rotation with dash offset**

Use the same upper-half geometry as the gauge:

```xml
<Path x:Name="NetworkSpeedTestSpinnerPath" Data="M 20,122 A 98,98 0 0 1 216,122"
      Stroke="{DynamicResource Brush.Accent}" StrokeThickness="3"
      StrokeDashArray="2 5" StrokeStartLineCap="Round" StrokeEndLineCap="Round"/>
```

Use dash-offset animation instead of `RotateTransform`:

```csharp
void StartNetworkSpeedTestSpinner()
{
    if (!SystemParameters.ClientAreaAnimation || networkSpeedTestSpinnerAnimating) return;
    NetworkSpeedTestSpinnerPath.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation
    {
        From = 0,
        To = -14,
        Duration = TimeSpan.FromMilliseconds(900),
        RepeatBehavior = RepeatBehavior.Forever
    });
    networkSpeedTestSpinnerAnimating = true;
}

void StopNetworkSpeedTestSpinner()
{
    NetworkSpeedTestSpinnerPath.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
    NetworkSpeedTestSpinnerPath.StrokeDashOffset = 0;
    networkSpeedTestSpinnerAnimating = false;
}
```

Keep the existing selection/platform phase gate in `UpdateNetworkSpeedTestView`.

- [ ] **Step 4: Add the two-segment angle helper**

Replace the current target expression with `GetNetworkSpeedTestGaugeAngle(rate)` and add:

```csharp
static double GetNetworkSpeedTestGaugeAngle(double rate)
{
    rate = Math.Clamp(rate, 0, 500);
    return rate <= 100
        ? -75 + rate / 100d * 130
        : 55 + Math.Log(1 + rate - 100) / Math.Log(401) * 20;
}
```

Keep the current rendered-angle handoff, but use a 180ms `DoubleAnimation` duration so every 250ms service snapshot produces visible movement.

- [ ] **Step 5: Verify the contract is green**

Run the Step 2 command. Expected: PASS.

- [ ] **Step 6: Commit the animation slice**

```powershell
git add src/ChronoIsle.App/Views/LifeIslandWindow.xaml src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs tests/ChronoIsle.UiTests/NetworkSpeedTestUiContractTests.cs
git commit -m "fix: keep speed gauge animation in semicircle"
```

### Task 3: Add the equivalent speed-unit display switch

**Files:**

- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml:586-603`
- Modify: `src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs:80-100,1960-2040`
- Test: `tests/ChronoIsle.UiTests/NetworkSpeedTestUiContractTests.cs`

- [ ] **Step 1: Write the failing unit-switch contract**

Add these checks across the two existing UI tests:

```csharp
Assert.Contains("x:Name=\"NetworkSpeedTestUnitButton\"", panel, StringComparison.Ordinal);
Assert.Contains("Content=\"Mbps ⇄ Mb/s\"", panel, StringComparison.Ordinal);
Assert.Contains("Click=\"NetworkSpeedTestUnitButton_Click\"", panel, StringComparison.Ordinal);
Assert.Contains("void NetworkSpeedTestUnitButton_Click", codeBehind, StringComparison.Ordinal);
Assert.Contains("NetworkSpeedTestDisplayUnit", codeBehind, StringComparison.Ordinal);
Assert.Contains("\"Mb/s\"", codeBehind, StringComparison.Ordinal);
```

- [ ] **Step 2: Verify the contract is red**

```powershell
dotnet test tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj --no-restore --filter "FullyQualifiedName~NetworkSpeedTestUiContractTests" -m:1 -nodeReuse:false
```

Expected: FAIL because the unit button and unit display state do not exist.

- [ ] **Step 3: Implement display-only unit state and refresh**

Add these members near the other view state:

```csharp
enum NetworkSpeedTestDisplayUnit { Mbps, MbPerSecond }
NetworkSpeedTestDisplayUnit networkSpeedTestDisplayUnit;

string NetworkSpeedTestRateUnit => networkSpeedTestDisplayUnit == NetworkSpeedTestDisplayUnit.Mbps
    ? "Mbps"
    : "Mb/s";
```

Add the secondary button below the phase text:

```xml
<Button x:Name="NetworkSpeedTestUnitButton" Content="Mbps ⇄ Mb/s"
        Style="{StaticResource IslandTab}" Click="NetworkSpeedTestUnitButton_Click"
        FontSize="10" Padding="8,3" Margin="0,5,0,0" HorizontalAlignment="Center"/>
```

Add the event handler:

```csharp
void NetworkSpeedTestUnitButton_Click(object sender, RoutedEventArgs e)
{
    networkSpeedTestDisplayUnit = networkSpeedTestDisplayUnit == NetworkSpeedTestDisplayUnit.Mbps
        ? NetworkSpeedTestDisplayUnit.MbPerSecond
        : NetworkSpeedTestDisplayUnit.Mbps;
    UpdateNetworkSpeedTestView(networkSpeedTest.Current);
}
```

Make `FormatNetworkSpeedTestRate` an instance method, append `NetworkSpeedTestRateUnit`, and set the center gauge unit from the same property. Do not change numeric values.

- [ ] **Step 4: Verify the contracts are green**

Run the Step 2 command. Expected: PASS with two tests.

- [ ] **Step 5: Run scoped checks and publish**

```powershell
dotnet test tests/ChronoIsle.Tests/ChronoIsle.Tests.csproj --no-restore --filter "FullyQualifiedName~NetworkSpeedTestServiceTests" -m:1 -nodeReuse:false
dotnet test tests/ChronoIsle.UiTests/ChronoIsle.UiTests.csproj --no-restore --filter "FullyQualifiedName~NetworkSpeedTestUiContractTests" -m:1 -nodeReuse:false
& .\scripts\dev-restart.ps1
```

Expected: all selected tests pass; Release publish starts a responding `ChronoIsle` process.

Verify deployed DLL identity:

```powershell
$buildDll = 'src\ChronoIsle.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\ChronoIsle.dll'
$publishDll = 'src\ChronoIsle.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\ChronoIsle.dll'
(Get-FileHash -Algorithm SHA256 $buildDll).Hash -eq (Get-FileHash -Algorithm SHA256 $publishDll).Hash
```

Expected: `True`.

- [ ] **Step 6: Commit the unit-switch slice**

```powershell
git add src/ChronoIsle.App/Views/LifeIslandWindow.xaml src/ChronoIsle.App/Views/LifeIslandWindow.xaml.cs tests/ChronoIsle.UiTests/NetworkSpeedTestUiContractTests.cs
git commit -m "feat: switch speed rate display unit"
```

## Plan Self-Review

- Spec coverage: Task 1 replaces all five self-drawn platform symbols; Task 2 supplies the selected C-style half-circle flow and responsive pointer mapping; Task 3 supplies the equivalent-unit switch and release validation.
- Placeholder scan: every code change, test, command, source URL, and commit is explicit.
- Type consistency: `NetworkSpeedTestSpinnerPath`, `GetNetworkSpeedTestGaugeAngle`, `NetworkSpeedTestDisplayUnit`, `NetworkSpeedTestRateUnit`, and `NetworkSpeedTestUnitButton_Click` are defined before later references.
