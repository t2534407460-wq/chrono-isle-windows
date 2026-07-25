# ChronoIsle deploy script.
# Stops the running app, builds Release, stages the hooks binary next to
# ChronoIsle.exe, forces hook reinstall on next launch (so settings.json
# points at the freshly built hooks.exe), and relaunches.

$ErrorActionPreference = 'Stop'

$root      = Split-Path $PSScriptRoot -Parent
$sln       = Join-Path $root 'ChronoIsle.sln'
$config    = 'Release'
$appDir    = Join-Path $root "src\ChronoIsle.App\bin\$config\net8.0-windows"
$appExe    = Join-Path $appDir 'ChronoIsle.exe'
$hooksSrc  = Join-Path $root "src\ChronoIsle.Hooks\bin\$config\net8.0\chrono-isle-hooks.exe"
$hooksDest = Join-Path $appDir 'chrono-isle-hooks.exe'
$manifest  = Join-Path $env:USERPROFILE '.claude\chrono-isle-manifest.claude.json'

# 1. Stop running instance so we can overwrite the binary.
Get-Process -Name ChronoIsle -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Stopping ChronoIsle (PID $($_.Id))..."
    $_.Kill()
    $_.WaitForExit(5000) | Out-Null
}

# 2. Release build.
Write-Host "Building $config..."
& dotnet build $sln -c $config --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)" }

# 3. Stage the entire hooks runtime next to ChronoIsle.exe.
#    .NET 8 apphost (chrono-isle-hooks.exe) needs its sibling .dll/.deps.json/
#    .runtimeconfig.json AND every dependency the App doesn't already pull in
#    (e.g. System.CommandLine.dll plus its localized resource folders).
#    Copying just the .exe leaves a hook that fails silently — claude.exe writes
#    "Could not load file or assembly System.CommandLine" to stderr and the
#    bridge never sees the event.
if (-not (Test-Path $hooksSrc)) { throw "Hooks binary not found at $hooksSrc" }
$hooksSrcDir = Split-Path $hooksSrc -Parent
Write-Host "Staging entire hooks runtime tree from $hooksSrcDir to $appDir"
Get-ChildItem $hooksSrcDir -Force | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $appDir $_.Name) -Recurse -Force
}

# 4. Remove manifest. SetupService.CheckAndAutoInstallAsync skips when it exists,
#    so deleting it forces InstallAsync on next launch and rewrites settings.json
#    with the newly staged hooks.exe path.
if (Test-Path $manifest) {
    Write-Host "Removing manifest to force hook reinstall on launch..."
    Remove-Item $manifest -Force
}

# 5. Launch.
Write-Host "Launching $appExe"
Start-Process $appExe

Write-Host ""
Write-Host "Deploy done. Hooks reinstall asynchronously a moment after launch;"
Write-Host "verify with: Get-Content `$env:USERPROFILE\.claude\settings.json"
