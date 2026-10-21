<#
.SYNOPSIS
    Builds and installs rMic: the LocalSystem service plus the tray client.

.DESCRIPTION
    Publishes both projects, copies them to the install directory, registers and starts the
    rMic service, adds a Start Menu shortcut, and launches the tray icon.

    Run this from an elevated PowerShell prompt.

.PARAMETER InstallDir
    Where the binaries go. Defaults to %ProgramFiles%\rMic.

.PARAMETER SkipBuild
    Reuse whatever is already in .\staging instead of running dotnet publish.

.PARAMETER SelfContained
    Bundle the .NET runtime so the target machine needs nothing pre-installed.
    Larger output, but no dependency on the .NET 8 Desktop Runtime.

.PARAMETER NoAutoStart
    Do not add the tray to this user's Windows startup items.

.EXAMPLE
    .\Install.ps1
.EXAMPLE
    .\Install.ps1 -SelfContained
#>
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'rMic'),
    [switch] $SkipBuild,
    [switch] $SelfContained,
    [switch] $NoAutoStart
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ServiceName = 'rMic'
$RepoRoot    = Split-Path -Parent $PSScriptRoot
$StagingDir  = Join-Path $RepoRoot 'staging'
$TrayExe     = Join-Path $InstallDir 'rMic.Tray.exe'
$ServiceExe  = Join-Path $InstallDir 'rMic.Service.exe'

function Assert-Administrator {
    $identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Install.ps1 must be run from an elevated PowerShell prompt (Run as administrator).'
    }
}

function Invoke-Publish {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'The .NET SDK (dotnet) was not found on PATH. Install the .NET 8 SDK and try again.'
    }

    if (Test-Path $StagingDir) { Remove-Item $StagingDir -Recurse -Force }

    $projects = @(
        (Join-Path $RepoRoot 'src\rMic.Service\rMic.Service.csproj'),
        (Join-Path $RepoRoot 'src\rMic.Tray\rMic.Tray.csproj')
    )

    foreach ($project in $projects) {
        Write-Host "Publishing $(Split-Path -Leaf $project)..." -ForegroundColor Cyan
        & dotnet publish $project `
            --configuration Release `
            --runtime win-x64 `
            -p:SelfContained=$($SelfContained.IsPresent) `
            --output $StagingDir | Out-Host

        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $project (exit $LASTEXITCODE)." }
    }
}

function Stop-ExistingInstall {
    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existing -and $existing.Status -ne 'Stopped') {
        Write-Host "Stopping the existing $ServiceName service..." -ForegroundColor Cyan
        Stop-Service -Name $ServiceName -Force
        $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }

    Get-Process -Name 'rMic.Tray' -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host 'Closing the running tray icon...' -ForegroundColor Cyan
        $_ | Stop-Process -Force
    }
}

function Install-Files {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Write-Host "Copying binaries to $InstallDir..." -ForegroundColor Cyan
    Copy-Item -Path (Join-Path $StagingDir '*') -Destination $InstallDir -Recurse -Force
}

function Register-rMicService {
    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    $binPath  = "`"$ServiceExe`""

    if ($existing) {
        Write-Host 'Updating the existing service registration...' -ForegroundColor Cyan
        & sc.exe config $ServiceName binPath= $binPath start= auto | Out-Null
    }
    else {
        Write-Host 'Registering the rMic service...' -ForegroundColor Cyan
        & sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= 'rMic Microphone Kill Switch' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed (exit $LASTEXITCODE)." }
    }

    & sc.exe description $ServiceName 'Disables and re-enables audio capture devices at the driver level on request from the rMic tray icon.' | Out-Null

    # Come back automatically if the service ever falls over while holding the mic off.
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null

    Write-Host 'Starting the service...' -ForegroundColor Cyan
    Start-Service -Name $ServiceName
}

function Install-Shortcuts {
    $startMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\rMic.lnk'
    $shell     = New-Object -ComObject WScript.Shell
    $shortcut  = $shell.CreateShortcut($startMenu)
    $shortcut.TargetPath       = $TrayExe
    $shortcut.WorkingDirectory = $InstallDir
    $shortcut.Description      = 'rMic microphone kill switch'
    $shortcut.Save()
    Write-Host "Start Menu shortcut created at $startMenu." -ForegroundColor Cyan

    if (-not $NoAutoStart) {
        $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        New-Item -Path $runKey -Force | Out-Null
        Set-ItemProperty -Path $runKey -Name 'rMic Tray' -Value "`"$TrayExe`""
        Write-Host 'Tray icon added to Windows startup for the current user.' -ForegroundColor Cyan
    }
}

function Start-Tray {
    # Launch through Explorer so the tray runs at the shell's integrity level rather than
    # inheriting this elevated prompt's token.
    Write-Host 'Starting the tray icon...' -ForegroundColor Cyan
    Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$TrayExe`""
}

Assert-Administrator

if (-not $SkipBuild) { Invoke-Publish }
if (-not (Test-Path $StagingDir)) { throw "No build output at $StagingDir. Run without -SkipBuild." }

Stop-ExistingInstall
Install-Files
Register-rMicService
Install-Shortcuts
Start-Tray

Write-Host ''
Write-Host 'rMic installed.' -ForegroundColor Green
Write-Host ''
Write-Host 'The tray icon starts hidden in the overflow menu. To pin it to the taskbar:'
Write-Host '  Settings > Personalization > Taskbar > Other system tray icons > rMic > On'
Write-Host ''
Write-Host "Devices it will switch:  & '$ServiceExe' --list"
Write-Host "Log:                     $env:ProgramData\rMic\service.log"
