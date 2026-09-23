[CmdletBinding()]
param(
    [string]$ProjectRoot,
    [string]$ExecutablePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Split-Path -Parent $PSScriptRoot
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class UiScreenshotNative {
    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hwnd, int command);
}
'@

function Save-WindowScreenshot {
    param(
        [Parameter(Mandatory = $true)] [System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)] [string]$Path
    )

    $bounds = $Window.Current.BoundingRectangle
    $rectangle = [System.Drawing.Rectangle]::new(
        [int]$bounds.Left,
        [int]$bounds.Top,
        [int]$bounds.Width,
        [int]$bounds.Height)
    if ($rectangle.Width -lt 330 -or $rectangle.Width -gt 720 -or $rectangle.Height -lt 220 -or $rectangle.Height -gt 560) {
        throw "Unexpected adaptive widget size: $($rectangle.Width)x$($rectangle.Height)"
    }

    $bitmap = [System.Drawing.Bitmap]::new($rectangle.Width, $rectangle.Height)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $deviceContext = $graphics.GetHdc()
            try {
                if (-not [UiScreenshotNative]::PrintWindow(
                        [IntPtr]$Window.Current.NativeWindowHandle,
                        $deviceContext,
                        0)) {
                    throw 'PrintWindow failed.'
                }
            }
            finally {
                $graphics.ReleaseHdc($deviceContext)
            }
        }
        finally {
            $graphics.Dispose()
        }

        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }

    Write-Output "[OK] Screenshot: $Path ($($rectangle.Width)x$($rectangle.Height))"
}

function Capture-RenderScreenshot {
    param(
        [Parameter(Mandatory = $true)] [string]$Executable,
        [Parameter(Mandatory = $true)] [string]$Path,
        [switch]$Settings
    )

    Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    $arguments = @('--demo', '--exit-on-close', "--capture-screenshot=$Path")
    if ($Settings) { $arguments += '--start-settings' }
    $captureProcess = Start-Process -FilePath $Executable -WorkingDirectory (Split-Path -Parent $Executable) -ArgumentList $arguments -PassThru
    try {
        if (-not $captureProcess.WaitForExit(10000)) {
            throw "Screenshot process did not exit: $Path"
        }
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
            throw "Screenshot was not generated: $Path (exit $($captureProcess.ExitCode))"
        }
        $file = Get-Item -LiteralPath $Path
        if ($file.Length -lt 1000) { throw "Screenshot is unexpectedly small: $Path" }
        Write-Output "[OK] Render screenshot: $Path ($($file.Length) bytes)"
    }
    finally {
        if (-not $captureProcess.HasExited) { Stop-Process -Id $captureProcess.Id -Force }
        $captureProcess.Dispose()
    }
}

function Get-DescendantNames {
    param([System.Windows.Automation.AutomationElement]$Window)
    return @($Window.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition) |
        ForEach-Object { $_.Current.Name } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Find-DescendantByName {
    param(
        [Parameter(Mandatory = $true)] [System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)] [string]$Name,
        [System.Windows.Automation.ControlType]$ControlType
    )

    $condition = if ($null -eq $ControlType) {
        [System.Windows.Automation.Condition]::TrueCondition
    } else {
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            $ControlType)
    }
    return @($Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition) |
        Where-Object { $_.Current.Name -eq $Name } | Select-Object -First 1)
}

function Wait-DescendantByName {
    param(
        [Parameter(Mandatory = $true)] [System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)] [string]$Name,
        [System.Windows.Automation.ControlType]$ControlType,
        [int]$TimeoutMilliseconds = 2500
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        $match = Find-DescendantByName -Window $Window -Name $Name -ControlType $ControlType
        if ($null -ne $match) { return @($match)[0] }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    return $null
}

function Invoke-NavigationElement {
    param([Parameter(Mandatory = $true)] [System.Windows.Automation.AutomationElement]$Element)

    try {
        $invoke = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $invoke.Invoke()
        return
    } catch {
        $selection = $Element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $selection.Select()
    }
}

if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = Join-Path $ProjectRoot 'bin\Release\net8.0-windows\CodexQuotaWidget.exe'
}
$exe = $ExecutablePath
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Build output not found: $exe"
}

$output = Join-Path $ProjectRoot 'artifacts\ui-test'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$dashboardShot = Join-Path $output 'dashboard.png'
$settingsShot = Join-Path $output 'settings.png'
Capture-RenderScreenshot -Executable $exe -Path $dashboardShot
Capture-RenderScreenshot -Executable $exe -Path $settingsShot -Settings
$process = Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -ArgumentList @('--demo', '--exit-on-close') -PassThru
$window = $null
try {
    $windowCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
            $process.Id),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Window))
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $process.Refresh()
        if ($process.HasExited) { throw "Widget exited early: $($process.ExitCode)" }
        $window = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children,
            $windowCondition)
        if ($null -eq $window) { Start-Sleep -Milliseconds 250 }
    } while ($null -eq $window -and [DateTime]::UtcNow -lt $deadline)
    if ($null -eq $window) { throw 'Widget window was not found.' }
    [void][UiScreenshotNative]::ShowWindow([IntPtr]$window.Current.NativeWindowHandle, 5)
    [void][UiScreenshotNative]::SetForegroundWindow([IntPtr]$window.Current.NativeWindowHandle)
    Start-Sleep -Milliseconds 350
    $extendedStyle = [UiScreenshotNative]::GetWindowLongPtr([IntPtr]$window.Current.NativeWindowHandle, -20).ToInt64()
    if (($extendedStyle -band 0x8) -eq 0) {
        throw 'Widget window is not topmost by default.'
    }

    $dashboardNames = Get-DescendantNames -Window $window
    $dashboardLabel = if ($dashboardNames -contains 'Dashboard') { 'Dashboard' } else { '额度' }
    $settingsLabel = if ($dashboardNames -contains 'Settings') { 'Settings' } else { '设置' }
    $refreshLabel = if ($dashboardNames -contains 'Refresh now') { 'Refresh now' } else { '刷新额度' }
    foreach ($required in @($dashboardLabel, $settingsLabel, $refreshLabel)) {
        if ($dashboardNames -notcontains $required) { throw "Dashboard is missing: $required" }
    }
    $dashboardRail = Find-DescendantByName -Window $window -Name $dashboardLabel -ControlType ([System.Windows.Automation.ControlType]::RadioButton)
    $settingsRail = Find-DescendantByName -Window $window -Name $settingsLabel -ControlType ([System.Windows.Automation.ControlType]::RadioButton)
    $refreshButton = Find-DescendantByName -Window $window -Name $refreshLabel -ControlType ([System.Windows.Automation.ControlType]::Button)
    $windowBounds = $window.Current.BoundingRectangle
    foreach ($element in @($dashboardRail, $settingsRail, $refreshButton)) {
        if ($null -eq $element) { throw 'Icon rail control was not found.' }
        $bounds = @($element)[0].Current.BoundingRectangle
        if ($bounds.Width -lt 24 -or $bounds.Height -lt 24) { throw "Icon control is too small: $($bounds.Width)x$($bounds.Height)" }
        if ($bounds.Left -lt ($windowBounds.Left + 4) -or $bounds.Right -gt ($windowBounds.Right - 4)) {
            throw "Icon control is too close to the window edge: $($bounds.Left),$($bounds.Top),$($bounds.Right),$($bounds.Bottom)"
        }
    }
    if ($dashboardNames -contains '关闭窗口' -or $dashboardNames -contains 'Close window') {
        throw 'The custom close button should not be present.'
    }
    Save-WindowScreenshot -Window $window -Path (Join-Path $output 'dashboard-printwindow.png')

    Invoke-NavigationElement -Element @($settingsRail)[0]
    $refreshIntervalLabel = if ($dashboardLabel -eq 'Dashboard') { 'Refresh interval' } else { '刷新间隔' }
    $settingsReady = Wait-DescendantByName -Window $window -Name $refreshIntervalLabel -TimeoutMilliseconds 2500
    if ($null -eq $settingsReady) {
        Write-Output ("Settings descendants: " + ((Get-DescendantNames -Window $window) -join ' | '))
        throw 'Settings page did not become ready.'
    }

    $settingsNames = Get-DescendantNames -Window $window
    $settingsRequired = if ($dashboardLabel -eq 'Dashboard') {
        @('Refresh interval', 'Language')
    } else {
        @('刷新间隔', '界面语言')
    }
    foreach ($required in $settingsRequired) {
        if ($settingsNames -notcontains $required) { throw "Settings is missing: $required" }
    }
    Save-WindowScreenshot -Window $window -Path (Join-Path $output 'settings-printwindow.png')

    # Verify that navigation still re-measures after the initial three-second
    # auto-size lock; a user resize must remain untouched, but a page switch
    # should not leave the smaller settings page inside a dashboard-sized shell.
    Start-Sleep -Milliseconds 3500
    $lockedSettingsBounds = $window.Current.BoundingRectangle
    Invoke-NavigationElement -Element @($dashboardRail)[0]
    $dashboardReady = Wait-DescendantByName -Window $window -Name 'Codex' -TimeoutMilliseconds 2500
    if ($null -eq $dashboardReady) { throw 'Dashboard did not become ready after returning from settings.' }
    Start-Sleep -Milliseconds 350
    $dashboardBoundsAfterSwitch = $window.Current.BoundingRectangle
    Invoke-NavigationElement -Element @($settingsRail)[0]
    $settingsReadyAfterLock = Wait-DescendantByName -Window $window -Name $refreshIntervalLabel -TimeoutMilliseconds 2500
    if ($null -eq $settingsReadyAfterLock) { throw 'Settings did not become ready after the auto-size lock.' }
    Start-Sleep -Milliseconds 350
    $settingsBoundsAfterSwitch = $window.Current.BoundingRectangle
    if ($settingsBoundsAfterSwitch.Height -gt $dashboardBoundsAfterSwitch.Height + 12 -or $settingsBoundsAfterSwitch.Width -gt $dashboardBoundsAfterSwitch.Width + 12) {
        throw "Settings page did not adapt after navigation: $([int]$dashboardBoundsAfterSwitch.Width)x$([int]$dashboardBoundsAfterSwitch.Height) -> $([int]$settingsBoundsAfterSwitch.Width)x$([int]$settingsBoundsAfterSwitch.Height)"
    }
    Invoke-NavigationElement -Element @($dashboardRail)[0]
    $dashboardReady = Wait-DescendantByName -Window $window -Name 'Codex' -TimeoutMilliseconds 2500
    if ($null -eq $dashboardReady) { throw 'Dashboard did not become ready after adaptive settings check.' }
    $namesAfterReturn = Get-DescendantNames -Window $window
    $expiryPattern = '^到期：\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}\(剩余\d+天\d+小时\d+分\)$|^Expires: \d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2} \(\d+d \d+h \d+m left\)$'
    if (-not ($namesAfterReturn | Where-Object { $_ -match $expiryPattern })) {
        throw 'Dashboard is missing the full expiry date and countdown format.'
    }

    Start-Sleep -Milliseconds 3500
    $transformPattern = $window.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
    if (-not $transformPattern.Current.CanResize) {
        throw 'Widget window does not expose free resize support.'
    }
    $beforeResize = $window.Current.BoundingRectangle
    $transformPattern.Resize($beforeResize.Width + 32, $beforeResize.Height + 20)
    Start-Sleep -Milliseconds 300
    $afterResize = $window.Current.BoundingRectangle
    if ($afterResize.Width -lt $beforeResize.Width + 24 -or $afterResize.Height -lt $beforeResize.Height + 12) {
        throw "Window resize did not apply: $([int]$beforeResize.Width)x$([int]$beforeResize.Height) -> $([int]$afterResize.Width)x$([int]$afterResize.Height)"
    }
    $transformPattern.Resize($beforeResize.Width, $beforeResize.Height)
    $transformPattern.Resize([Math]::Max(420, $beforeResize.Width - 32), [Math]::Max(270, $beforeResize.Height - 20))
    Start-Sleep -Milliseconds 300
    $minimumResize = $window.Current.BoundingRectangle
    if ($minimumResize.Width -lt 420 -or $minimumResize.Height -lt 270) {
        throw "Window minimum resize clipped below the adaptive floor: $([int]$minimumResize.Width)x$([int]$minimumResize.Height)"
    }
    $transformPattern.Resize($beforeResize.Width, $beforeResize.Height)
    Write-Output '[OK] UI Automation verified adaptive dashboard and compact settings layout.'
}
finally {
    if ($null -ne $window -and -not $process.HasExited) {
        [void][UiScreenshotNative]::PostMessage(
            [IntPtr]$window.Current.NativeWindowHandle,
            0x0010,
            [IntPtr]::Zero,
            [IntPtr]::Zero)
        [void]$process.WaitForExit(10000)
    }
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id
    }
    $process.Dispose()
}
