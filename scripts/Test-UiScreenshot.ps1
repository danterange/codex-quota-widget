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
Add-Type -AssemblyName System.Windows.Forms
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

# Capture the target window only; desktop capture includes the actual DWM material.
function Save-WindowScreenshot {
    param(
        [Parameter(Mandatory = $true)] [System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)] [string]$Path,
        [switch]$Desktop,
        [switch]$IncludeTooltipContext
    )

    $bounds = $Window.Current.BoundingRectangle
    $rectangle = [System.Drawing.Rectangle]::new(
        [int]$bounds.Left,
        [int]$bounds.Top,
        [int]$bounds.Width,
        [int]$bounds.Height)
    if ($IncludeTooltipContext) {
        $virtualScreen = [System.Windows.Forms.SystemInformation]::VirtualScreen
        $expanded = [System.Drawing.Rectangle]::Inflate($rectangle, 70, 70)
        $rectangle = [System.Drawing.Rectangle]::Intersect($expanded, $virtualScreen)
    }
    if ($rectangle.Width -lt 300 -or $rectangle.Width -gt 1000 -or $rectangle.Height -lt 140 -or $rectangle.Height -gt 700) {
        throw "Unexpected adaptive widget size: $($rectangle.Width)x$($rectangle.Height)"
    }

    $bitmap = [System.Drawing.Bitmap]::new($rectangle.Width, $rectangle.Height)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            if ($Desktop) {
                $graphics.CopyFromScreen($rectangle.Location, [System.Drawing.Point]::Empty, $rectangle.Size)
            } else {
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
        [switch]$Settings,
        [switch]$Free,
        [ValidateSet('Night', 'Day', 'Glass')]
        [string]$Theme = 'Night'
    )

    Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    $arguments = @($(if ($Free) { '--demo-free' } else { '--demo' }), '--exit-on-close', "--capture-screenshot=$Path", "--theme=$Theme")
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

function Wait-DescendantByAutomationId {
    param(
        [Parameter(Mandatory = $true)] [System.Windows.Automation.AutomationElement]$Window,
        [Parameter(Mandatory = $true)] [string]$AutomationId,
        [int]$TimeoutMilliseconds = 2500
    )

    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        $match = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $match -and -not $match.Current.IsOffscreen) { return $match }
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
$freeDashboardShot = Join-Path $output 'free-dashboard.png'
$dayDashboardShot = Join-Path $output 'dashboard-day.png'
$glassDashboardShot = Join-Path $output 'dashboard-glass.png'
Capture-RenderScreenshot -Executable $exe -Path $dashboardShot -Theme Night
Capture-RenderScreenshot -Executable $exe -Path $dayDashboardShot -Theme Day
Capture-RenderScreenshot -Executable $exe -Path $glassDashboardShot -Theme Glass
Capture-RenderScreenshot -Executable $exe -Path $settingsShot -Settings -Theme Night
Capture-RenderScreenshot -Executable $exe -Path $freeDashboardShot -Free -Theme Night

# Free accounts can have a valid monthly window and a credit balance without 5h/7d data.
$freeProcess = Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -ArgumentList @('--demo-free', '--exit-on-close', '--theme=Night') -PassThru
$freeWindow = $null
try {
    $freeWindowCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $freeProcess.Id),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window))
    $freeDeadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $freeProcess.Refresh()
        if ($freeProcess.HasExited) { throw "Free widget exited early: $($freeProcess.ExitCode)" }
        $freeWindow = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $freeWindowCondition)
        if ($null -eq $freeWindow) { Start-Sleep -Milliseconds 200 }
    } while ($null -eq $freeWindow -and [DateTime]::UtcNow -lt $freeDeadline)
    if ($null -eq $freeWindow) { throw 'Free widget window was not found.' }
    Start-Sleep -Milliseconds 300
    $freeNames = Get-DescendantNames -Window $freeWindow
    foreach ($required in @('免费版', '月度', '剩余额度', '61,986', '刚刚更新')) {
        if ($freeNames -notcontains $required) { throw "Free dashboard is missing: $required" }
    }
    if (-not ($freeNames | Where-Object { $_ -match '^下次恢复' })) { throw 'Free dashboard is missing the earliest recovery summary.' }
    if ($freeNames -contains '会员到期时间未知') { throw 'Free dashboard should not show an unknown membership row.' }
    foreach ($controlId in @('MonthlyExpiry', 'RemainingCredits', 'EarliestRecovery', 'LastUpdated', 'ResetCredits')) {
        $freeControl = $freeWindow.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $controlId))
        if ($null -eq $freeControl) { throw "Free dashboard control missing: $controlId" }
    }
    $monthly = $freeWindow.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'MonthlyExpiry'))
    if ($monthly.Current.HelpText -notmatch '\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}') { throw 'Free monthly tooltip lacks complete reset date.' }
    $freeResetCredits = $freeWindow.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ResetCredits'))
    if ($freeResetCredits.Current.HelpText -notmatch '最早重置券到期.*\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}') { throw 'Free reset credits tooltip lacks earliest expiry.' }
    Save-WindowScreenshot -Window $freeWindow -Path (Join-Path $output 'free-dashboard-printwindow.png')
    Write-Output '[OK] Free monthly quota, remaining credits and hidden membership row passed.'
}
finally {
    if ($null -ne $freeWindow -and -not $freeProcess.HasExited) {
        [void][UiScreenshotNative]::PostMessage([IntPtr]$freeWindow.Current.NativeWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
        [void]$freeProcess.WaitForExit(10000)
    }
    if (-not $freeProcess.HasExited) { Stop-Process -Id $freeProcess.Id }
    $freeProcess.Dispose()
}
$process = Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -ArgumentList @('--demo', '--exit-on-close', '--theme=Glass', '--test-no-persist-settings') -PassThru
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
    if (($extendedStyle -band 0x40000) -ne 0) {
        throw 'Widget window still registers as a taskbar application.'
    }
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
    foreach ($controlId in @('RestartCodexButton', 'ThemeButton', 'SnapButton', 'PinButton', 'MinimizeButton', 'MaximizeButton', 'CloseButton')) {
        $control = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $controlId))
        if ($null -eq $control) { throw "Title control missing: $controlId" }
        $bounds = $control.Current.BoundingRectangle
        if ($bounds.Width -lt 24 -or $bounds.Left -lt $windowBounds.Left -or $bounds.Right -gt $windowBounds.Right) { throw "Title control clipped: $controlId" }
        if ([string]::IsNullOrWhiteSpace($control.Current.Name) -or [string]::IsNullOrWhiteSpace($control.Current.HelpText)) { throw "Title control lacks accessible tooltip text: $controlId" }
    }
    $themeButton = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'ThemeButton'))
    $initialThemeName = $themeButton.Current.Name
    foreach ($index in 1..3) {
        Invoke-NavigationElement -Element $themeButton
        Start-Sleep -Milliseconds 180
    }
    if ($themeButton.Current.Name -ne $initialThemeName) { throw "Theme cycle did not return to its initial state: $initialThemeName -> $($themeButton.Current.Name)" }
    Write-Output '[OK] Theme button completed a full three-theme cycle without persisting user settings.'
    Save-WindowScreenshot -Window $window -Path (Join-Path $output 'dashboard-printwindow.png')
    Save-WindowScreenshot -Window $window -Path (Join-Path $output 'desktop-glass.png') -Desktop
    $fiveHourExpiry = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'FiveHourExpiry'))
    if ($null -eq $fiveHourExpiry) { throw 'Five-hour expiry control is unavailable for tooltip verification.' }
    $expiryBounds = $fiveHourExpiry.Current.BoundingRectangle
    [System.Windows.Forms.Cursor]::Position = [System.Drawing.Point]::new([int]($expiryBounds.Left + 8), [int]($expiryBounds.Top + 8))
    Start-Sleep -Milliseconds 900
    Save-WindowScreenshot -Window $window -Path (Join-Path $output 'tooltip-glass.png') -Desktop -IncludeTooltipContext
    [System.Windows.Forms.Cursor]::Position = [System.Drawing.Point]::new(0, 0)
    Start-Sleep -Milliseconds 500

    Invoke-NavigationElement -Element @($settingsRail)[0]
    $refreshIntervalLabel = if ($dashboardLabel -eq 'Dashboard') { 'Refresh interval' } else { '刷新间隔' }
    $settingsReady = Wait-DescendantByAutomationId -Window $window -AutomationId 'RefreshIntervalComboBox' -TimeoutMilliseconds 2500
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
    $dashboardReady = Wait-DescendantByAutomationId -Window $window -AutomationId 'FiveHourExpiry' -TimeoutMilliseconds 2500
    if ($null -eq $dashboardReady) { throw 'Dashboard did not become ready after returning from settings.' }
    Start-Sleep -Milliseconds 350
    $dashboardBoundsAfterSwitch = $window.Current.BoundingRectangle
    Invoke-NavigationElement -Element @($settingsRail)[0]
    $settingsReadyAfterLock = Wait-DescendantByAutomationId -Window $window -AutomationId 'RefreshIntervalComboBox' -TimeoutMilliseconds 2500
    if ($null -eq $settingsReadyAfterLock) { throw 'Settings did not become ready after the auto-size lock.' }
    Start-Sleep -Milliseconds 350
    $settingsBoundsAfterSwitch = $window.Current.BoundingRectangle
    if ($settingsBoundsAfterSwitch.Height -gt $dashboardBoundsAfterSwitch.Height + 12 -or $settingsBoundsAfterSwitch.Width -gt $dashboardBoundsAfterSwitch.Width + 12) {
        throw "Settings page did not adapt after navigation: $([int]$dashboardBoundsAfterSwitch.Width)x$([int]$dashboardBoundsAfterSwitch.Height) -> $([int]$settingsBoundsAfterSwitch.Width)x$([int]$settingsBoundsAfterSwitch.Height)"
    }
    Invoke-NavigationElement -Element @($dashboardRail)[0]
    $dashboardReady = Wait-DescendantByAutomationId -Window $window -AutomationId 'FiveHourExpiry' -TimeoutMilliseconds 2500
    if ($null -eq $dashboardReady) { throw 'Dashboard did not become ready after adaptive settings check.' }
    $expiryPattern = '^剩余\d+天\d+小时\d+分$|^\d+d \d+h \d+m left$|^已到期$|^Expired$'
    if ($dashboardReady.Current.Name -notmatch $expiryPattern) {
        throw 'Dashboard is missing the full expiry date and countdown format.'
    }
    foreach ($tooltipId in @('FiveHourExpiry', 'SevenDayExpiry', 'MembershipExpiry')) {
        $tooltipControl = Wait-DescendantByAutomationId -Window $window -AutomationId $tooltipId -TimeoutMilliseconds 2500
        if ($null -eq $tooltipControl -or [string]::IsNullOrWhiteSpace($tooltipControl.Current.HelpText)) {
            throw "Expiry tooltip missing: $tooltipId"
        }
        if ($tooltipControl.Current.HelpText -notmatch '\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2}') {
            throw "Expiry tooltip lacks complete date: $tooltipId"
        }
    }

    Start-Sleep -Milliseconds 3500
    $transformPattern = $window.GetCurrentPattern([System.Windows.Automation.TransformPattern]::Pattern)
    if (-not $transformPattern.Current.CanResize) {
        throw 'Widget window does not expose free resize support.'
    }
    # Start away from the screen edge so native resize is not constrained by
    # the bottom-right startup position and every captured pixel is on-screen.
    $captureArea = [System.Windows.Forms.Screen]::FromHandle([IntPtr]$window.Current.NativeWindowHandle).WorkingArea
    $transformPattern.Move($captureArea.Left + 32, $captureArea.Top + 32)
    Start-Sleep -Milliseconds 350
    $beforeResize = $window.Current.BoundingRectangle
    $transformPattern.Resize($beforeResize.Width + 32, $beforeResize.Height + 20)
    Start-Sleep -Milliseconds 300
    $afterResize = $window.Current.BoundingRectangle
    if ($afterResize.Width -lt $beforeResize.Width + 24 -or $afterResize.Height -lt $beforeResize.Height + 12) {
        throw "Window resize did not apply: $([int]$beforeResize.Width)x$([int]$beforeResize.Height) -> $([int]$afterResize.Width)x$([int]$afterResize.Height)"
    }
    $transformPattern.Resize($beforeResize.Width, $beforeResize.Height)
    $transformPattern.Resize(300, 280)
    Start-Sleep -Milliseconds 300
    $minimumResize = $window.Current.BoundingRectangle
    if ($minimumResize.Width -lt 300 -or $minimumResize.Height -lt 160) {
        throw "Window minimum resize clipped below the adaptive floor: $([int]$minimumResize.Width)x$([int]$minimumResize.Height)"
    }
    foreach ($controlId in @('RestartCodexButton', 'ThemeButton', 'SnapButton', 'PinButton', 'MinimizeButton', 'MaximizeButton', 'CloseButton')) {
        $titleControl = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $controlId))
        $titleBounds = $titleControl.Current.BoundingRectangle
        if ($titleBounds.Left -lt $minimumResize.Left -or $titleBounds.Right -gt $minimumResize.Right) { throw "Title control clipped at the 300px minimum width: $controlId" }
    }
    $transformPattern.Resize(320, 280)
    Start-Sleep -Milliseconds 200
    Save-WindowScreenshot -Window $window -Path (Join-Path $output 'narrow.png')
    $transformPattern.Resize(600, 360)
    # UIA resize returns before WPF paints; wait before capturing the new client area.
    Start-Sleep -Milliseconds 350
    $wideBounds = $window.Current.BoundingRectangle
    if ([Math]::Abs($wideBounds.Width - 600) -gt 2 -or [Math]::Abs($wideBounds.Height - 360) -gt 2) { throw 'Wide resize did not settle' }
    Save-WindowScreenshot -Window $window -Path (Join-Path $output 'wide.png')
    $transformPattern.Resize($beforeResize.Width, $beforeResize.Height)
    # Exercise real title buttons and inspect OS state, not only their labels.
    $pin = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'PinButton'))
    Invoke-NavigationElement -Element $pin
    Start-Sleep -Milliseconds 200
    if (([UiScreenshotNative]::GetWindowLongPtr([IntPtr]$window.Current.NativeWindowHandle, -20).ToInt64() -band 0x8) -ne 0) { throw 'Unpin failed' }
    Invoke-NavigationElement -Element $pin
    Start-Sleep -Milliseconds 200
    if (([UiScreenshotNative]::GetWindowLongPtr([IntPtr]$window.Current.NativeWindowHandle, -20).ToInt64() -band 0x8) -eq 0) { throw 'Pin failed' }
    $transformPattern.Resize(600, 360)
    Start-Sleep -Milliseconds 350
    $beforeSnap = $window.Current.BoundingRectangle
    $snap = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'SnapButton'))
    Invoke-NavigationElement -Element $snap
    Start-Sleep -Milliseconds 450
    $snappedBounds = $window.Current.BoundingRectangle
    if ($snappedBounds.Width -ge ($beforeSnap.Width - 30) -or $snappedBounds.Height -ge ($beforeSnap.Height - 30)) {
        throw "Snap-to-corner did not restore a content-fit size: $beforeSnap -> $snappedBounds"
    }
    if ($snappedBounds.Width -gt 440 -or $snappedBounds.Height -gt 330) {
        throw "Snap-to-corner content-fit size is unexpectedly large: $snappedBounds"
    }
    $visibleScrollBars = @($window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ScrollBar)) |
        Where-Object { -not $_.Current.IsOffscreen -and $_.Current.BoundingRectangle.Width -gt 0 -and $_.Current.BoundingRectangle.Height -gt 0 })
    if ($visibleScrollBars.Count -gt 0) { throw 'Snap-to-corner content-fit mode left a visible scrollbar.' }
    $snapWorkArea = [System.Windows.Forms.Screen]::FromHandle([IntPtr]$window.Current.NativeWindowHandle).WorkingArea
    $snapGap = @(
        [Math]::Abs($snappedBounds.Left - ($snapWorkArea.Left + 2)),
        [Math]::Abs($snappedBounds.Right - ($snapWorkArea.Right - 2)),
        [Math]::Abs($snappedBounds.Top - ($snapWorkArea.Top + 2)),
        [Math]::Abs($snappedBounds.Bottom - ($snapWorkArea.Bottom - 2)))
    if (($snapGap | Where-Object { $_ -le 3 }).Count -lt 2) { throw "Snap-to-corner did not use a 2px work-area gap: $snappedBounds vs $snapWorkArea" }
    $maximize = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'MaximizeButton'))
    $windowPattern = $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    Invoke-NavigationElement -Element $maximize
    Start-Sleep -Milliseconds 350
    if ($windowPattern.Current.WindowVisualState -ne [System.Windows.Automation.WindowVisualState]::Maximized) { throw 'Maximize failed' }
    $maxBounds = $window.Current.BoundingRectangle
    $workArea = [System.Windows.Forms.Screen]::FromHandle([IntPtr]$window.Current.NativeWindowHandle).WorkingArea
    if ($maxBounds.Bottom -gt $workArea.Bottom + 2 -or $maxBounds.Right -gt $workArea.Right + 2) { throw "Maximized window overlaps work area: $maxBounds vs $workArea" }
    Invoke-NavigationElement -Element $maximize
    Start-Sleep -Milliseconds 350
    if ($windowPattern.Current.WindowVisualState -ne [System.Windows.Automation.WindowVisualState]::Normal) { throw 'Restore failed' }
    $restoredBounds = $window.Current.BoundingRectangle
    if ([Math]::Abs($restoredBounds.Width - $snappedBounds.Width) -gt 2 -or [Math]::Abs($restoredBounds.Height - $snappedBounds.Height) -gt 2) { throw 'Restore lost the snapped content-fit window size' }
    $minimize = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'MinimizeButton'))
    Invoke-NavigationElement -Element $minimize
    Start-Sleep -Milliseconds 350
    if ($windowPattern.Current.WindowVisualState -ne [System.Windows.Automation.WindowVisualState]::Minimized) { throw 'Minimize failed' }
    $windowPattern.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
    Start-Sleep -Milliseconds 350
    $close = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'CloseButton'))
    Invoke-NavigationElement -Element $close
    if (-not $process.WaitForExit(5000)) { throw 'Close button failed in exit-on-close mode' }
    Write-Output '[OK] Layout, narrow/wide resize, pin/unpin, maximize/restore, minimize/restore and close passed.'
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
