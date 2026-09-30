DarkSkies SkinGate 1.1.2 — install once (host and all joiners)

1. Install BepInEx via NOMM if needed.
2. Import SkinGate-1.1.2.zip in NOMM → Install → enable.

Airframes with no squadron skin assigned show vanilla skins.
Airframes that have a squadron skin only show that skin.

You do NOT need allowlist.json on client PCs.
The host runs Discord /sync-skins, then hosts a session.
Clients receive the allowlist from the host automatically.

Check BepInEx\LogOutput.log for:
  SkinGate SYNC recv … hash=…
  SkinGate SYNC ack … (on the host)

Host: press F11 after /sync-skins if the game is already open.
