#requires -Version 7.4
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$modulePath = Join-Path -Path $PSScriptRoot -ChildPath 'CodexDesktopRestart.Common.psm1'
Import-Module -Name $modulePath -Force -ErrorAction Stop
Assert-CodexDesktopRestartHost

# This entry point deliberately makes no directory, process, login, or Desktop state changes.
Get-CodexDesktopRestartInspection | ConvertTo-Json -Depth 30
