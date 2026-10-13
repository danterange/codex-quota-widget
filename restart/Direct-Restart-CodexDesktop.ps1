#requires -Version 7.4
[CmdletBinding()]
param(
    [ValidateSet('Desktop', 'Simulate')]
    [string]$Mode = 'Desktop',

    [switch]$Execute,

    [switch]$DirectForce
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$modulePath = Join-Path -Path $PSScriptRoot -ChildPath 'CodexDesktopRestart.Common.psm1'
Import-Module -Name $modulePath -Force -ErrorAction Stop
Assert-CodexDesktopRestartHost

function Write-DirectRestartResult {
    <#
    .SYNOPSIS
    Emits the one machine-readable result that the widget service consumes without exposing local paths, sessions, or diagnostic command output.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [object]$Value
    )

    Write-Output ('[RESULT] ' + ($Value | ConvertTo-Json -Compress -Depth 10))
}

function Wait-ForExactPackageExit {
    <#
    .SYNOPSIS
    Waits only for identities captured before the direct restart, preventing stale package workers from being mistaken for a newly launched Desktop instance.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$CapturedProcesses,

        [ValidateRange(1, 30)]
        [int]$TimeoutSeconds = 10
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $live = @(Get-LiveCapturedProcesses -CapturedProcesses $CapturedProcesses)
        if ($live.Count -eq 0) {
            return @()
        }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)

    return @(Get-LiveCapturedProcesses -CapturedProcesses $CapturedProcesses)
}

function Stop-ExactPackageProcesses {
    <#
    .SYNOPSIS
    Force-stops only captured, identity-rechecked executables under the exact OpenAI.Codex install root; external CLI and terminal descendants never enter this set.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$LiveCapturedProcesses,

        [Parameter(Mandatory = $true)]
        [string]$InstallLocation
    )

    $eligible = @(Select-ForceEligibleCapturedProcesses -LiveCapturedProcesses $LiveCapturedProcesses -InstallLocation $InstallLocation |
            Sort-Object { [int]$_.captured.depth } -Descending)
    foreach ($entry in $eligible) {
        if (@(Get-LiveCapturedProcesses -CapturedProcesses @($entry.captured)).Count -eq 0) {
            continue
        }

        try {
            Stop-Process -Id ([int]$entry.captured.pid) -Force -ErrorAction Stop
        }
        catch {
            # A process that exited after the identity recheck is already in the desired state; any other failure remains a hard stop.
            if (@(Get-LiveCapturedProcesses -CapturedProcesses @($entry.captured)).Count -gt 0) {
                throw
            }
        }
    }
}

function Start-ExactDesktopApplication {
    <#
    .SYNOPSIS
    Opens only the verified current OpenAI.Codex AUMID through Explorer, never a guessed executable path or a codex CLI command.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [object]$Desktop
    )

    $explorerPath = Join-Path -Path $env:WINDIR -ChildPath 'explorer.exe'
    if (-not (Test-Path -LiteralPath $explorerPath -PathType Leaf)) {
        throw 'Verified Windows Explorer launcher is unavailable.'
    }

    Start-Process -FilePath $explorerPath -ArgumentList ('shell:AppsFolder\' + [string]$Desktop.aumid) -WindowStyle Hidden
}

function Wait-ForFreshDesktopRoot {
    <#
    .SYNOPSIS
    Confirms that the launch created a new exact ChatGPT.exe root rather than reusing one captured before force-close.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [object]$Desktop,

        [Parameter(Mandatory = $true)]
        [string[]]$PreviousRootKeys,

        [ValidateRange(5, 45)]
        [int]$TimeoutSeconds = 20
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $freshRoots = @(Get-CodexDesktopProcessCapture -DesktopTarget $Desktop | Where-Object {
                $_.is_root -and ("{0}|{1}" -f $_.pid, $_.creation_key) -notin $PreviousRootKeys
            })
        if ($freshRoots.Count -gt 0) {
            return $freshRoots
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    return @()
}

try {
    if ($DirectForce -and -not $Execute) {
        throw 'Direct force restart requires -Execute.'
    }
    if ($Execute -and -not $DirectForce) {
        throw 'Direct restart requires the explicit -DirectForce confirmation parameter.'
    }
    if ($Mode -eq 'Simulate') {
        Write-DirectRestartResult -Value ([ordered]@{
                state = if ($Execute) { 'direct_desktop_relaunched' } else { 'direct_restart_planned' }
                direct_force = [bool]$DirectForce
                host_calls = $false
                exitCode = 0
            })
        exit 0
    }

    $desktop = Get-CodexDesktopTarget
    if (-not $desktop.available) {
        throw 'Verified OpenAI.Codex Desktop installation is unavailable.'
    }
    $captured = @(Get-CodexDesktopProcessCapture -DesktopTarget $desktop)
    $roots = @($captured | Where-Object { $_.is_root })
    if (-not $Execute) {
        Write-DirectRestartResult -Value ([ordered]@{
                state = 'direct_restart_planned'
                rootProcessCount = $roots.Count
                direct_force = $false
                exitCode = 0
            })
        exit 0
    }

    $previousRootKeys = @($roots | ForEach-Object { '{0}|{1}' -f $_.pid, $_.creation_key })
    $liveCaptured = @(Get-LiveCapturedProcesses -CapturedProcesses $captured)
    Stop-ExactPackageProcesses -LiveCapturedProcesses $liveCaptured -InstallLocation ([string]$desktop.install_location)
    $remaining = Wait-ForExactPackageExit -CapturedProcesses $captured
    if ($remaining.Count -gt 0) {
        Write-DirectRestartResult -Value ([ordered]@{
                state = 'direct_force_close_incomplete'
                remainingPackageProcessCount = $remaining.Count
                exitCode = 3
            })
        exit 3
    }

    Start-ExactDesktopApplication -Desktop $desktop
    $freshRoots = Wait-ForFreshDesktopRoot -Desktop $desktop -PreviousRootKeys $previousRootKeys
    if ($freshRoots.Count -eq 0) {
        Write-DirectRestartResult -Value ([ordered]@{
                state = 'direct_desktop_launch_timeout'
                exitCode = 4
            })
        exit 4
    }

    Write-DirectRestartResult -Value ([ordered]@{
            state = 'direct_desktop_relaunched'
            hadRunningDesktop = ($roots.Count -gt 0)
            freshRootCount = $freshRoots.Count
            exitCode = 0
        })
    exit 0
}
catch {
    Write-DirectRestartResult -Value ([ordered]@{
            state = 'direct_restart_failed'
            exitCode = 1
        })
    exit 1
}
