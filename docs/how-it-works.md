# How KR Popup Tamer works

Kingdoms Reborn ships its full debug symbols (`PrototypeCity-Win64-Shipping.pdb`) next to the game exe.
Every address, class layout and enum used by this project was read from that PDB, then checked with
disassembly and with read-only reads of a running game. The `tools/` folder contains the scripts used.

## Card choice windows ("Yearly Action")

The "Yearly Action - Choose an Action" window is a *rare card hand* owned by `BuildingCardSystem`:

| Field | Offset | Meaning |
|---|---|---|
| `_cardsRareHand` | +0x158 | cards of the choice currently shown (`std::vector<CardEnum>`) |
| `_rareHandData.rareHandEnum` | +0x170 | kind of choice (`RareHandEnum`) |
| `_rareHandsDataQueued` | +0x198 | FIFO of waiting choices (`std::vector<RareHandData>`, 0x28 bytes each) |

- `BuildingCardSystem::RollRareHand` always appends to the queue. It starts the next choice
  (`RollRareHandExecute`) only when the current hand is empty. `TryRefreshRareHand` does the same once per game-second.
- The hand is emptied only when the player submits a card (`GameSimulationCore::SelectRareCard` ->
  `DoneSelectRareHand`). There is no decline and no timeout.
- `BuildingCardSystem::TickRound` queues `Actions_StealBuyWood` (35) and `Actions_KidnapImmigrants` (36) every game
  year (72000 ticks) once you own the Steal / Kidnap cards. Townhall level 2/3 upgrades queue them too.

So one unanswered choice blocks every later one: prizes ("CHOOSE YOUR PRIZE"), era and biome bonuses, and the
next Yearly Action, and the queue grows by two a year. Only *hiding* the window would silently cost you those prizes.

**The patch.** `RollRareHandExecute` is a switch on `rareHandEnum`. For Townhall level 2-5 hands the game already
jumps straight to the common exit (`0x2640443`), which leaves the hand empty. The UI then never shows it
(`UMainGameUI::Tick` queues the window only for a non-empty hand), and the next queued choice starts one
game-second later. KR Popup Tamer sends cases 35 and 36 to that same exit:

```
0x26400f4  mov ecx,4   ->  jmp stub -> jmp 0x2640443     (Buy Wood / Steal)
0x2640187  mov ecx,4   ->  jmp stub -> jmp 0x2640443     (Kidnap / Immigrants)
```

## Text popups (Year Summary, ...)

Text popups live in `PopupSystem` (one FIFO per player, `GameSimulationCore+0x6a0`). `UPopupUI::Tick` shows only
the front entry, and it re-derives the window from the queue every frame. So hiding a widget does nothing; the
entry must never be added. Each popup type is created by one piece of simulation code, which builds a `PopupInfo`
and calls `GameSimulationCore::AddPopup(PopupInfo)` (vtable slot 0x868). The popup is passed by value, and in MSVC
x64 the callee destroys it. So each patch skips the temporary copy and the call together, and the caller still
destroys its own local `PopupInfo`.

Year Summary (`PopupReceiverEnum::YearlySummary` = 0x1f) is built in `PlayerOwnedManager::TickRound`:

```
0x2712ee7  mov rax,[rsi+0x188] ... call PopupInfo(copy) ... call [rbx+0x868]   ->  jmp stub -> jmp 0x2712f14
```

The other text popups are built earlier in the same function. Each patch skips the whole construction block, not
just the call, because there the `PopupInfo` is built directly into the by-value argument slot. For Wandering Traders
this is the same path the game itself takes on apocalypse maps.

| Popup | Receiver | Site -> resume | Effect of skipping |
|---|---|---|---|
| Wandering Traders | `CaravanBuyer` 0xd | `0x2712483 -> 0x27125d9` | Refuse is a no-op; Trade only opens the trade UI |
| Slave Traders | `SlaveTrader` 0xe | `0x27126f2 -> 0x2712849` | Refuse is a no-op; Buy = 5 immigrants for 300 |
| Immigrants asking to join | `YearlyImmigrationEvent` 0x1b | `0x27128ac -> 0x27128f5` | same as letting it expire: no immigrants, no gift hand |

Unanswered text popups expire by themselves after 18000 ticks (checked on round ticks), so a skipped popup behaves
exactly like one nobody answered.

## Irrigation

- **Pump capacity.** `OverlaySystem::RefreshIrrigationFill` runs a round-robin flood fill from every pump through
  the ditch tiles. Each pump gets `Building::efficiency(pump) + 16` water, where the extra 16 pays for the pump's
  own footprint ring.
  - Simulation, `0x286380c`: `lea r13d,[rax+0x10]; cmp rdi,rsi` becomes `lea r13d,[rax+rax+0x10]; cmp edi,esi`.
    The comparison is between two pointers into the same small heap block, so the 32-bit compare gives the same
    result.
  - Display: two small changes (`0x27b2a1e`, `0x279c939`) keep the "Water usage used/capacity" line correct.
- **Reach.** Human fertility is `min(95, (10 - (int)dist) * 100 / 5)` over the nearest filled ditch, computed in a
  lambda at `0x267bab0`. Changing the two constants to `20` and `50` gives `min(95, (20 - dist) * 10)`:
  95% out to 10 tiles, zero at 20.
  - The per-region scan covers 3x3 regions of 32 tiles, so 20 is within bounds.
  - No buffer is indexed by the radius.
- Both are event driven. Only `GameSimulationCore::TickSimulation` calls the refresh functions, and only for towns
  whose ditches or pumps changed. That is why a canal edit is needed after toggling.
- `Farm::_fertility` is cached at farm construction (and when an Irrigation Reservoir finishes), and it is saved.
  Existing farms therefore keep their old value until rebuilt.

## Auto-Trade 8 times a year

The Trading Company buildings you see trading "once at the end of the year" are not running their own trade
cycle. Built companies add 240/480/720 to their town's Auto-Trade capacity (`TownManager::GetMaxAutoTradeAmount`).
The town's Auto-Trade orders execute in one block of `TownManager::TickRound`, which is gated by
`Ticks % 72000 == 0` (`0x271f82a..0x271f85b`).

- `TickRound` itself runs only on round ticks (`Ticks % 9000 == 0`), from `GameSimulationCore::TickSimulation`
  through `ExecuteOnSettledPlayersAndAI`.
- The per-round amounts are recalculated just before it (`CalculateAutoTradeAmountNextRound_Helper`,
  `RefreshAutoTradeFulfillment`).

Replacing the gate's `jne` (`0x271f85b`) with a 6-byte NOP therefore runs the block every round: 8 trades a year,
using the game's own amount rules.

- The block does not read the gate's registers or flags; its live-ins are `rsp, rbp, rsi, r12, r13, r14`.
- Exactly 12 trades a year would need injected code that re-enters `TickRound` at extra ticks. That was designed
  and reviewed, but not shipped, in favour of the simpler, fail-safe change.

## Counting stubs

To show real statistics, a patched site jumps into a small stub in a page that the app allocates next to the
game module:

```
mov  eax, [player-owner + playerId offset]   ; e.g. [r14+0x68] BuildingCardSystem::_playerId
cmp  eax, [page + 0x08]                      ; local player id, kept up to date by the app
jne  skip
lock inc dword [page + 0x10 + 4*index]       ; the counter shown in the window
skip:
jmp  resume
```

EAX and the flags are dead at every resume address (checked on every path). The page starts with the magic
`KRPT`, so a restarted app finds it again through the patched jumps and the counters continue.

## Safety measures

- The exe's PE timestamp and image size must match the supported build, otherwise nothing is written.
- The 8 bytes before and after every site must match the expected code before anything is written.
- Code is written with every game thread suspended (and confirmed stopped with `GetThreadContext`), then read back.
- If any thread is stopped strictly inside a range being changed (where an old or new instruction boundary could
  fall), nothing is written and the app retries a second later.
- Unchecking an option writes the original bytes back. Nothing on disk is modified. Everything is gone when the game exits.
- Single player only: these patches change the lockstep simulation, so multiplayer peers without them would desync.
