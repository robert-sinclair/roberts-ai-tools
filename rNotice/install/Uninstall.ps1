<#
.SYNOPSIS
    Removes rNotice from this machine.

.DESCRIPTION
    Deletes the install directory, the Add or Remove Programs entry and the Start Menu
    shortcut. Run from an elevated PowerShell prompt.

    Windows calls this script with -Quiet when you uninstall from Settings > Apps.

    Per-user state is left alone unless you pass -Purge. It is small, it lives in each
    user's own profile, and removing other people's data from a machine-wide uninstall
    would be the wrong default.

.PARAMETER InstallDir
    Where rNotice was installed. Read from the registry when not given.

.PARAMETER Purge
    Also delete this user's rNotice data in %LOCALAPPDATA%\rNotice — settings, logs and
    the local store. Only affects the account running the uninstall.

.PARAMETER Quiet
    No prompts, no pauses. Used by Add or Remove Programs.

.EXAMPLE
    .\Uninstall.ps1
.EXAMPLE
    .\Uninstall.ps1 -Purge
#>
[CmdletBinding()]
param(
    [string] $InstallDir,
    [switch] $Purge,
    [switch] $Quiet
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ArpKey    = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\rNotice'
$Shortcut  = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\rNotice.lnk'
$UserData  = Join-Path $env:LOCALAPPDATA 'rNotice'

function Assert-Administrator {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Uninstall.ps1 must be run from an elevated PowerShell prompt (Run as administrator).'
    }
}

Assert-Administrator

if (-not $InstallDir) {
    $entry = Get-ItemProperty -Path $ArpKey -ErrorAction SilentlyContinue
    if ($entry -and $entry.InstallLocation) {
        $InstallDir = $entry.InstallLocation
    }
    else {
        $InstallDir = Join-Path $env:ProgramFiles 'rNotice'
    }
}

Get-Process -Name 'rNotice.Probe' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host 'Closing the running probe...' -ForegroundColor Cyan
    $_ | Stop-Process -Force
}
Start-Sleep -Milliseconds 400

if (Test-Path $Shortcut) {
    Remove-Item $Shortcut -Force
    Write-Host 'Start Menu shortcut removed.' -ForegroundColor Cyan
}

if (Test-Path $ArpKey) {
    Remove-Item $ArpKey -Recurse -Force
    Write-Host 'Add or Remove Programs entry removed.' -ForegroundColor Cyan
}

if (Test-Path $InstallDir) {
    # This script is running from inside the directory it is deleting, so the file is locked.
    # Remove everything else, then hand the directory itself to a detached cmd.exe.
    $self = $MyInvocation.MyCommand.Path
    Get-ChildItem $InstallDir -Force | Where-Object { $_.FullName -ne $self } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

    Start-Process -FilePath 'cmd.exe' `
        -ArgumentList "/c timeout /t 2 /nobreak >nul & rmdir /s /q `"$InstallDir`"" `
        -WindowStyle Hidden
    Write-Host "Install directory removed: $InstallDir" -ForegroundColor Cyan
}

if ($Purge) {
    if (Test-Path $UserData) {
        Remove-Item $UserData -Recurse -Force
        Write-Host "Your rNotice data removed: $UserData" -ForegroundColor Cyan
    }
}
else {
    if (Test-Path $UserData) {
        Write-Host ''
        Write-Host "Your settings and logs were left at $UserData."
        Write-Host 'Re-run with -Purge to remove them. Other users keep their own.'
    }
}

Write-Host ''
Write-Host 'rNotice uninstalled.' -ForegroundColor Green

if (-not $Quiet) {
    Write-Host ''
    Write-Host 'Press any key to close.'
    [void]$Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
}
