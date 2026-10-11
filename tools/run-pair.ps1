# Build, then launch host (Steam install) + client (C:\dev\wandwars-client) side by side.
# Each instance gets its own autotest config; prints both trace logs when done.
param(
    [string]$HostConfig = "",
    [string]$ClientConfig = "",
    [int]$TimeoutSec = 60,
    [string]$HostDir = "E:\Program Files (x86)\Steam\steamapps\common\Wand Wars",
    [string]$ClientDir = "C:\dev\wandwars-client",
    [switch]$NoBuild
)
$ErrorActionPreference = "Stop"
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
if (-not $NoBuild) {
    dotnet build "$PSScriptRoot\..\src\WandWarsMP" -c Release -v q -nologo | Select-Object -Last 2
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}
Get-Process WandWars -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
foreach ($pair in @(@($HostDir, $HostConfig), @($ClientDir, $ClientConfig))) {
    $dir, $cfg = $pair
    Set-Content "$dir\wwmp_dev.txt" ""
    if ($cfg) { Set-Content "$dir\wwmp_autotest.txt" $cfg } else { Remove-Item "$dir\wwmp_autotest.txt" -ErrorAction SilentlyContinue }
}
# SteamManager deletes steam_appid.txt and relaunches via Steam (which refuses a 2nd copy);
# the SteamAppId env var makes Steamworks treat the process as Steam-launched instead.
$env:SteamAppId = "422110"
try {
    Start-Process "$HostDir\WandWars.exe" -WorkingDirectory $HostDir | Out-Null
    Start-Sleep 4
    Start-Process "$ClientDir\WandWars.exe" -WorkingDirectory $ClientDir | Out-Null
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    Start-Sleep 3
    while ((Get-Process WandWars -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep 1 }
    if (Get-Process WandWars -ErrorAction SilentlyContinue) { Write-Warning "timeout, killing"; Get-Process WandWars | Stop-Process -Force }
} finally {
    Remove-Item "$HostDir\wwmp_autotest.txt","$ClientDir\wwmp_autotest.txt" -ErrorAction SilentlyContinue
}
foreach ($d in $HostDir, $ClientDir) {
    "==== $d"
    if (Test-Path "$d\BepInEx\wwmp_trace.log") { Get-Content "$d\BepInEx\wwmp_trace.log" } else { "(no trace)" }
    $missing = (Select-String "$d\WandWars_Data\output_log.txt" -Pattern "script on this Behaviour" -ErrorAction SilentlyContinue).Count
    if ($missing) { Write-Warning "$missing missing-script errors" }
}
