# DarkSkies SkinGate 1.1.8 (Nuclear Option)

BepInEx plugin: Discord squadron roles → per-SteamID livery allowlist (file-based `allowlist.json`).

Network allowlist sync was removed in 1.1.7+ — Mirage custom messages were disconnecting joiners ("Local Client Stopped"). **1.1.8** excludes that code from the build entirely.

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

- **Everyone:** SkinGate **1.1.8** + the same `allowlist.json` from Discord `/sync-skins` (attachment)
- Put the file in `BepInEx\plugins\DarkSkies.SkinGate\allowlist.json`
- Press **F11** (or restart) after updating the file
- Host BepInEx log must say `DarkSkies SkinGate 1.1.8 loaded` — **not** `1.1.6` / `host sync active`

## Config

`BepInEx\config\com.darkskies.skingate.cfg` — `AllowlistPath`, `ReloadKey` (F11), filter/enforce toggles.
