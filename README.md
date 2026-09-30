# DarkSkies SkinGate 1.1.9 (Nuclear Option)

BepInEx plugin: Discord squadron roles → per-SteamID livery allowlist.

**Host** owns `allowlist.json` (from Discord `/sync-skins`). **Joiners** receive that allowlist automatically over **Steam NetworkingMessages** when they join (not Mirage — Mirage custom messages caused Local Client Stopped).

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

- **Everyone:** same SkinGate version (**1.1.9+**)
- **Host only:** run Discord `/sync-skins` so `allowlist.json` sits next to `SkinGate.dll`; press **F11** if already in-game
- **Joiners:** install the mod only — no allowlist file required; hangar updates when the host list arrives over Steam
- After a squadron role change: `/sync-skins` on Discord, host F11 (or rejoin) so joiners get the new list

## Config

`BepInEx\config\com.darkskies.skingate.cfg` — `AllowlistPath`, `ReloadKey` (F11), filter/enforce toggles.
