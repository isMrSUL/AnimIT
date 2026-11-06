"""Reading WWE SmackDown! Here Comes the Pain (PS2) - its extracted disc folder, read only.

    PAC/M.PAC      DPAC archive; MOT/GAME = a PAC of YMKs motion banks
    PAC/MOVES.PAC  DPAC archive; MOVE/WAZA = the move records (64 bytes each)

DPAC (PS2): "DPAC", u32 directory size, the directory at 0x800, data from 0x4000
in 2048-byte sectors. Each directory group: 4-char type, u16 count, u16 sector;
count bit 15 set = numbered entries (u16 key, u16 size/256) stored one after
another from that sector; else count/2 named entries (4-char name, u16 sector,
u16 size/256).
PAC: "PAC ", u32 count, then 8-byte entries (u16 key, u24 offset, u24 size)
relative to the end of the table.
A motion bank (HCTP keeps 2011's layout without its 16-byte "YMKs" header):
256-byte event-size table, u32 count, 16-byte entries {u8 y (track), u8 x
(phase), u16 move id, u32 offset, u32 frames, u32 runtime}, then the data.
Tracks: 0 attacker, 1 victim, 2-4 tag partners, 20/21 alternates, 30-32
cameras, 50-61 the tag/extra people and props. Ids +0x4000 (16384) are a
move's extra tracks.
Move records (MOVE/WAZA): a 32-byte header, then per record the name (32 bytes,
zero-terminated, space padded) and 32 bytes of data: +0 kind, +1 class, +4
category bits, +8 u16 move id. The first records are the roster.
"""
import os
import struct

# the extracted HCTP disc folder (PAC\M.PAC, PAC\MOVES.PAC); env HCTP_ROOT for command-line use
DEFAULT_ROOT = os.environ.get('HCTP_ROOT', '')


def dpac(data):
    """yield (type, name, bytes) for every entry of a DPAC archive"""
    if data[:4] != b'DPAC':
        raise ValueError('not a DPAC archive')
    p = 0x800
    end = p + struct.unpack_from('<I', data, 4)[0]
    while p < end:
        typ = data[p:p + 4].decode('latin1').strip()
        count, sector = struct.unpack_from('<HH', data, p + 4)
        p += 8
        if count & 0x8000:
            off = 0x4000 + sector * 2048
            for _ in range(count & 0x7fff):
                key, size = struct.unpack_from('<HH', data, p)
                p += 4
                size *= 256
                yield typ, '%04x' % key, data[off:off + size]
                off = (off + size + 2047) & ~2047
            continue
        for _ in range(count // 2):
            name = data[p:p + 4].decode('latin1').strip()
            sector, size = struct.unpack_from('<HH', data, p + 4)
            p += 8
            off = 0x4000 + sector * 2048
            yield typ, name, data[off:off + size * 256]


def pac(data):
    """yield (key, bytes) for every entry of a PAC"""
    if data[:4] != b'PAC ':
        raise ValueError('not a PAC')
    count = struct.unpack_from('<I', data, 4)[0]
    base = 8 + count * 8
    for i in range(count):
        rec = data[8 + i * 8:16 + i * 8]
        key = int.from_bytes(rec[:2], 'little')
        off = int.from_bytes(rec[2:5], 'little')
        size = int.from_bytes(rec[5:8], 'little')
        yield key, data[base + off:base + off + size]


def bank_entries(raw):
    """a motion bank -> (event-size table, [dict(id, x, y, frames, rt, data)])"""
    T = raw[:256]
    n = struct.unpack_from('<I', raw, 256)[0]
    base = 260 + 16 * n
    ents = [struct.unpack_from('<BBHIII', raw, 260 + 16 * i) for i in range(n)]
    offs = sorted(set(e[3] for e in ents)) + [len(raw) - base]
    nxt = {o: offs[i + 1] for i, o in enumerate(offs[:-1])}
    return T, [dict(y=e[0], x=e[1], id=e[2], frames=e[4], rt=e[5], data=raw[base + e[3]:base + nxt[e[3]]])
               for e in ents if e[2] != 0x7fff]


class HCTP:
    """the game's motions {(id, x, y): dict(T, frames, data, bank)} and move names {id: name}"""

    def __init__(self, root=None):
        root = root or DEFAULT_ROOT
        if not root:
            raise ValueError('No HCTP folder given.')
        self.root = root
        m = open(os.path.join(root, 'PAC', 'M.PAC'), 'rb').read()
        game = next(raw for t, n, raw in dpac(m) if (t, n) == ('MOT', 'GAME'))
        self.motions = {}
        for bank, raw in pac(game):
            if len(raw) < 260:
                continue
            T, ents = bank_entries(raw)
            for e in ents:
                self.motions.setdefault((e['id'], e['x'], e['y']),
                                        dict(T=T, frames=e['frames'], rt=e['rt'], data=e['data'], bank=bank))
        mv = open(os.path.join(root, 'PAC', 'MOVES.PAC'), 'rb').read()
        waza = next(raw for t, n, raw in dpac(mv) if (t, n) == ('MOVE', 'WAZA'))
        self.records = {}
        # a 32-byte header, then 64-byte records: name (32 bytes), then the data
        for o in range(0x20, len(waza) - 63, 64):
            name = waza[o:o + 32].split(b'\0')[0].decode('latin1').strip()
            r = waza[o + 32:o + 64]
            mid = struct.unpack_from('<H', r, 8)[0]
            if name and mid >= 1000 and mid not in self.records:
                self.records[mid] = dict(name=name, kind=r[0], cls=r[1], bits=r[4:8].hex(), raw=r.hex())
        self.ids = sorted({k[0] for k in self.motions})

    def name(self, mid):
        r = self.records.get(mid)
        return r['name'] if r else ''

    def tracks(self, mid):
        """every (x, y) of a move: its own and its extra (+0x4000) tracks"""
        return sorted((k[1], k[2]) for k in self.motions if k[0] == mid)

    def keys(self, mid):
        return sorted(k for k in self.motions if k[0] == mid)
