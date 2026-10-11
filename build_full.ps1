$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$dotnetDir = "$env:LOCALAPPDATA\Microsoft\dotnet"
$dotnetExe = "$dotnetDir\dotnet.exe"
$env:DOTNET_ROOT = $dotnetDir
$env:PATH = "$dotnetDir;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

$baseDir = $PSScriptRoot
$proj = Join-Path $baseDir "NativeApp\Z-Station_Studio.csproj"
$outDir = Join-Path $baseDir "PublishOut"

Write-Host "Stopping any running instances..."
Get-Process -Name "Z-Station_Studio" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Write-Host "Publishing standalone single-file with native libraries..."
& $dotnetExe publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $outDir
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

$builtExe = Join-Path $outDir "Z-Station_Studio.exe"
if (-not (Test-Path $builtExe)) {
    throw "Built executable not found: $builtExe"
}

$destExe = Join-Path $baseDir "Z-Station_Studio.exe"
$copied = $false
for ($i = 0; $i -lt 10; $i++) {
    try {
        Copy-Item $builtExe $destExe -Force
        $copied = $true
        break
    }
    catch {
        Write-Host "File locked, waiting 1s before retry..."
        Start-Sleep -Seconds 1
    }
}
if (-not $copied) {
    throw "Failed to copy built executable to: $destExe"
}
Write-Host "Copied to root: $destExe"

$localApp = Join-Path $env:LOCALAPPDATA "Z-Station_Studio"
if (Test-Path $localApp) {
    Copy-Item $builtExe (Join-Path $localApp "Z-Station_Studio.exe") -Force
}

$hash = (Get-FileHash -Path $destExe -Algorithm SHA256).Hash
$ticks = (Get-Item $destExe).LastWriteTimeUtc.Ticks
Write-Host "SHA256: $hash"
Write-Host "Ticks: $ticks"

# Update version JSON files
$appVersionJson = Join-Path $baseDir "app_version.json"
$updatesDir = Join-Path $baseDir "Updates"
$destZip = Join-Path $updatesDir "Z-Station_Studio_Update.zip"

$verText = [System.IO.File]::ReadAllText($appVersionJson, [System.Text.Encoding]::UTF8)
$verJson = ConvertFrom-Json $verText
$verJson.PublishedAt = (Get-Date -Format "yyyy-MM-dd HH:mm")
$verJson.SourceExeLastWriteUtcTicks = $ticks
$verJson.SourceExeSha256 = $hash

$tempStage = Join-Path ([System.IO.Path]::GetTempPath()) ("ZStation_PackStage_" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempStage -Force | Out-Null

try {
    Copy-Item $destExe (Join-Path $tempStage "Z-Station_Studio.exe") -Force
    $manifestJson = ConvertTo-Json $verJson -Depth 5
    [System.IO.File]::WriteAllText((Join-Path $tempStage "app_version.json"), $manifestJson, [System.Text.Encoding]::UTF8)
    Copy-Item (Join-Path $updatesDir "update_source.json") (Join-Path $tempStage "update_source.json") -Force

    if (Test-Path $destZip) { Remove-Item $destZip -Force }

    Write-Host "Creating zip package: $destZip"
    [System.IO.Compression.ZipFile]::CreateFromDirectory($tempStage, $destZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

    $zipSize = (Get-Item $destZip).Length
    Write-Host "Zip package size: $zipSize bytes"

    $verJson.PackageSizeBytes = $zipSize
    $manifestFinalJson = ConvertTo-Json $verJson -Depth 5

    [System.IO.File]::WriteAllText((Join-Path $updatesDir "version.json"), $manifestFinalJson, [System.Text.Encoding]::UTF8)
    [System.IO.File]::WriteAllText($appVersionJson, $manifestFinalJson, [System.Text.Encoding]::UTF8)
    if (Test-Path $localApp) {
        [System.IO.File]::WriteAllText((Join-Path $localApp "app_version.json"), $manifestFinalJson, [System.Text.Encoding]::UTF8)
    }
}
finally {
    if (Test-Path $tempStage) { Remove-Item $tempStage -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host "Full build and packaging completed successfully!"
