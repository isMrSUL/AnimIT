"""Extract files from UE4 IoStore containers (Oodle-compressed) - read only.
Oodle: WWE 2K26's own oo2core_9_win64.dll (installed Steam game), loaded read-only.

    python -I ioextract.py <utoc> <name substring> <out dir> [--max N]
"""
import ctypes
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from iostore import Toc

OODLE = next((p for p in (r'C:\Program Files (x86)\Steam\steamapps\common\WWE 2K26\oo2core_9_win64.dll',
                          r'C:\Program Files (x86)\Steam\steamapps\common\WWE 2K26\Meta2_Release_Build\oo2core_9_win64.dll',
                          r'C:\Program Files (x86)\Steam\steamapps\common\WWE 2K26\cakeview\oo2core_9_win64.dll')
              if os.path.exists(p)), None)
_oo = None


def oodle():
    global _oo
    if _oo is None:
        _oo = ctypes.WinDLL(OODLE)
        f = _oo.OodleLZ_Decompress
        f.restype = ctypes.c_int64
        f.argtypes = [ctypes.c_void_p, ctypes.c_int64, ctypes.c_void_p, ctypes.c_int64, ctypes.c_int, ctypes.c_int,
                      ctypes.c_int, ctypes.c_void_p, ctypes.c_int64, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p,
                      ctypes.c_int64, ctypes.c_int]
    return _oo


class Container:
    def __init__(self, utoc):
        self.toc = Toc(utoc)
        self.ucas = open(utoc[:-5] + '.ucas', 'rb')
        self.names = self.toc.names()
        self.by_name = {v: k for k, v in self.names.items()}

    def read(self, idx):
        off, ln = self.toc.offlen[idx]
        bs = self.toc.cblock_size
        out = bytearray()
        for bi in range(off // bs, (off + ln + bs - 1) // bs):
            boff, csz, usz, meth = self.toc.blocks[bi]
            self.ucas.seek(boff)
            raw = self.ucas.read((csz + 15) & ~15 if meth else csz)[:csz]
            if self.toc.methods[meth] == 'None':
                data = raw[:usz]
            else:
                src = ctypes.create_string_buffer(raw, len(raw))
                dst = ctypes.create_string_buffer(usz)
                got = oodle().OodleLZ_Decompress(src, csz, dst, usz, 1, 0, 0, None, 0, None, None, None, 0, 3)
                if got != usz:
                    raise ValueError('oodle failed on block %d (%d != %d)' % (bi, got, usz))
                data = dst.raw
            out += data
        start = off - (off // bs) * bs
        return bytes(out[start:start + ln])


if __name__ == '__main__':
    utoc, needle, outdir = sys.argv[1], sys.argv[2], sys.argv[3]
    mx = int(sys.argv[sys.argv.index('--max') + 1]) if '--max' in sys.argv else 50
    c = Container(utoc)
    n = 0
    for idx, name in sorted(c.names.items(), key=lambda kv: kv[1]):
        if needle.lower() in name.lower():
            data = c.read(idx)
            p = os.path.join(outdir, name.replace('../../../', '').replace('/', os.sep))
            os.makedirs(os.path.dirname(p), exist_ok=True)
            open(p, 'wb').write(data)
            print(len(data), name)
            n += 1
            if n >= mx:
                break
