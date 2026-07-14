# Stops only the Debug OpenIsland.exe built by this repository, then builds and starts it.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\OpenIsland.App\OpenIsland.App.csproj'
$app = Join-Path $root 'src\OpenIsland.App\bin\Debug\net8.0-windows10.0.19041.0\OpenIsland.exe'

Get-Process -Name OpenIsland -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and [IO.Path]::GetFullPath($_.Path) -eq [IO.Path]::GetFullPath($app) } catch { $false }
} | ForEach-Object {
    Write-Host "Stopping OpenIsland PID $($_.Id)..."
    Stop-Process -Id $_.Id -ErrorAction Stop
    if (-not $_.WaitForExit(5000)) { throw "OpenIsland did not exit within 5 seconds." }
}

Write-Host 'Building Debug Island...'
& dotnet build $project -c Debug --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed; application was not started.' }
if (-not (Test-Path -LiteralPath $app)) { throw "Expected executable not found: $app" }
$shortcutPath = Join-Path ([Environment]::GetFolderPath("Desktop")) "Island Test.lnk"
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $app
$shortcut.WorkingDirectory = Split-Path $app -Parent
$icon = Join-Path $root 'src\OpenIsland.App\Assets\face-desktop-v2.ico'
$shortcut.IconLocation = ("{0},0" -f $icon)
$shortcut.Description = "Island test build"
$shortcut.Save()
$legacyShortcutPath = Join-Path ([Environment]::GetFolderPath("Desktop")) "OpenIsland Life Assistant Test.lnk"
if (Test-Path -LiteralPath $legacyShortcutPath) { Remove-Item -LiteralPath $legacyShortcutPath -Force }
Write-Host "Updated desktop shortcut: $shortcutPath"
$process = Start-Process -FilePath $app -PassThru
Write-Host "Started OpenIsland PID $($process.Id): $app"