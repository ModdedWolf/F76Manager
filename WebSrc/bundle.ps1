param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$webSrc = $PSScriptRoot
$entry = Join-Path $webSrc "js\main.js"
$outfile = Join-Path $webSrc "js\app.bundle.js"

if (-not (Test-Path $entry)) {
    Write-Error "Bundle entry not found: $entry"
}

$esbuildArgs = @(
    $entry,
    "--bundle",
    "--outfile=$outfile",
    "--format=iife",
    "--target=es2020",
    "--platform=browser",
    "--legal-comments=none"
)

if ($Configuration -eq "Release") {
    $esbuildArgs += "--minify"
}

Write-Host "Bundling WebSrc JS ($Configuration)..." -ForegroundColor Cyan

$esbuildLocal = Join-Path $webSrc "node_modules\.bin\esbuild.cmd"
if (Test-Path $esbuildLocal) {
    & $esbuildLocal @esbuildArgs
} else {
    npx --yes esbuild@0.25.5 @esbuildArgs
}

if (-not (Test-Path $outfile)) {
    Write-Error "Bundle output was not created: $outfile"
}

$sizeKb = [math]::Round((Get-Item $outfile).Length / 1KB, 1)
Write-Host "  -> js/app.bundle.js ($sizeKb KB)" -ForegroundColor Green
