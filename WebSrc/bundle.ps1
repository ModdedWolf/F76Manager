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

    $tempOut = Join-Path ([System.IO.Path]::GetTempPath()) ("f76-bundle-" + [guid]::NewGuid().ToString("N") + ".js")

    $esbuildArgs = @(
        $Entry,
        "--bundle",
        "--outfile=$tempOut",
        "--format=iife",
        "--target=es2020",
        "--platform=browser",
        "--legal-comments=none",
        "--log-level=warning"
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

    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $tempOut)) {
        Remove-Item $tempOut -Force -ErrorAction SilentlyContinue
        Write-Error "esbuild failed for $(Split-Path $Entry -Leaf)"
    }

    # The output may be briefly locked by antivirus, the editor or a running app.
    $copied = $false
    for ($attempt = 1; $attempt -le 20 -and -not $copied; $attempt++) {
        try {
            [System.IO.File]::Copy($tempOut, $OutFile, $true)
            $copied = $true
        } catch {
            Start-Sleep -Milliseconds 250
        }
    }
    Remove-Item $tempOut -Force -ErrorAction SilentlyContinue
    if (-not $copied) {
        Write-Error "Could not write $OutFile - it is locked by another program. Close F76 Manager (or anything holding the file) and try again."
    }

    $sizeKb = [math]::Round((Get-Item $OutFile).Length / 1KB, 1)
    Write-Host "  -> $(Split-Path $OutFile -Leaf) ($sizeKb KB)" -ForegroundColor Green
}

Write-Host "Bundling WebSrc JS ($Configuration)..." -ForegroundColor Cyan
Invoke-Esbuild -Entry (Join-Path $webSrc "js\main.js") -OutFile (Join-Path $webSrc "js\app.bundle.js")
Invoke-Esbuild -Entry (Join-Path $webSrc "js\logs-popout.js") -OutFile (Join-Path $webSrc "js\logs-popout.bundle.js")
Invoke-Esbuild -Entry (Join-Path $webSrc "js\theme-creator.js") -OutFile (Join-Path $webSrc "js\theme-creator.bundle.js")
exit 0
