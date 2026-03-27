# package-release.ps1
# Builds a versioned release zip ready to upload to GitHub Releases.
#
# Usage:
#   .\package-release.ps1              # uses version from Plugin.cs, e.g. CCTVPlugin-v1.2.3.zip
#   .\package-release.ps1 -Version 1.2.3

param(
    [string]$Version = ""
)

# ── Resolve version ──────────────────────────────────────────────────────────
if (-not $Version) {
    # Pull from <Version> in manifest.xml
    $manifest = Join-Path $PSScriptRoot "manifest.xml"
    if (Test-Path $manifest) {
        $xml = [xml](Get-Content $manifest)
        $Version = $xml.PluginManifest.Version
    }
    if (-not $Version) { $Version = "0.0.0" }
}

$zipName = "CCTVPlugin-v$Version.zip"
$staging = Join-Path $env:TEMP "CCTVRelease-$Version"
$zipPath = Join-Path $PSScriptRoot $zipName

# ── Source paths ─────────────────────────────────────────────────────────────
# Prefer local project outputs, fall back to a Torch installation path if present
$defaultPluginSrc  = "C:\Torch\Plugins\CCTVPlugin"
$localPluginCandidates = @(
    "$PSScriptRoot\CCTVPlugin\bin\Release",
    "$PSScriptRoot\CCTVPlugin\bin\Release\net48",
    "$PSScriptRoot\CCTVPlugin\bin\Debug\net48"
)

$pluginSrc = $null
if (Test-Path $defaultPluginSrc) { $pluginSrc = $defaultPluginSrc }
else {
    foreach ($p in $localPluginCandidates) {
        if (Test-Path $p) { $pluginSrc = $p; break }
    }
}

$captureCandidates = @(
    "$PSScriptRoot\CCTVCapture\bin\x64\Release\net48",
    "$PSScriptRoot\CCTVCapture\bin\Release\net48"
)

$captureSrc = $null
foreach ($p in $captureCandidates) {
    if (Test-Path (Join-Path $p "CCTVCapture.exe")) { $captureSrc = $p; break }
}

$modSrc     = Join-Path $PSScriptRoot "CCTVMod"

# Validate required sources and provide clearer diagnostics
$missing = @()
if (-not $pluginSrc) { $missing += "plugin (searched: $defaultPluginSrc and local outputs)" }
if (-not $captureSrc) { $missing += "CCTVCapture (searched: $($captureCandidates -join ', '))" }
if (-not (Test-Path $modSrc)) { $missing += $modSrc }

if ($missing.Count -gt 0) {
    Write-Error "One or more required source paths are missing:`n  $(($missing -join "`n  "))`nBuild the solution (Release) or copy the plugin into C:\\Torch\\Plugins\\CCTVPlugin before packaging."
    exit 1
}

Write-Host "Using plugin source:  $pluginSrc" -ForegroundColor Yellow
Write-Host "Using capture source: $captureSrc" -ForegroundColor Yellow

# ── Stage files ───────────────────────────────────────────────────────────────
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging | Out-Null

# Include repository-level docs and license in the release root
foreach ($doc in @("LICENSE", "NOTICE", "README.md", "CONTRIBUTORS.md")) {
    $src = Join-Path $PSScriptRoot $doc
    if (Test-Path $src) {
        Copy-Item -Path $src -Destination $staging -Force
    }
}

# Plugin — dll + manifest only (no .pdb)
$pluginDst = Join-Path $staging "CCTVPlugin"
New-Item -ItemType Directory -Path $pluginDst | Out-Null
Get-ChildItem $pluginSrc -File | Where-Object { $_.Extension -notin @(".pdb") } |
    Copy-Item -Destination $pluginDst

# CCTVCapture — exe + config + CCTVCommon.dll (no .pdb, no .lnk)
$captureDst = Join-Path $staging "CCTVCapture"
New-Item -ItemType Directory -Path $captureDst | Out-Null
Get-ChildItem $captureSrc -File | Where-Object { $_.Extension -notin @(".pdb") -and $_.Extension -ne ".lnk" } |
    Copy-Item -Destination $captureDst

# CCTVMod — full folder (SE compiles the scripts at runtime)
Copy-Item $modSrc -Destination (Join-Path $staging "CCTVMod") -Recurse

# ── Zip ───────────────────────────────────────────────────────────────────────
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $zipPath)

Remove-Item $staging -Recurse -Force

Write-Host ""
Write-Host "Release zip created: $zipPath" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Go to https://github.com/SilentAssassin82/CCTVPlugin/releases/new"
Write-Host "  2. Tag: v$Version   Title: v$Version"
Write-Host "  3. Drag $zipName into the assets box"
Write-Host "  4. Publish"
