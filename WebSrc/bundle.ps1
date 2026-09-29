param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$webSrc = $PSScriptRoot

function Invoke-Esbuild {
    param(
        [string]$Entry,
        [string]$OutFile
    )

    if (-not (Test-Path $Entry)) {
        Write-Error "Bundle entry not found: $Entry"
    }

    $esbuildArgs = @(
        $Entry,
        "--bundle",
        "--outfile=$OutFile",
        "--format=iife",
        "--target=es2020",
        "--platform=browser",
        "--legal-comments=none"
    )

    if ($Configuration -eq "Release") {
        $esbuildArgs += "--minify"
    }

    $esbuildLocal = Join-Path $webSrc "node_modules\.bin\esbuild.cmd"
    if (Test-Path $esbuildLocal) {
        & $esbuildLocal @esbuildArgs
    } else {
        npx --yes esbuild@0.25.5 @esbuildArgs
    }

    if (-not (Test-Path $OutFile)) {
        Write-Error "Bundle output was not created: $OutFile"
    }

    $sizeKb = [math]::Round((Get-Item $OutFile).Length / 1KB, 1)
    Write-Host "  -> $(Split-Path $OutFile -Leaf) ($sizeKb KB)" -ForegroundColor Green
}

Write-Host "Bundling WebSrc JS ($Configuration)..." -ForegroundColor Cyan
Invoke-Esbuild -Entry (Join-Path $webSrc "js\main.js") -OutFile (Join-Path $webSrc "js\app.bundle.js")
Invoke-Esbuild -Entry (Join-Path $webSrc "js\logs-popout.js") -OutFile (Join-Path $webSrc "js\logs-popout.bundle.js")
Invoke-Esbuild -Entry (Join-Path $webSrc "js\theme-creator.js") -OutFile (Join-Path $webSrc "js\theme-creator.bundle.js")
