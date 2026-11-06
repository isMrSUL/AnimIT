"""Reading SvR 2011's own move data from an installed game's pac folder (read only).

    m.pac    YMKs/YMBs motion banks (MOT/BM.., MOT/RR.., MVMT/<category>, ...)
    mpsp.pac MOTP/GAME: the packed copies every match loads
    misc.pac MOVS/WAZE: 160-byte move records (id +0x90, category bits +0, name +0x10)
             WAZA/DATA: child 0 EXH (36-byte per-key move data), 1 sound events, 2 MBD

Uses the port's own readers (vendor/svrfmt.py, vendor/svr10_moves.py). The
index is rebuilt from the selected game; no executable pickle cache is loaded.
"""
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, 'vendor'))
import svr10_moves as sm  # noqa: E402

# the SvR 2011 game folder; the worker sets it to the game chosen in AnimIT (env SVR2011_GAME for command-line use)
DEFAULT_GAME = os.environ.get('SVR2011_GAME', '')


class Game11:
    def __init__(self, game=None):
        # looked up at call time: a default argument would freeze the value from import time (then Game11() read a
        # hard-coded folder instead of the game chosen in AnimIT)
        game = game or DEFAULT_GAME
        if not game:
            raise ValueError('No SvR 2011 game folder given.')
        self.game = game
        pacdir = os.path.join(game, 'pac')
        motions = {}   # (id, x, y) -> [dict(path, kind, T, frames, data)]
        banks = {}     # path -> (kind, set of keys)
        for pacname in ('m.pac', 'mpsp.pac'):
            h, groups, tr = sm.read_epac(os.path.join(pacdir, pacname))
            for path, raw in sm.walk_banks(groups):
                if pacname == 'mpsp.pac' and not path.startswith('MOTP'):
                    continue
                kind = raw[:4]
                T = raw[0x10:0x110]
                keys = set()
                for e in sm.bank_entries(raw):
                    if e['id'] in sm.SENTINEL_IDS:
                        continue
                    k = (e['id'], e['x'], e['y'])
                    keys.add(k)
                    motions.setdefault(k, []).append(dict(path=path, pac=pacname, kind=kind, T=T, frames=e['frames'],
                                                          data=e['data']))
                banks[path] = (kind, keys)
        h, mg, tr = sm.read_epac(os.path.join(pacdir, 'misc.pac'))
        wz = sm.unpack(sm.entry_get(mg, b'MOVS', b'WAZE'))
        st, n, ids = sm.waze_load(wz)
        waze = {}
        for mid, offs in ids.items():
            r = wz[offs[0]:offs[0] + 160]
            waze[mid] = dict(flags=r[:16].hex(), name=r[0x10:0x90].split(b'\0')[0].decode('latin1').strip(),
                             record=r.hex())
        w = sm.waza_children(mg)
        exh, evt, mbd = {}, {}, {}
        for g, _, u in sm.subgroups(w[0]):
            for r in sm.exh_parse(u, 36):
                exh.setdefault(struct.unpack_from('<H', r, 0)[0], []).append((g, r))
        for g, _, u in sm.subgroups(w[1]):
            p = sm.evt_parse(u)
            if p:
                recs, ev = p
                for r in recs:
                    evt.setdefault(struct.unpack_from('<H', r, 0)[0], []).append((g, r, b''.join(sm.evt_list(r, ev))))
        for g, _, u in sm.subgroups(w[2]):
            if u[:4] == b'MBD\0':  # "MBD\0", u32 count, 8-byte records (u16 id first)
                cnt = struct.unpack_from('<I', u, 4)[0]
                if 8 + 8 * cnt <= len(u):
                    for k in range(cnt):
                        r = u[8 + 8 * k:16 + 8 * k]
                        mbd.setdefault(struct.unpack_from('<H', r, 0)[0], []).append((g, r))
        self.motions, self.banks, self.waze, self.exh, self.evt, self.mbd = motions, banks, waze, exh, evt, mbd

    def ymks(self, mid):
        """the move's YMKs copies (not packed MOTP, not YMBs): {(x, y): entry}"""
        out = {}
        for (i, x, y), es in self.motions.items():
            if i == mid:
                for e in es:
                    if e['kind'] == b'YMKs' and e['pac'] == 'm.pac':
                        out.setdefault((x, y), e)
        return out

    def kinds(self, mid):
        return {e['kind'] for (i, x, y), es in self.motions.items() if i == mid for e in es}

    def usable(self, mid):
        """a move 2011 lets players pick: category bits set"""
        w = self.waze.get(mid)
        return bool(w) and bool(bytes.fromhex(w['flags'])[:15].strip(b'\0'))

    def paths(self, mid):
        return sorted({e['path'] for (i, x, y), es in self.motions.items() if i == mid for e in es})
