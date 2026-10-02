<#
.SYNOPSIS
    Builds the HADA installers (MSI) and portable packages (ZIP) for x64 and ARM64.

.DESCRIPTION
    Publishes the service and the tray app self-contained, so the target computer needs no .NET runtime, and packs
    them twice per architecture, into artifacts\installer:
      HADA-<version>-<x64|arm64>.msi            the installer
      HADA-<version>-<x64|arm64>-portable.zip   the same programs, to unpack and run without installing
    Both architectures can be built on either.

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
$version = ([xml](Get-Content (Join-Path $repoRoot 'Directory.Build.props'))).Project.PropertyGroup.Version
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

# What makes a folder a portable copy: this file, next to the service and tray folders. Its text is for whoever opens it.
$portableMarker = @"
This file makes this folder a portable copy of HADA $version.

With it here, HADA keeps everything it writes in the "data" folder next to it, runs its
service as an ordinary program instead of a Windows service, and needs no installation
and no administrator rights. Delete the whole folder to remove it.

Start:   Start HADA.cmd   (or tray\HADA.Tray.exe)
Stop:    Exit, in the menu of the HADA icon in the notification area   (or Stop HADA.cmd)
Update:  stop HADA, replace the "service" and "tray" folders with those of the new
         version, and keep "data".

https://github.com/inowakowski/home-assistant-desktop-app
"@

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

    Write-Host "Packing the portable copy for $arch..." -ForegroundColor Cyan
    $portableRoot = Join-Path $artifacts "portable\$arch\HADA"
    if (Test-Path $portableRoot) {
        Remove-Item $portableRoot -Recurse -Force
    }

    New-Item $portableRoot -ItemType Directory -Force | Out-Null
    Copy-Item (Join-Path $publishRoot 'service') $portableRoot -Recurse
    Copy-Item (Join-Path $publishRoot 'tray') $portableRoot -Recurse
    Set-Content (Join-Path $portableRoot 'HADA.portable') $portableMarker -Encoding UTF8
    Set-Content (Join-Path $portableRoot 'Start HADA.cmd') "@echo off`r`nstart `"`" `"%~dp0tray\HADA.Tray.exe`"" -Encoding ASCII
    Set-Content (Join-Path $portableRoot 'Stop HADA.cmd') "@echo off`r`n`"%~dp0tray\HADA.Tray.exe`" --exit" -Encoding ASCII

    $installerFolder = Join-Path $artifacts 'installer'
    New-Item $installerFolder -ItemType Directory -Force | Out-Null
    $zip = Join-Path $installerFolder "HADA-$version-$arch-portable.zip"
    if (Test-Path $zip) {
        Remove-Item $zip -Force
    }

    # Entry by entry, and not with CreateFromDirectory or Compress-Archive: under Windows PowerShell those write
    # backslashes into the archive, which other unpackers than Windows' own take for parts of the file name.
    # Every entry is below "HADA/", so unpacking gives one folder and not hundreds of files.
    $archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem $portableRoot -Recurse -File) {
            $entry = 'HADA/' + $file.FullName.Substring($portableRoot.Length + 1).Replace('\', '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $file.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally {
        $archive.Dispose()
    }

    Write-Host "Building the installer for $arch..." -ForegroundColor Cyan
    dotnet build (Join-Path $repoRoot 'installer\HADA.Installer.wixproj') -c Release `
        "-p:InstallerPlatform=$arch" "-p:PublishRoot=$publishRoot\" --nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Building the installer for $arch failed."
    }
}

Write-Host ''
Get-ChildItem (Join-Path $artifacts 'installer') -Recurse -Include *.msi, *.zip |
    ForEach-Object { Write-Host ("{0} ({1:N0} MB)" -f $_.FullName, ($_.Length / 1MB)) -ForegroundColor Green }
