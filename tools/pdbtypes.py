# PDB type dumper (class layouts with field offsets, enum values) via dbghelp SymGetTypeInfo.
# Usage:
#   python pdbtypes.py class <TypeName>        e.g. class BuildingCardSystem
#   python pdbtypes.py enum <EnumName> [filter] e.g. enum CardEnum Steal
#   python pdbtypes.py enumval <EnumName> <int|0xhex>
import ctypes, ctypes.wintypes as wt, sys
import sym
from sym import dbghelp, HPROC, BASE, SYMBOL_INFOW, SIZEOF_SYMBOL_INFOW, MAX_NAME

TI_GET_SYMTAG, TI_GET_SYMNAME, TI_GET_LENGTH, TI_GET_TYPE, TI_GET_TYPEID, TI_GET_BASETYPE = 0, 1, 2, 3, 4, 5
TI_FINDCHILDREN, TI_GET_DATAKIND, TI_GET_OFFSET, TI_GET_VALUE, TI_GET_COUNT, TI_GET_CHILDRENCOUNT = 7, 8, 10, 11, 12, 13
TI_GET_BITPOSITION = 14
TAG = {5: "Function", 7: "Data", 11: "UDT", 12: "Enum", 13: "FunctionType", 14: "Pointer", 15: "Array",
       16: "BaseType", 17: "Typedef", 18: "BaseClass", 24: "VTableShape", 25: "VTable"}
BASETYPES = {1: "void", 2: "char", 3: "wchar_t", 6: "int", 7: "uint", 8: "float", 10: "bool", 13: "long", 14: "ulong", 31: "char16_t", 32: "char32_t"}

dbghelp.SymGetTypeInfo.argtypes = [wt.HANDLE, ctypes.c_uint64, wt.ULONG, ctypes.c_int, ctypes.c_void_p]
dbghelp.SymGetTypeInfo.restype = wt.BOOL

def ti(tid, kind, ctype=wt.DWORD):
    v = ctype()
    if not dbghelp.SymGetTypeInfo(HPROC, BASE, tid, kind, ctypes.byref(v)): return None
    return v.value

def name(tid):
    p = ctypes.c_void_p()
    if not dbghelp.SymGetTypeInfo(HPROC, BASE, tid, TI_GET_SYMNAME, ctypes.byref(p)) or not p.value: return None
    s = ctypes.wstring_at(p.value)
    ctypes.windll.kernel32.LocalFree(p)
    return s

def children(tid):
    n = ti(tid, TI_GET_CHILDRENCOUNT) or 0
    if not n: return []
    class P(ctypes.Structure):
        _fields_ = [("Count", wt.ULONG), ("Start", wt.ULONG), ("ChildId", wt.ULONG * n)]
    p = P(); p.Count = n; p.Start = 0
    if not dbghelp.SymGetTypeInfo(HPROC, BASE, tid, TI_FINDCHILDREN, ctypes.byref(p)): return []
    return list(p.ChildId)

def typename(tid, depth=0):
    if tid is None or depth > 8: return "?"
    tag = ti(tid, TI_GET_SYMTAG)
    if tag == 14: return typename(ti(tid, TI_GET_TYPE), depth + 1) + "*"
    if tag == 15:
        return "%s[%s]" % (typename(ti(tid, TI_GET_TYPE), depth + 1), ti(tid, TI_GET_COUNT))
    if tag == 16:
        bt, ln = ti(tid, TI_GET_BASETYPE), ti(tid, TI_GET_LENGTH, ctypes.c_uint64)
        return "%s%d" % (BASETYPES.get(bt, "bt%d_" % bt), ln * 8) if bt not in (1, 10) else BASETYPES[bt]
    if tag == 13: return "fn"
    return name(tid) or TAG.get(tag, "tag%s" % tag)

def lookup_type(tname):
    s = SYMBOL_INFOW(); s.SizeOfStruct = SIZEOF_SYMBOL_INFOW; s.MaxNameLen = MAX_NAME
    if not dbghelp.SymGetTypeFromNameW(HPROC, ctypes.c_uint64(BASE), ctypes.c_wchar_p(tname), ctypes.byref(s)):
        sys.exit("type not found: " + tname)
    return s.TypeIndex

def dump_class(tid, base_off=0, indent="", seen=None):
    length = ti(tid, TI_GET_LENGTH, ctypes.c_uint64)
    print("%s// %s size=0x%x" % (indent, name(tid), length or 0))
    rows = []
    for c in children(tid):
        tag = ti(c, TI_GET_SYMTAG)
        if tag == 18:  # base class
            off = ti(c, TI_GET_OFFSET) or 0
            btid = ti(c, TI_GET_TYPE)
            print("%s  [base +0x%x] %s" % (indent, base_off + off, name(btid)))
        elif tag == 7 and ti(c, TI_GET_DATAKIND) == 7:  # DataIsMember
            off = ti(c, TI_GET_OFFSET)
            bit = ti(c, TI_GET_BITPOSITION)
            rows.append((base_off + (off or 0), name(c), typename(ti(c, TI_GET_TYPE)), bit))
        elif tag == 7:
            rows.append((-1, name(c), "static " + typename(ti(c, TI_GET_TYPE)), None))
    for off, n, t, bit in sorted(rows, key=lambda r: r[0]):
        loc = "static" if off < 0 else "+0x%04x" % off
        print("%s  %s  %-40s %s%s" % (indent, loc, t, n, "" if bit is None else " :bit%d" % bit))

def variant_int(tid):
    buf = (ctypes.c_ubyte * 24)()
    if not dbghelp.SymGetTypeInfo(HPROC, BASE, tid, TI_GET_VALUE, ctypes.byref(buf)): return None
    vt = buf[0] | (buf[1] << 8)
    raw = bytes(buf[8:16])
    size = {2: 2, 3: 4, 16: 1, 17: 1, 18: 2, 19: 4, 20: 8, 21: 8, 22: 4, 23: 4}.get(vt, 8)
    signed = vt in (2, 3, 16, 20, 22)
    return int.from_bytes(raw[:size], "little", signed=signed)

def enum_values(tid):
    return [(variant_int(c), name(c)) for c in children(tid)]

if __name__ == "__main__":
    cmd, arg = sys.argv[1], sys.argv[2]
    tid = lookup_type(arg)
    if cmd == "class":
        dump_class(tid)
    elif cmd == "enum":
        flt = sys.argv[3].lower() if len(sys.argv) > 3 else None
        for v, n in enum_values(tid):
            if not flt or flt in n.lower(): print("0x%04x %6d %s" % (v, v, n))
    elif cmd == "enumval":
        want = int(sys.argv[3], 0)
        for v, n in enum_values(tid):
            if v == want: print("0x%04x %6d %s" % (v, v, n))
