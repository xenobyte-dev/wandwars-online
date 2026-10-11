# Build a release zip: extract into the Wand Wars folder (next to WandWars.exe) to install.
# Contents: BepInEx 5 (x86) + our plugin + a BepInEx.cfg with the required entrypoint.
param(
    [string]$BepInExZip = "",   # path to BepInEx_win_x86_5.4.23.x.zip; downloaded if omitted
    [string]$BepInExVersion = "5.4.23.5"
)
$ErrorActionPreference = "Stop"
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
$root = Resolve-Path "$PSScriptRoot\.."
$version = ([xml](Get-Content "$root\src\WandWarsMP\WandWarsMP.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$stage = "$root\dist\stage"
$out = "$root\dist\WandWarsOnline-$version.zip"

dotnet build "$root\src\WandWarsMP" -c Release -v q -nologo | Select-Object -Last 2
if ($LASTEXITCODE -ne 0) { throw "build failed" }

if (-not $BepInExZip) {
    $BepInExZip = "$root\dist\BepInEx_win_x86_$BepInExVersion.zip"
    if (-not (Test-Path $BepInExZip)) {
        New-Item -ItemType Directory -Force "$root\dist" | Out-Null
        Invoke-WebRequest "https://github.com/BepInEx/BepInEx/releases/download/v$BepInExVersion/BepInEx_win_x86_$BepInExVersion.zip" -OutFile $BepInExZip
    }
}

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive $BepInExZip $stage
New-Item -ItemType Directory -Force "$stage\BepInEx\plugins\WandWarsMP", "$stage\BepInEx\config" | Out-Null
Copy-Item "$root\src\WandWarsMP\bin\Release\WandWarsMP.dll" "$stage\BepInEx\plugins\WandWarsMP\"
Copy-Item "$root\packaging\BepInEx.cfg" "$stage\BepInEx\config\BepInEx.cfg"
Copy-Item "$root\packaging\README.txt" "$stage\WandWarsOnline-README.txt"

Remove-Item $out -ErrorAction SilentlyContinue
Compress-Archive "$stage\*" $out
Remove-Item $stage -Recurse -Force
Write-Host "Built $out"
