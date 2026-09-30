# Deploy SkinGate.dll + meta.json into the game.
# Stable folder/id (no version) so NOSMR host/client mod lists match across updates.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dll = Join-Path $root 'bin\SkinGate.dll'
$pluginsRoot = 'C:\Steam\steamapps\common\Nuclear Option\BepInEx\plugins'
$disabledRoot = 'C:\Steam\steamapps\common\Nuclear Option\BepInEx\disabledPlugins'

$pluginCs = Get-Content (Join-Path $root 'src\SkinGatePlugin.cs') -Raw
if ($pluginCs -notmatch 'PluginVersion\s*=\s*"([^"]+)"') {
  Write-Error 'Could not find PluginVersion in SkinGatePlugin.cs'
}
$version = $Matches[1]
$modId = 'DarkSkies.SkinGate'
$destDir = Join-Path $pluginsRoot $modId

if (-not (Test-Path $dll)) {
  Write-Error "Build first: dotnet build `"$root\SkinGate.csproj`" -c Release"
}

function Remove-OldSkinGateFolders([string]$rootDir) {
  if (-not (Test-Path $rootDir)) { return }
  Get-ChildItem $rootDir -Directory -ErrorAction SilentlyContinue |
    Where-Object {
      $_.Name -eq 'SkinGate' -or
      $_.Name -eq 'DarkSkies.SkinGate' -or
      $_.Name -like 'DarkSkies SkinGate*'
    } |
    ForEach-Object {
      if ($_.FullName -eq $destDir) { return }
      $oldAllow = Join-Path $_.FullName 'allowlist.json'
      if ((Test-Path $oldAllow) -and -not (Test-Path (Join-Path $destDir 'allowlist.json'))) {
        New-Item -ItemType Directory -Force -Path $destDir | Out-Null
        Copy-Item -Force $oldAllow (Join-Path $destDir 'allowlist.json')
      }
      Remove-Item -Recurse -Force $_.FullName
      Write-Host "Removed old plugin folder: $($_.Name)"
    }
}

Remove-OldSkinGateFolders $pluginsRoot
Remove-OldSkinGateFolders $disabledRoot

# Remove leftover SkinGate zips that NOMM treats as separate mods
Get-ChildItem $pluginsRoot, $disabledRoot -File -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -match 'SkinGate.*\.zip$' } |
  ForEach-Object {
    Remove-Item -Force $_.FullName
    Write-Host "Removed leftover zip: $($_.Name)"
  }

New-Item -ItemType Directory -Force -Path $destDir | Out-Null
Copy-Item -Force $dll (Join-Path $destDir 'SkinGate.dll')

$metaPath = Join-Path $root 'dist\meta.json'
@"
{
  "id": "$modId",
  "artifact": {
    "fileName": "DarkSkies-SkinGate-$version.zip",
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
Copy-Item -Force $metaPath (Join-Path $destDir 'meta.json')

Write-Host "Deployed to $destDir\SkinGate.dll"
Write-Host "NOMM/NOSMR mod id: $modId (version $version)"
