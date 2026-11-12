$Desktop = [System.Environment]::GetFolderPath('Desktop')
$ShortcutPath = Join-Path $Desktop "Z-Station Studio.lnk"

# Clean up old shortcuts to avoid confusion
$oldLnks = @("HNH SFX Finder.lnk", "Z Station Studio.lnk", "z-station.lnk")
foreach ($old in $oldLnks) {
    $p = Join-Path $Desktop $old
    if (Test-Path $p) { Remove-Item -Path $p -Force -ErrorAction SilentlyContinue }
}
$ScriptDir = if (![string]::IsNullOrEmpty($PSScriptRoot)) { $PSScriptRoot } elseif ($MyInvocation.MyCommand.Path) { Split-Path -Parent $MyInvocation.MyCommand.Path } else { (Get-Location).Path }
$TargetExe = Join-Path $ScriptDir "Z-Station_Studio.exe"
if (-not (Test-Path $TargetExe)) {
    $TargetExe = Join-Path $ScriptDir "z-station.exe"
}

$WshShell = New-Object -ComObject WScript.Shell
$Shortcut = $WshShell.CreateShortcut($ShortcutPath)
$Shortcut.TargetPath = $TargetExe
$Shortcut.WorkingDirectory = $ScriptDir
$Shortcut.Description = "Z-Station Studio"
$Shortcut.IconLocation = "$TargetExe,0"

$Shortcut.Save()
Write-Host "[OK] Da tao thanh cong Shortcut tai: $ShortcutPath" -ForegroundColor Green
