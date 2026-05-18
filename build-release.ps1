# build-release.ps1 — produce the RemSound client release zip, and PROVE it carries
# no personal data before it can ship.
#
# Why this script exists
# ----------------------
# v1.5 and v1.6 were released with the developer's logs/, profiles/ and recordings/
# folders inside the zip — because those releases were hand-zipped from a publish/
# folder the app had been run from, which had accumulated that runtime data, instead
# of using this script. This version removes the human step that went wrong:
#   * It ALWAYS publishes into a fresh, empty staging folder — never a reused dir.
#   * It then SCANS the staged files AND the finished zip, and ABORTS (deletes the
#     zip, exits non-zero) if anything that could carry personal data is present.
# Never hand-zip publish/ again. Run this. If it aborts, the release does not ship.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File build-release.ps1 -Tag v1.7
#
# The -Tag value must match the GitHub release tag. The zip is named RemSound-<Tag>.zip
# because the in-app updater downloads exactly that asset name (AssetNameTemplate in
# RemSoundUpdater.cs: "RemSound-{tag}.zip").

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidatePattern('^v[0-9]+\.[0-9]+$')]
    [string]$Tag
)

$ErrorActionPreference = 'Stop'

$repo    = $PSScriptRoot
$proj    = Join-Path $repo 'src\RemSound.App\RemSound.App.csproj'
$distDir = Join-Path $repo 'dist'
$zipPath = Join-Path $distDir "RemSound-$Tag.zip"
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("remsound-release-" + [guid]::NewGuid().ToString('N'))

# Anything matching these must NEVER appear in a release. Folders by name; files by
# extension / exact name. RemSound.deps.json and RemSound.runtimeconfig.json are
# legitimate app files and are deliberately NOT matched (different names).
$forbiddenFolders = @('logs', 'profiles', 'recordings')
function Test-Forbidden([string]$path) {
    $p = $path -replace '\\', '/'
    foreach ($f in $forbiddenFolders) {
        if ($p -match "(^|/)$f/") { return $true }
    }
    if ($p -match '\.log$') { return $true }
    if ($p -match '(^|/)remsound\.config\.json$') { return $true }
    return $false
}

# 1. Fresh, empty staging folder — the whole point. The app has never run here, so
#    there is nothing to leak.
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null

Write-Host "Publishing $Tag to clean staging: $staging" -ForegroundColor Cyan
& dotnet publish $proj -c Release -o $staging | Out-Null
if ($LASTEXITCODE -ne 0) { Remove-Item $staging -Recurse -Force; throw "dotnet publish failed (exit $LASTEXITCODE)" }

# 2. Debug symbols are not personal data, but they don't belong in a release either.
Get-ChildItem -Path $staging -Filter *.pdb -Recurse | Remove-Item -Force

# 3. SAFETY CHECK on the staged files.
$bad = @()
Get-ChildItem -Path $staging -Recurse -Force | ForEach-Object {
    $rel = $_.FullName.Substring($staging.Length).TrimStart('\', '/')
    if (Test-Forbidden $rel) { $bad += $rel }
}
if ($bad.Count -gt 0) {
    Write-Host ""
    Write-Host "RELEASE ABORTED - staged folder contains files that must not ship:" -ForegroundColor Red
    $bad | Sort-Object -Unique | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Remove-Item $staging -Recurse -Force
    exit 1
}

# 4. Zip it. Keep dist/ to a single artefact — drop any prior versioned zip.
New-Item -ItemType Directory -Path $distDir -Force | Out-Null
Get-ChildItem -Path $distDir -Filter 'RemSound-v*.zip' -ErrorAction SilentlyContinue | Remove-Item -Force
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath -CompressionLevel Optimal -Force

# 5. SAFETY CHECK again, on the finished zip itself — belt and braces.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $leaked = @($zip.Entries | Where-Object { Test-Forbidden $_.FullName })
    $entryCount = $zip.Entries.Count
} finally {
    $zip.Dispose()
}
if ($leaked.Count -gt 0) {
    Write-Host ""
    Write-Host "RELEASE ABORTED - finished zip contains forbidden entries:" -ForegroundColor Red
    $leaked | ForEach-Object { Write-Host "  $($_.FullName)" -ForegroundColor Red }
    Remove-Item $zipPath -Force
    Remove-Item $staging -Recurse -Force
    exit 1
}

Remove-Item $staging -Recurse -Force
$size = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
Write-Host ""
Write-Host "OK - clean release zip verified: $zipPath ($size MB, $entryCount entries)" -ForegroundColor Green
Write-Host "     No logs / profiles / recordings / config present." -ForegroundColor Green
Write-Host ""
Write-Host "Next:" -ForegroundColor Cyan
Write-Host "  gh release create $Tag `"$zipPath`" --title `"RemSound $Tag`" --notes-file RELEASE_NOTES.md"
