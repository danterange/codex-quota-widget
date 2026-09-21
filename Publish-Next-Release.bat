@echo off
setlocal EnableExtensions DisableDelayedExpansion

rem Run this file from the repository root. It refuses a dirty worktree.
cd /d "%~dp0"
title Codex Quota Widget - Publish Next Release

echo.
echo ================================================
echo   Codex Quota Widget - Publish Next Release
echo ================================================
echo.

where git >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Git was not found in PATH.
    goto :failed
)

if not exist ".git\HEAD" (
    echo [ERROR] This folder is not a Git repository: %CD%
    goto :failed
)

rem Refuse to hide uncommitted product changes by committing them automatically.
set "DIRTY="
for /f "delims=" %%S in ('git status --porcelain') do set "DIRTY=1"
if defined DIRTY (
    echo [ERROR] The worktree contains uncommitted or untracked files:
    git status --short
    goto :failed
)

for /f "usebackq delims=" %%V in (`powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$raw = [System.IO.File]::ReadAllText('CodexQuotaWidget.csproj'); $match = [regex]::Match($raw, '<Version>(\d+)\.(\d+)\.(\d+)</Version>'); if (-not $match.Success) { exit 2 }; $major = [int64]$match.Groups[1].Value; $minor = [int64]$match.Groups[2].Value; $patch = [int64]$match.Groups[3].Value + 1; if ($patch -ge 10) { $patch = 0; $minor++ }; if ($minor -ge 10) { $minor = 0; $major++ }; '{0}.{1}.{2}' -f $major, $minor, $patch"`) do set "NEXT_VERSION=%%V"

if not defined NEXT_VERSION (
    echo [ERROR] Could not read a three-part Version from CodexQuotaWidget.csproj.
    goto :failed
)

set "NEXT_TAG=v%NEXT_VERSION%"
echo Next version: %NEXT_VERSION%
echo Target tag:   %NEXT_TAG%
echo.

git rev-parse --verify -q "refs/tags/%NEXT_TAG%" >nul 2>&1
if not errorlevel 1 (
    echo [ERROR] The local tag %NEXT_TAG% already exists.
    goto :failed
)

echo [1/5] Updating the project version...
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$path = 'CodexQuotaWidget.csproj'; $next = $env:NEXT_VERSION; $raw = [System.IO.File]::ReadAllText($path); $updated = [regex]::Replace($raw, '(<Version>)\d+\.\d+\.\d+(</Version>)', ('${1}' + $next + '${2}'), 1); if ($updated -eq $raw) { throw 'Version element was not updated.' }; [System.IO.File]::WriteAllText($path, $updated, [System.Text.UTF8Encoding]::new($false))"
if errorlevel 1 (
    echo [ERROR] Version update failed.
    goto :failed
)

echo [2/5] Committing the version bump...
git add -- CodexQuotaWidget.csproj
git commit -m "Prepare release %NEXT_TAG%"
if errorlevel 1 (
    echo [ERROR] Version commit failed; no tag was created.
    goto :failed
)

echo [3/5] Creating the annotated tag...
git tag -a "%NEXT_TAG%" -m "Release %NEXT_TAG%"
if errorlevel 1 (
    echo [ERROR] Tag creation failed.
    goto :failed
)

echo [4/5] Pushing the version commit to GitHub...
git push origin HEAD
if errorlevel 1 (
    echo [ERROR] Commit push failed; the tag was not pushed.
    goto :failed
)

echo [5/5] Pushing the tag and triggering GitHub Actions...
git push origin "%NEXT_TAG%"
if errorlevel 1 (
    echo [ERROR] Tag push failed. You can retry with: git push origin %NEXT_TAG%
    goto :failed
)

echo.
echo [DONE] Pushed %NEXT_TAG%.
echo        GitHub Actions will build, test, package and publish the Release.
echo.
if defined RELEASE_NONINTERACTIVE exit /b 0
pause
exit /b 0

:failed
echo.
echo Operation failed. Files were not rolled back automatically.
if defined RELEASE_NONINTERACTIVE exit /b 1
pause
exit /b 1
