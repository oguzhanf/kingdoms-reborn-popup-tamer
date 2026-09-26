# Read-only: shows every patch site from src/KRPopupTamer/Catalog.cs in the running game (original or patched,
# disassembled) and, for jumps into a KR Popup Tamer counting page, its header. Usage: python live_sites.py
import sys, re, live_cardsys as L, capstone
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
src = open(r"C:\krpk\src\KRPopupTamer\Catalog.cs", encoding="utf-8").read()
sites = re.findall(r'new CodeSite\((0x[0-9a-fA-F]+), "([0-9A-Fa-f ]+)"', src)
p = L.Proc(L.find_pid()[0]); base, _ = p.module()
for rva_s, orig in sites:
    rva = int(rva_s, 16); o = bytes.fromhex(orig.replace(" ", ""))
    live = p.read(base + rva, len(o))
    state = "ORIGINAL" if live == o else "PATCHED"
    dis = " ; ".join(i.mnemonic + " " + i.op_str for i in md.disasm(live, base + rva))
    extra = ""
    if live[0] == 0xE9 and state == "PATCHED":
        t = base + rva + 5 + int.from_bytes(live[1:5], "little", signed=True)
        page = t & ~0xFFF
        try:
            hdr = p.read(page, 16)
            extra = " -> %s page 0x%x magic=%s layout=0x%08x pid=%d stub@+0x%x" % ("cave" if hdr[:4] == b"KRPT" else "module", page, hdr[:4], int.from_bytes(hdr[4:8], "little"), int.from_bytes(hdr[8:12], "little", signed=True), t - page)
        except Exception as e:
            extra = " -> ? %s" % e
    print("0x%08x %-8s %s%s" % (rva, state, dis[:70], extra))
