# Publishes the current Release build to the established local ChronoIsle path,
# replacing the previous test build before launching it.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\ChronoIsle.App\ChronoIsle.App.csproj'
$deployDir = Join-Path $root 'src\ChronoIsle.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish'
$app = Join-Path $deployDir 'ChronoIsle.exe'
$appBinRoot = [IO.Path]::GetFullPath((Join-Path $root 'src\ChronoIsle.App\bin'))

Get-Process -Name ChronoIsle -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and [IO.Path]::GetFullPath($_.Path).StartsWith($appBinRoot, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
} | ForEach-Object {
    Write-Host "Stopping ChronoIsle PID $($_.Id)..."
    Stop-Process -Id $_.Id -ErrorAction Stop
    if (-not $_.WaitForExit(5000)) { throw "ChronoIsle did not exit within 5 seconds." }
}

Write-Host "Publishing Release ChronoIsle to $deployDir..."
& dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $deployDir --nologo
if ($LASTEXITCODE -ne 0) { throw 'Release publish failed; application was not started.' }
if (-not (Test-Path -LiteralPath $app)) { throw "Expected executable not found: $app" }

$desktop = [Environment]::GetFolderPath('Desktop')
$testShortcutPath = Join-Path $desktop 'Island Test.lnk'
if (Test-Path -LiteralPath $testShortcutPath) { Remove-Item -LiteralPath $testShortcutPath -Force }
$shortcutPath = Join-Path $desktop 'ChronoIsle.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $app
$shortcut.WorkingDirectory = Split-Path $app -Parent
$icon = Join-Path $root 'src\ChronoIsle.App\Assets\face-desktop-v2.ico'
$shortcut.IconLocation = ("{0},0" -f $icon)
$shortcut.Description = '时屿 ChronoIsle'
$shortcut.Save()

$legacyShortcutPaths = @(
    (Join-Path $desktop 'OpenIsland.lnk'),
    (Join-Path $desktop 'OpenIsland Life Assistant Test.lnk')
)
foreach ($legacyShortcutPath in $legacyShortcutPaths) {
    if (Test-Path -LiteralPath $legacyShortcutPath) { Remove-Item -LiteralPath $legacyShortcutPath -Force }
}
Write-Host "Updated desktop shortcut: $shortcutPath"
$process = Start-Process -FilePath $app -PassThru
Write-Host "Started ChronoIsle PID $($process.Id): $app"