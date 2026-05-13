# Build script for a tagged RemSound release.
#
#   .\build-release.ps1 v1.0
#
# Produces dist\RemSound-v1.0.zip, ready for `gh release create`. The asset name matches
# what RemSoundUpdater expects on the GitHub Releases page (RemSound-<tag>.zip); change
# RemSoundUpdater.AssetNameTemplate if you rename here.

[CmdletBinding()]
param(
    [Parameter(Mandatory=$true, Position=0)]
    [string]$Tag
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot

Write-Host "Cleaning publish staging..." -ForegroundColor Cyan
$stage = Join-Path $repoRoot 'src\RemSound.App\bin\Release\net10.0-windows\publish'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

Write-Host "Publishing framework-dependent..." -ForegroundColor Cyan
& dotnet publish (Join-Path $repoRoot 'src\RemSound.App\RemSound.App.csproj') -c Release | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

$distDir = Join-Path $repoRoot 'dist'
if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir | Out-Null }

$zipName = "RemSound-$Tag.zip"
$zipPath = Join-Path $distDir $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Write-Host "Zipping $zipName..." -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -CompressionLevel Optimal

$size = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
Write-Host ""
Write-Host "Built $zipPath ($size MB)" -ForegroundColor Green
Write-Host ""
Write-Host "Next:"
Write-Host "  git add -A; git commit -m 'Release $Tag'; git push"
Write-Host "  gh release create $Tag $zipPath --title `"$Tag`" --notes-file RELEASE_NOTES.md"
