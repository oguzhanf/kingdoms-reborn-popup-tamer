# Checks every CodeSite in src/KRPopupTamer/Catalog.cs against the game exe on disk:
# the 8 bytes before, the original bytes and the 8 bytes after must match exactly, and the PE identity must match.
# Usage: python check_catalog.py   (set KR_EXE to point at another install)
import os, re, sys
import pefile

EXE = os.environ.get("KR_EXE", r"D:\SteamLibrary\steamapps\common\Kingdoms Reborn\PunCity\Binaries\Win64\PrototypeCity-Win64-Shipping.exe")
CATALOG = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "KRPopupTamer", "Catalog.cs")

src = open(CATALOG, encoding="utf-8").read()
pe = pefile.PE(EXE, fast_load=True)
stamp = int(re.search(r"BuildTimeDateStamp = (0x[0-9a-fA-F]+)", src).group(1), 16)
size = int(re.search(r"BuildSizeOfImage = (0x[0-9a-fA-F]+)", src).group(1), 16)
ok = pe.FILE_HEADER.TimeDateStamp == stamp and pe.OPTIONAL_HEADER.SizeOfImage == size
print("PE identity: %s (exe 0x%08x/0x%x, catalog 0x%08x/0x%x)" % ("OK" if ok else "MISMATCH", pe.FILE_HEADER.TimeDateStamp,
      pe.OPTIONAL_HEADER.SizeOfImage, stamp, size))

def hexbytes(s): return bytes.fromhex(s.replace(" ", ""))

sites = re.findall(r'new CodeSite\((0x[0-9a-fA-F]+), "([0-9A-Fa-f ]+)", "([0-9A-Fa-f ]+)", "([0-9A-Fa-f ]+)"\)', src)
for rva_s, orig, before, after in sites:
    rva = int(rva_s, 16)
    o, b, a = hexbytes(orig), hexbytes(before), hexbytes(after)
    live = pe.get_data(rva - len(b), len(b) + len(o) + len(a))
    good = live == b + o + a
    ok &= good
    print("%s 0x%08x  %s" % ("OK  " if good else "FAIL", rva, "" if good else "exe has " + live.hex(" ").upper()))
for rva_s, orig, new in re.findall(r'new BytesPatch\(new CodeSite\((0x[0-9a-fA-F]+), "([0-9A-Fa-f ]+)", "[0-9A-Fa-f ]+", "[0-9A-Fa-f ]+"\),\s*"([0-9A-Fa-f ]+)"\)', src):
    same_length = len(hexbytes(orig)) == len(hexbytes(new))
    ok &= same_length
    print("%s 0x%08x  BytesPatch length %s" % ("OK  " if same_length else "FAIL", int(rva_s, 16), "matches" if same_length else "differs"))
print("%d sites checked: %s" % (len(sites), "ALL OK" if ok else "PROBLEMS FOUND"))
sys.exit(0 if ok else 1)
