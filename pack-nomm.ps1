# Pack SkinGate-1.1.2.zip for NOMM (host + clients). Allowlist optional in the zip.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root 'dist'
$dll = Join-Path $root 'bin\SkinGate.dll'
$pluginAllowlist = 'C:\Steam\steamapps\common\Nuclear Option\BepInEx\plugins\SkinGate\allowlist.json'
$botAllowlist = 'C:\DarkSkies\Discord Bot\data\allowlist.json'
$outZip = Join-Path $dist 'SkinGate-1.1.2.zip'
$staging = Join-Path $env:TEMP 'SkinGate-nomm-pack'

if (-not (Test-Path $dll)) {
  Write-Error "Build first: dotnet build `"$root\SkinGate.csproj`" -c Release"
}

New-Item -ItemType Directory -Force -Path $dist, $staging | Out-Null

Copy-Item -Force $dll (Join-Path $staging 'SkinGate.dll')
Copy-Item -Force (Join-Path $dist 'meta.json') (Join-Path $staging 'meta.json')
Copy-Item -Force (Join-Path $dist 'SQUAD-README.txt') (Join-Path $staging 'SQUAD-README.txt')

$allowSrc = if (Test-Path $pluginAllowlist) { $pluginAllowlist }
  elseif (Test-Path $botAllowlist) { $botAllowlist }
  else { $null }
if ($allowSrc) {
  Copy-Item -Force $allowSrc (Join-Path $staging 'allowlist.json')
} else {
  Write-Host 'Note: no allowlist.json yet — clients do not need one; host should /sync-skins after install.'
}

if (Test-Path $outZip) { Remove-Item -Force $outZip }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $outZip -CompressionLevel Optimal

Remove-Item -Recurse -Force $staging

$desktop = [Environment]::GetFolderPath('Desktop')
Copy-Item -Force $outZip (Join-Path $desktop 'SkinGate-1.1.2.zip')

Write-Host "Created: $outZip"
Write-Host "Copied to Desktop: SkinGate-1.1.2.zip"
