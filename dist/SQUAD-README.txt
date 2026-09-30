DarkSkies SkinGate 1.1.8 — install once (host and all joiners)

CRITICAL: Host and joiners must use the SAME SkinGate version (1.1.8).
Older builds with network allowlist sync cause "Local Client Stopped" on join.

Install
1. Remove old SkinGate / DarkSkies SkinGate * folders and any SkinGate zips in plugins.
2. Import DarkSkies-SkinGate-1.1.8.zip (folder must be BepInEx\plugins\DarkSkies.SkinGate).
3. Enable the mod.

Allowlist (everyone who needs squadron skins)
1. Admin runs Discord /sync-skins — it attaches allowlist.json.
2. Host already gets the file beside SkinGate.dll automatically.
3. Joiners download allowlist.json into:
   BepInEx\plugins\DarkSkies.SkinGate\allowlist.json
4. Press F11 in-game (or restart) to reload.

Without allowlist.json, players only see vanilla skins.
