$ErrorActionPreference = 'Stop'
$logPath = Join-Path $PSScriptRoot 'sync.log'
if ((Test-Path -LiteralPath $logPath) -and (Get-Item -LiteralPath $logPath).Length -gt 2MB) {
    $oldLog = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'sync.previous.log'))
    if ([IO.Path]::GetDirectoryName($oldLog) -ne [IO.Path]::GetFullPath($PSScriptRoot)) { throw 'Unexpected log directory.' }
    Move-Item -LiteralPath $logPath -Destination $oldLog -Force
}
$started = Get-Date
try {
    $lines = & (Join-Path $PSScriptRoot 'ChronoIsle.Knowledge.Sync.exe') --config (Join-Path $PSScriptRoot 'sync-config.json') 2>&1
    $code = $LASTEXITCODE
    Add-Content -LiteralPath $logPath -Value (@($started.ToString('o')) + @($lines) + @("Exit=$code"))
    $statusPath = Join-Path $PSScriptRoot 'status.json'
    $previousSuccess = $null
    if (Test-Path -LiteralPath $statusPath) {
        try { $previousSuccess = (Get-Content -Raw -LiteralPath $statusPath | ConvertFrom-Json).LastSuccess } catch { }
    }
    [ordered]@{ LastRun=$started.ToString('o'); LastSuccess=$(if ($code -eq 0) { (Get-Date).ToString('o') } else { $previousSuccess }); ExitCode=$code } |
        ConvertTo-Json | Set-Content -LiteralPath ($statusPath + '.tmp')
    Move-Item -LiteralPath ($statusPath + '.tmp') -Destination $statusPath -Force
    exit $code
} catch {
    Add-Content -LiteralPath $logPath -Value ($started.ToString('o') + ' 同步任务未完成：' + $_.Exception.GetType().Name)
    exit 1
}
