# PDB-backed symbol search + annotated disassembly for PrototypeCity-Win64-Shipping.exe.
# Usage:
#   python sym.py find <glob>              e.g. find "UMainGameUI::*RareCard*"
#   python sym.py dis <symbol|0xRVA> [max_insns]
#   python sym.py at <0xRVA>               symbol containing an RVA
#   python sym.py xref <symbol|0xRVA>      direct call/jmp/lea references to the target (slow: scans .text)
import ctypes, ctypes.wintypes as wt, sys, os, re, struct
import pefile, capstone

EXE = os.environ.get("KR_EXE", r"D:\SteamLibrary\steamapps\common\Kingdoms Reborn\PunCity\Binaries\Win64\PrototypeCity-Win64-Shipping.exe")
BASE = 0x140000000

dbghelp = ctypes.WinDLL(os.path.join(os.environ["SystemRoot"], "System32", "dbghelp.dll"))
MAX_NAME = 2000

class SYMBOL_INFOW(ctypes.Structure):
    _fields_ = [("SizeOfStruct", wt.ULONG), ("TypeIndex", wt.ULONG), ("Reserved", ctypes.c_uint64 * 2),
                ("Index", wt.ULONG), ("Size", wt.ULONG), ("ModBase", ctypes.c_uint64), ("Flags", wt.ULONG),
                ("Value", ctypes.c_uint64), ("Address", ctypes.c_uint64), ("Register", wt.ULONG),
                ("Scope", wt.ULONG), ("Tag", wt.ULONG), ("NameLen", wt.ULONG), ("MaxNameLen", wt.ULONG),
                ("Name", wt.WCHAR * MAX_NAME)]

ENUMCB = ctypes.WINFUNCTYPE(wt.BOOL, ctypes.POINTER(SYMBOL_INFOW), wt.ULONG, ctypes.c_void_p)
HPROC = wt.HANDLE(0x1234)
dbghelp.SymSetOptions(0x2 | 0x10000)  # UNDNAME | ALLOW_ABSOLUTE (no deferred loads: SymFromAddr must see symbols)
if not dbghelp.SymInitializeW(HPROC, ctypes.c_wchar_p(os.path.dirname(EXE)), False):
    sys.exit("SymInitialize failed")
dbghelp.SymLoadModuleExW.restype = ctypes.c_uint64
dbghelp.SymLoadModuleExW.argtypes = [wt.HANDLE, wt.HANDLE, ctypes.c_wchar_p, ctypes.c_wchar_p, ctypes.c_uint64, wt.DWORD, ctypes.c_void_p, wt.DWORD]
if not dbghelp.SymLoadModuleExW(HPROC, None, EXE, None, BASE, 0, None, 0):
    sys.exit("SymLoadModuleEx failed: %d" % ctypes.get_last_error())

def find(mask):
    out = []
    def cb(p, size, ctx):
        s = p.contents
        out.append((s.Address - BASE, s.Size, s.Tag, s.Name[:s.NameLen]))
        return True
    dbghelp.SymEnumSymbolsW(HPROC, ctypes.c_uint64(BASE), ctypes.c_wchar_p(mask), ENUMCB(cb), None)
    return sorted(out)

_addr_cache = {}
SIZEOF_SYMBOL_INFOW = 88  # sizeof(SYMBOL_INFOW) with Name[1], as dbghelp expects

def at(rva):
    if rva in _addr_cache: return _addr_cache[rva]
    s = SYMBOL_INFOW(); s.SizeOfStruct = SIZEOF_SYMBOL_INFOW; s.MaxNameLen = MAX_NAME
    disp = ctypes.c_uint64(0)
    r = None
    if dbghelp.SymFromAddrW(HPROC, ctypes.c_uint64(BASE + rva), ctypes.byref(disp), ctypes.byref(s)):
        r = (s.Name[:s.NameLen], disp.value, s.Address - BASE, s.Size)
    _addr_cache[rva] = r
    return r

def by_name(name):
    s = SYMBOL_INFOW(); s.SizeOfStruct = SIZEOF_SYMBOL_INFOW; s.MaxNameLen = MAX_NAME
    if dbghelp.SymFromNameW(HPROC, ctypes.c_wchar_p(name), ctypes.byref(s)):
        return s.Address - BASE, s.Size
    hits = find(name)
    hits = [h for h in hits if h[1] > 0] or hits
    if len(hits) == 1: return hits[0][0], hits[0][1]
    if not hits: sys.exit("no symbol " + name)
    for h in hits[:40]: print("  candidate 0x%x size=%d %s" % (h[0], h[1], h[3]))
    sys.exit("ambiguous")

_pe = None
def pe():
    global _pe
    if _pe is None: _pe = pefile.PE(EXE, fast_load=True)
    return _pe

def read(rva, n):
    return pe().get_data(rva, n)

def label(rva):
    r = at(rva)
    if not r: return ""
    name, disp, _, _ = r
    return name + ("+0x%x" % disp if disp else "")

def disasm(rva, size=None, max_insns=4000):
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64); md.detail = False
    size = size or 0x4000
    code = read(rva, size)
    n = 0
    for ins in md.disasm(code, BASE + rva):
        n += 1
        text = "%s %s" % (ins.mnemonic, ins.op_str)
        note = ""
        m = re.search(r"\[rip ([+-]) 0x([0-9a-f]+)\]", ins.op_str)
        if m:
            off = int(m.group(2), 16) * (1 if m.group(1) == "+" else -1)
            tgt = ins.address + ins.size + off - BASE
            note = "  ; 0x%x %s" % (tgt, label(tgt))
            if ins.mnemonic == "lea":
                try:
                    raw = read(tgt, 160)
                    if raw[1:2] == b"\0" and 32 <= raw[0] < 127:
                        w = raw.decode("utf-16le", "ignore").split("\0")[0]
                        if len(w) > 2: note += '  L"%s"' % w[:80]
                    else:
                        a = raw.split(b"\0")[0]
                        if len(a) > 3 and all(32 <= c < 127 for c in a): note += '  "%s"' % a[:80].decode()
                except Exception: pass
        elif ins.mnemonic in ("call", "jmp") or ins.mnemonic.startswith("j"):
            try:
                tgt = int(ins.op_str, 16) - BASE
                if ins.mnemonic == "call" or not (rva <= tgt < rva + size):
                    note = "  ; " + label(tgt)
            except ValueError: pass
        print("%08x  %-60s%s" % (ins.address - BASE, text, note))
        if n >= max_insns: break
        if size and ins.address + ins.size >= BASE + rva + size: break

def text_section():
    for s in pe().sections:
        if s.Name.rstrip(b"\0") == b".text":
            return s.VirtualAddress, s.get_data()
    raise SystemExit("no .text")

def xref(target):
    va, data = text_section()
    hits = []
    # E8/E9 rel32 calls/jmps
    for m in re.finditer(b"[\xe8\xe9]", data):
        i = m.start()
        if i + 5 > len(data): break
        rel = struct.unpack_from("<i", data, i + 1)[0]
        if va + i + 5 + rel == target: hits.append(va + i)
    # rip-relative (lea/mov/cmp ...): any disp32 that resolves to target at instruction end in 7 bytes (common)
    for L in (6, 7, 8, 9, 10):
        for i in range(0, len(data) - L):
            pass
    return hits

def xref_riprel(target, lengths=(7,)):
    # Candidate instruction starts whose rip-relative disp32 (ending the instruction) resolves to target.
    # Heuristic: assumes the disp32 is the last 4 bytes of an instruction of length L (true for lea/mov/cmp
    # without immediates). Verify hits by disassembling around them.
    import numpy as np
    va, data = text_section()
    buf = np.frombuffer(data, dtype=np.uint8)
    hits = set()
    for k in range(4):  # disp32 fields starting at offset p = 4*j + k
        n = (len(buf) - k) // 4
        disp = np.frombuffer(buf[k:k + 4 * n].tobytes(), dtype="<i4").astype(np.int64)
        p = np.arange(n, dtype=np.int64) * 4 + k  # disp offset inside .text; instruction end = p + 4
        for idx in np.nonzero(va + p + 4 + disp == target)[0]:
            hits.add(va + int(p[idx]))  # RVA of the disp32 field; the instruction ends 4 bytes later
    return sorted(hits)

if __name__ == "__main__":
    cmd = sys.argv[1]
    if cmd == "find":
        for rva, size, tag, name in find(sys.argv[2]):
            print("0x%08x %6d tag=%-2d %s" % (rva, size, tag, name))
    elif cmd == "at":
        print(at(int(sys.argv[2], 16)))
    elif cmd == "dis":
        t = sys.argv[2]
        if t.startswith("0x"):
            rva = int(t, 16); r = at(rva); size = r[3] - r[1] if r and r[3] else 0x400
            print("; %s" % (r,))
        else:
            rva, size = by_name(t); print("; %s rva=0x%x size=%d" % (t, rva, size))
        disasm(rva, size or 0x400, int(sys.argv[3]) if len(sys.argv) > 3 else 4000)
    elif cmd == "xref":
        t = sys.argv[2]
        rva = int(t, 16) if t.startswith("0x") else by_name(t)[0]
        for h in xref(rva):
            print("0x%08x  in %s" % (h, label(h)))
    elif cmd == "xrefdata":
        rva = int(sys.argv[2], 16) if sys.argv[2].startswith("0x") else by_name(sys.argv[2])[0]
        for h in xref_riprel(rva):
            print("disp32 at 0x%08x  in %s" % (h, label(h)))
