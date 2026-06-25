param(
    [switch]$BumpPatch,
    [switch]$DebugOnly
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Bump-VersionPatch {
    param([string]$Version)
    if ($Version -match '^(\d+)\.(\d+)\.(\d+)$') {
        $patch = [int]$Matches[3] + 1
        return "$($Matches[1]).$($Matches[2]).$patch"
    }
    return $Version
}

function Test-EmbeddedWebMarker {
    param(
        [string]$ExePath,
        [string]$Marker
    )
    if (-not (Test-Path $ExePath)) { return $false }
    $bytes = [System.IO.File]::ReadAllBytes($ExePath)
    $markerBytes = [System.Text.Encoding]::UTF8.GetBytes($Marker)
    if ($markerBytes.Length -gt $bytes.Length) { return $false }
    for ($i = 0; $i -le ($bytes.Length - $markerBytes.Length); $i++) {
        $match = $true
        for ($j = 0; $j -lt $markerBytes.Length; $j++) {
            if ($bytes[$i + $j] -ne $markerBytes[$j]) {
                $match = $false
                break
            }
        }
        if ($match) { return $true }
    }
    return $false
}

$scriptDir = $PSScriptRoot
$releaseDir = "$scriptDir\..\..\Release"
$sourceDir = $scriptDir
$publishDir = "$sourceDir\bin\Release\net8.0-windows\win-x64\publish"
$versionFile = "$sourceDir\version.json"
$form1Path = "$sourceDir\Form1.cs"
$rootDir = Resolve-Path "$scriptDir\..\.."

Write-Host "--- F76 Manager Build ---" -ForegroundColor Cyan

$newVer = $null
if (Test-Path $versionFile) {
    $vData = Get-Content $versionFile | ConvertFrom-Json
    if ($BumpPatch) {
        $bumped = Bump-VersionPatch $vData.version
        if ($bumped -ne $vData.version) {
            $vData.version = $bumped
            $vData | ConvertTo-Json -Depth 10 | Set-Content $versionFile
            Write-Host "  Auto-bumped version to $bumped" -ForegroundColor Green
        }
    }
    $newVer = $vData.version
    Write-Host "Target Version: $newVer" -ForegroundColor Green

    $content = Get-Content $form1Path
    $newContent = $content -replace '(public|private) const string CurrentVersion = ".*?";', "`$1 const string CurrentVersion = `"$newVer`";"
    $newContent | Set-Content $form1Path

    $csprojPath = "$sourceDir\F76ManagerApp.csproj"
    if (Test-Path $csprojPath) {
        [xml]$csproj = Get-Content $csprojPath
        $propertyGroup = $csproj.Project.PropertyGroup
        if ($null -eq $propertyGroup) {
            $propertyGroup = $csproj.CreateElement("PropertyGroup")
            $csproj.Project.AppendChild($propertyGroup) | Out-Null
        }
        function Set-XmlProperty {
            param ($group, $name, $value)
            $elem = $group.SelectSingleNode($name)
            if ($null -eq $elem) {
                $elem = $csproj.CreateElement($name)
                $group.AppendChild($elem) | Out-Null
            }
            $elem.InnerText = $value
        }
        Set-XmlProperty $propertyGroup "InformationalVersion" $newVer
        if ($newVer -match '^\d+\.\d+\.\d+') {
            Set-XmlProperty $propertyGroup "Version" $newVer
            Set-XmlProperty $propertyGroup "FileVersion" $newVer
            Set-XmlProperty $propertyGroup "AssemblyVersion" $newVer
        } else {
            Set-XmlProperty $propertyGroup "Version" "0.0.0"
            Set-XmlProperty $propertyGroup "FileVersion" "0.0.0.0"
            Set-XmlProperty $propertyGroup "AssemblyVersion" "0.0.0.0"
            Write-Host "  Non-numeric version '$newVer' - using 0.0.0 for assembly metadata." -ForegroundColor Yellow
        }
        $csproj.Save($csprojPath)
    }
}

if ($DebugOnly) {
    Write-Host "Building Debug configuration..." -ForegroundColor Yellow
    Push-Location $sourceDir
    dotnet build -c Debug
    if (!$?) { Pop-Location; exit 1 }
    Pop-Location
    $debugExe = "$sourceDir\bin\Debug\net8.0-windows\win-x64\F76ManagerApp.exe"
    Write-Host "Debug build complete:" -ForegroundColor Green
    Write-Host "  $debugExe" -ForegroundColor Cyan
    exit 0
}

$keepFolders = @("Backups", "Bundles", "BundledThemes", "Disabled Mods", "Logs", "Profiles", "Settings", "Themes")
$procs = @("F76Manager.exe", "F76ManagerApp.exe", "F76MUpdater.exe", "msedgewebview2.exe")
foreach ($p in $procs) {
    try { cmd /c "taskkill /F /IM $p /T 2>nul" } catch {}
}
Start-Sleep -Seconds 1

if (Test-Path $releaseDir) {
    Get-ChildItem $releaseDir | ForEach-Object {
        if ($keepFolders -notcontains $_.Name) {
            Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    @("ManagedResources", "F76MUpdater.exe", "F76Updater.exe") | ForEach-Object {
        $p = Join-Path $releaseDir $_
        if (Test-Path $p) { Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue }
    }
} else {
    New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
}

$embedDir = "$sourceDir\www"
New-Item -ItemType Directory -Path $embedDir -Force | Out-Null
$mirrorDirs = @("js", "css", "assets", "locales")
foreach ($subdir in $mirrorDirs) {
    $destSub = Join-Path $embedDir $subdir
    if (Test-Path $destSub) { Remove-Item $destSub -Recurse -Force -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Path $destSub -Force | Out-Null
}
Copy-Item "$rootDir\WebSrc\index.html" -Destination $embedDir -Force
Copy-Item "$rootDir\WebSrc\js\*" -Destination "$embedDir\js" -Recurse -Force
Copy-Item "$rootDir\WebSrc\css\*" -Destination "$embedDir\css" -Recurse -Force
Copy-Item "$rootDir\WebSrc\assets\*" -Destination "$embedDir\assets" -Recurse -Force
Copy-Item "$rootDir\WebSrc\locales\*" -Destination "$embedDir\locales" -Recurse -Force
Write-Host "  Web assets synced from WebSrc." -ForegroundColor Gray

$bundledSrc = "$rootDir\WebSrc\bundled-themes"
$bundledDest = "$releaseDir\BundledThemes"
if (Test-Path $bundledSrc) {
    $packages = Get-ChildItem -Path $bundledSrc -Filter "F76Manager-Theme-*.f76theme" -ErrorAction SilentlyContinue
    if ($packages.Count -gt 0) {
        New-Item -ItemType Directory -Path $bundledDest -Force | Out-Null
        Get-ChildItem $bundledDest -Filter "F76Manager-Theme-*.f76theme" -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
        Copy-Item "$bundledSrc\F76Manager-Theme-*.f76theme" -Destination $bundledDest -Force
    }
}

Write-Host "Publishing Release build..." -ForegroundColor Yellow
Push-Location $sourceDir
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:DebugType=none /p:DebugSymbols=false
if (!$?) { Pop-Location; exit 1 }
Pop-Location

$exePath = "$publishDir\F76ManagerApp.exe"
if (-not (Test-Path $exePath)) {
    Write-Host "Error: Published exe not found at $exePath" -ForegroundColor Red
    exit 1
}

$embedMarker = "__F76_BOOT_UI_THEME"
if (-not (Test-EmbeddedWebMarker -ExePath $exePath -Marker $embedMarker)) {
    Write-Host "Warning: Published exe may not contain embedded WebSrc marker '$embedMarker'." -ForegroundColor Yellow
}

Get-ChildItem $publishDir | Where-Object {
    $_.Name -notmatch '\.pdb$' -and $_.Name -notmatch 'Updater'
} | ForEach-Object {
    if ($keepFolders -notcontains $_.Name) {
        Copy-Item $_.FullName -Destination $releaseDir -Recurse -Force
    }
}
if (Test-Path "$releaseDir\F76ManagerApp.exe") {
    Rename-Item "$releaseDir\F76ManagerApp.exe" "F76Manager.exe" -Force
}

$toolsSrc = "$scriptDir\Tools"
if (Test-Path $toolsSrc) {
    if (Test-Path "$releaseDir\Tools") { Remove-Item "$releaseDir\Tools" -Recurse -Force -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Path "$releaseDir\Tools" -Force | Out-Null
    Copy-Item "$toolsSrc\*" -Destination "$releaseDir\Tools" -Recurse -Force
}

$stagingDir = "$releaseDir\SetupStaging"
$releaseZipPath = "$releaseDir\F76Manager_Nexus.zip"
if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
if (Test-Path $releaseZipPath) { Remove-Item $releaseZipPath -Force }
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
Copy-Item "$releaseDir\F76Manager.exe" -Destination $stagingDir -Force
if (Test-Path "$releaseDir\Tools") { Copy-Item "$releaseDir\Tools" -Destination "$stagingDir\Tools" -Recurse -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($stagingDir, $releaseZipPath)
Remove-Item $stagingDir -Recurse -Force -ErrorAction SilentlyContinue

if (Test-Path $versionFile) {
    Copy-Item $versionFile -Destination $releaseDir -Force
}

Write-Host "Build complete!" -ForegroundColor Green
Write-Host "  App:   $releaseDir\F76Manager.exe" -ForegroundColor Cyan
Write-Host "  Nexus: $releaseZipPath" -ForegroundColor Cyan
