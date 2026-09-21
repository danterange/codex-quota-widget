<#
.SYNOPSIS
Builds a self-contained Windows x64 ZIP, per-user installer, and SHA-256 manifest.
.DESCRIPTION
The project Version is the release source of truth. Each run publishes into an
isolated staging directory so files from older builds cannot enter a release.
Requires the .NET 8 SDK and Inno Setup 6.
#>
[CmdletBinding()]
param(
    [string]$IsccPath,
    [ValidateSet('Release')]
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolve an explicit compiler first, then standard Inno Setup installations.
# A missing compiler fails before the expensive self-contained publish starts.
function Find-InnoCompiler {
    param([string]$RequestedPath)

    if ($RequestedPath) {
        return (Resolve-Path -LiteralPath $RequestedPath -ErrorAction Stop).Path
    }

    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw 'Inno Setup 6 was not found. Install it or pass -IsccPath with the full ISCC.exe path.'
}

$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$projectPath = Join-Path $projectRoot 'CodexQuotaWidget.csproj'
$artifactsRoot = Join-Path $projectRoot 'artifacts'
$stagingRoot = $null

try {
    [xml]$project = Get-Content -LiteralPath $projectPath -Raw
    $versionNode = $project.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $versionNode -or $versionNode.InnerText -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
        throw 'The project must define an explicit Version such as 0.1.0 or 0.2.0-beta.1.'
    }
    $version = $versionNode.InnerText
    $numericVersion = ($version -split '-')[0]
    $compiler = Find-InnoCompiler -RequestedPath $IsccPath
    $null = Get-Command 'dotnet' -ErrorAction Stop
    $iconPath = Join-Path $projectRoot 'assets\app.ico'
    if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) {
        throw "Release icon is missing: $iconPath"
    }

    $null = New-Item -ItemType Directory -Path $artifactsRoot -Force
    $artifactsRoot = (Resolve-Path -LiteralPath $artifactsRoot).Path
    $stagingRoot = Join-Path $artifactsRoot ('.build-' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $stagingRoot
    $publishRoot = Join-Path $stagingRoot 'publish'
    $packageName = "codex-quota-widget-v$version-windows-x64"

    # Ship the desktop runtime with the app so end users need no .NET install.
    # Trimming is disabled because WPF and reflection need their runtime metadata.
    $publishArguments = @(
        'publish', $projectPath, '--configuration', $Configuration,
        '--runtime', 'win-x64', '--self-contained', 'true',
        '--output', $publishRoot,
        '-p:PublishSingleFile=false', '-p:PublishTrimmed=false',
        '-p:DebugType=None', '-p:DebugSymbols=false'
    )
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'CodexQuotaWidget.exe') -PathType Leaf)) {
        throw 'Publish succeeded without the expected executable.'
    }

    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $publishRoot
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $publishRoot
    $zipPath = Join-Path $stagingRoot "$packageName.zip"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($publishRoot, $zipPath)

    $installerArguments = @(
        "/DAppVersion=$version", "/DNumericVersion=$numericVersion",
        "/DPublishDir=$publishRoot", "/DProjectDir=$projectRoot",
        "/DArtifactDir=$stagingRoot", "/DOutputName=$packageName-setup",
        (Join-Path $projectRoot 'installer\CodexQuotaWidget.iss')
    )
    & $compiler @installerArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup failed with exit code $LASTEXITCODE."
    }
    $setupPath = Join-Path $stagingRoot "$packageName-setup.exe"
    if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
        throw 'Inno Setup succeeded without the expected installer.'
    }

    # Hash exactly this run's two distributables, never a wildcard of old outputs.
    $hashLines = foreach ($artifact in @($zipPath, $setupPath)) {
        $hash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $([System.IO.Path]::GetFileName($artifact))"
    }
    $manifestPath = Join-Path $stagingRoot 'SHA256SUMS.txt'
    [System.IO.File]::WriteAllLines($manifestPath, [string[]]$hashLines, [System.Text.UTF8Encoding]::new($false))

    # Publish outputs only once both package formats have been built successfully.
    foreach ($artifact in @($zipPath, $setupPath, $manifestPath)) {
        Copy-Item -LiteralPath $artifact -Destination $artifactsRoot -Force
        Write-Output "[OK] $(Join-Path $artifactsRoot ([System.IO.Path]::GetFileName($artifact)))"
    }
}
finally {
    # Only delete this invocation's GUID directory after resolving and checking it.
    if ($null -ne $stagingRoot -and (Test-Path -LiteralPath $stagingRoot -PathType Container)) {
        $resolvedStaging = (Resolve-Path -LiteralPath $stagingRoot).Path
        $safePrefix = $artifactsRoot.TrimEnd('\') + '\'
        if (-not $resolvedStaging.StartsWith($safePrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
            [System.IO.Path]::GetFileName($resolvedStaging) -notmatch '^\.build-[0-9a-f]{32}$') {
            throw "Refusing to clean unexpected staging directory: $resolvedStaging"
        }
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}
