DarkSkies SkinGate 1.1.9 — install once (host and all joiners)

CRITICAL: Host and joiners must use the SAME SkinGate version (1.1.9).

Host owns allowlist.json. Joiners get it automatically over Steam when they join
(no Mirage custom messages — those caused "Local Client Stopped").

Install
1. Remove old SkinGate / DarkSkies SkinGate * folders and any SkinGate zips in plugins.
2. Import DarkSkies-SkinGate-1.1.9.zip (folder must be BepInEx\plugins\DarkSkies.SkinGate).
3. Enable the mod.

Allowlist
1. Admin runs Discord /sync-skins (updates the host PC file).
2. Host: if already in-game, press F11 (or wait ~2s) to reload + push to joiners.
3. Joiners: do nothing with allowlist.json — hangar skins come from the host on join.

Without a host allowlist (or before Steam sync finishes), players only see vanilla skins.
