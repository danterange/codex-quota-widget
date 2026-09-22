<#
.SYNOPSIS
Verifies release checksums, per-user installation, demo rendering, reinstall and removal.
.DESCRIPTION
Run with Windows PowerShell 5.1 on an interactive Windows desktop. Uses an isolated
install directory and /NOICONS so validation cannot replace the user's shortcuts.
#>
[CmdletBinding()]
param([string]$ArtifactDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $ArtifactDirectory) { $ArtifactDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts' }
$artifactRoot = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{50F582B7-A12E-46BA-B7DB-157546C1D7F9}_is1'
if (Test-Path -LiteralPath $uninstallKey) { throw 'An existing installation was found. Run this test in a clean Windows user account.' }
[xml]$project = Get-Content -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'CodexQuotaWidget.csproj')
$version = $project.Project.PropertyGroup.Version
$package = "codex-quota-widget-v$version-windows-x64"
foreach ($line in Get-Content -LiteralPath (Join-Path $artifactRoot 'SHA256SUMS.txt')) {
    if ($line -notmatch '^([a-fA-F0-9]{64})  ([^\\/]+)$') { throw 'Invalid checksum entry' }
    $expected = $Matches[1]
    $file = Join-Path $artifactRoot $Matches[2]
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine $expected) { throw "Checksum mismatch: $file" }
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class ReleaseWindow {
    // Request a normal WPF shutdown so timers and tray resources are released.
    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
}
"@

# A real window and bound demo values prove the shipped EXE loads its runtime and resources.
function Test-Application {
    param([string]$Executable)
    # Keep the WPF window visible; hiding the process also hides its top-level window
    # and makes UI Automation report a false negative even though the app is running.
    # Match the working directory used by the installed shortcut.
    # 生产环境的关闭按钮会隐藏到托盘；验收参数让 WM_CLOSE 走应用的正常退出和资源释放路径。
    $process = Start-Process -FilePath $Executable -WorkingDirectory (Split-Path -Parent $Executable) -ArgumentList @('--demo', '--exit-on-close') -PassThru
    $window = $null
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        # 同一 PID 可能有输入法等辅助 Pane；只接受真正承载 WPF 内容的 Window，避免误向辅助窗口发送关闭消息。
        $condition = [System.Windows.Automation.AndCondition]::new([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id), [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))
        do {
            if ($process.HasExited) { throw "Application exited early: $($process.ExitCode)" }
            $process.Refresh()
            $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
            if ($null -ne $window) {
                $names = @($window.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name })
                if ($names -contains '42%' -and $names -contains '68%') { break }
            }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($null -eq $window -or $names -notcontains '42%' -or $names -notcontains '68%') { throw 'Demo window did not render quota values' }
        Write-Output '[OK] Shipped EXE rendered 42% and 68% demo quotas'
    } finally {
        if ($null -ne $window -and -not $process.HasExited) {
            [void][ReleaseWindow]::PostMessage([IntPtr]$window.Current.NativeWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
            if (-not $process.WaitForExit(10000)) { throw "Application did not close: PID $($process.Id)" }
        }
        $process.Dispose()
    }
}

# Wait for installer completion and propagate native failures instead of trusting launch success.
function Invoke-Installer {
    param([string]$Executable, [string[]]$Arguments)
    # Direct invocation of a GUI EXE may return before it completes in PowerShell.
    # Quote each argument for Start-Process, which joins its array into one command line.
    $quotedArguments = @($Arguments | ForEach-Object { '"' + $_ + '"' })
    $process = Start-Process -FilePath $Executable -ArgumentList $quotedArguments -WindowStyle Hidden -PassThru -Wait
    try { if ($process.ExitCode -ne 0) { throw "Installer exited with $($process.ExitCode)" } }
    finally { $process.Dispose() }
}

# Confirm the completed installer placed the application in the requested directory.
function Wait-ForFile {
    param([string]$Path)
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Test-Path -LiteralPath $Path -PathType Leaf) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 200
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Installer did not create: $Path" }
}

# Allow delayed uninstaller cleanup while still failing on retained application files.
function Wait-ForMissingFile {
    param([string]$Path)
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ((Test-Path -LiteralPath $Path -PathType Leaf) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 200
    }
    if (Test-Path -LiteralPath $Path -PathType Leaf) { throw "Uninstall left the executable behind: $Path" }
}

$testRoot = Join-Path $artifactRoot ('smoke-' + [Guid]::NewGuid().ToString('N'))
$installRoot = Join-Path $testRoot 'installed'
$portableRoot = Join-Path $testRoot 'portable'
$null = New-Item -ItemType Directory -Path $testRoot
$setup = Join-Path $artifactRoot "$package-setup.exe"
# An earlier guard prevents test registration from replacing an existing installation.
$installArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', ('/LOG=' + (Join-Path $testRoot 'install.log')), ('/DIR=' + $installRoot))
try {
    Invoke-Installer -Executable $setup -Arguments $installArgs
    $exe = Join-Path $installRoot 'CodexQuotaWidget.exe'
    Wait-ForFile -Path $exe
    if ((Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -notlike "$version*") { throw 'Installed EXE version mismatch' }
    Test-Application -Executable $exe
    Invoke-Installer -Executable $setup -Arguments $installArgs
    Wait-ForFile -Path $exe
    Test-Application -Executable $exe
    Write-Output '[OK] Reinstall completed and application still runs'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $artifactRoot "$package.zip"), $portableRoot)
    Test-Application -Executable (Join-Path $portableRoot 'CodexQuotaWidget.exe')
} finally {
    $uninstaller = Join-Path $installRoot 'unins000.exe'
if (Test-Path -LiteralPath $uninstaller) {
        Invoke-Installer -Executable $uninstaller -Arguments @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    }
}
Wait-ForMissingFile -Path (Join-Path $installRoot 'CodexQuotaWidget.exe')
Write-Output "[OK] Installation, reinstall, portable launch and uninstall passed. Logs: $testRoot"
