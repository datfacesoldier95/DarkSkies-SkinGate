# Deploy SkinGate.dll into the game after build
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dll = Join-Path $root 'bin\SkinGate.dll'
$destDir = 'C:\Steam\steamapps\common\Nuclear Option\BepInEx\plugins\SkinGate'
if (-not (Test-Path $dll)) {
  Write-Error "Build first: dotnet build `"$root\SkinGate.csproj`" -c Release"
}
New-Item -ItemType Directory -Force -Path $destDir | Out-Null
Copy-Item -Force $dll (Join-Path $destDir 'SkinGate.dll')
Write-Host "Deployed to $destDir\SkinGate.dll"
