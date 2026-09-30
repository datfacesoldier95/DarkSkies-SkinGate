# Deploy SkinGate.dll + meta.json into the game (folder name = NOMM display id).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dll = Join-Path $root 'bin\SkinGate.dll'
$pluginsRoot = 'C:\Steam\steamapps\common\Nuclear Option\BepInEx\plugins'

$pluginCs = Get-Content (Join-Path $root 'src\SkinGatePlugin.cs') -Raw
if ($pluginCs -notmatch 'PluginVersion\s*=\s*"([^"]+)"') {
  Write-Error 'Could not find PluginVersion in SkinGatePlugin.cs'
}
$version = $Matches[1]
$modFolderName = "DarkSkies SkinGate $version"
$destDir = Join-Path $pluginsRoot $modFolderName

if (-not (Test-Path $dll)) {
  Write-Error "Build first: dotnet build `"$root\SkinGate.csproj`" -c Release"
}

# Remove legacy / previous version folders so NOMM shows one entry.
Get-ChildItem $pluginsRoot -Directory -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -eq 'SkinGate' -or $_.Name -like 'DarkSkies SkinGate *' } |
  ForEach-Object {
    if ($_.FullName -ne $destDir) {
      # Keep allowlist if present
      $oldAllow = Join-Path $_.FullName 'allowlist.json'
      if ((Test-Path $oldAllow) -and -not (Test-Path (Join-Path $destDir 'allowlist.json'))) {
        New-Item -ItemType Directory -Force -Path $destDir | Out-Null
        Copy-Item -Force $oldAllow (Join-Path $destDir 'allowlist.json')
      }
      Remove-Item -Recurse -Force $_.FullName
      Write-Host "Removed old plugin folder: $($_.Name)"
    }
  }

New-Item -ItemType Directory -Force -Path $destDir | Out-Null
Copy-Item -Force $dll (Join-Path $destDir 'SkinGate.dll')

$metaSrc = Join-Path $root 'dist\meta.json'
if (Test-Path $metaSrc) {
  Copy-Item -Force $metaSrc (Join-Path $destDir 'meta.json')
}

Write-Host "Deployed to $destDir\SkinGate.dll"
Write-Host "NOMM library name: $modFolderName"
