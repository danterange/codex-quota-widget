#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$RunId,

    [Parameter(Mandatory = $true)]
    [string]$ReadyPath,

    [Parameter(Mandatory = $true)]
    [string]$PermitPath,

    [switch]$ForceAfterTimeout,

    [switch]$Simulate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$reportDirectory = Split-Path -Path $ManifestPath -Parent
if ([string]::IsNullOrWhiteSpace($reportDirectory)) {
    $reportDirectory = (Get-Location).Path
}
$reportPath = Join-Path -Path $reportDirectory -ChildPath 'restart-report.json'
$events = [Collections.Generic.List[object]]::new()
$report = [ordered]@{
    schema_version = 1
    run_id = $null
    mode = $null
    started_at = (Get-Date).ToUniversalTime().ToString('o')
    state = 'starting'
    events = $events
    recovery = [ordered]@{
        state = 'manual_confirmation_required'
        message = 'Codex Desktop 没有经验证的公开接口来无损自动恢复正在流式生成的回合。'
        active_thread_candidates = @()
    }
}

<#
.SYNOPSIS
Writes the final supervisor report even when the restart module cannot be imported.
#>
function Write-SupervisorReport {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Report,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $directory = Split-Path -Path $Path -Parent
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    if ($null -ne (Get-Command -Name Write-AtomicJson -ErrorAction SilentlyContinue)) {
        Write-AtomicJson -Value $Report -Path $Path
        return
    }

    # 模块导入前的失败仍须可被调用方观察；以独立的原子替换避免读到半截 JSON。
    $temporaryPath = "$Path.$PID.tmp"
    try {
        $json = $Report | ConvertTo-Json -Compress -Depth 30
        [IO.File]::WriteAllText($temporaryPath, $json, [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporaryPath, $Path, $true)
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Wait-ForSupervisorPermit {
    <#
    .SYNOPSIS
    Waits for the parent to authorize this exact run after readiness is proven; without the permit the child exits before requesting any Desktop close.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$PermitPath,

        [Parameter(Mandatory = $true)]
        [string]$RunId,

        [ValidateRange(1, 15)]
        [int]$TimeoutSeconds = 8
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if (Test-Path -LiteralPath $PermitPath -PathType Leaf) {
            try {
                $permit = Get-Content -LiteralPath $PermitPath -Raw -ErrorAction Stop | ConvertFrom-Json -Depth 10
                if ([string]::Equals([string]$permit.run_id, $RunId, [StringComparison]::Ordinal) -and [string]$permit.state -eq 'supervisor_authorized') {
                    return $true
                }
            }
            catch {
                # 父进程以原子替换写许可；短暂读取竞争不应被当作关闭授权。
            }
        }
        Start-Sleep -Milliseconds 150
    } while ((Get-Date) -lt $deadline)

    return $false
}

$modulePath = Join-Path -Path $PSScriptRoot -ChildPath 'CodexDesktopRestart.Common.psm1'
function Request-NormalRootClose {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$RootProcesses,

        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.List[object]]$Events
    )

    foreach ($root in $RootProcesses) {
        $live = Get-LiveCapturedProcesses -CapturedProcesses @($root)
        if ($live.Count -eq 0) {
            Add-RestartReportEvent -Events $Events -Stage 'root_already_exited' -Detail ([pscustomobject]@{ pid = [int]$root.pid })
            continue
        }

        try {
            $process = Get-Process -Id ([int]$root.pid) -ErrorAction Stop
            $requested = $false
            if ($process.MainWindowHandle -ne 0) {
                $requested = $process.CloseMainWindow()
            }
            Add-RestartReportEvent -Events $Events -Stage 'normal_close_requested' -Detail ([pscustomobject]@{
                    pid = [int]$root.pid
                    close_window_message_sent = [bool]$requested
                })
        }
        catch {
            Add-RestartReportEvent -Events $Events -Stage 'normal_close_request_failed' -Detail ([pscustomobject]@{
                    pid = [int]$root.pid
                    reason = '无法向已捕获的根窗口发送关闭请求。'
                })
        }
    }
}

function Wait-ForCapturedProcessExit {
    <#
    .SYNOPSIS
    Waits for every captured package process, not only visible roots, so an old renderer or app-server cannot be mistaken for a fresh Desktop launch.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$CapturedProcesses,

        [ValidateRange(1, 120)]
        [int]$TimeoutSeconds
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $liveProcesses = Get-LiveCapturedProcesses -CapturedProcesses $CapturedProcesses
        if ($liveProcesses.Count -eq 0) {
            return @()
        }
        Start-Sleep -Milliseconds 400
    } while ((Get-Date) -lt $deadline)

    return @(Get-LiveCapturedProcesses -CapturedProcesses $CapturedProcesses)
}

function Stop-ForceEligiblePackageProcesses {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$LiveCapturedProcesses,

        [Parameter(Mandatory = $true)]
        [string]$InstallLocation,

        [Parameter(Mandatory = $true)]
        [System.Collections.Generic.List[object]]$Events
    )

    $eligible = @(Select-ForceEligibleCapturedProcesses -LiveCapturedProcesses $LiveCapturedProcesses -InstallLocation $InstallLocation |
            Sort-Object { [int]$_.captured.depth } -Descending)
    foreach ($entry in $eligible) {
        # Recheck immediately before termination so a PID reuse cannot become a stop target.
        $stillLive = Get-LiveCapturedProcesses -CapturedProcesses @($entry.captured)
        if ($stillLive.Count -eq 0) {
            continue
        }
        Stop-Process -Id ([int]$entry.captured.pid) -Force -ErrorAction Stop
        Add-RestartReportEvent -Events $Events -Stage 'force_closed_exact_package_process' -Detail ([pscustomobject]@{
                pid = [int]$entry.captured.pid
                depth = [int]$entry.captured.depth
                executable_path = [string]$entry.captured.executable_path
            })
    }
}

function Start-ExactDesktopApplication {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Desktop
    )

    $explorerPath = Join-Path -Path $env:WINDIR -ChildPath 'explorer.exe'
    if (-not (Test-Path -LiteralPath $explorerPath -PathType Leaf)) {
        throw '未找到 Windows Explorer，无法用动态 AUMID 拉起 Desktop。'
    }
    Start-Process -FilePath $explorerPath -ArgumentList ('shell:AppsFolder\' + [string]$Desktop.aumid) -WindowStyle Hidden
}

function Wait-ForFreshDesktopRoot {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Desktop,

        [Parameter(Mandatory = $true)]
        [string[]]$PreviousRootKeys,

        [ValidateRange(5, 120)]
        [int]$TimeoutSeconds
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $captures = Get-CodexDesktopProcessCapture -DesktopTarget $Desktop
        $freshRoots = @($captures | Where-Object {
                $_.is_root -and ("{0}|{1}" -f $_.pid, $_.creation_key) -notin $PreviousRootKeys
            })
        if ($freshRoots.Count -gt 0) {
            return $freshRoots
        }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)

    return @()
}

function Wait-ForDesktopHealth {
    param(
        [Parameter(Mandatory = $true)]
        [object]$CodexCommand,

        [ValidateRange(5, 60)]
        [int]$TimeoutSeconds
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $last = $null
    do {
        $last = Get-CodexDesktopHealthStatus -CodexCommand $CodexCommand
        if ($last.available -and $last.cached_chatgpt_login -and $last.auth_credentials_ok -and $last.desktop_running -and $last.desktop_app_server_handshake_ok) {
            return $last
        }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)

    return $last
}

try {
    Import-Module -Name $modulePath -Force -ErrorAction Stop
    Assert-CodexDesktopRestartHost
    $manifest = Read-VerifiedRestartManifest -ManifestPath $ManifestPath
    $report.run_id = [string]$manifest.run_id
    $report.mode = [string]$manifest.mode
    Add-RestartReportEvent -Events $events -Stage 'manifest_verified' -Detail ([pscustomobject]@{ schema_version = $manifest.schema_version })

    $expectedReadyPath = Join-Path -Path $reportDirectory -ChildPath 'supervisor-ready.json'
    $expectedPermitPath = Join-Path -Path $reportDirectory -ChildPath 'supervisor-permit.json'
    if (-not [string]::Equals([string]$manifest.run_id, $RunId, [StringComparison]::Ordinal) -or
        -not [string]::Equals([IO.Path]::GetFullPath($ReadyPath), [IO.Path]::GetFullPath($expectedReadyPath), [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals([IO.Path]::GetFullPath($PermitPath), [IO.Path]::GetFullPath($expectedPermitPath), [StringComparison]::OrdinalIgnoreCase)) {
        throw '监督进程的运行标识、就绪标记或关闭许可路径不匹配，拒绝继续。'
    }

    # 父进程只有在此标记出现后才会写许可；在收到许可前监督器不能触发任何 Desktop 关闭动作。
    Write-AtomicJson -Value ([ordered]@{
            run_id = [string]$manifest.run_id
            state = 'supervisor_ready'
            ready_at = (Get-Date).ToUniversalTime().ToString('o')
        }) -Path $ReadyPath
    if (-not (Wait-ForSupervisorPermit -PermitPath $PermitPath -RunId $RunId)) {
        $report.state = 'supervisor_authorization_not_received'
        Add-RestartReportEvent -Events $events -Stage 'authorization_not_received' -Detail ([pscustomobject]@{ message = '父进程未在限定时间内确认本次关闭授权。' })
        $report.finished_at = (Get-Date).ToUniversalTime().ToString('o')
        Write-SupervisorReport -Report $report -Path $reportPath
        exit 7
    }
    Add-RestartReportEvent -Events $events -Stage 'authorization_received' -Detail ([pscustomobject]@{ run_id = [string]$manifest.run_id })

    $report.recovery.active_thread_candidates = @($manifest.session_snapshot.active_thread_candidates)
    if ($Simulate) {
        if ([string]$manifest.mode -ne 'simulation') {
            throw '模拟监督进程拒绝执行非模拟清单。'
        }
        Add-RestartReportEvent -Events $events -Stage 'simulation_no_host_calls' -Detail ([pscustomobject]@{
                appx_lookup = $false
                process_lookup = $false
                codex_cli = $false
                close = $false
                force_stop = $false
                launch = $false
            })
        Add-RestartReportEvent -Events $events -Stage 'recovery_manifest_recorded' -Detail ([pscustomobject]@{
                active_thread_candidate_count = @($manifest.session_snapshot.active_thread_candidates).Count
                recovery_state = 'manual_confirmation_required'
            })
        $report.state = 'simulated_desktop_relaunched_manifest_ready'
        $report.finished_at = (Get-Date).ToUniversalTime().ToString('o')
        Write-SupervisorReport -Report $report -Path $reportPath
        exit 0
    }

    if ([string]$manifest.mode -ne 'desktop') {
        throw 'Desktop 监督进程拒绝执行未知模式的清单。'
    }
    if (-not [bool]$manifest.desktop.available -or [string]::IsNullOrWhiteSpace([string]$manifest.desktop.executable_path) -or [string]::IsNullOrWhiteSpace([string]$manifest.desktop.aumid)) {
        throw '清单中缺少经过验证的 Desktop 路径或 AUMID。'
    }

    $allCaptured = @($manifest.captured_processes)
    $roots = @($allCaptured | Where-Object { $_.is_root })
    $trustedPackageProcesses = @($allCaptured | Where-Object { $_.trusted_package_process })
    if ($roots.Count -eq 0) {
        throw '清单中没有可验证的 Desktop 根进程。'
    }
    foreach ($root in $roots) {
        if (-not [string]::Equals([string]$root.executable_path, [string]$manifest.desktop.executable_path, [StringComparison]::OrdinalIgnoreCase)) {
            throw '清单根进程路径不匹配，拒绝关闭任何进程。'
        }
    }
    if ($trustedPackageProcesses.Count -eq 0) {
        throw '清单中没有可信的包内进程，拒绝启动新的 Desktop。'
    }

    Add-RestartReportEvent -Events $events -Stage 'session_snapshot_recorded' -Detail ([pscustomobject]@{
            active_thread_candidate_count = @($manifest.session_snapshot.active_thread_candidates).Count
            recovery_state = 'manual_confirmation_required'
        })
    Request-NormalRootClose -RootProcesses $roots -Events $events
    $remainingTrustedProcesses = Wait-ForCapturedProcessExit -CapturedProcesses $trustedPackageProcesses -TimeoutSeconds ([int]$manifest.grace_seconds)
    if ($remainingTrustedProcesses.Count -gt 0) {
        if (-not ($ForceAfterTimeout -and [bool]$manifest.force_after_timeout)) {
            $report.state = 'desktop_close_timeout_needs_force_confirmation'
            Add-RestartReportEvent -Events $events -Stage 'normal_close_timeout' -Detail ([pscustomobject]@{ remaining_trusted_package_process_count = $remainingTrustedProcesses.Count })
            $report.finished_at = (Get-Date).ToUniversalTime().ToString('o')
            Write-SupervisorReport -Report $report -Path $reportPath
            exit 2
        }

        $liveCaptured = Get-LiveCapturedProcesses -CapturedProcesses $allCaptured
        Stop-ForceEligiblePackageProcesses -LiveCapturedProcesses $liveCaptured -InstallLocation ([string]$manifest.desktop.install_location) -Events $events
        $remainingTrustedProcesses = Wait-ForCapturedProcessExit -CapturedProcesses $trustedPackageProcesses -TimeoutSeconds 10
        if ($remainingTrustedProcesses.Count -gt 0) {
            $report.state = 'desktop_force_close_incomplete'
            Add-RestartReportEvent -Events $events -Stage 'force_close_incomplete' -Detail ([pscustomobject]@{ remaining_trusted_package_process_count = $remainingTrustedProcesses.Count })
            $report.finished_at = (Get-Date).ToUniversalTime().ToString('o')
            Write-SupervisorReport -Report $report -Path $reportPath
            exit 3
        }
    }

    $externalDescendants = @(Get-LiveCapturedProcesses -CapturedProcesses $allCaptured | Where-Object { -not [bool]$_.captured.trusted_package_process })
    if ($externalDescendants.Count -gt 0) {
        Add-RestartReportEvent -Events $events -Stage 'external_descendants_retained' -Detail ([pscustomobject]@{
                count = $externalDescendants.Count
                names = @($externalDescendants | ForEach-Object { [string]$_.captured.name } | Sort-Object -Unique)
            })
    }

    $previousRootKeys = @($allCaptured | ForEach-Object { '{0}|{1}' -f $_.pid, $_.creation_key })
    Start-ExactDesktopApplication -Desktop $manifest.desktop
    Add-RestartReportEvent -Events $events -Stage 'desktop_launch_requested' -Detail ([pscustomobject]@{ aumid = [string]$manifest.desktop.aumid })
    $freshRoots = Wait-ForFreshDesktopRoot -Desktop $manifest.desktop -PreviousRootKeys $previousRootKeys -TimeoutSeconds ([int]$manifest.launch_timeout_seconds)
    if ($freshRoots.Count -eq 0) {
        $report.state = 'desktop_launch_timeout'
        $report.finished_at = (Get-Date).ToUniversalTime().ToString('o')
        Write-SupervisorReport -Report $report -Path $reportPath
        exit 4
    }
    Add-RestartReportEvent -Events $events -Stage 'desktop_launch_observed' -Detail ([pscustomobject]@{ fresh_root_count = $freshRoots.Count })

    $codexCommand = Get-CodexCommandDescriptor
    $health = Wait-ForDesktopHealth -CodexCommand $codexCommand -TimeoutSeconds ([Math]::Min([int]$manifest.launch_timeout_seconds, 45))
    $report.desktop_health = $health
    if (-not $health.cached_chatgpt_login) {
        $report.state = 'desktop_relaunched_login_user_action_required'
        Add-RestartReportEvent -Events $events -Stage 'login_user_action_required' -Detail ([pscustomobject]@{ message = '缓存 ChatGPT 登录不可用；SSO、MFA 或通行密钥必须由用户完成。' })
        $report.finished_at = (Get-Date).ToUniversalTime().ToString('o')
        Write-SupervisorReport -Report $report -Path $reportPath
        exit 5
    }
    if (-not ($health.available -and $health.auth_credentials_ok -and $health.desktop_running -and $health.desktop_app_server_handshake_ok)) {
        $report.state = 'desktop_relaunched_health_unverified'
        Add-RestartReportEvent -Events $events -Stage 'desktop_health_unverified' -Detail ([pscustomobject]@{ message = 'Desktop 已观察到启动，但无法在限定时间内验证完整握手。' })
        $report.finished_at = (Get-Date).ToUniversalTime().ToString('o')
        Write-SupervisorReport -Report $report -Path $reportPath
        exit 6
    }

    Add-RestartReportEvent -Events $events -Stage 'desktop_health_verified' -Detail ([pscustomobject]@{ cached_chatgpt_login = $true })
    $report.state = 'desktop_relaunched_manifest_ready'
    $report.finished_at = (Get-Date).ToUniversalTime().ToString('o')
    Write-SupervisorReport -Report $report -Path $reportPath
    exit 0
}
catch {
    $report.state = 'failed'
    $report.error = $_.Exception.Message
    $report.finished_at = (Get-Date).ToUniversalTime().ToString('o')
    try {
        Write-SupervisorReport -Report $report -Path $reportPath
    }
    catch {
        # The caller still receives the nonzero exit code if the report directory itself is unavailable.
    }
    Write-Error $_.Exception.Message
    exit 1
}
