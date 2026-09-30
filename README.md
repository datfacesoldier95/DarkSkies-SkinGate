# DarkSkies SkinGate 1.1.4 (Nuclear Option)

BepInEx plugin: Discord squadron roles → per-SteamID livery allowlist. Host pushes the allowlist to clients over Mirage.

If an airframe has no squadron-assigned skin in its livery list, the hangar shows **vanilla builtins** for that airframe.

## Build / deploy / release

Local-only:

```powershell
cd "C:\DarkSkies\NO Skin Gate"
& "C:\DarkSkies\.dotnet\dotnet.exe" build SkinGate.csproj -c Release
.\deploy.ps1
.\pack-nomm.ps1
```

GitHub release **and** update this PC (preferred):

```powershell
cd "C:\DarkSkies\NO Skin Gate"
# bump PluginVersion in src\SkinGatePlugin.cs first
.\release.ps1 -Notes "What changed"
```

## Multiplayer

- **Host:** needs SkinGate + `allowlist.json` from Discord `/sync-skins`
- **Clients:** need SkinGate only; receive allowlist from host (ACK logged on host)
- While waiting for sync, clients show **vanilla skins only** (fail closed)

## Config

`BepInEx\config\com.darkskies.skingate.cfg` — `AllowlistPath`, `ReloadKey` (F11), filter/enforce toggles.

## Log markers

- `SkinGate SYNC send … hash=`
- `SkinGate SYNC recv … hash=`
- `SkinGate SYNC ack hash=`
