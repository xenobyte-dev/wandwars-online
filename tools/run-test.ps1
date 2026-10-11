# Build plugin, run game with an autotest config, wait for it to exit, show plugin log lines.
param(
    [string]$Config = "mode=census`nseconds=20`nbots=4",
    [int]$TimeoutSec = 120,
    [string]$GameDir = "E:\Program Files (x86)\Steam\steamapps\common\Wand Wars",
    [switch]$NoBuild
)
$ErrorActionPreference = "Stop"
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
if (-not $NoBuild) {
    dotnet build "$PSScriptRoot\..\src\WandWarsMP" -c Release -v q -nologo | Select-Object -Last 3
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}
Get-Process WandWars -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Set-Content "$GameDir\wwmp_autotest.txt" $Config
$env:SteamAppId = "422110"   # run directly instead of relaunching through Steam
try {
    Start-Process "$GameDir\WandWars.exe" -WorkingDirectory $GameDir | Out-Null
    # Steam may relaunch the exe under a new PID, so track by name rather than handle.
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    Start-Sleep 3
    while ((Get-Process WandWars -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep 1 }
    if (Get-Process WandWars -ErrorAction SilentlyContinue) {
        Write-Warning "timeout, killing"
        Get-Process WandWars | Stop-Process -Force
    }
} finally {
    Remove-Item "$GameDir\wwmp_autotest.txt" -ErrorAction SilentlyContinue
}
if (Test-Path "$GameDir\BepInEx\wwmp_trace.log") { Get-Content "$GameDir\BepInEx\wwmp_trace.log" } else { Write-Warning "no trace log" }
