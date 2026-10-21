<#
.SYNOPSIS
    Removes rMic and re-enables any capture device it had disabled.

.DESCRIPTION
    Re-enables every microphone first, on purpose: uninstalling the thing that holds the switch
    must never leave you with a permanently dead microphone. This enables all capture devices
    rMic can see, including any that were already disabled before rMic was installed.

    Run this from an elevated PowerShell prompt.

.PARAMETER InstallDir
    Where rMic was installed. Defaults to %ProgramFiles%\rMic.

.PARAMETER Purge
    Also delete %ProgramData%\rMic (config, state and logs).

.EXAMPLE
    .\Uninstall.ps1
.EXAMPLE
    .\Uninstall.ps1 -Purge
#>
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'rMic'),
    [switch] $Purge
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ServiceName = 'rMic'
$ServiceExe  = Join-Path $InstallDir 'rMic.Service.exe'

function Assert-Administrator {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Uninstall.ps1 must be run from an elevated PowerShell prompt (Run as administrator).'
    }
}

Assert-Administrator

if (Test-Path $ServiceExe) {
    Write-Host 'Re-enabling all microphone devices...' -ForegroundColor Cyan
    & $ServiceExe --enable-all | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Warning 'rMic could not re-enable every capture device. Check Device Manager > Audio inputs and outputs before finishing.'
    }
}
else {
    Write-Warning "$ServiceExe not found, so devices could not be re-enabled automatically. Check Device Manager > Audio inputs and outputs."
}

Get-Process -Name 'rMic.Tray' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host 'Closing the tray icon...' -ForegroundColor Cyan
    $_ | Stop-Process -Force
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Write-Host 'Stopping the service...' -ForegroundColor Cyan
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    Write-Host 'Removing the service registration...' -ForegroundColor Cyan
    & sc.exe delete $ServiceName | Out-Null
}

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty -Path $runKey -Name 'rMic Tray' -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -Path $runKey -Name 'rMic Tray'
    Write-Host 'Removed the Windows startup entry.' -ForegroundColor Cyan
}

$startMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\rMic.lnk'
if (Test-Path $startMenu) { Remove-Item $startMenu -Force }

if (Test-Path $InstallDir) {
    # The service process needs a moment to release its image before the files can go.
    Start-Sleep -Seconds 2
    try {
        Remove-Item $InstallDir -Recurse -Force
        Write-Host "Removed $InstallDir." -ForegroundColor Cyan
    }
    catch {
        Write-Warning "Could not remove $InstallDir ($($_.Exception.Message)). Delete it manually after the next reboot."
    }
}

if ($Purge) {
    $dataDir = Join-Path $env:ProgramData 'rMic'
    if (Test-Path $dataDir) {
        Remove-Item $dataDir -Recurse -Force
        Write-Host "Removed $dataDir." -ForegroundColor Cyan
    }
    $userDir = Join-Path $env:AppData 'rMic'
    if (Test-Path $userDir) { Remove-Item $userDir -Recurse -Force }
}

Write-Host ''
Write-Host 'rMic removed.' -ForegroundColor Green
