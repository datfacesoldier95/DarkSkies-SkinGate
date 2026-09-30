# Build -> deploy to this PC -> pack NOMM zip -> push GitHub release.
# Usage: .\release.ps1 [-Notes "changelog"]
param(
  [string]$Notes = ""
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$env:Path = [System.Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
  [System.Environment]::GetEnvironmentVariable('Path', 'User')

$dotnet = if (Test-Path 'C:\DarkSkies\.dotnet\dotnet.exe') {
  'C:\DarkSkies\.dotnet\dotnet.exe'
} else {
  'dotnet'
}
$git = 'C:\Program Files\Git\cmd\git.exe'

$pluginCs = Get-Content (Join-Path $root 'src\SkinGatePlugin.cs') -Raw
if ($pluginCs -notmatch 'PluginVersion\s*=\s*"([^"]+)"') {
  throw 'Could not find PluginVersion in SkinGatePlugin.cs'
}
$version = $Matches[1]
$tag = "v$version"
$zipName = "DarkSkies-SkinGate-$version.zip"
$displayName = "DarkSkies.SkinGate $version"

Write-Host "=== $displayName release ==="

& $dotnet build (Join-Path $root 'SkinGate.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

& (Join-Path $root 'deploy.ps1')
& (Join-Path $root 'pack-nomm.ps1')

$zipPath = Join-Path $root "dist\$zipName"
if (-not (Test-Path $zipPath)) { throw "Missing $zipPath" }

& $git add -A
$user = gh api user --jq .login
$emailId = gh api user --jq .id
$env:GIT_AUTHOR_NAME = $user
$env:GIT_AUTHOR_EMAIL = "$emailId+$user@users.noreply.github.com"
$env:GIT_COMMITTER_NAME = $env:GIT_AUTHOR_NAME
$env:GIT_COMMITTER_EMAIL = $env:GIT_AUTHOR_EMAIL

$status = & $git status --porcelain
if ($status) {
  if (-not $Notes) { $Notes = $displayName }
  & $git commit --trailer "Co-authored-by: Cursor <cursoragent@cursor.com>" -m "Release $displayName"
  & $git push
} else {
  & $git push
}

if (-not $Notes) {
  $Notes = "$displayName - host and all joiners must update. In NOMM the mod appears as '$displayName'."
}

$releaseExists = $false
cmd /c "gh release view $tag >NUL 2>&1"
if ($LASTEXITCODE -eq 0) { $releaseExists = $true }

if ($releaseExists) {
  Write-Host "Release $tag already exists - uploading asset..."
  gh release upload $tag $zipPath --clobber
} else {
  gh release create $tag $zipPath --title $displayName --notes $Notes
}

Write-Host ""
Write-Host "Local install updated (NOMM name: $displayName)."
Write-Host "GitHub: https://github.com/datfacesoldier95/DarkSkies-SkinGate/releases/tag/$tag"
