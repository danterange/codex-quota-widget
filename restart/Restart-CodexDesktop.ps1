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
        grace_seconds = 5
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
        $supervisorPid = Start-CodexDesktopRestartSupervisor -SupervisorScript $supervisorPath -ManifestPath $manifestPath -ForceAfterTimeout:$ForceAfterTimeout -Simulate
        Write-RestartResult -Value ([ordered]@{
                state = 'simulation_scheduled'
                message = '已启动隔离模拟监督进程；它不调用 AppX、CIM、codex CLI、关闭或启动真实桌面应用。'
                manifestPath = $manifestPath
                reportPath = (Join-Path -Path $run.path -ChildPath 'restart-report.json')
                runId = $run.id
                supervisorPid = $supervisorPid
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
        grace_seconds = $GraceSeconds
        launch_timeout_seconds = $LaunchTimeoutSeconds
        force_after_timeout = [bool]$ForceAfterTimeout
        recovery_contract = '桌面端进行中的回合只会标记为 interrupted_requires_manual_resume。重启后需要在 ChatGPT Desktop 中由用户确认或继续；不承诺自动恢复。'
        desktop_only_notice = '该重启只针对 OpenAI.Codex MSIX Desktop，不会关闭或恢复现有 codex CLI 会话。'
    }
    Write-AtomicJson -Value $manifest -Path $manifestPath
    Write-ManifestChecksum -ManifestPath $manifestPath
    $supervisorPid = Start-CodexDesktopRestartSupervisor -SupervisorScript $supervisorPath -ManifestPath $manifestPath -ForceAfterTimeout:$ForceAfterTimeout
    Write-RestartResult -Value ([ordered]@{
            state = 'desktop_restart_scheduled'
            message = '已创建最小恢复清单并启动 Desktop 专用监督进程。缓存 ChatGPT 登录会被验证，但不会自动绕过 SSO、MFA 或通行密钥。'
            manifestPath = $manifestPath
            reportPath = (Join-Path -Path $run.path -ChildPath 'restart-report.json')
            runId = $run.id
            supervisorPid = $supervisorPid
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
