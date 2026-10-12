# Wand Wars Online

Unofficial fan mod that adds **native online multiplayer** to [Wand Wars](https://store.steampowered.com/app/422110/) (Steam).
Not affiliated with or endorsed by the game's publisher.

- Online through the game's own menus: main menu → **online** → host → invite Steam friends.
- Steam peer-to-peer networking: **no port forwarding**, no servers to run.
- Up to **8 players** (online players, local controllers and CPU bots mixed).
- All lobby modes: Arcane Arena, Team Arena, Merlinball, Hexout, Team Hexout, Eggnarok
  (Hen Hunt, Last Hen Standing, King of the Coop), Medley, Spellstorm.
  Above 4 players, King of the Coop and Spellstorm are unavailable.
- Host-authoritative netcode with client-side prediction and fair-play input delay for the host.

## Install

1. Download `WandWarsOnline-<version>.zip` from [Releases](../../releases).
2. In Steam: right-click Wand Wars → Manage → Browse local files.
3. Extract the zip into that folder (next to `WandWars.exe`).
4. Launch the game from Steam as usual.

Everyone in a lobby needs the same mod version, and every player needs to own the game on Steam.

## Play

- **Host:** main menu → online → host game → invite friends (opens the Steam invite list) → start.
- **Friends:** accept the Steam invite (or "Join Game" from the friends list).
- Everyone then goes through the normal character, mode and stage screens together.

## Uninstall

Delete `winhttp.dll` from the game folder (and the `BepInEx` folder if you like).

## Building

Requires the .NET SDK and a Wand Wars install with BepInEx 5 (x86). See
`src/WandWarsMP/WandWarsMP.csproj` for the game path, then run `tools/package.ps1`.
No game files are included in this repository or in releases.
