# KR Popup Tamer for Kingdoms Reborn

A small Windows tray app that stops Kingdoms Reborn's recurring popups — most of all the endless
**"Yearly Action – Choose an Action"** card window — and optionally **doubles irrigation**. It patches the
running game in memory. No game files are modified, and nothing remains after the game exits.

- Pick which popups to suppress, each with its own checkbox.
- See how many windows it has suppressed, this session and in total.
- Runs in the notification area (tray). It can start with Windows and attaches whenever the game starts.

> Unofficial fan tool, not affiliated with the Kingdoms Reborn developers. **Single-player only**: the patches
> change the game's lockstep simulation. The app detects multiplayer games and switches every option off there.

## Options

| Option | Default | What happens |
|---|---|---|
| **Yearly Action** (Buy Wood / Steal, Kidnap / Immigrants) | on | The yearly card choice is never shown. It is made empty, the same way the game already treats Townhall level 2–5 rewards, so the choices queued behind it (prizes, era and biome bonuses) still appear. You don't get these action cards, including the free pick after townhall level 2/3 upgrades. |
| **Year Summary** | on | The "Year N Summary" statistics window is not added to the popup queue. Purely informational. |
| **Wandering Traders** | on | "Wandering Traders have arrived…". *Refuse* does nothing in the game; *Trade* only opens the trade window. |
| **Slave Traders** | on | "Noticing you need workers, slave traders have arrived…". *Refuse* does nothing; you lose the option to buy those 5 workers. |
| **Immigrants asking to join (yearly)** | off | "3 Immigrants wishes to join your City." Suppressing it has the same result as letting it expire unanswered: no immigrants and no "gift from the immigrants" prize. |
| **Irrigation pump capacity ×2** | off | Each Irrigation Pump fills twice as many canal tiles. The pump's "Water usage" line shows the doubled capacity. |
| **Irrigation reach ×2** | off | Canals give full (95%) fertility out to radius 10 instead of 5, fading to zero at 20 instead of 10. |

Why not simply hide the Yearly Action window? The game keeps one active card choice plus a queue behind it. An
unanswered choice blocks every later one, including prizes and era bonuses, and the game queues two new Yearly
Actions every year. One save had **349** of them waiting. Hiding the window would have silently cost those prizes.

## Requirements

- Windows 10/11 x64
- Kingdoms Reborn (Steam), the build whose exe has PE timestamp `0x698c804c`. On any other build the app shows
  **Unsupported game build** and changes nothing.
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0), if you use the small release zip

## How to run the game with it

1. Download `KRPopupTamer.zip` from [Releases](../../releases), unzip it anywhere, and run `KRPopupTamer.exe`.
   A green **KR** icon appears in the tray and the window opens.
2. Tick the popups you want to suppress (and the irrigation options, if you want them).
3. Start Kingdoms Reborn as usual from Steam, or with the **Launch Kingdoms Reborn** button.
   The app attaches automatically and applies your options within a second. The order doesn't matter: you can
   also start the app while the game is already running.
4. Play. Closing the window keeps the app in the tray. Right-click the tray icon → **Exit** to quit.

Optional: tick **Start with Windows** and **Start minimized to the tray**, and you never have to think about it again.
Run the app as administrator only if you run the game as administrator.

### Good to know

- **An already-open card choice stays.** If a Yearly Action window is already on screen when the patch is
  applied, or a save is loaded with one pending, answer it once. The rest of the queue then clears on its own,
  one per game-second.
- **Irrigation needs a recalculation.** The game recomputes irrigation only when a canal changes. After you turn
  an irrigation option on or off, place or remove one canal tile in each town.
  - Farms cache their fertility when they are built. Rebuild a farm, or build an Irrigation Reservoir nearby, to
    give it the new value. Farms built while the option is on keep their boost.
  - Irrigation changes apply to every town, AI included.
- **Unticking an option restores the original game code at once.** Exiting the app leaves the active options in
  place until the game closes, and then everything is gone.
- Popups that were already in the queue, or saved in a save file, still show once.

## How it works

The game ships its full debug symbols (PDB), so every address, class layout and enum could be read exactly.
Each option is a small set of patches:

- *Skip patches* jump over the code that creates a popup. They go through a tiny counting stub, which provides the
  statistics.
- *Byte patches* change constants (irrigation).

Before writing, the app checks the exe version and the bytes around every patch site. It writes with all game
threads suspended, refuses to write while any thread is stopped inside the code being changed, and reads the
result back. Details, addresses and reasoning are in [docs/how-it-works.md](docs/how-it-works.md).

## Build from source

```
dotnet publish src/KRPopupTamer -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

`tools/` contains the Python research tools used to build this: PDB symbol search and annotated disassembly
(`sym.py`), class/enum dumps (`pdbtypes.py`), read-only live inspectors (`live_cardsys.py` for the card system,
`live_sites.py` for the state of every patch site), and `check_catalog.py`, which verifies every patch site against
your game exe (useful after a game update).
They need `pip install capstone pefile numpy`; set `KR_EXE` if the game is not in the default Steam library path.

## License

MIT, see [LICENSE](LICENSE).
