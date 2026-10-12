# Build, then launch host (Steam install) + N clients (C:\dev\wandwars-client, wandwars-client2, ...).
param(
    [string]$HostConfig = "",
    [string]$ClientConfig = "",
    [int]$Clients = 2,
    [int]$TimeoutSec = 120,
    [string]$HostDir = "E:\Program Files (x86)\Steam\steamapps\common\Wand Wars",
    [switch]$NoBuild
)
$ErrorActionPreference = "Stop"
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
if (-not $NoBuild) {
    dotnet build "$PSScriptRoot\..\src\WandWarsMP" -c Release -v q -nologo | Select-Object -Last 2
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}
$clientDirs = @()
for ($i = 1; $i -le $Clients; $i++) { $clientDirs += $(if ($i -eq 1) { "C:\dev\wandwars-client" } else { "C:\dev\wandwars-client$i" }) }
# Only the first copy is deployed by the build; keep the others in step.
foreach ($d in $clientDirs) { Copy-Item "$HostDir\BepInEx\plugins\WandWarsMP\WandWarsMP.dll" "$d\BepInEx\plugins\WandWarsMP\" -Force }
Get-Process WandWars -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
$all = @($HostDir) + $clientDirs
foreach ($dir in $all) {
    Set-Content "$dir\wwmp_dev.txt" ""
    $cfg = if ($dir -eq $HostDir) { $HostConfig } else { $ClientConfig }
    if ($cfg) { Set-Content "$dir\wwmp_autotest.txt" $cfg }
}
$env:SteamAppId = "422110"
try {
    Start-Process "$HostDir\WandWars.exe" -WorkingDirectory $HostDir | Out-Null
    Start-Sleep 4
    foreach ($d in $clientDirs) { Start-Process "$d\WandWars.exe" -WorkingDirectory $d | Out-Null; Start-Sleep 2 }
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    Start-Sleep 3
    while ((Get-Process WandWars -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep 1 }
    if (Get-Process WandWars -ErrorAction SilentlyContinue) { Write-Warning "timeout, killing"; Get-Process WandWars | Stop-Process -Force }
} finally {
    foreach ($d in $all) { if (Test-Path "$d\wwmp_autotest.txt") { Clear-Content "$d\wwmp_autotest.txt"; [IO.File]::Delete("$d\wwmp_autotest.txt") } }
}
