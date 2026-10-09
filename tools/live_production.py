# Read-only: watches every building's production statistics and work counter in the running game for a while, then
# prints per building type, separately for the local player and for AI players: how many batches finished, the most
# common amount per batch, and the work added per game tick. Use it to see the output / work speed multipliers act.
# Usage: python live_production.py [seconds=60]
import sys, time, struct, collections
import live_cardsys as L

SECONDS = float(sys.argv[1]) if len(sys.argv) > 1 else 60
CARD_NAMES = L.CARD if all(isinstance(k, int) for k in L.CARD) else {v: k for k, v in L.CARD.items()}
p = L.Proc(L.find_pid()[0]); base, _ = p.module()

def sim_and_player():
    world = p.ptr(base + 0x16852af0)                     # GWorld
    gi = p.ptr(world + 0x188)
    pc = p.ptr(p.ptr(p.ptr(gi + 0x38)) + 0x30)
    gm = p.ptr(pc + 0x5d8)
    return p.ptr(gm + 0x298), p.i32(gm + 0x488)          # GameSimulationCore, local player id

def snapshot(sim):
    bs = p.ptr(sim + 0x368)                              # GameSimulationCore::_buildingSystem
    first, count = p.vector(bs + 0x78, 8, 1_000_000)     # BuildingSystem::_buildings (unique_ptr<Building>)
    ptrs = struct.unpack("<%dQ" % count, p.read(first, 8 * count)) if count else ()
    out = {}
    for i, b in enumerate(ptrs):
        if not L.ok_ptr(b): continue
        try:
            raw = p.read(b, 0x158)
            player, enum = struct.unpack_from("<iH", raw, 0x94)
            if not raw[0x121]: continue                  # _isConstructed
            work = struct.unpack_from("<i", raw, 0xe4)[0] # _workDone100
            produced = 0
            outer_first, outer_last = struct.unpack_from("<QQ", raw, 0x140)  # _seasonalProductionPairs
            if L.ok_ptr(outer_first) and outer_last > outer_first:
                pf, pc_ = p.vector(outer_first, 8, 256)  # current bucket: vector<ResourcePair{u8 enum; i32 count@+4}>
                if pc_:
                    pairs = p.read(pf, 8 * pc_)
                    produced = sum(struct.unpack_from("<i", pairs, 8 * k + 4)[0] for k in range(pc_))
            out[i] = (player, enum, work, produced)
        except L.ReadError:
            continue
    return out

sim, me = sim_and_player()
ticks = lambda: p.i32(base + 0x61771bc)                  # Time::_Ticks
t0, prev = ticks(), snapshot(sim)
batches = collections.defaultdict(list)                  # (mine, enum) -> amounts per observed increase
work_gain = collections.defaultdict(int)                 # (mine, enum) -> work added (only rising steps)
work_steps = collections.defaultdict(list)               # (mine, enum) -> each rise of _workDone100
buildings = collections.defaultdict(set)
end = time.time() + SECONDS
while time.time() < end:
    time.sleep(0.25)
    cur = snapshot(sim)
    for i, (player, enum, work, produced) in cur.items():
        if i not in prev: continue
        key = (player == me, enum)
        buildings[key].add(i)
        _, _, w0, p0 = prev[i]
        if produced > p0: batches[key].append(produced - p0)
        if work > w0: work_gain[key] += work - w0; work_steps[key].append(work - w0)
    prev = cur
elapsed = ticks() - t0
print("local player %d, %d game ticks in %.0f s" % (me, elapsed, SECONDS))
print("%-5s %-28s %5s %8s %10s %12s %10s" % ("owner", "building", "count", "batches", "per batch", "work/tick/bld", "work step"))
for key in sorted(buildings, key=lambda k: (not k[0], CARD_NAMES.get(k[1], ""))):
    mine, enum = key
    amounts = batches.get(key, [])
    if not amounts and not work_gain.get(key): continue
    common = collections.Counter(amounts).most_common(1)[0][0] if amounts else 0
    per_tick = work_gain[key] / max(elapsed, 1) / len(buildings[key])
    steps = work_steps.get(key, [])
    step = collections.Counter(steps).most_common(1)[0][0] if steps else 0
    print("%-5s %-28s %5d %8d %10d %12.2f %10d" % ("me" if mine else "AI", CARD_NAMES.get(enum, enum), len(buildings[key]),
          len(amounts), common, per_tick, step))
