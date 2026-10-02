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
$makePri = Find-SdkTool 'MakePri.exe'
$outputDirectory = Join-Path $env:LOCALAPPDATA 'ChronoIsle\package-identity'
$layoutDirectory = Join-Path $outputDirectory 'layout'
$packagePath = Join-Path $outputDirectory 'ChronoIsle.Identity.msix'
New-Item -ItemType Directory -Force -Path $layoutDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Package.appxmanifest') -Destination (Join-Path $layoutDirectory 'AppxManifest.xml') -Force

# Sparse package logos resolve at the external location, not inside the identity package.
# Use the same checked-in assets that dotnet publish deploys, including for existing installs.
$logoSourceDirectory = Join-Path $PSScriptRoot 'Assets'
foreach ($logoDirectory in @((Join-Path $layoutDirectory 'Assets'), (Join-Path $PublishDirectory 'Assets'))) {
    New-Item -ItemType Directory -Force -Path $logoDirectory | Out-Null
    foreach ($logoName in (Get-ChildItem -LiteralPath $logoSourceDirectory -Filter '*.png' -File).Name) {
        Copy-Item -LiteralPath (Join-Path $logoSourceDirectory $logoName) -Destination (Join-Path $logoDirectory $logoName) -Force
    }
}

# Windows only selects targetsize/altform variants when they are indexed in resources.pri.
$priConfig = Join-Path $outputDirectory 'priconfig.xml'
& $makePri createconfig /cf $priConfig /dq zh-CN /o
if ($LASTEXITCODE -ne 0) { throw 'MakePri did not create its configuration.' }
[xml]$configuration = Get-Content -LiteralPath $priConfig -Raw
foreach ($packaging in @($configuration.SelectNodes('//packaging'))) { $packaging.ParentNode.RemoveChild($packaging) | Out-Null }
$configuration.Save($priConfig)
$priPath = Join-Path $layoutDirectory 'resources.pri'
& $makePri new /pr $layoutDirectory /cf $priConfig /of $priPath /o
if ($LASTEXITCODE -ne 0) { throw 'MakePri did not index the package icons.' }
Copy-Item -LiteralPath $priPath -Destination (Join-Path $PublishDirectory 'resources.pri') -Force
Copy-Item -LiteralPath $priPath -Destination (Join-Path $PSScriptRoot 'resources.pri') -Force

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
# MSIX validates the local-machine Trusted People store. This step needs an elevated shell.
if (-not (Get-ChildItem -Path 'Cert:\LocalMachine\TrustedPeople' | Where-Object Thumbprint -eq $certificate.Thumbprint)) {
    Import-Certificate -FilePath $certificatePath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
}

& $signTool sign /fd SHA256 /s My /sha1 $certificate.Thumbprint $packagePath
if ($LASTEXITCODE -ne 0) { throw 'SignTool did not sign the external-location package.' }

$existingPackage = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
if ($existingPackage) {
    Add-AppxPackage -Path $packagePath -ExternalLocation $PublishDirectory -ForceUpdateFromAnyVersion
}
else {
    Add-AppxPackage -Path $packagePath -ExternalLocation $PublishDirectory
}

$registeredPackage = Get-AppxPackage -Name $packageName -ErrorAction Stop
if (-not $registeredPackage) { throw "Package identity $packageName was not registered." }

Write-Host "Registered $($registeredPackage.PackageFullName) for $PublishDirectory"
