# READ-ONLY live inspector for Kingdoms Reborn (PrototypeCity-Win64-Shipping.exe) rare-card-hand state.
#
# Access: OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ = 0x0410), then only VirtualQueryEx and
# ReadProcessMemory. Nothing is written to the game, no threads are touched, no debugger is attached.
# Every read is checked with VirtualQueryEx first (committed, not PAGE_GUARD, not PAGE_NOACCESS).
#
# Pointer chain (offsets from the PDB class dumps; every hop is type-checked by its vtable symbol):
#   GWorld (UWorldProxy) -> UWorld +0x188 OwningGameInstance -> UGameInstance +0x38 LocalPlayers[0]
#   -> ULocalPlayer(UPlayer) +0x30 PlayerController (APunPlayerController)
#      +0x2b0 MyHUD (APunHUD) -> +0x388 _mainGameUI (UMainGameUI) -> +0x2d0 RareCardHandOverlay (UOverlay)
#      +0x5d8 gameManager (AGameManager) -> +0x298 _simulation (GameSimulationCore), +0x488 _controllerPlayerId
#   GameSimulationCore +0x6b8 _cardSystem (vector<BuildingCardSystem>, elem 0x1f0) [playerId]
#   (= GameSimulationCore::cardSystem @0x26245d0: movsxd rax,edx; imul rax,rax,0x1f0; add rax,[rcx+0x6b8])
#   Second root for cross-check: GEngine +0x788 GameViewport -> +0x80 GameInstance, +0x78 World.
#
# Usage:
#   python live_cardsys.py                      one full snapshot + code integrity check
#   python live_cardsys.py --repeat 3 --interval 3
#   python live_cardsys.py --watch 60 --interval 1   one summary line per sample for 60 s
#   python live_cardsys.py --json               snapshot as JSON (for other scripts)
import argparse, ctypes, ctypes.wintypes as wt, json, os, struct, sys, time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import sym, pdbtypes  # noqa: E402  (PDB-backed; dbghelp uses a fake handle, never the game handle)

PROC_NAME = "PrototypeCity-Win64-Shipping.exe"
ACCESS = 0x0400 | 0x0010  # PROCESS_QUERY_INFORMATION | PROCESS_VM_READ
PREFERRED_BASE = sym.BASE  # 0x140000000

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)


class PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ProcessID", wt.DWORD),
                ("th32DefaultHeapID", ctypes.c_size_t), ("th32ModuleID", wt.DWORD), ("cntThreads", wt.DWORD),
                ("th32ParentProcessID", wt.DWORD), ("pcPriClassBase", wt.LONG), ("dwFlags", wt.DWORD),
                ("szExeFile", wt.WCHAR * 260)]


class MBI(ctypes.Structure):  # MEMORY_BASIC_INFORMATION (x64)
    _fields_ = [("BaseAddress", ctypes.c_uint64), ("AllocationBase", ctypes.c_uint64),
                ("AllocationProtect", wt.DWORD), ("PartitionId", wt.WORD), ("RegionSize", ctypes.c_uint64),
                ("State", wt.DWORD), ("Protect", wt.DWORD), ("Type", wt.DWORD)]


class MODULEINFO(ctypes.Structure):
    _fields_ = [("lpBaseOfDll", ctypes.c_void_p), ("SizeOfImage", wt.DWORD), ("EntryPoint", ctypes.c_void_p)]


k32.OpenProcess.argtypes = [wt.DWORD, wt.BOOL, wt.DWORD]; k32.OpenProcess.restype = wt.HANDLE
k32.CloseHandle.argtypes = [wt.HANDLE]
k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
k32.VirtualQueryEx.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.POINTER(MBI), ctypes.c_size_t]
k32.VirtualQueryEx.restype = ctypes.c_size_t
k32.CreateToolhelp32Snapshot.argtypes = [wt.DWORD, wt.DWORD]; k32.CreateToolhelp32Snapshot.restype = wt.HANDLE
k32.Process32FirstW.argtypes = [wt.HANDLE, ctypes.POINTER(PROCESSENTRY32W)]
k32.Process32NextW.argtypes = [wt.HANDLE, ctypes.POINTER(PROCESSENTRY32W)]
psapi.EnumProcessModulesEx.argtypes = [wt.HANDLE, ctypes.POINTER(wt.HMODULE), wt.DWORD, ctypes.POINTER(wt.DWORD), wt.DWORD]
psapi.GetModuleBaseNameW.argtypes = [wt.HANDLE, wt.HMODULE, wt.LPWSTR, wt.DWORD]
psapi.GetModuleInformation.argtypes = [wt.HANDLE, wt.HMODULE, ctypes.POINTER(MODULEINFO), wt.DWORD]


class ReadError(Exception):
    pass


def find_pid(name=PROC_NAME):
    snap = k32.CreateToolhelp32Snapshot(0x2, 0)  # TH32CS_SNAPPROCESS
    pids = []
    try:
        e = PROCESSENTRY32W(); e.dwSize = ctypes.sizeof(e)
        ok = k32.Process32FirstW(snap, ctypes.byref(e))
        while ok:
            if e.szExeFile.lower() == name.lower(): pids.append(e.th32ProcessID)
            ok = k32.Process32NextW(snap, ctypes.byref(e))
    finally:
        k32.CloseHandle(snap)
    return pids


def ok_ptr(v):
    return v is not None and 0x10000 <= v <= 0x7FFFFFFFFFFF


class Proc:
    def __init__(self, pid):
        self.pid = pid
        self.h = k32.OpenProcess(ACCESS, False, pid)
        if not self.h: raise SystemExit("OpenProcess(0x0410, pid=%d) failed: %d" % (pid, ctypes.get_last_error()))

    def close(self):
        if self.h: k32.CloseHandle(self.h); self.h = None

    def module(self, name=PROC_NAME):
        arr = (wt.HMODULE * 1024)(); need = wt.DWORD()
        if not psapi.EnumProcessModulesEx(self.h, arr, ctypes.sizeof(arr), ctypes.byref(need), 3):
            raise SystemExit("EnumProcessModulesEx failed: %d" % ctypes.get_last_error())
        buf = ctypes.create_unicode_buffer(260)
        for m in arr[:need.value // ctypes.sizeof(wt.HMODULE)]:
            psapi.GetModuleBaseNameW(self.h, m, buf, 260)
            if buf.value.lower() == name.lower():
                mi = MODULEINFO(); psapi.GetModuleInformation(self.h, m, ctypes.byref(mi), ctypes.sizeof(mi))
                return mi.lpBaseOfDll, mi.SizeOfImage
        raise SystemExit("module %s not found" % name)

    def _readable(self, addr, n):
        end = addr + n
        while addr < end:
            mbi = MBI()
            if not k32.VirtualQueryEx(self.h, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)): return False
            if mbi.State != 0x1000 or mbi.Protect == 0 or mbi.Protect & 0x101: return False  # MEM_COMMIT; no GUARD/NOACCESS
            addr = mbi.BaseAddress + mbi.RegionSize
        return True

    def read(self, addr, n):
        if n <= 0: return b""
        if not ok_ptr(addr) or not self._readable(addr, n): raise ReadError("unreadable 0x%x+0x%x" % (addr or 0, n))
        buf = ctypes.create_string_buffer(n); got = ctypes.c_size_t(0)
        if not k32.ReadProcessMemory(self.h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got)) or got.value != n:
            raise ReadError("RPM 0x%x+0x%x failed err=%d" % (addr, n, ctypes.get_last_error()))
        return buf.raw

    def u8(self, a): return self.read(a, 1)[0]
    def u16(self, a): return struct.unpack("<H", self.read(a, 2))[0]
    def i32(self, a): return struct.unpack("<i", self.read(a, 4))[0]
    def u64(self, a): return struct.unpack("<Q", self.read(a, 8))[0]

    def ptr(self, a):
        v = self.u64(a)
        if not ok_ptr(v): raise ReadError("bad pointer 0x%x read at 0x%x" % (v, a))
        return v

    def vector(self, a, elem, max_count=4096):
        """MSVC std::vector {first,last,end}: returns (first, count)."""
        first, last, end = struct.unpack("<QQQ", self.read(a, 24))
        if first == 0 and last == 0 and end == 0: return 0, 0
        if not (ok_ptr(first) and first <= last <= end and (last - first) % elem == 0 and (last - first) // elem <= max_count):
            raise ReadError("implausible vector at 0x%x: %x %x %x" % (a, first, last, end))
        return first, (last - first) // elem

    def vector_bool(self, a):
        """MSVC std::vector<bool> {vector<uint32> words; size_t size @+0x18}: returns list of bools."""
        first, nwords = self.vector(a, 4)
        size = self.u64(a + 0x18)
        if size > nwords * 32: raise ReadError("vector<bool> size %d > %d words" % (size, nwords))
        words = struct.unpack("<%dI" % nwords, self.read(first, nwords * 4)) if nwords else ()
        return [bool(words[i >> 5] >> (i & 31) & 1) for i in range(size)]

    def tarray(self, a, max_count=4096):
        data, num, mx = struct.unpack("<Qii", self.read(a, 16))
        if num == 0: return data, 0
        if not (ok_ptr(data) and 0 < num <= mx and num <= max_count): raise ReadError("implausible TArray at 0x%x" % a)
        return data, num

    def fstring(self, a):
        data, num = self.tarray(a, 1 << 16)
        return self.read(data, num * 2).decode("utf-16le", "replace").rstrip("\0") if num else ""


# ---------------- PDB-derived constants ----------------
def _enum(name):
    return dict(pdbtypes.enum_values(pdbtypes.lookup_type(name)))


CARD = _enum("CardEnum")
RAREHAND = _enum("RareHandEnum")
EXCL = _enum("ExclusiveUIEnum")
VIS = _enum("ESlateVisibility")


def _global(name):
    hits = [h for h in sym.find(name) if h[3] == name]
    if len(hits) != 1: raise SystemExit("global %s: %r" % (name, hits))
    return hits[0][0]


RVA_GWORLD = _global("GWorld")    # 0x16852af0, UWorldProxy { UWorld* World; }
RVA_GENGINE = _global("GEngine")  # 0x1684f468, UEngine*
# Time statics (int32), written by Time::SetTickCount @0x2707190: seconds=ticks/60, minutes=seconds/60,
# seasons=minutes/5, years=seasons/4  => 1 game year = 72000 ticks.
RVA_TIME = {k: _global("Time::_" + k) for k in ("Ticks", "Seasons", "Years")}


def cname(v, names): return "%s(%d)" % (names.get(v, "?"), v)


def vt_name(p, base, obj):
    vt = p.u64(obj)
    r = sym.at(vt - base)
    return r[0] if r and r[1] == 0 else ("0x%x (%s)" % (vt - base, r[0] + "+0x%x" % r[1] if r else "no symbol"))


def ftext(p, base, a):
    """FText @a: +0 ITextData* (TSharedRef). Display string found by pattern-matching the concrete class's
    vtable slot 2 (GetDisplayString) on disk: '48 8b 41 08 c3' -> *(FString**)(td+8) (TLocalizedTextData,
    fn 0x1ff8ad0); '48 8b 41 08 48 85 c0 75 04 48 8d 41 XX c3' -> td+8 ptr, else td+XX (TGeneratedTextData,
    e.g. 0x2a37780 with XX=0x38). Other classes (string-table texts) are reported as undecoded."""
    td = p.ptr(a)
    vt = p.u64(td)
    cls = vt_name(p, base, td)
    slot2 = p.u64(vt + 0x10) - base
    code = sym.read(slot2, 16)
    if code[:5] == bytes.fromhex("488b4108c3"):
        fs = p.ptr(td + 8)
    elif code[:12] == bytes.fromhex("488b41084885c07504488d41") and code[13] == 0xc3:
        fs = p.u64(td + 8) or td + code[12]
    else:
        return None, cls
    return p.fstring(fs), cls


# ---------------- snapshot ----------------
EXPECT = {  # object -> substring expected in the symbol of its vtable pointer
    "world": "UWorld::`vftable'", "gi": "UPunGameInstance::`vftable'", "lp": "ULocalPlayer::`vftable'",
    "pc": "APunPlayerController::`vftable'", "hud": "APunHUD::`vftable'", "gm": "AGameManager::`vftable'",
    "sim": "GameSimulationCore::`vftable'", "mgui": "UMainGameUI::`vftable'", "overlay": "UOverlay::`vftable'",
}


def snapshot(p, base):
    S = {"pid": p.pid, "base": base, "chain": [], "checks": [], "errors": []}
    chain, checks = S["chain"], S["checks"]

    def hop(key, label, addr, fn=p.ptr):
        v = fn(addr)
        chain.append((key, label, addr, v))
        return v

    def check(desc, ok, detail=""):
        checks.append((desc, bool(ok), detail))

    try:
        world = hop("world", "GWorld [base+0x%x] UWorldProxy.World" % RVA_GWORLD, base + RVA_GWORLD)
        gi = hop("gi", "UWorld+0x188 OwningGameInstance", world + 0x188)
        lp_data, lp_num = p.tarray(gi + 0x38)
        check("UGameInstance.LocalPlayers.Num >= 1", lp_num >= 1, "num=%d" % lp_num)
        lp = hop("lp", "UGameInstance+0x38 LocalPlayers[0]", lp_data)
        pc = hop("pc", "ULocalPlayer(UPlayer)+0x30 PlayerController", lp + 0x30)
        hud = hop("hud", "APlayerController+0x2b0 MyHUD", pc + 0x2b0)
        gm = hop("gm", "APunPlayerController+0x5d8 gameManager", pc + 0x5d8)
        sim = hop("sim", "AGameManager+0x298 _simulation (shared_ptr.ptr)", gm + 0x298)
        pid = hop("playerId", "AGameManager+0x488 _controllerPlayerId", gm + 0x488, p.i32)
        cs_first, cs_n = p.vector(sim + 0x6b8, 0x1f0, 64)
        chain.append(("cs_vec", "GameSimulationCore+0x6b8 _cardSystem (vector<BuildingCardSystem>) first/count", sim + 0x6b8, (cs_first, cs_n)))
        check("0 <= playerId < _cardSystem.size()", 0 <= pid < cs_n, "pid=%d size=%d" % (pid, cs_n))
        cs = cs_first + pid * 0x1f0
        chain.append(("cs", "BuildingCardSystem = first + playerId*0x1f0", cs_first, cs))
        mgui = hop("mgui", "APunHUD+0x388 _mainGameUI", hud + 0x388)
        overlay = hop("overlay", "UMainGameUI+0x2d0 RareCardHandOverlay", mgui + 0x2d0)
        S.update(world=world, gi=gi, pc=pc, hud=hud, gm=gm, sim=sim, playerId=pid, cs=cs, mgui=mgui, overlay=overlay)

        # type checks via vtable symbols
        objs = dict(world=world, gi=gi, lp=lp, pc=pc, hud=hud, gm=gm, sim=sim, mgui=mgui, overlay=overlay)
        for k, exp in EXPECT.items():
            n = vt_name(p, base, objs[k])
            check("vtable(%s) is %s" % (k, exp), exp in n, n)
        # structural cross-checks
        eng = p.ptr(base + RVA_GENGINE); gvp = p.ptr(eng + 0x788)
        gi2, world2 = p.ptr(gvp + 0x80), p.ptr(gvp + 0x78)
        check("GEngine+0x788 GameViewport +0x80 GameInstance == GWorld path", gi2 == gi, "0x%x vs 0x%x" % (gi2, gi))
        check("GEngine+0x788 GameViewport +0x78 World == GWorld", world2 == world, "0x%x vs 0x%x" % (world2, world))
        ci = p.u64(hud + 0x370)
        check("APunHUD+0x370 _controllerInterface == PC+0x5c8", ci == pc + 0x5c8, "0x%x vs 0x%x" % (ci, pc + 0x5c8))
        s_ptr, s_pid = p.u64(cs + 0x60), p.i32(cs + 0x68)
        check("BuildingCardSystem+0x60 _simulation == sim", s_ptr == sim, "0x%x" % s_ptr)
        check("BuildingCardSystem+0x68 _playerId == playerId", s_pid == pid, "%d" % s_pid)
        S["tickCount"] = p.i32(sim + 0xb8)  # GameSimulationCore::_tickCount
        S["time"] = {k: p.i32(base + r) for k, r in RVA_TIME.items()}

        # BuildingCardSystem rare-hand state
        f, n = p.vector(cs + 0x158, 2)
        S["cardsRareHand"] = list(struct.unpack("<%dH" % n, p.read(f, 2 * n))) if n else []
        S["rareHandReserved"] = p.vector_bool(cs + 0x90)
        S["rareHandEnum"] = p.u8(cs + 0x170)
        S["rareHandObjectId"] = p.i32(cs + 0x190)
        try:
            S["rareHandMessage"] = ftext(p, base, cs + 0x178)
        except ReadError as e:
            S["rareHandMessage"] = (None, str(e))
        qf, qn = p.vector(cs + 0x198, 0x28, 1 << 16)
        raw = p.read(qf, qn * 0x28) if qn else b""
        S["queued"] = []
        for i in range(qn):  # RareHandData: +0 rareHandEnum(u8), +8 FText message, +0x20 objectId
            msg = None
            if i < 3 or i == qn - 1:  # decode a few messages only (each costs several reads)
                try: msg = ftext(p, base, qf + i * 0x28 + 8)[0]
                except ReadError: pass
            S["queued"].append((raw[i * 0x28], struct.unpack_from("<i", raw, i * 0x28 + 0x20)[0], msg))
        S["isPendingCommand"] = p.u8(cs + 0x1b6)
        S["cardHandQueueCount"] = p.i32(cs + 0x1b8)
        S["rerollCountThisRound"] = p.i32(cs + 0x1b0)
        S["justRerolledRareHand"] = p.u8(cs + 0x24)

        # HUD sticky exclusive-UI queue (vector<ExclusiveUIEnum>, 1-byte elements; popped in PunTick @0x290b3a9)
        hf, hn = p.vector(hud + 0x320, 1, 256)
        S["stickyUIQueue"] = list(p.read(hf, hn)) if hn else []
        S["hudInitialized"] = p.u8(hud + 0x350)

        # MainGameUI side
        S["overlayVisibility"] = p.u8(overlay + 0xc3)  # UWidget::Visibility
        S["ui_rareHandObjectId"] = p.i32(mgui + 0x898)
        S["ui_lastRareHandReserveStatus"] = p.vector_bool(mgui + 0x7b0)
        for key, off in (("ui_text", 0x888), ("ui_text2", 0x890)):  # UTextBlock* ; UTextBlock::Text @+0x128
            try:
                tb = p.ptr(mgui + off)
                S[key] = ftext(p, base, tb + 0x128)
            except ReadError as e:
                S[key] = (None, str(e))
        box = p.ptr(mgui + 0x878)  # RareCardHandBox (UWrapBox) ; UPanelWidget::Slots @+0x108
        sd, sn = p.tarray(box + 0x108, 64)
        S["ui_cards"] = []
        for i in range(sn):
            slot = p.ptr(sd + 8 * i)
            child = p.ptr(slot + 0x30)  # UPanelSlot::Content
            cls = vt_name(p, base, child)
            ce = p.u16(child + 0x2c0) if "UBuildingPlacementButton" in cls else None  # cardStatus.cardEnum
            S["ui_cards"].append((cls, ce, p.u8(child + 0xc3)))
    except ReadError as e:
        S["errors"].append(str(e))
    return S


def queue_hist(queued):
    h = {}
    for r, _, _ in queued: h[r] = h.get(r, 0) + 1
    return ",".join("%sx%d" % (RAREHAND.get(r, str(r)), n) for r, n in sorted(h.items()))


def summary(S):
    if S["errors"]: return "ERROR " + "; ".join(S["errors"])
    q = "%d:%s" % (len(S["queued"]), queue_hist(S["queued"]))
    return ("tick=%d year=%d season=%d rareHandEnum=%s hand=[%s] reserved=%s queued=[%s] pending=%d stickyUIQueue=[%s] overlay=%s ui_cards=[%s]" % (
        S["tickCount"], S["time"]["Years"], S["time"]["Seasons"], cname(S["rareHandEnum"], RAREHAND), ",".join(CARD.get(c, hex(c)) for c in S["cardsRareHand"]),
        "".join("1" if b else "0" for b in S["rareHandReserved"]), q, S["isPendingCommand"],
        ",".join(EXCL.get(b, str(b)) for b in S["stickyUIQueue"]), cname(S["overlayVisibility"], VIS),
        ",".join(CARD.get(c, str(c)) for _, c, _ in S["ui_cards"])))


def print_snapshot(S):
    print("pid=%d module base=0x%x (delta from preferred 0x%x)" % (S["pid"], S["base"], S["base"] - PREFERRED_BASE))
    print("-- pointer chain")
    for key, label, addr, v in S["chain"]:
        vs = "(0x%x, %d)" % v if isinstance(v, tuple) else ("0x%x" % v if key != "playerId" else str(v))
        print("  %-8s %-72s @0x%x -> %s" % (key, label, addr, vs))
    print("-- checks")
    for desc, ok, detail in S["checks"]:
        print("  [%s] %s  %s" % ("OK" if ok else "FAIL", desc, detail))
    if S["errors"]:
        print("-- ERRORS: " + "; ".join(S["errors"])); return
    print("-- BuildingCardSystem @0x%x (playerId %d), GameSimulationCore::_tickCount(+0xb8)=%d  Time::_Ticks=%d _Seasons=%d _Years=%d" % (
        S["cs"], S["playerId"], S["tickCount"], S["time"]["Ticks"], S["time"]["Seasons"], S["time"]["Years"]))
    print("  _cardsRareHand (+0x158)        : [%s]" % ", ".join("%s(0x%x)" % (CARD.get(c, "?"), c) for c in S["cardsRareHand"]))
    print("  _cardsRareHandReserved (+0x90) : size(+0xa8)=%d bits=%s" % (len(S["rareHandReserved"]), S["rareHandReserved"]))
    msg, cls = S["rareHandMessage"]
    print("  _rareHandData (+0x170)         : rareHandEnum=%s objectId=%d message=%r  [%s]" % (
        cname(S["rareHandEnum"], RAREHAND), S["rareHandObjectId"], msg, cls))
    Q = S["queued"]
    print("  _rareHandsDataQueued (+0x198)  : count=%d  by enum: %s" % (len(Q), queue_hist(Q)))
    for i in sorted(set([0, 1, 2, len(Q) - 1])):
        if 0 <= i < len(Q):
            r, o, m = Q[i]
            print("      [%d] %s objectId=%d message=%r" % (i, cname(r, RAREHAND), o, m))
    print("  _isPendingCommand (+0x1b6)=%d _cardHandQueueCount (+0x1b8)=%d _rerollCountThisRound (+0x1b0)=%d justRerolledRareHand (+0x24)=%d" % (
        S["isPendingCommand"], S["cardHandQueueCount"], S["rerollCountThisRound"], S["justRerolledRareHand"]))
    print("-- APunHUD @0x%x  _isInitialized(+0x350)=%d" % (S["hud"], S["hudInitialized"]))
    print("  _stickyUIQueue (+0x320..+0x330, vector<ExclusiveUIEnum>): [%s]" % ", ".join(cname(b, EXCL) for b in S["stickyUIQueue"]))
    print("-- UMainGameUI @0x%x" % S["mgui"])
    print("  RareCardHandOverlay (+0x2d0) @0x%x UWidget::Visibility (+0xc3) = %s" % (S["overlay"], cname(S["overlayVisibility"], VIS)))
    print("  rareHandObjectId (+0x898)=%d  _lastRareHandReserveStatus (+0x7b0)=%s" % (S["ui_rareHandObjectId"], S["ui_lastRareHandReserveStatus"]))
    print("  RareCardHandText (+0x888).Text=%r  RareCardHandText2 (+0x890).Text=%r" % (S["ui_text"][0], S["ui_text2"][0]))
    print("  RareCardHandBox (+0x878) children: %s" % ["%s card=%s vis=%s" % (
        cls.split("::")[0], "%s(0x%x)" % (CARD.get(c, "?"), c) if c is not None else "-", cname(v, VIS)) for cls, c, v in S["ui_cards"]])
    print("SUMMARY " + summary(S))


# ---------------- code integrity (live vs on-disk, relocation-aware) ----------------
RANGES = [(0x27468b0, 0x27468d0), (0x26e7110, 0x26e7140), (0x290b3d0, 0x290b4a0)]


def reloc_sites(lo, hi):
    dd = sym.pe().OPTIONAL_HEADER.DATA_DIRECTORY[5]
    data = sym.pe().get_data(dd.VirtualAddress, dd.Size)
    out, i = [], 0
    while i + 8 <= len(data):
        page, bsize = struct.unpack_from("<II", data, i)
        if bsize < 8: break
        if page <= hi and page + 0x1000 >= lo - 8:
            for j in range(i + 8, i + bsize, 2):
                e = struct.unpack_from("<H", data, j)[0]
                if e >> 12 == 10 and lo - 8 < page + (e & 0xfff) < hi: out.append(page + (e & 0xfff))
        i += bsize
    return out


def integrity(p, base):
    delta = base - PREFERRED_BASE
    for lo, hi in RANGES:
        relocs = reloc_sites(lo, hi)
        disk = bytearray(sym.read(lo - 8, hi - lo + 16))
        for s in relocs:
            o = s - (lo - 8)
            struct.pack_into("<Q", disk, o, (struct.unpack_from("<Q", disk, o)[0] + delta) & (2**64 - 1))
        disk = bytes(disk[8:8 + hi - lo])
        try:
            live = p.read(base + lo, hi - lo)
        except ReadError as e:
            print("  0x%x..0x%x: %s" % (lo, hi, e)); continue
        diffs = [lo + i for i in range(hi - lo) if live[i] != disk[i]]
        print("  0x%x..0x%x (%d bytes, %d DIR64 relocs in range%s): %s" % (
            lo, hi, hi - lo, len(relocs), (" " + ",".join(hex(r) for r in relocs)) if relocs else "",
            "IDENTICAL" if not diffs else "DIFF at " + ", ".join("0x%x live=%02x disk=%02x" % (d, live[d - lo], disk[d - lo]) for d in diffs[:32])))


def main():
    ap = argparse.ArgumentParser(description="READ-ONLY Kingdoms Reborn rare-hand inspector")
    ap.add_argument("--pid", type=int)
    ap.add_argument("--repeat", type=int, default=1, help="number of full snapshots")
    ap.add_argument("--interval", type=float, default=2.0, help="seconds between samples")
    ap.add_argument("--watch", type=float, help="print one SUMMARY line per sample for this many seconds")
    ap.add_argument("--json", action="store_true", help="print the snapshot dict as JSON")
    ap.add_argument("--no-integrity", action="store_true")
    a = ap.parse_args()
    pid = a.pid
    if pid is None:
        pids = find_pid()
        if len(pids) != 1: raise SystemExit("expected one %s, found %r" % (PROC_NAME, pids))
        pid = pids[0]
    p = Proc(pid)
    try:
        base, size = p.module()
        if a.watch:
            end, last = time.time() + a.watch, None
            while time.time() < end:
                s = summary(snapshot(p, base))
                print("%s %s%s" % (time.strftime("%H:%M:%S"), s, "" if s != last else "  (unchanged)"), flush=True)
                last = s
                time.sleep(a.interval)
            return
        for i in range(a.repeat):
            if i: time.sleep(a.interval)
            S = snapshot(p, base)
            if a.json:
                print(json.dumps({k: v for k, v in S.items()}, default=str))
            else:
                print("=== snapshot %d/%d at %s" % (i + 1, a.repeat, time.strftime("%H:%M:%S")))
                print_snapshot(S)
        if not a.no_integrity and not a.json:
            print("=== code integrity (live vs disk, DIR64 relocations applied with delta 0x%x)" % (base - PREFERRED_BASE))
            integrity(p, base)
    finally:
        p.close()


if __name__ == "__main__":
    main()
