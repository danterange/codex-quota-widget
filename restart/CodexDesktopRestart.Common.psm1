#requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-CodexDesktopRestartHost {
    <#
    .SYNOPSIS
    Refuses unsupported Windows PowerShell hosts before any restart action is considered.
    #>
    if ($PSVersionTable.PSEdition -ne 'Core' -or $PSVersionTable.PSVersion -lt [version]'7.4') {
        throw '此技能仅支持 PowerShell 7.4 或更高版本（pwsh.exe）。请不要使用 Windows PowerShell 5.1（powershell.exe）运行。'
    }
}

function ConvertTo-RestartPath {
    <#
    .SYNOPSIS
    Normalizes a local path for case-insensitive, segment-aware comparisons.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    return [IO.Path]::GetFullPath($Path).TrimEnd([char[]]@('\', '/'))
}

function Test-PathWithinRoot {
    <#
    .SYNOPSIS
    Checks whether a path is exactly a trusted root or lies below it on a directory boundary.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Root
    )

    try {
        $normalizedPath = ConvertTo-RestartPath -Path $Path
        $normalizedRoot = ConvertTo-RestartPath -Path $Root
    }
    catch {
        return $false
    }

    if ([string]::Equals($normalizedPath, $normalizedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    return $normalizedPath.StartsWith($normalizedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

function Get-CodexCommandDescriptor {
    <#
    .SYNOPSIS
    Resolves the local Codex CLI without invoking it or exposing configuration contents.
    #>
    try {
        # Prefer a native executable. `Get-Command codex` often resolves the npm .ps1 shim, which CreateProcess cannot launch directly.
        $command = $null
        foreach ($candidate in @('codex.exe', 'codex.cmd', 'codex')) {
            $command = Get-Command -Name $candidate -CommandType Application, ExternalScript -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($null -ne $command) {
                break
            }
        }
        if ($null -eq $command) {
            throw '未解析到可执行的 codex 命令。'
        }
        $path = if (-not [string]::IsNullOrWhiteSpace([string]$command.Path)) { [string]$command.Path } else { [string]$command.Source }
        if ([string]::IsNullOrWhiteSpace($path)) {
            throw '命令没有可执行路径。'
        }

        return [pscustomobject]@{
            available = $true
            path = $path
            reason = $null
        }
    }
    catch {
        return [pscustomobject]@{
            available = $false
            path = $null
            reason = '未找到 codex CLI；不会尝试安装或启动 CLI。'
        }
    }
}

function Invoke-ReadOnlyCodexCommand {
    <#
    .SYNOPSIS
    Runs a short-lived read-only Codex CLI probe and returns only redacted outcome metadata to callers.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [ValidateRange(3, 45)]
        [int]$TimeoutSeconds = 15
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $extension = [IO.Path]::GetExtension($ExecutablePath)
    $isPowerShellShim = [string]::Equals($extension, '.ps1', [StringComparison]::OrdinalIgnoreCase)
    $startInfo.FileName = if ($isPowerShellShim) { Join-Path -Path $PSHOME -ChildPath 'pwsh.exe' } else { $ExecutablePath }
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    if ($isPowerShellShim) {
        foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $ExecutablePath)) {
            [void]$startInfo.ArgumentList.Add($argument)
        }
    }
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::Start($startInfo)
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        # This is only the short-lived probe process created by this function, never an existing CLI session.
        $process.Kill($true)
        $process.WaitForExit()
        return [pscustomobject]@{
            completed = $false
            timed_out = $true
            exit_code = $null
            output = ''
        }
    }

    [Threading.Tasks.Task]::WaitAll(@($stdoutTask, $stderrTask))
    return [pscustomobject]@{
        completed = $true
        timed_out = $false
        exit_code = $process.ExitCode
        output = ($stdoutTask.Result + "`n" + $stderrTask.Result)
    }
}

function Get-CachedChatGptLoginStatus {
    <#
    .SYNOPSIS
    Checks whether the local CLI reports an already-cached ChatGPT login without reading auth files.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [object]$CodexCommand
    )

    if (-not $CodexCommand.available) {
        return [pscustomobject]@{
            checked = $false
            cached_chatgpt_login = $false
            reason = '未找到 codex CLI，无法验证缓存登录。'
        }
    }

    try {
        $result = Invoke-ReadOnlyCodexCommand -ExecutablePath $CodexCommand.path -Arguments @('login', 'status')
        return [pscustomobject]@{
            checked = [bool]$result.completed
            cached_chatgpt_login = ($result.completed -and $result.exit_code -eq 0 -and $result.output -match '(?i)logged\s+in\s+using\s+chatgpt')
            reason = if ($result.timed_out) { '登录状态检查超时。' } elseif ($result.exit_code -ne 0) { '登录状态检查未成功。' } elseif ($result.output -notmatch '(?i)logged\s+in\s+using\s+chatgpt') { '当前不是可复用的 ChatGPT 登录。' } else { $null }
        }
    }
    catch {
        return [pscustomobject]@{
            checked = $false
            cached_chatgpt_login = $false
            reason = '登录状态检查不可用。'
        }
    }
}

function Get-CodexDesktopTarget {
    <#
    .SYNOPSIS
    Finds the installed OpenAI.Codex MSIX package and its exact executable/AUMID pair.
    #>
    try {
        if ($null -eq (Get-Command -Name Get-AppxPackage -ErrorAction SilentlyContinue) -or
            $null -eq (Get-Command -Name Get-StartApps -ErrorAction SilentlyContinue)) {
            throw '当前系统未提供 AppX 查询命令。'
        }

        $package = Get-AppxPackage -Name 'OpenAI.Codex' -ErrorAction Stop |
            Sort-Object -Property Version -Descending |
            Select-Object -First 1
        if ($null -eq $package) {
            throw '未安装 OpenAI.Codex MSIX 包。'
        }
        $expectedPublisherId = '2p2nqsd0c76g0'
        $expectedFamilyName = 'OpenAI.Codex_' + $expectedPublisherId
        if (-not [string]::Equals([string]$package.PublisherId, $expectedPublisherId, [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals([string]$package.PackageFamilyName, $expectedFamilyName, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'OpenAI.Codex 包发布者或 PackageFamilyName 未通过可信校验。'
        }

        $executablePath = Join-Path -Path $package.InstallLocation -ChildPath 'app\ChatGPT.exe'
        if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
            throw '未找到已安装包内的 ChatGPT.exe。'
        }

        $startApp = Get-StartApps |
            Where-Object { $_.AppID -like ("{0}!*" -f $package.PackageFamilyName) } |
            Select-Object -First 1
        if ($null -eq $startApp) {
            throw '未解析到 OpenAI.Codex 的动态 AUMID。'
        }

        return [pscustomobject]@{
            available = $true
            package_full_name = [string]$package.PackageFullName
            package_family_name = [string]$package.PackageFamilyName
            install_location = [string]$package.InstallLocation
            executable_path = $executablePath
            aumid = [string]$startApp.AppID
            reason = $null
        }
    }
    catch {
        return [pscustomobject]@{
            available = $false
            package_full_name = $null
            package_family_name = $null
            install_location = $null
            executable_path = $null
            aumid = $null
            reason = $_.Exception.Message
        }
    }
}

function New-CapturedProcessRecord {
    <#
    .SYNOPSIS
    Converts a CIM process into the immutable identity record used by restart manifests.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [object]$Process,

        [Parameter(Mandatory = $true)]
        [bool]$IsRoot,

        [Parameter(Mandatory = $true)]
        [int]$Depth,

        [Parameter(Mandatory = $true)]
        [string]$InstallLocation
    )

    $path = [string]$Process.ExecutablePath
    $isPackageProcess = -not [string]::IsNullOrWhiteSpace($path) -and (Test-PathWithinRoot -Path $path -Root $InstallLocation)
    $name = [string]$Process.Name
    $isExplicitlyProtected = $name -in @('cmd.exe', 'powershell.exe', 'pwsh.exe', 'conhost.exe')
    return [pscustomobject]@{
        pid = [int]$Process.ProcessId
        parent_pid = [int]$Process.ParentProcessId
        creation_key = [string]$Process.CreationDate
        executable_path = $path
        name = $name
        depth = $Depth
        is_root = $IsRoot
        trusted_package_process = $isPackageProcess
        force_eligible = ($isPackageProcess -and -not $isExplicitlyProtected)
    }
}

function Get-CodexDesktopProcessCapture {
    <#
    .SYNOPSIS
    Captures only descendants of exact OpenAI.Codex MSIX ChatGPT.exe roots; it never uses names or window titles as selectors.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [object]$DesktopTarget
    )

    if (-not $DesktopTarget.available) {
        return @()
    }

    try {
        $allProcesses = @(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop)
        $matchingExecutables = @($allProcesses | Where-Object {
                -not [string]::IsNullOrWhiteSpace([string]$_.ExecutablePath) -and
                [string]::Equals([string]$_.ExecutablePath, [string]$DesktopTarget.executable_path, [StringComparison]::OrdinalIgnoreCase)
            })
        $matchingIds = [Collections.Generic.HashSet[int]]::new()
        foreach ($process in $matchingExecutables) {
            [void]$matchingIds.Add([int]$process.ProcessId)
        }
        $roots = @($matchingExecutables | Where-Object { -not $matchingIds.Contains([int]$_.ParentProcessId) })

        $childrenByParent = @{}
        foreach ($process in $allProcesses) {
            $parentId = [int]$process.ParentProcessId
            if ($null -eq $childrenByParent[$parentId]) {
                $childrenByParent[$parentId] = [Collections.Generic.List[object]]::new()
            }
            [void]$childrenByParent[$parentId].Add($process)
        }

        $queue = [Collections.Generic.Queue[object]]::new()
        $seen = [Collections.Generic.HashSet[int]]::new()
        $records = [Collections.Generic.List[object]]::new()
        foreach ($root in $roots) {
            $queue.Enqueue([pscustomobject]@{ process = $root; depth = 0; is_root = $true })
        }

        while ($queue.Count -gt 0) {
            $current = $queue.Dequeue()
            $id = [int]$current.process.ProcessId
            if (-not $seen.Add($id)) {
                continue
            }

            [void]$records.Add((New-CapturedProcessRecord -Process $current.process -IsRoot ([bool]$current.is_root) -Depth ([int]$current.depth) -InstallLocation ([string]$DesktopTarget.install_location)))
            if ($childrenByParent.ContainsKey($id)) {
                foreach ($child in $childrenByParent[$id]) {
                    $queue.Enqueue([pscustomobject]@{ process = $child; depth = ([int]$current.depth + 1); is_root = $false })
                }
            }
        }

        return $records.ToArray()
    }
    catch {
        return @()
    }
}

function Test-CapturedIdentity {
    <#
    .SYNOPSIS
    Compares a live process snapshot with a captured PID, creation time, and exact path to defeat PID reuse.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [object]$Captured,

        [AllowNull()]
        [object]$Current
    )

    if ($null -eq $Current) {
        return $false
    }

    return ([int]$Captured.pid -eq [int]$Current.ProcessId) -and
        [string]::Equals([string]$Captured.creation_key, [string]$Current.CreationDate, [StringComparison]::Ordinal) -and
        [string]::Equals([string]$Captured.executable_path, [string]$Current.ExecutablePath, [StringComparison]::OrdinalIgnoreCase)
}

function Get-LiveCapturedProcesses {
    <#
    .SYNOPSIS
    Resolves only manifest-captured identities; it never performs a fresh name-based scan for stop targets.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$CapturedProcesses
    )

    $live = [Collections.Generic.List[object]]::new()
    foreach ($captured in $CapturedProcesses) {
        $current = Get-CimInstance -ClassName Win32_Process -Filter ("ProcessId = {0}" -f [int]$captured.pid) -ErrorAction SilentlyContinue
        if (Test-CapturedIdentity -Captured $captured -Current $current) {
            [void]$live.Add([pscustomobject]@{ captured = $captured; current = $current })
        }
    }
    return $live.ToArray()
}

function Select-ForceEligibleCapturedProcesses {
    <#
    .SYNOPSIS
    Applies both the manifest bit and a fresh package-root boundary test before any forced stop can be considered.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$LiveCapturedProcesses,

        [Parameter(Mandatory = $true)]
        [string]$InstallLocation
    )

    return @($LiveCapturedProcesses | Where-Object {
            [bool]$_.captured.force_eligible -and
            (Test-PathWithinRoot -Path ([string]$_.captured.executable_path) -Root $InstallLocation) -and
            ([string]$_.captured.name -notin @('cmd.exe', 'powershell.exe', 'pwsh.exe', 'conhost.exe'))
        })
}

function Get-CodexSessionSnapshot {
    <#
    .SYNOPSIS
    Records only writer-lock file names as best-effort active thread identifiers; no SQLite, auth, title, or message content is read.
    #>
    param(
        [string]$CodexHome = $(if ([string]::IsNullOrWhiteSpace($env:CODEX_HOME)) { Join-Path -Path $env:USERPROFILE -ChildPath '.codex' } else { $env:CODEX_HOME })
    )

    $lockDirectory = Join-Path -Path $CodexHome -ChildPath 'thread-writer-locks'
    $observedAt = (Get-Date).ToUniversalTime().ToString('o')
    if (-not (Test-Path -LiteralPath $lockDirectory -PathType Container)) {
        return [pscustomobject]@{
            available = $false
            observed_at = $observedAt
            source = 'writer_lock_filenames'
            active_thread_candidates = @()
            reason = '未发现线程写锁目录，无法证明当前没有进行中的桌面端回合。'
            limitations = @('为避免漏记进行中的会话，缺少写锁目录时拒绝执行重启。', '桌面端没有经验证的自动恢复当前流式回合接口。')
        }
    }

    try {
        $threadIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($item in Get-ChildItem -LiteralPath $lockDirectory -File -ErrorAction Stop) {
            if ($item.Name -match '^(?<thread>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\.lock$') {
                [void]$threadIds.Add($Matches.thread.ToLowerInvariant())
            }
        }

        $candidates = @($threadIds | Sort-Object | ForEach-Object {
                [pscustomobject]@{
                    thread_id = $_
                    observed_at = $observedAt
                    writer_lock_present = $true
                    recovery_status = 'interrupted_requires_manual_resume'
                }
            })
        return [pscustomobject]@{
            available = $true
            observed_at = $observedAt
            source = 'writer_lock_filenames'
            active_thread_candidates = $candidates
            limitations = @('锁文件只是活动信号，可能滞后。', '桌面端没有经验证的自动恢复当前流式回合接口。')
        }
    }
    catch {
        return [pscustomobject]@{
            available = $false
            observed_at = $observedAt
            source = 'writer_lock_filenames'
            active_thread_candidates = @()
            reason = '无法读取线程写锁目录。'
            limitations = @('没有会话快照时，执行重启前应由用户确认。')
        }
    }
}

function Get-CodexDesktopRestartInspection {
    <#
    .SYNOPSIS
    Produces a complete capability report without closing, starting, writing, or changing any Codex process.
    #>
    $target = Get-CodexDesktopTarget
    $capture = @(Get-CodexDesktopProcessCapture -DesktopTarget $target)
    $command = Get-CodexCommandDescriptor
    $login = Get-CachedChatGptLoginStatus -CodexCommand $command
    $snapshot = Get-CodexSessionSnapshot
    $roots = @($capture | Where-Object { $_.is_root })

    return [pscustomobject]@{
        schema_version = 1
        inspected_at = (Get-Date).ToUniversalTime().ToString('o')
        desktop = $target
        captured_processes = $capture
        root_process_count = $roots.Count
        login = $login
        session_snapshot = $snapshot
        can_schedule_desktop_restart = ($target.available -and $roots.Count -gt 0 -and $snapshot.available)
        desktop_only_notice = '此功能只针对 ChatGPT Desktop 的 OpenAI.Codex MSIX 进程；不会关闭、重启或恢复 codex CLI 会话。'
        recovery_notice = '任何检测到的桌面端活跃线程都只能标记为需要手动确认/继续，不能保证自动恢复。'
    }
}

function Write-AtomicJson {
    <#
    .SYNOPSIS
    Persists run metadata atomically so the isolated supervisor never consumes a partially written manifest.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [object]$Value,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $directory = Split-Path -Path $Path -Parent
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory -Force -ErrorAction Stop | Out-Null
    }

    $temporaryPath = Join-Path -Path $directory -ChildPath ('.{0}.{1}.tmp' -f [IO.Path]::GetFileName($Path), [guid]::NewGuid().ToString('N'))
    $json = $Value | ConvertTo-Json -Depth 30
    [IO.File]::WriteAllText($temporaryPath, $json, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
}

function New-CodexDesktopRestartRun {
    <#
    .SYNOPSIS
    Allocates one isolated run directory below the current user's local application data.
    #>
    param(
        [string]$RunRoot = (Join-Path -Path $env:LOCALAPPDATA -ChildPath 'CodexDesktopRestart\runs')
    )

    $resolvedRoot = [IO.Path]::GetFullPath($RunRoot)
    if (-not (Test-Path -LiteralPath $resolvedRoot -PathType Container)) {
        New-Item -ItemType Directory -Path $resolvedRoot -Force -ErrorAction Stop | Out-Null
    }
    $runId = '{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [guid]::NewGuid().ToString('N').Substring(0, 8)
    $runPath = Join-Path -Path $resolvedRoot -ChildPath $runId
    New-Item -ItemType Directory -Path $runPath -ErrorAction Stop | Out-Null
    return [pscustomobject]@{ id = $runId; path = $runPath }
}

function Write-ManifestChecksum {
    <#
    .SYNOPSIS
    Adds an accidental-corruption checksum; it is not presented as an adversarial integrity boundary.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$ManifestPath
    )

    $hash = (Get-FileHash -LiteralPath $ManifestPath -Algorithm SHA256 -ErrorAction Stop).Hash
    [IO.File]::WriteAllText("$ManifestPath.sha256", $hash, [Text.UTF8Encoding]::new($false))
}

function Read-VerifiedRestartManifest {
    <#
    .SYNOPSIS
    Reads an expected-schema manifest after checking its atomic-write checksum.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$ManifestPath
    )

    $hashPath = "$ManifestPath.sha256"
    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf) -or -not (Test-Path -LiteralPath $hashPath -PathType Leaf)) {
        throw '恢复清单或其校验文件不存在。'
    }
    $expectedHash = (Get-Content -LiteralPath $hashPath -Raw -ErrorAction Stop).Trim()
    $actualHash = (Get-FileHash -LiteralPath $ManifestPath -Algorithm SHA256 -ErrorAction Stop).Hash
    if (-not [string]::Equals($expectedHash, $actualHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw '恢复清单校验失败；为了避免消费意外损坏的进程清单，未执行任何关闭或启动操作。'
    }

    $manifest = Get-Content -LiteralPath $ManifestPath -Raw -ErrorAction Stop | ConvertFrom-Json -Depth 30
    if ($manifest.schema_version -ne 1 -or [string]::IsNullOrWhiteSpace([string]$manifest.run_id) -or $null -eq $manifest.desktop -or $null -eq $manifest.captured_processes) {
        throw '恢复清单架构无效。'
    }
    return $manifest
}

function Add-RestartReportEvent {
    <#
    .SYNOPSIS
    Appends an auditable stage entry without including tokens, prompt text, or chat bodies.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[object]]$Events,

        [Parameter(Mandatory = $true)]
        [string]$Stage,

        [Parameter(Mandatory = $true)]
        [object]$Detail
    )

    [void]$Events.Add([pscustomobject]@{
            at = (Get-Date).ToUniversalTime().ToString('o')
            stage = $Stage
            detail = $Detail
        })
}

function Get-DoctorCheck {
    <#
    .SYNOPSIS
    Returns a redacted doctor check by identifier when the current CLI schema exposes it.
    #>
    param(
        [AllowNull()]
        [object]$Checks,

        [Parameter(Mandatory = $true)]
        [string]$Id
    )

    if ($null -eq $Checks) {
        return $null
    }
    $property = $Checks.PSObject.Properties[$Id]
    return if ($null -eq $property) { $null } else { $property.Value }
}

function Get-CodexDesktopHealthStatus {
    <#
    .SYNOPSIS
    Checks redacted CLI diagnostics after launch; it never exports doctor output or authentication data.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [object]$CodexCommand
    )

    $login = Get-CachedChatGptLoginStatus -CodexCommand $CodexCommand
    if (-not $CodexCommand.available) {
        return [pscustomobject]@{
            available = $false
            cached_chatgpt_login = $false
            desktop_running = $false
            desktop_app_server_handshake_ok = $false
            reason = '未找到 codex CLI，无法进行健康检查。'
        }
    }

    try {
        $result = Invoke-ReadOnlyCodexCommand -ExecutablePath $CodexCommand.path -Arguments @('doctor', '--json') -TimeoutSeconds 30
        if (-not $result.completed -or $result.timed_out) {
            throw '诊断命令未在时限内完成。'
        }
        $doctor = $result.output | ConvertFrom-Json -Depth 30
        $auth = Get-DoctorCheck -Checks $doctor.checks -Id 'auth.credentials'
        $desktopVersion = Get-DoctorCheck -Checks $doctor.checks -Id 'desktop.app.version'
        $handshake = Get-DoctorCheck -Checks $doctor.checks -Id 'desktop.app_server.handshake'
        $runningValue = if ($null -eq $desktopVersion) { $null } else { $desktopVersion.details.running }
        return [pscustomobject]@{
            available = $true
            cached_chatgpt_login = [bool]$login.cached_chatgpt_login
            auth_credentials_ok = ($null -ne $auth -and [string]$auth.status -eq 'ok')
            desktop_running = ([string]$runningValue -eq 'true')
            desktop_app_server_handshake_ok = ($null -ne $handshake -and [string]$handshake.status -eq 'ok')
            reason = $null
        }
    }
    catch {
        return [pscustomobject]@{
            available = $false
            cached_chatgpt_login = [bool]$login.cached_chatgpt_login
            auth_credentials_ok = $false
            desktop_running = $false
            desktop_app_server_handshake_ok = $false
            reason = '无法完成或解析已脱敏的 Codex 健康检查。'
        }
    }
}

function Start-CodexDesktopRestartSupervisor {
    <#
    .SYNOPSIS
    Starts a detached pwsh supervisor so the caller can safely end before Desktop receives its close request.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$SupervisorScript,

        [Parameter(Mandatory = $true)]
        [string]$ManifestPath,

        [switch]$ForceAfterTimeout,

        [switch]$Simulate
    )

    $pwshPath = Join-Path -Path $PSHOME -ChildPath 'pwsh.exe'
    if (-not (Test-Path -LiteralPath $pwshPath -PathType Leaf)) {
        throw '未找到当前 pwsh.exe，拒绝使用不明 PowerShell 主机启动监督进程。'
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $pwshPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $SupervisorScript, '-ManifestPath', $ManifestPath)) {
        [void]$startInfo.ArgumentList.Add($argument)
    }
    if ($ForceAfterTimeout) {
        [void]$startInfo.ArgumentList.Add('-ForceAfterTimeout')
    }
    if ($Simulate) {
        [void]$startInfo.ArgumentList.Add('-Simulate')
    }

    return [Diagnostics.Process]::Start($startInfo).Id
}

Export-ModuleMember -Function @(
    'Assert-CodexDesktopRestartHost',
    'Test-PathWithinRoot',
    'Get-CodexCommandDescriptor',
    'Get-CachedChatGptLoginStatus',
    'Get-CodexDesktopTarget',
    'Get-CodexDesktopProcessCapture',
    'Test-CapturedIdentity',
    'Get-LiveCapturedProcesses',
    'Select-ForceEligibleCapturedProcesses',
    'Get-CodexSessionSnapshot',
    'Get-CodexDesktopRestartInspection',
    'Write-AtomicJson',
    'New-CodexDesktopRestartRun',
    'Write-ManifestChecksum',
    'Read-VerifiedRestartManifest',
    'Add-RestartReportEvent',
    'Get-CodexDesktopHealthStatus',
    'Start-CodexDesktopRestartSupervisor'
)
