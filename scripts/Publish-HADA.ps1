<#
.SYNOPSIS
    Builds the HADA installers (MSI) for x64 and ARM64.

.DESCRIPTION
    Publishes the service and the tray app self-contained, so the target computer needs no .NET runtime, and packs
    them into one installer per architecture: artifacts\installer\HADA-<version>-<x64|arm64>.msi.
    Both can be built on either architecture.

.EXAMPLE
    .\scripts\Publish-HADA.ps1
    .\scripts\Publish-HADA.ps1 -Platform arm64
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string[]]$Platform = @('x64', 'arm64')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $repoRoot 'artifacts'

foreach ($arch in $Platform) {
    $rid = "win-$arch"
    $publishRoot = Join-Path $artifacts "publish\$rid"
    if (Test-Path $publishRoot) {
        Remove-Item $publishRoot -Recurse -Force
    }

    foreach ($app in 'Service', 'Tray') {
        Write-Host "Publishing HADA.$app for $rid..." -ForegroundColor Cyan
        dotnet publish (Join-Path $repoRoot "src\HADA.$app") -c Release -r $rid --self-contained `
            -o (Join-Path $publishRoot $app.ToLowerInvariant()) --nologo -v quiet
        if ($LASTEXITCODE -ne 0) {
            throw "Publishing HADA.$app for $rid failed."
        }
    }

    Write-Host "Building the installer for $arch..." -ForegroundColor Cyan
    dotnet build (Join-Path $repoRoot 'installer\HADA.Installer.wixproj') -c Release `
        "-p:InstallerPlatform=$arch" "-p:PublishRoot=$publishRoot\" --nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Building the installer for $arch failed."
    }
}

Write-Host ''
Get-ChildItem (Join-Path $artifacts 'installer') -Filter *.msi -Recurse |
    ForEach-Object { Write-Host ("Installer: {0} ({1:N0} MB)" -f $_.FullName, ($_.Length / 1MB)) -ForegroundColor Green }
