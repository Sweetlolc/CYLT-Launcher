$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$publishRoot = Join-Path $repoRoot 'artifacts\publish'
$releaseRoot = Join-Path $repoRoot 'release'
$toolRoot = Join-Path $repoRoot 'artifacts\tools'
$wix = Join-Path $toolRoot 'wix.exe'
New-Item -ItemType Directory -Path $publishRoot, $releaseRoot -Force | Out-Null
if (-not (Test-Path -LiteralPath $wix)) {
    dotnet tool install wix --version 6.0.2 --tool-path $toolRoot
    if ($LASTEXITCODE -ne 0) { throw 'WiX installation failed.' }
}
& $wix extension add WixToolset.UI.wixext/6.0.2
if ($LASTEXITCODE -ne 0) { throw 'WiX UI extension installation failed.' }
dotnet publish (Join-Path $repoRoot 'src\Cylt.Launcher\Cylt.Launcher.csproj') -c Release -r win-x64 --self-contained true -o $publishRoot `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '使用说明.txt') -Destination $publishRoot
$licenseRtf = Join-Path $repoRoot 'artifacts\license.rtf'
$license = [IO.File]::ReadAllText((Join-Path $repoRoot 'LICENSE'))
$escaped = $license.Replace('\', '\\').Replace('{', '\{').Replace('}', '\}').Replace("`r", '').Replace("`n", '\par ')
[IO.File]::WriteAllText($licenseRtf, '{\rtf1\ansi\deff0{\fonttbl{\f0 Segoe UI;}}\f0\fs20 ' + $escaped + '}', [Text.Encoding]::ASCII)
$msi = Join-Path $releaseRoot 'CYLT-Launcher-0.1.0-preview-x64.msi'
& $wix build (Join-Path $PSScriptRoot 'Package.wxs') -arch x64 -ext WixToolset.UI.wixext `
    -d "Payload=$publishRoot" -d "LicenseRtf=$licenseRtf" -o $msi -pdbtype none
if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }
$hash = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $releaseRoot 'SHA256SUMS.txt'), "$hash  $([IO.Path]::GetFileName($msi))`n", [Text.Encoding]::ASCII)
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '使用说明.txt') -Destination $releaseRoot
Get-Item -LiteralPath $msi | Select-Object FullName, Length
Get-FileHash -LiteralPath $msi -Algorithm SHA256
