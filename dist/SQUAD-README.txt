DarkSkies SkinGate — install once (host and all joiners)

IMPORTANT: Host and joiners must use the SAME SkinGate version or the server will not appear.

1. Install BepInEx via NOMM if needed.
2. Remove any old SkinGate installs first:
   - folders named SkinGate, DarkSkies SkinGate *, or DarkSkies.SkinGate
   - any SkinGate*.zip sitting in BepInEx\plugins
3. Import DarkSkies-SkinGate-*.zip in NOMM (Add from File), or extract so you get:
   BepInEx\plugins\DarkSkies.SkinGate\
4. Enable the mod. NOMM id is DarkSkies.SkinGate (stable). Version is in the mod details.

You do NOT need allowlist.json on client PCs.
Host runs Discord /sync-skins, then hosts. Clients sync in-session.

Check BepInEx\LogOutput.log for:
  SkinGate SYNC recv … hash=…
  SkinGate SYNC ack … (on the host)
