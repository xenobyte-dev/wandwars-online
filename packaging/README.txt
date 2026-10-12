WAND WARS ONLINE
================

Install
  1. In Steam: right-click Wand Wars > Manage > Browse local files.
  2. Extract everything in this zip into that folder (next to WandWars.exe).
  3. Launch the game from Steam as usual.

Play
  Main menu > "online".
  - Host: "host game" creates a Steam lobby; "invite friends" opens the Steam invite list.
  - Friends: accept the invite (or "Join Game" in the Steam friends list). The game goes
    online automatically and waits for the host.
  - Host picks "start": everyone goes through the normal character select, mode and stage
    screens together (online players are extra controllers), then the match starts.
  Everyone needs the same mod version.
  Up to 8 players online (any mix of online players, local pads and CPU bots). Character select
  shows 8 slots while online. Above 4 players, King of the Coop and Spellstorm are unavailable
  (their arenas/HUD are built for 4); Eggnarok and Medley skip King of the Coop.

  Advanced: F2 opens a small panel for direct-IP play (LAN / port-forwarded UDP 27420).
  BepInEx\config\wandwarsmp.cfg: HostInputDelay (auto/off/ms) keeps things fair for online players.

Uninstall
  Delete winhttp.dll from the game folder (or the BepInEx folder too).
