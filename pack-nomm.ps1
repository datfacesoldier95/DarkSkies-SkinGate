# Pack DarkSkies-SkinGate-<version>.zip for NOMM (host + clients).
# Stable mod id DarkSkies.SkinGate (version only in meta.artifact.version) for multiplayer match.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root 'dist'
$dll = Join-Path $root 'bin\SkinGate.dll'
$botAllowlist = 'C:\DarkSkies\Discord Bot\data\allowlist.json'
$pluginsRoot = 'C:\Steam\steamapps\common\Nuclear Option\BepInEx\plugins'
$staging = Join-Path $env:TEMP 'SkinGate-nomm-pack'

$pluginCs = Get-Content (Join-Path $root 'src\SkinGatePlugin.cs') -Raw
if ($pluginCs -notmatch 'PluginVersion\s*=\s*"([^"]+)"') {
  Write-Error 'Could not find PluginVersion in SkinGatePlugin.cs'
}
$version = $Matches[1]
$modId = 'DarkSkies.SkinGate'
$zipName = "DarkSkies-SkinGate-$version.zip"
$outZip = Join-Path $dist $zipName

if (-not (Test-Path $dll)) {
  Write-Error "Build first: dotnet build `"$root\SkinGate.csproj`" -c Release"
}

$metaPath = Join-Path $dist 'meta.json'
@"
{
  "id": "$modId",
  "artifact": {
    "fileName": "$zipName",
    "version": "$version",
    "category": "release",
    "type": "plugin",
    "gameVersion": "0.34.2",
    "downloadUrl": "",
    "downloadURL": null,
    "hash": "",
    "extends": null,
    "dependencies": [],
    "incompatibilities": []
  }
}
"@ | Set-Content -Path $metaPath -Encoding utf8

if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
$modStaging = Join-Path $staging $modId
New-Item -ItemType Directory -Force -Path $dist, $modStaging | Out-Null

Copy-Item -Force $dll (Join-Path $modStaging 'SkinGate.dll')
Copy-Item -Force $metaPath (Join-Path $modStaging 'meta.json')
Copy-Item -Force (Join-Path $dist 'SQUAD-README.txt') (Join-Path $modStaging 'SQUAD-README.txt')

$pluginAllowlist = Get-ChildItem $pluginsRoot -Recurse -Filter 'allowlist.json' -ErrorAction SilentlyContinue |
  Where-Object { $_.DirectoryName -match 'SkinGate' } |
  Select-Object -First 1 -ExpandProperty FullName

$allowSrc = if ($pluginAllowlist) { $pluginAllowlist }
  elseif (Test-Path $botAllowlist) { $botAllowlist }
  else { $null }
if ($allowSrc) {
  Copy-Item -Force $allowSrc (Join-Path $modStaging 'allowlist.json')
} else {
  Write-Host 'Note: no allowlist.json yet — clients do not need one; host should /sync-skins after install.'
}

if (Test-Path $outZip) { Remove-Item -Force $outZip }
Compress-Archive -Path (Join-Path $staging $modId) -DestinationPath $outZip -CompressionLevel Optimal

Remove-Item -Recurse -Force $staging

$desktop = [Environment]::GetFolderPath('Desktop')
Copy-Item -Force $outZip (Join-Path $desktop $zipName)

Write-Host "Created: $outZip"
Write-Host "Copied to Desktop: $zipName"
Write-Host "NOMM/NOSMR mod id: $modId (version $version)"
