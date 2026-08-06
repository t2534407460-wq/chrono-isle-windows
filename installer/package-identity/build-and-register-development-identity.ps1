[CmdletBinding()]
param(
    [string]$PublishDirectory
)

$ErrorActionPreference = 'Stop'

$packageName = 'Tr11111.ChronoIsle'
$publisher = 'CN=ChronoIsle Development'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

if (-not $PublishDirectory) {
    $PublishDirectory = Join-Path $root 'src\ChronoIsle.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish'
}

$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
$appPath = Join-Path $PublishDirectory 'ChronoIsle.exe'
if (-not (Test-Path -LiteralPath $appPath)) {
    throw "ChronoIsle publish executable was not found: $appPath"
}

$toolsRoot = Join-Path (Join-Path $env:USERPROFILE '.nuget\packages') 'microsoft.windows.sdk.buildtools\10.0.26100.7463\bin'
function Find-SdkTool([string]$name) {
    $tool = Get-ChildItem -LiteralPath $toolsRoot -Recurse -File -Filter $name |
        Where-Object { $_.DirectoryName.EndsWith('\x64', [StringComparison]::OrdinalIgnoreCase) } |
        Select-Object -First 1
    if (-not $tool) {
        throw "Microsoft.Windows.SDK.BuildTools is missing $name. Run dotnet restore installer\\package-identity\\ChronoIsle.PackageIdentity.Tools.csproj first."
    }

    return $tool.FullName
}

$makeAppx = Find-SdkTool 'MakeAppx.exe'
$signTool = Find-SdkTool 'SignTool.exe'
$outputDirectory = Join-Path $env:LOCALAPPDATA 'ChronoIsle\package-identity'
$layoutDirectory = Join-Path $outputDirectory 'layout'
$packagePath = Join-Path $outputDirectory 'ChronoIsle.Identity.msix'
New-Item -ItemType Directory -Force -Path $layoutDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Package.appxmanifest') -Destination (Join-Path $layoutDirectory 'AppxManifest.xml') -Force

& $makeAppx pack /o /d $layoutDirectory /nv /p $packagePath
if ($LASTEXITCODE -ne 0) { throw 'MakeAppx did not create the external-location package.' }

$certificate = Get-ChildItem -Path 'Cert:\CurrentUser\My' |
    Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(1) } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
if (-not $certificate) {
    $certificate = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature -FriendlyName 'ChronoIsle Development Identity' -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddYears(2)
}

$certificatePath = Join-Path $outputDirectory 'ChronoIsle.Development.cer'
Export-Certificate -Cert $certificate -FilePath $certificatePath -Force | Out-Null
if (-not (Get-ChildItem -Path 'Cert:\CurrentUser\TrustedPeople' | Where-Object Thumbprint -eq $certificate.Thumbprint)) {
    Import-Certificate -FilePath $certificatePath -CertStoreLocation 'Cert:\CurrentUser\TrustedPeople' | Out-Null
}

& $signTool sign /fd SHA256 /s My /sha1 $certificate.Thumbprint $packagePath
if ($LASTEXITCODE -ne 0) { throw 'SignTool did not sign the external-location package.' }

$existingPackage = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
if ($existingPackage) {
    throw "Package identity $packageName is already registered for this user. Remove it before registering this development package again."
}

Add-AppxPackage -Path $packagePath -ExternalLocation $PublishDirectory
$registeredPackage = Get-AppxPackage -Name $packageName -ErrorAction Stop
if (-not $registeredPackage) { throw "Package identity $packageName was not registered." }

Write-Host "Registered $($registeredPackage.PackageFullName) for $PublishDirectory"
