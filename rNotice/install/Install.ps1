<#
.SYNOPSIS
    Installs rNotice for all users on this machine.

.DESCRIPTION
    Publishes the projects, copies them to %ProgramFiles%\rNotice, registers rNotice in
    Add or Remove Programs, and creates an All Users Start Menu shortcut.

    Run from an elevated PowerShell prompt. Installing is the only step that needs
    administrator rights — rNotice itself runs unelevated, as the signed-in user.

    Re-run this script to upgrade. It stops anything running, replaces the files in place
    and updates the version shown in Add or Remove Programs. Settings and sign-in state
    live in each user's own profile and are never touched.

.PARAMETER InstallDir
    Where the binaries go. Defaults to %ProgramFiles%\rNotice.

.PARAMETER SkipBuild
    Reuse whatever is already in .\staging instead of running dotnet publish.

.PARAMETER SelfContained
    Bundle the .NET runtime so the machine needs nothing pre-installed. Larger output,
    but no dependency on the .NET 8 Desktop Runtime.

.PARAMETER Launch
    Run the probe once when the install finishes.

.EXAMPLE
    .\Install.ps1
.EXAMPLE
    .\Install.ps1 -SelfContained -Launch
#>
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'rNotice'),
    [switch] $SkipBuild,
    [switch] $SelfContained,
    [switch] $Launch
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$AppName     = 'rNotice'
$Publisher   = 'Bravura Security'
$RepoRoot    = Split-Path -Parent $PSScriptRoot
$StagingDir  = Join-Path $RepoRoot 'staging'
$ProbeExe    = Join-Path $InstallDir 'rNotice.Probe.exe'
$UninstallPs = Join-Path $InstallDir 'Uninstall.ps1'
$ArpKey      = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\rNotice'

function Assert-Administrator {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Install.ps1 installs for all users, so it must be run from an elevated PowerShell prompt (Run as administrator).'
    }
}

function Get-ProductVersion {
    # Single source of truth for the version: the csproj, not a constant in this script.
    $csproj = Join-Path $RepoRoot 'src\rNotice.Probe\rNotice.Probe.csproj'
    $xml    = [xml](Get-Content $csproj)
    $value  = $xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $value) { throw "No <Version> found in $csproj." }
    return "$value".Trim()
}

function Invoke-Publish {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'The .NET SDK (dotnet) was not found on PATH. Install the .NET 8 SDK and try again.'
    }

    if (Test-Path $StagingDir) { Remove-Item $StagingDir -Recurse -Force }

    $project = Join-Path $RepoRoot 'src\rNotice.Probe\rNotice.Probe.csproj'
    Write-Host "Publishing $(Split-Path -Leaf $project)..." -ForegroundColor Cyan

    & dotnet publish $project `
        --configuration Release `
        --runtime win-x64 `
        -p:SelfContained=$($SelfContained.IsPresent) `
        --output $StagingDir | Out-Host

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
}

function Stop-ExistingInstall {
    Get-Process -Name 'rNotice.Probe' -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host 'Closing the running probe...' -ForegroundColor Cyan
        $_ | Stop-Process -Force
    }
    # Give Windows a moment to release the file handles before overwriting them.
    Start-Sleep -Milliseconds 400
}

function Install-Files {
    $existing = Get-ItemProperty -Path $ArpKey -Name 'DisplayVersion' -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "Upgrading rNotice $($existing.DisplayVersion) in place..." -ForegroundColor Cyan
    }

    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Write-Host "Copying binaries to $InstallDir..." -ForegroundColor Cyan
    Copy-Item -Path (Join-Path $StagingDir '*') -Destination $InstallDir -Recurse -Force

    # The uninstaller has to live beside the binaries: Add or Remove Programs calls it from
    # there, long after this repo may have moved.
    Copy-Item -Path (Join-Path $PSScriptRoot 'Uninstall.ps1') -Destination $UninstallPs -Force
}

function Register-AddRemovePrograms {
    param([string] $Version)

    Write-Host 'Registering in Add or Remove Programs...' -ForegroundColor Cyan

    $sizeKb = [math]::Round(
        ((Get-ChildItem $InstallDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1KB))

    $parts   = $Version.Split('.')
    $command = "`"$PSHOME\powershell.exe`" -NoProfile -ExecutionPolicy Bypass -File `"$UninstallPs`""

    New-Item -Path $ArpKey -Force | Out-Null

    $values = [ordered]@{
        DisplayName     = $AppName
        DisplayVersion  = $Version
        Publisher       = $Publisher
        InstallLocation = $InstallDir
        DisplayIcon     = $ProbeExe
        UninstallString = $command
        # Lets Windows uninstall without showing a console window.
        QuietUninstallString = "$command -Quiet"
        InstallDate     = (Get-Date -Format 'yyyyMMdd')
        URLInfoAbout    = 'https://github.com/Bravura-Security/roberts-ai-tools'
        EstimatedSize   = $sizeKb
        NoModify        = 1
        NoRepair        = 1
        VersionMajor    = [int]$parts[0]
        VersionMinor    = [int]$parts[1]
    }

    foreach ($name in $values.Keys) {
        $value = $values[$name]
        $type  = if ($value -is [int]) { 'DWord' } else { 'String' }
        New-ItemProperty -Path $ArpKey -Name $name -Value $value -PropertyType $type -Force | Out-Null
    }
}

function Install-Shortcut {
    $startMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\rNotice.lnk'
    $shell     = New-Object -ComObject WScript.Shell
    $shortcut  = $shell.CreateShortcut($startMenu)
    $shortcut.TargetPath       = $ProbeExe
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.Description      = 'rNotice permission probe'
    $shortcut.Save()
    Write-Host "Start Menu shortcut created for all users." -ForegroundColor Cyan
}

Assert-Administrator

$version = Get-ProductVersion

if (-not $SkipBuild) { Invoke-Publish }
if (-not (Test-Path $StagingDir)) { throw "No build output at $StagingDir. Run without -SkipBuild." }

Stop-ExistingInstall
Install-Files
Register-AddRemovePrograms -Version $version
Install-Shortcut

Write-Host ''
Write-Host "rNotice $version installed for all users." -ForegroundColor Green
Write-Host ''
Write-Host "  Location   $InstallDir"
Write-Host "  Uninstall  Settings > Apps > Installed apps > rNotice"
Write-Host ''
Write-Host 'This build is a read-only permission probe. It writes nothing to your mailbox,'
Write-Host 'calendar or OneDrive. Run it as yourself, not from this elevated prompt:'
Write-Host ''
Write-Host "  & '$ProbeExe'"
Write-Host ''

if ($Launch) {
    # Launch via Explorer so the probe runs as the signed-in user rather than inheriting this
    # elevated token. Signing in as an administrator would cache the wrong account.
    Write-Host 'Starting the probe as the signed-in user...' -ForegroundColor Cyan
    Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$ProbeExe`""
}
