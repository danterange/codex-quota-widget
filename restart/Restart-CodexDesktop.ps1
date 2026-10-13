#requires -Version 7.4
[CmdletBinding()]
param(
    [ValidateSet('Desktop', 'Simulate')]
    [string]$Mode = 'Desktop',

    [switch]$Execute,

    [switch]$ForceAfterTimeout,

    [ValidateRange(5, 90)]
    [int]$GraceSeconds = 20,

    [ValidateRange(10, 90)]
    [int]$LaunchTimeoutSeconds = 45,

    [string]$RunRoot,

    [string]$SimulationFixture
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$modulePath = Join-Path -Path $PSScriptRoot -ChildPath 'CodexDesktopRestart.Common.psm1'
$supervisorPath = Join-Path -Path $PSScriptRoot -ChildPath 'Invoke-CodexDesktopRestartSupervisor.ps1'
Import-Module -Name $modulePath -Force -ErrorAction Stop
Assert-CodexDesktopRestartHost

function Write-RestartResult {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Value
    )

    # Product callers parse exactly one final machine-readable line and must not depend on localized prose.
    Write-Output ('[RESULT] ' + ($Value | ConvertTo-Json -Compress -Depth 20))
}
function New-RestartRun {
    <#
    .SYNOPSIS
    Uses the module's safe default run root unless an explicit nonempty test or caller root was supplied.
    #>
    if ([string]::IsNullOrWhiteSpace($RunRoot)) {
        return New-CodexDesktopRestartRun
    }

    return New-CodexDesktopRestartRun -RunRoot $RunRoot
}

function Get-EffectiveGraceSeconds {
    <#
    .SYNOPSIS
    Preserves the full normal-close window on a first attempt, but keeps only a final one-second grace window after the user has already confirmed force-close following a prior timeout.
    #>
    param(
        [ValidateRange(1, 90)]
        [int]$NormalGraceSeconds,

        [bool]$ForceCloseConfirmed
    )

    if ($ForceCloseConfirmed) {
        return 1
    }

    return $NormalGraceSeconds
}

function Wait-ForSupervisorReady {
    <#
    .SYNOPSIS
    Waits for a detached supervisor to validate this exact manifest before the parent reports that any Desktop work is scheduled.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$ReadyPath,

        [Parameter(Mandatory = $true)]
        [string]$RunId,

        [Parameter(Mandatory = $true)]
        [int]$SupervisorPid,

        [ValidateRange(1, 15)]
        [int]$TimeoutSeconds = 8
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if (Test-Path -LiteralPath $ReadyPath -PathType Leaf) {
            try {
                $ready = Get-Content -LiteralPath $ReadyPath -Raw -ErrorAction Stop | ConvertFrom-Json -Depth 10
                if ([string]::Equals([string]$ready.run_id, $RunId, [StringComparison]::Ordinal) -and [string]$ready.state -eq 'supervisor_ready') {
                    return $true
                }
            }
            catch {
                # 监督器以原子替换写标记；短暂的读取竞争应等待下一轮。
            }
        }

        if ($null -eq (Get-Process -Id $SupervisorPid -ErrorAction SilentlyContinue)) {
            return $false
        }
        Start-Sleep -Milliseconds 150
    } while ((Get-Date) -lt $deadline)

    return $false
}

function Start-VerifiedSupervisor {
    <#
    .SYNOPSIS
    Starts the detached supervisor, verifies its run-bound readiness proof, and only then grants the run-bound permit that allows any Desktop close action.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [object]$Run,

        [Parameter(Mandatory = $true)]
        [string]$ManifestPath,

        [switch]$ForceAfterTimeout,

        [switch]$Simulate
    )

    $readyPath = Join-Path -Path ([string]$Run.path) -ChildPath 'supervisor-ready.json'
    $permitPath = Join-Path -Path ([string]$Run.path) -ChildPath 'supervisor-permit.json'
    $supervisor = Start-CodexDesktopRestartSupervisor -SupervisorScript $supervisorPath -ManifestPath $ManifestPath -RunId ([string]$Run.id) -ReadyPath $readyPath -PermitPath $permitPath -ForceAfterTimeout:$ForceAfterTimeout -Simulate:$Simulate
    $isReady = Wait-ForSupervisorReady -ReadyPath $readyPath -RunId ([string]$Run.id) -SupervisorPid ([int]$supervisor.pid)
    if ($isReady) {
        # 只有本次 run 的 ready 标记已验证，父脚本才会写入允许关闭 Desktop 的绑定许可。
        Write-AtomicJson -Value ([ordered]@{
                run_id = [string]$Run.id
                state = 'supervisor_authorized'
                authorized_at = (Get-Date).ToUniversalTime().ToString('o')
            }) -Path $permitPath
    }
    return [pscustomobject]@{
        ready = [bool]$isReady
        pid = [int]$supervisor.pid
        ready_path = $readyPath
        permit_path = $permitPath
    }
}

function Write-SupervisorBootstrapFailure {
    <#
    .SYNOPSIS
    Persists a non-sensitive terminal outcome when the detached child never proves readiness, guaranteeing that no Desktop close was attempted by that child.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [object]$Run,

        [Parameter(Mandatory = $true)]
        [string]$Mode,

        [Parameter(Mandatory = $true)]
        [string]$ReportPath,

        [Parameter(Mandatory = $true)]
        [int]$SupervisorPid
    )

    Write-AtomicJson -Value ([ordered]@{
            schema_version = 1
            run_id = [string]$Run.id
            mode = $Mode
            state = 'supervisor_bootstrap_failed'
            failure_stage = 'bootstrap'
            supervisor_pid = $SupervisorPid
            message = 'Desktop 监督进程未在关闭动作前证明已就绪；现有 Desktop 未被关闭。'
            finished_at = (Get-Date).ToUniversalTime().ToString('o')
        }) -Path $ReportPath
}

function New-SimulationManifest {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Fixture,

        [Parameter(Mandatory = $true)]
        [object]$Run
    )

    if ($null -eq $Fixture.desktop -or $null -eq $Fixture.captured_processes -or $null -eq $Fixture.session_snapshot) {
        throw '模拟夹具缺少 desktop、captured_processes 或 session_snapshot。'
    }

    return [ordered]@{
        schema_version = 1
        run_id = $Run.id
        mode = 'simulation'
        created_at = (Get-Date).ToUniversalTime().ToString('o')
        desktop = $Fixture.desktop
        captured_processes = @($Fixture.captured_processes)
        session_snapshot = $Fixture.session_snapshot
        grace_seconds = Get-EffectiveGraceSeconds -NormalGraceSeconds 5 -ForceCloseConfirmed ([bool]$ForceAfterTimeout)
        launch_timeout_seconds = 5
        force_after_timeout = [bool]$ForceAfterTimeout
        recovery_contract = '检测到的线程只能标记为 interrupted_requires_manual_resume；模拟和桌面重启均不会调用 thread/resume。'
    }
}

try {
    if ($ForceAfterTimeout -and -not $Execute) {
        throw '强制结束只能与 -Execute 一起使用，并且应在用户单独确认后调用。'
    }

    if ($Mode -eq 'Simulate') {
        $fixturePath = if ([string]::IsNullOrWhiteSpace($SimulationFixture)) {
            Join-Path -Path $PSScriptRoot -ChildPath 'tests\fixtures\simulation-happy.json'
        }
        else {
            $SimulationFixture
        }

        if (-not (Test-Path -LiteralPath $fixturePath -PathType Leaf)) {
            throw "模拟夹具不存在：$fixturePath"
        }
        if (-not $Execute) {
            Write-RestartResult -Value ([ordered]@{
                    state = 'simulation_planned'
                    message = '模拟模式不会查询、关闭或启动真实 Codex Desktop；传入 -Execute 后仅使用夹具运行状态机。'
                    manifestPath = $null
                    reportPath = $null
                    supervisorPid = $null
                    exitCode = 0
                })
            exit 0
        }

        $fixture = Get-Content -LiteralPath $fixturePath -Raw -ErrorAction Stop | ConvertFrom-Json -Depth 30
        $run = New-RestartRun
        $manifestPath = Join-Path -Path $run.path -ChildPath 'manifest.json'
        $manifest = New-SimulationManifest -Fixture $fixture -Run $run
        Write-AtomicJson -Value $manifest -Path $manifestPath
        Write-ManifestChecksum -ManifestPath $manifestPath
        $reportPath = Join-Path -Path $run.path -ChildPath 'restart-report.json'
        $supervisor = Start-VerifiedSupervisor -Run $run -ManifestPath $manifestPath -ForceAfterTimeout:$ForceAfterTimeout -Simulate
        if (-not $supervisor.ready) {
            Write-SupervisorBootstrapFailure -Run $run -Mode 'simulation' -ReportPath $reportPath -SupervisorPid ([int]$supervisor.pid)
            Write-RestartResult -Value ([ordered]@{
                    state = 'supervisor_bootstrap_failed'
                    message = '重启监督器未能安全启动；模拟模式未调用任何真实 Desktop 操作。'
                    manifestPath = $manifestPath
                    reportPath = $reportPath
                    runId = $run.id
                    supervisorPid = $supervisor.pid
                    exitCode = 1
                })
            exit 1
        }
        Write-RestartResult -Value ([ordered]@{
                state = 'simulation_scheduled'
                message = '已启动隔离模拟监督进程；它不调用 AppX、CIM、codex CLI、关闭或启动真实桌面应用。'
                manifestPath = $manifestPath
                reportPath = $reportPath
                runId = $run.id
                supervisorPid = $supervisor.pid
                exitCode = 0
            })
        exit 0
    }

    $inspection = Get-CodexDesktopRestartInspection
    $roots = @($inspection.captured_processes | Where-Object { $_.is_root })
    if (-not $Execute) {
        $ready = [bool]$inspection.desktop.available -and $roots.Count -gt 0 -and [bool]$inspection.session_snapshot.available
        $dryRunExitCode = if ($ready) { 0 } else { 2 }
        Write-RestartResult -Value ([ordered]@{
                state = if ($ready) { 'desktop_restart_planned' } else { 'desktop_restart_not_ready' }
                message = if ($ready) { 'dry-run 已完成；未写清单、未关闭、未启动任何应用。' } else { 'dry-run 已完成，但当前不满足安全执行条件；请检查 desktop、运行中的根进程和会话快照。' }
                manifestPath = $null
                reportPath = $null
                supervisorPid = $null
                rootProcessCount = $roots.Count
                cachedChatGptLogin = [bool]$inspection.login.cached_chatgpt_login
                sessionSnapshotAvailable = [bool]$inspection.session_snapshot.available
                exitCode = $dryRunExitCode
            })
        exit $dryRunExitCode
    }

    if (-not $inspection.desktop.available) {
        throw '未检测到可验证的 OpenAI.Codex Desktop MSIX 安装，拒绝以名称或窗口标题猜测进程。'
    }
    if ($roots.Count -eq 0) {
        throw '未检测到精确匹配的 Codex Desktop 根进程；不会启动或关闭任何程序。'
    }
    if (-not $inspection.session_snapshot.available) {
        throw '无法记录最小会话快照；为避免遗漏进行中的桌面线程，拒绝执行。'
    }

    $run = New-RestartRun
    $manifestPath = Join-Path -Path $run.path -ChildPath 'manifest.json'
    $manifest = [ordered]@{
        schema_version = 1
        run_id = $run.id
        mode = 'desktop'
        created_at = (Get-Date).ToUniversalTime().ToString('o')
        desktop = $inspection.desktop
        captured_processes = @($inspection.captured_processes)
        session_snapshot = $inspection.session_snapshot
        grace_seconds = Get-EffectiveGraceSeconds -NormalGraceSeconds $GraceSeconds -ForceCloseConfirmed ([bool]$ForceAfterTimeout)
        launch_timeout_seconds = $LaunchTimeoutSeconds
        force_after_timeout = [bool]$ForceAfterTimeout
        recovery_contract = '桌面端进行中的回合只会标记为 interrupted_requires_manual_resume。重启后需要在 ChatGPT Desktop 中由用户确认或继续；不承诺自动恢复。'
        desktop_only_notice = '该重启只针对 OpenAI.Codex MSIX Desktop，不会关闭或恢复现有 codex CLI 会话。'
    }
    Write-AtomicJson -Value $manifest -Path $manifestPath
    Write-ManifestChecksum -ManifestPath $manifestPath
    $reportPath = Join-Path -Path $run.path -ChildPath 'restart-report.json'
    $supervisor = Start-VerifiedSupervisor -Run $run -ManifestPath $manifestPath -ForceAfterTimeout:$ForceAfterTimeout
    if (-not $supervisor.ready) {
        Write-SupervisorBootstrapFailure -Run $run -Mode 'desktop' -ReportPath $reportPath -SupervisorPid ([int]$supervisor.pid)
        Write-RestartResult -Value ([ordered]@{
                state = 'supervisor_bootstrap_failed'
                message = '重启监督器未能在关闭 Desktop 前证明已就绪；现有 Desktop 未被关闭。'
                manifestPath = $manifestPath
                reportPath = $reportPath
                runId = $run.id
                supervisorPid = $supervisor.pid
                exitCode = 1
            })
        exit 1
    }
    Write-RestartResult -Value ([ordered]@{
            state = 'desktop_restart_scheduled'
            message = '已创建最小恢复清单并启动 Desktop 专用监督进程。缓存 ChatGPT 登录会被验证，但不会自动绕过 SSO、MFA 或通行密钥。'
            manifestPath = $manifestPath
            reportPath = $reportPath
            runId = $run.id
            supervisorPid = $supervisor.pid
            exitCode = 0
        })
    exit 0
}
catch {
    Write-RestartResult -Value ([ordered]@{
            state = 'restart_rejected'
            message = $_.Exception.Message
            manifestPath = $null
            reportPath = $null
            supervisorPid = $null
            exitCode = 1
        })
    exit 1
}
