<#
.SYNOPSIS
    Build a WinGrid11 installer.

.DESCRIPTION
    Publishes WinGrid11 self-contained for win-x64 (no .NET prerequisite
    needed on the user's machine) and runs Inno Setup to produce a
    versioned installer at .\dist\WinGrid11-<version>-Setup.exe.

    By default the script prompts for a version, with one-keystroke
    shortcuts for patch/minor/major bumps. The chosen version is
    persisted back to <Version> in WinGrid11.csproj so the next build
    starts from there. End users just download the new installer and
    run it; Inno Setup's stable AppId means it's an in-place upgrade
    that preserves their settings (kept in %AppData%\WinGrid11\) and
    autostart preference.

.PARAMETER Version
    Override the version explicitly. Format: 0.2.0 (SemVer). Suppresses
    the interactive prompt and persists the value to the csproj.

.PARAMETER NonInteractive
    Skip the prompt and use the current csproj version as-is. Useful
    for CI builds where you don't want to pause for input.

.PARAMETER SkipPublish
    Reuse the existing .\publish folder. Useful when iterating only on
    the .iss script.

.PARAMETER Configuration
    dotnet publish configuration. Defaults to Release.

.EXAMPLE
    pwsh .\build-installer.ps1
    Prompts for the version, bumps it, persists it, then builds.

.EXAMPLE
    pwsh .\build-installer.ps1 -Version 0.3.0
    Sets the csproj version to 0.3.0 and builds - no prompt.

.EXAMPLE
    pwsh .\build-installer.ps1 -NonInteractive
    Builds with the csproj's existing version. No prompt, no bump.

.NOTES
    Requirements:
      * .NET 8 SDK
      * Inno Setup 6 (https://jrsoftware.org/isdl.php) - auto-detected.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = "Release",
    [switch]$SkipPublish,
    [switch]$NonInteractive
)

$ErrorActionPreference = "Stop"

$root        = $PSScriptRoot
$projectFile = Join-Path $root "src\WinGrid11\WinGrid11.csproj"
$publishDir  = Join-Path $root "publish"
$distDir     = Join-Path $root "dist"
$issFile     = Join-Path $root "installer\WinGrid11.iss"

# --- Helpers ----------------------------------------------------------------

function Get-CsprojVersion {
    param([string]$Path)
    try {
        $node = (Select-Xml -Path $Path -XPath "//PropertyGroup/Version" -ErrorAction Stop |
                 Select-Object -First 1)
        if ($node) { return $node.Node.InnerText.Trim() }
    } catch {}
    return $null
}

function Set-CsprojVersion {
    param([string]$Path, [string]$NewVersion)
    # Regex over the raw file (instead of [xml].Save) to preserve all
    # whitespace, comments, and existing formatting.
    $content = Get-Content -Raw -Path $Path
    $pattern = '(<Version>)[^<]*(</Version>)'
    if ($content -notmatch $pattern) {
        throw "Could not find <Version>...</Version> in $Path."
    }
    $updated = [regex]::Replace($content, $pattern, "`${1}$NewVersion`${2}")
    if ($updated -ne $content) {
        Set-Content -Path $Path -Value $updated -NoNewline -Encoding utf8
    }
}

function Get-BumpedVersion {
    param([string]$Current, [ValidateSet('major','minor','patch')][string]$Part)
    if ($Current -notmatch '^(\d+)\.(\d+)\.(\d+)(.*)$') {
        throw "Current version '$Current' isn't SemVer (expected major.minor.patch). Use -Version or pick custom."
    }
    $major = [int]$Matches[1]; $minor = [int]$Matches[2]; $patch = [int]$Matches[3]
    switch ($Part) {
        'major' { $major++; $minor = 0; $patch = 0 }
        'minor' { $minor++; $patch = 0 }
        'patch' { $patch++ }
    }
    return "$major.$minor.$patch"
}

function Test-SemVer {
    param([string]$V)
    return $V -match '^\d+\.\d+\.\d+(\.\d+)?(-[\w\.]+)?$'
}

function Read-VersionInteractive {
    param([string]$Current)

    $patch = if ($Current -match '^\d+\.\d+\.\d+') { Get-BumpedVersion -Current $Current -Part 'patch' } else { '?' }
    $minor = if ($Current -match '^\d+\.\d+\.\d+') { Get-BumpedVersion -Current $Current -Part 'minor' } else { '?' }
    $major = if ($Current -match '^\d+\.\d+\.\d+') { Get-BumpedVersion -Current $Current -Part 'major' } else { '?' }

    while ($true) {
        Write-Host ""
        Write-Host "Current version: " -NoNewline
        Write-Host $Current -ForegroundColor Cyan
        Write-Host "  [1] Patch  -> $patch"
        Write-Host "  [2] Minor  -> $minor"
        Write-Host "  [3] Major  -> $major"
        Write-Host "  [c] Custom"
        Write-Host "  [k] Keep current"
        $choice = (Read-Host "Choice [1]").Trim().ToLowerInvariant()
        if ([string]::IsNullOrEmpty($choice)) { $choice = '1' }

        switch ($choice) {
            '1' { return $patch }
            '2' { return $minor }
            '3' { return $major }
            'k' { return $Current }
            'c' {
                $custom = (Read-Host "Enter version (e.g. 0.3.0)").Trim()
                if (Test-SemVer $custom) { return $custom }
                Write-Warning "Not a valid SemVer string. Try again."
            }
            default { Write-Warning "Unknown choice '$choice'. Try again." }
        }
    }
}

# --- Resolve version --------------------------------------------------------

$currentVersion = Get-CsprojVersion -Path $projectFile
if (-not $currentVersion) { $currentVersion = "0.0.0" }

if ($Version) {
    if (-not (Test-SemVer $Version)) {
        throw "Version '$Version' isn't a valid SemVer string."
    }
}
elseif ($NonInteractive) {
    $Version = $currentVersion
}
else {
    $Version = Read-VersionInteractive -Current $currentVersion
}

# Persist the chosen version unless it's already what's in the csproj
# (avoids a no-op write that touches the file's mtime / git status).
if ($Version -ne $currentVersion) {
    Write-Host "==> Updating csproj: $currentVersion -> $Version" -ForegroundColor Cyan
    Set-CsprojVersion -Path $projectFile -NewVersion $Version
}

Write-Host "==> WinGrid11 v$Version" -ForegroundColor Cyan

# --- Locate Inno Setup compiler ahead of the long publish step --------------
$iscc = $null
foreach ($candidate in @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
)) {
    if (Test-Path $candidate) { $iscc = $candidate; break }
}
if (-not $iscc) {
    throw "Inno Setup 6 not found. Install from https://jrsoftware.org/isdl.php and re-run."
}

# --- Publish (self-contained, no .NET runtime needed by end users) ----------
if (-not $SkipPublish) {
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

    Write-Host "==> Publishing self-contained build..." -ForegroundColor Cyan
    & dotnet publish $projectFile `
        --configuration $Configuration `
        --runtime win-x64 `
        --self-contained true `
        --property:Version=$Version `
        --property:PublishReadyToRun=true `
        --property:DebugType=none `
        --property:DebugSymbols=false `
        --output $publishDir `
        --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

    # Drop pdb/xml artefacts the publish flags should have skipped but
    # sometimes leak through, to keep installer slim.
    Get-ChildItem -Path $publishDir -Recurse -Include *.pdb, *.xml -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
}
else {
    if (-not (Test-Path (Join-Path $publishDir "WinGrid11.exe"))) {
        throw "-SkipPublish set but $publishDir doesn't contain a published build."
    }
}

# --- Build installer --------------------------------------------------------
if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Force -Path $distDir | Out-Null
}

Write-Host "==> Building installer..." -ForegroundColor Cyan
& $iscc $issFile `
    "/DPublishDir=$publishDir" `
    "/DOutputDir=$distDir" `
    "/DAppVersion=$Version"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed (exit $LASTEXITCODE)." }

$installer = Join-Path $distDir "WinGrid11-$Version-Setup.exe"
if (Test-Path $installer) {
    $sizeMb = [Math]::Round((Get-Item $installer).Length / 1MB, 1)
    Write-Host ""
    Write-Host "Done. $installer ($sizeMb MB)" -ForegroundColor Green
}
else {
    Write-Warning "Installer not found at expected path: $installer"
}
