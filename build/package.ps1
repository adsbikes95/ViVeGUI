# Builds portable ViVeTool Studio and ViVeTool packages for Windows x64 and ARM64,
# with license notices, a source-code offer and SHA-256 checksums. Builds are unsigned.
param([string]$OutputDirectory = 'artifacts', [string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$version = ([xml](Get-Content 'src/Directory.Build.props')).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$commit = (git rev-parse --short HEAD).Trim()
$staging = Join-Path $root 'artifacts-staging'
Remove-Item -Recurse -Force $staging, $OutputDirectory -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $staging, $OutputDirectory | Out-Null

function Add-Notices([string]$Directory) {
    Copy-Item LICENSE (Join-Path $Directory 'LICENSE.txt') -Force
    Copy-Item THIRD-PARTY-NOTICES.md (Join-Path $Directory 'THIRD-PARTY-NOTICES.md') -Force
    @"
ViVeTool Studio $version (commit $commit)
Corresponding source code (GPL-3.0): https://github.com/adsbikes95/ViVeGUI/tree/$commit
Original project: https://github.com/thebookisclosed/ViVe
These binaries are not code-signed.
"@ | Set-Content (Join-Path $Directory 'SOURCE.txt') -Encoding utf8
}

foreach ($rid in @('win-x64', 'win-arm64')) {
    foreach ($mode in @('framework-dependent', 'self-contained')) {
        $name = "ViVeToolStudio-$version-$rid-$mode"
        $dir = Join-Path $staging $name
        $sc = if ($mode -eq 'self-contained') { 'true' } else { 'false' }
        dotnet publish src/ViVe.Studio/ViVe.Studio.csproj -c $Configuration -r $rid --self-contained $sc -o $dir -p:DebugType=none
        if ($LASTEXITCODE -ne 0) { throw "publish failed for $name" }
        Add-Notices $dir
        Compress-Archive -Path (Join-Path $dir '*') -DestinationPath (Join-Path $OutputDirectory "$name.zip") -Force
    }
}

# ViVeTool CLI: the .NET Framework 4.8.1 build runs on stock Windows 10/11 (AnyCPU, works on x64 and ARM64).
$cliName = "ViVeTool-$version-net481-anycpu"
$cliDir = Join-Path $staging $cliName
dotnet publish ViVeTool/ViVeTool.csproj -c $Configuration -f net481 -o $cliDir -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw 'publish failed for CLI' }
Add-Notices $cliDir
Compress-Archive -Path (Join-Path $cliDir '*') -DestinationPath (Join-Path $OutputDirectory "$cliName.zip") -Force

$sums = Get-ChildItem $OutputDirectory -Filter *.zip | Sort-Object Name | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
$sums | Set-Content (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
$sums | ForEach-Object { Write-Host $_ }
