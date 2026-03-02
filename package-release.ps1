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
$pluginSrc  = "C:\Torch\Plugins\CCTVPlugin"
$captureSrc = Join-Path $PSScriptRoot "CCTVCapture\bin\Release\net48"
$modSrc     = Join-Path $PSScriptRoot "CCTVMod"

foreach ($path in @($pluginSrc, $captureSrc, $modSrc)) {
    if (-not (Test-Path $path)) {
        Write-Error "Source not found: $path  (build the solution first)"
        exit 1
    }
}

# ── Stage files ───────────────────────────────────────────────────────────────
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging | Out-Null

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
