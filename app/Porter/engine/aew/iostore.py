"""UE4 IoStore container reader (.utoc / .ucas, TOC version 2-3, UE 4.26/4.27) - read only.

    python -I iostore.py info <utoc>            header, compression methods, flags
    python -I iostore.py list <utoc>            file names from the directory index (if the container is indexed)
"""
import struct
import sys


class Toc:
    def __init__(self, path):
        d = open(path, 'rb').read()
        self.path = path
        assert d[:16] == b'-==--==--==--==-', 'not a utoc'
        self.version = d[16]
        (self.header_size, self.entry_count, self.cblock_count, self.cblock_entry_size, self.cmeth_count,
         self.cmeth_len, self.cblock_size, self.dir_index_size, self.partition_count) = struct.unpack_from('<9I', d, 20)
        self.container_id, = struct.unpack_from('<Q', d, 56)
        self.key_guid = d[64:80]
        self.flags = d[80]
        self.partition_size, = struct.unpack_from('<Q', d, 88) if self.header_size >= 96 else (0,)
        o = self.header_size
        self.chunk_ids = [d[o + 12 * i:o + 12 * i + 12] for i in range(self.entry_count)]
        o += 12 * self.entry_count
        self.offlen = []
        for i in range(self.entry_count):
            b = d[o + 10 * i:o + 10 * i + 10]
            off = int.from_bytes(b[0:5], 'big')
            ln = int.from_bytes(b[5:10], 'big')
            self.offlen.append((off, ln))
        o += 10 * self.entry_count
        self.blocks = []
        for i in range(self.cblock_count):
            b = d[o + 12 * i:o + 12 * i + 12]
            off = int.from_bytes(b[0:5], 'little')
            csz = int.from_bytes(b[5:8], 'little')
            usz = int.from_bytes(b[8:11], 'little')
            meth = b[11]
            self.blocks.append((off, csz, usz, meth))
        o += 12 * self.cblock_count
        self.methods = ['None'] + [d[o + self.cmeth_len * i:o + self.cmeth_len * (i + 1)].split(b'\0')[0].decode()
                                   for i in range(self.cmeth_count)]
        o += self.cmeth_len * self.cmeth_count
        if self.flags & 4:   # signed
            hsz, = struct.unpack_from('<i', d, o)
            o += 4 + hsz + hsz + 20 * self.cblock_count
        self.dir_index = d[o:o + self.dir_index_size] if self.flags & 8 else b''
        o += self.dir_index_size if self.flags & 8 else 0
        self.meta_off = o
        self.raw = d

    def names(self):
        """-> {toc entry index: path} from the directory index"""
        if not self.dir_index:
            return {}
        b, o = self.dir_index, 0

        def fstr():
            nonlocal o
            n, = struct.unpack_from('<i', b, o)
            o += 4
            if n == 0:
                return ''
            if n > 0:
                s = b[o:o + n - 1].decode('latin1')
                o += n
                return s
            n = -n
            s = b[o:o + 2 * (n - 1)].decode('utf-16-le')
            o += 2 * n
            return s
        mount = fstr()
        ndir, = struct.unpack_from('<i', b, o); o += 4
        dirs = [struct.unpack_from('<4I', b, o + 16 * i) for i in range(ndir)]   # name, first child, next sibling, first file
        o += 16 * ndir
        nfile, = struct.unpack_from('<i', b, o); o += 4
        files = [struct.unpack_from('<3I', b, o + 12 * i) for i in range(nfile)]  # name, next file, user data (toc index)
        o += 12 * nfile
        nstr, = struct.unpack_from('<i', b, o); o += 4
        strs = [fstr() for _ in range(nstr)]
        out = {}
        NONE = 0xFFFFFFFF

        def walk(di, prefix):
            name, child, sib, f = dirs[di]
            path = prefix + (strs[name] + '/' if name != NONE else '')
            while f != NONE:
                fn, nxt, user = files[f]
                out[user] = mount + path + strs[fn]
                f = nxt
            c = child
            while c != NONE:
                walk(c, path)
                c = dirs[c][2]
        if dirs:
            walk(0, '')
        return out


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    cmd, path = sys.argv[1], sys.argv[2]
    t = Toc(path)
    if cmd == 'info':
        print('%s: version %d entries %d blocks %d block size %d methods %s flags 0x%X (compressed %d encrypted %d '
              'signed %d indexed %d) partitions %d dir index %d bytes key %s' % (
                  path, t.version, t.entry_count, t.cblock_count, t.cblock_size, t.methods[1:], t.flags, t.flags & 1,
                  t.flags >> 1 & 1, t.flags >> 2 & 1, t.flags >> 3 & 1, t.partition_count, t.dir_index_size,
                  t.key_guid.hex()))
        from collections import Counter
        print('  block methods used:', Counter(t.methods[b[3]] for b in t.blocks))
    elif cmd == 'list':
        for i, n in sorted(t.names().items(), key=lambda kv: kv[1]):
            off, ln = t.offlen[i]
            print('%d\t%s\t%d' % (i, n, ln))
