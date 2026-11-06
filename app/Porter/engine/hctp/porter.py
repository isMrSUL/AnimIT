"""Here Comes the Pain -> SvR 2011 move porter.

    python porter.py survey [--find TEXT] [--all]        list HCTP's moves and whether they can be ported
    python porter.py info <id>                           one move: tracks, donor candidates
    python porter.py port <ids> [--out DIR] [--install] [--name NAME] [--pack-id ID] [--donor ID=DONOR,...]
                                                         build a move pack (default: ./out/<pack-id>)

<ids>: comma list of HCTP move ids, or @file (one id per line). --install also copies the pack to
the game's Mods\\Moves\\<pack-id> (it takes effect the next time the game starts). The pack can also
be opened in the Mod Maker (Moves -> Open a pack folder) and saved as a .svrmod there.

A ported move = HCTP's own animation (every phase and track, converted: convert.py) + the
gameplay records of a 2011 "donor" move of the same kind: its EXH data (positions, damage,
reversals), sound events, category bits (which Create-a-Moveset slots take it) and banks.
Donor: a 2011 move players can pick, with YMKs motions and EXH records, that starts from the same
position (event 92 at frame 0) - preferring the same id block, the same phases, similar length.
The move's id: HCTP's own when 2011 kept a free move record there (Yuke's ids are shared across
the series), else the nearest free record. 2011 can't take new move records.
A move whose animation 2011 already has (same id, same attacker/victim lengths) is not ported.
Fixes applied, as SvR 2011 did to its own moves:
  * sitting-in-the-corner moves (donor record +0x73 = 12, victim starting near the post):
    victim root z -253, attacker +253 (2011 moved that pose out of the corner)
  * a victim ending on the mat whose last op 49 names no motion gets the donor victim's
    follow-up (lying / selling) instead of getting up at once
  * HCTP's extra tracks (id + 16384, y 50-60) go under the move's own id, as 2011 keeps them
"""
import argparse
import json
import os
import shutil
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path[:0] = [HERE, os.path.join(HERE, 'vendor')]
import convert  # noqa: E402
import game11  # noqa: E402
import hctp  # noqa: E402
import ymks  # noqa: E402

EXTRA = 0x4000
SIT_SHIFT, SIT_MIN_Z = 253.0, -450.0
END_LOW_Y = -400.0


class Porter:
    def __init__(self, hctp_root=None, game=None):
        self.h = hctp.HCTP(hctp_root)
        self.g = game11.Game11(game)
        self.game = self.g.game
        self.T11 = next(e['T'] for e in self.g.motions[(718, 0, 0)] if e['kind'] == b'YMKs')
        self._donors = None

    # ---- what 2011 has
    def used_ids(self, skip_pack=None):
        used = {k[0] for k in self.g.motions}
        mods = os.path.join(self.game, 'Mods')
        for root, dirs, files in os.walk(mods):
            if 'pack.txt' not in files or os.path.basename(root) == skip_pack:
                continue
            if os.path.exists(os.path.join(root, 'disabled')):
                continue
            for line in open(os.path.join(root, 'pack.txt'), errors='replace'):
                f = line.split()
                if f and f[0] == 'motion' and len(f) > 3:
                    used.add(int(f[3]))
                elif f and f[0] in ('waze', 'wazecopy', 'wazename') and len(f) > 1:
                    used.add(int(f[1]))
        return used

    def donors(self):
        if self._donors is None:
            out = {}
            for mid in self.g.waze:
                if mid < 2000 or mid >= EXTRA or not self.g.usable(mid) or mid not in self.g.exh:
                    continue
                tr = self.g.ymks(mid)
                if (0, 0) not in tr and not any(y == 0 for x, y in tr):
                    continue
                if b'YMBs' in self.g.kinds(mid):
                    continue
                x0 = min(x for x, y in tr if y == 0)
                a = tr[(x0, 0)]
                try:
                    sc = convert.start_class(a['data'], a['T'])
                except Exception:
                    continue
                rec = bytes.fromhex(self.g.waze[mid]['record'])
                if rec[0] == 0:  # hidden in Create-a-Moveset
                    continue
                v = tr.get((x0, 1))
                out[mid] = dict(start=sc, xs={x for x, y in tr}, ys={y for x, y in tr}, frames=a['frames'],
                                situation=rec[0x73], name=self.g.waze[mid]['name'],
                                mvmt={p for p in self.g.paths(mid) if p.startswith('MVMT')},
                                place=self.place(convert.first_root(a['data']),
                                                 convert.first_root(v['data']) if v else None),
                                down=self.lying(convert.last_root(v['data']) if v else None))
            self._donors = out
        return self._donors

    @staticmethod
    def lying(root):
        """the victim ends on the mat (a slam, a drop), stands, or None (no victim)"""
        return None if root is None else root[1] > END_LOW_Y

    def source_down(self, mid):
        xs = sorted({k[1] for k in self.h.keys(mid) if k[2] == 1})
        if not xs:
            return None
        e = self.h.motions[(mid, xs[0], 1)]
        last = None
        try:
            for frame, rec in ymks.parse_stream(e['data']).records():
                pose = ymks.split_record(rec)[1]
                if pose:
                    for s in convert.decode_hctp_body(pose)['subs']:
                        if 'root' in s:
                            last = s['root']
        except (ValueError, IndexError, struct.error):
            return None
        return self.lying(last)

    @staticmethod
    def place(att, vic):
        """where the two start: attacker root, victim root (x, y, z; 0.01 units), or None"""
        if att is None:
            return None
        return tuple(att) + (tuple(vic) if vic is not None else (None, None, None))

    def source_place(self, mid):
        xs = sorted({k[1] for k in self.h.keys(mid) if k[2] == 0})
        if not xs:
            return None
        def root(y):
            e = self.h.motions.get((mid, xs[0], y))
            if not e:
                return None
            for frame, rec in ymks.parse_stream(e['data']).records():
                pose = ymks.split_record(rec)[1]
                if pose:
                    for s in convert.decode_hctp_body(pose)['subs']:
                        if 'root' in s:
                            return tuple(float(v) for v in s['root'])
            return None
        try:
            return self.place(root(0), root(1))
        except (ValueError, IndexError, struct.error):
            return None

    @staticmethod
    def place_distance(a, b):
        """how differently two moves place the wrestlers at their start (units of 0.5 model units)"""
        if a is None or b is None:
            return 99
        if (a[3] is None) != (b[3] is None):
            return 60
        d = 0.0
        if a[3] is not None:  # the victim relative to the attacker, and both heights
            rel = [a[3 + i] - a[i] for i in range(3)]
            rel2 = [b[3 + i] - b[i] for i in range(3)]
            d += sum((u - v) ** 2 for u, v in zip(rel, rel2)) ** 0.5
            d += abs(a[4] - b[4])
        d += abs(a[1] - b[1])
        return int(d // 50)

    RR_CAP = 16384000

    def rr_bank(self):
        """the RR bank with the most room (2011: RR03, ~0.8 MB of 16)"""
        if not hasattr(self, '_rr'):
            sizes = {}
            for (i, x, y), es in self.g.motions.items():
                for e in es:
                    p = e['path']
                    if p.startswith('MOT/RR') and not p.startswith('MOT/RRBS'):
                        sizes[p] = sizes.get(p, 0) + len(e['data']) + 16
            self._rr = min(sizes, key=sizes.get)
            self._rr_size = sizes
        return self._rr

    # ---- HCTP side
    def source_keys(self, mid):
        """(source key, 2011 key without the id) for every track of an HCTP move"""
        keys = [(k, (k[1], k[2])) for k in self.h.keys(mid)]
        keys += [(k, (k[1], k[2])) for k in self.h.keys(mid + EXTRA) if k[2] >= 50]
        return keys

    def start_of(self, mid):
        att = [k for k in self.h.keys(mid) if k[2] == 0]
        if not att:
            return None
        e = self.h.motions[min(att)]
        return convert.start_class(e['data'], e['T'])

    def in_2011(self, mid):
        """2011 has this animation already: same id, same attacker / victim lengths"""
        tr = self.g.ymks(mid)
        for y in (0, 1):
            src = self.h.motions.get((mid, 0, y))
            dst = tr.get((0, y))
            if (src is None) != (dst is None):
                return False
            if src and src['frames'] != dst['frames']:
                return False
        return (0, 0) in tr

    def donor_candidates(self, mid):
        sc = self.start_of(mid)
        xs = {x for (k, (x, y)) in self.source_keys(mid)}
        a = self.h.motions.get((mid, min(xs) if xs else 0, 0))
        frames = a['frames'] if a else 0
        place = self.source_place(mid)
        down = self.source_down(mid)
        # Yuke's kept their id ranges per situation: 2011's record at the same id (often another
        # animation, or a cut move) tells where the move happens (+0x73: 10 standing in the
        # corner, 12 sitting in the corner...) and its menu category bank
        ref = self.g.waze.get(mid)
        sit = bytes.fromhex(ref['record'])[0x73] if ref else None
        mvmt = {p for p in self.g.paths(mid) if p.startswith('MVMT')}
        cands = []
        for d, info in self.donors().items():
            if info['start'] != sc:
                continue
            score = (0 if sit is None or info['situation'] == sit else 1,
                     0 if not mvmt or mvmt & info['mvmt'] else 1,
                     0 if down is None or info['down'] == down else 1,
                     self.place_distance(place, info['place']) // 2,
                     abs(info['frames'] - frames) // 40,
                     0 if d // 100 == mid // 100 else 1 if d // 1000 == mid // 1000 else 2,
                     len(xs ^ info['xs']),
                     abs(info['frames'] - frames) // 10,
                     abs(d - mid))
            cands.append((score, d))
        return [d for s, d in sorted(cands)]

    # ---- conversion of one move
    def convert_move(self, mid, target, donor, id_map):
        """-> (tracks {(x, y): data}, notes) or raises ValueError"""
        notes = []
        dinfo = self.donors()[donor]
        tracks = {}
        corner = dinfo['situation'] == 12
        # the corner shift goes per phase, when the victim starts near the post (as 2010/HCTP's pose)
        shift_x = set()
        if corner:
            for (k, (x, y)) in self.source_keys(mid):
                if y == 1:
                    r = convert.first_root(self._converted(k, id_map))
                    if r is not None and r[2] > SIT_MIN_Z:
                        shift_x.add(x)
            if shift_x:
                notes.append('sitting-corner move: victim root z -%d, attacker +%d (phases %s)'
                             % (SIT_SHIFT, SIT_SHIFT, sorted(shift_x)))
        for (k, (x, y)) in self.source_keys(mid):
            e = self.h.motions[k]
            dz = 0.0
            if x in shift_x and y in (0, 1, 50, 51):
                dz = -SIT_SHIFT if y in (1, 51) else SIT_SHIFT
            try:
                data, n, refs = convert.convert_track(e['data'], y, e['T'], self.T11, id_map, (0.0, 0.0, dz))
            except (ValueError, IndexError, struct.error) as err:
                if convert.is_person(y) and k[0] < EXTRA:   # (the +16384 alternates are optional)
                    raise ValueError('track %d/%d: %s' % (x, y, err))
                notes.append('track %d/%d left out (%s)' % (x, y, err))
                continue
            missing = [r for r in refs if r not in id_map and not any(kk[0] == r for kk in self.g.motions)]
            if missing and y == 0:
                raise ValueError('track %d/%d starts motions 2011 lacks: %s' % (x, y, missing))
            tracks[(x, y)] = data
        # the victim's end
        dv = self.g.ymks(donor)
        for (x, y), data in list(tracks.items()):
            if y != 1:
                continue
            c = convert.chain(data, self.T11)
            r = convert.last_root(data)
            dtrack = dv.get((x, 1)) or dv.get((min(xx for xx, yy in dv if yy == 1), 1)) if any(yy == 1 for xx, yy in dv) else None
            dchain = convert.chain(dtrack['data'], dtrack['T']) if dtrack else None
            bad = c is not None and c not in id_map.values() and c != 0 and not any(kk[0] == c for kk in self.g.motions)
            if (c == 0 and r is not None and r[1] > END_LOW_Y and dchain) or (bad and dchain):
                tracks[(x, y)] = convert.set_chain(data, self.T11, c, dchain)
                notes.append('victim %d: follow-up %s -> %d (the donor\'s)' % (x, c, dchain))
        self.hand_off(mid, donor, tracks, notes)
        return tracks, notes

    def hand_off(self, mid, donor, tracks, notes):
        """2011's hand-off: the victim sets flag 21 and clears 2 where it lets go, and the attacker clears
        16 (HCTP's victim clears 26 instead, which 2011 lacks: without a release the attacker froze
        while the victim went on). Only for the donor kinds of move that have it."""
        dv = self.g.ymks(donor)
        src = {xy: k for k, xy in self.source_keys(mid)}
        for x in sorted(x for (x, y) in tracks if y == 0 and (x, 1) in tracks):
            da, dvt = dv.get((x, 0)) or dv.get((0, 0)), dv.get((x, 1)) or dv.get((0, 1))
            if not da or not dvt:
                continue
            drel = convert.flag_frames(dvt['data'], dvt['T'], 80, 2)
            if not drel:
                continue
            att, vic = tracks[(x, 0)], tracks[(x, 1)]
            na, nv = ymks.parse_stream(att).nframes(), ymks.parse_stream(vic).nframes()
            h = self.h.motions.get(src.get((x, 1)))
            c26 = convert.flag_frames(h['data'], h['T'], 80, 26) if h else []
            vrel = convert.flag_frames(vic, self.T11, 80, 2)
            c16 = convert.flag_frames(att, self.T11, 80, 16)
            if c26:
                frame, why = c26[-1], "HCTP's victim release (flag 26)"
            elif vrel:
                frame, why = vrel[-1], "the victim's release"
            elif c16:
                frame, why = c16[-1], "the attacker's flag 16"
            else:
                frame, why = round(drel[-1] * na / da['frames']), "the donor's timing"
            if not vrel:
                f = min(frame, nv - 2)
                tracks[(x, 1)] = convert.add_events(vic, self.T11, f, [(81, b'\x51\x15'), (80, b'\x50\x02')])
                notes.append('victim %d: release (set 21, clear 2) at frame %d, %s' % (x, f, why))
            if not c16:
                f = min(frame, na - 2)
                tracks[(x, 0)] = convert.add_events(att, self.T11, f, [(80, b'\x50\x10')])
                notes.append('attacker %d: clear flag 16 at frame %d, %s' % (x, f, why))

    def _converted(self, k, id_map):
        e = self.h.motions[k]
        return convert.convert_track(e['data'], k[2], e['T'], self.T11, id_map)[0]

    # ---- the pack
    def port(self, ids, out, pack_id, pack_name, donor_override=None):
        donor_override = donor_override or {}
        used = self.used_ids(skip_pack=pack_id)
        free = sorted(i for i in self.g.waze if 2000 <= i < EXTRA and i not in used)
        plan = {}
        report = dict(ported=[], skipped=[])
        for mid in ids:
            if not self.h.keys(mid):
                report['skipped'].append(dict(id=mid, why='HCTP has no motion with this id'))
                continue
            if self.in_2011(mid):
                report['skipped'].append(dict(id=mid, name=self.h.name(mid), why='already in 2011 (same animation)'))
                continue
            if not free:
                raise ValueError('No free SvR 2011 move records remain.')
            target = mid if (mid in self.g.waze and mid not in used and 2000 <= mid < EXTRA) else \
                min(free, key=lambda i: (abs(i - mid), i))
            used.add(target)
            if target in free:
                free.remove(target)
            plan[mid] = target
        id_map = dict(plan)
        os.makedirs(os.path.join(out, 'motions'), exist_ok=True)
        lines = ['# Here Comes the Pain moves for SvR 2011 (hctp_porter): HCTP animations, 2011 donor records']
        for mid, target in plan.items():
            name = self.h.name(mid) or 'HCTP move %d' % mid
            cands = [donor_override[mid]] if mid in donor_override else self.donor_candidates(mid)
            why = []
            tracks = None
            for donor in cands[:12]:
                if donor not in self.donors():
                    why.append('%d: not a usable 2011 move' % donor)
                    continue
                try:
                    tracks, notes = self.convert_move(mid, target, donor, id_map)
                    break
                except (ValueError, IndexError, struct.error) as err:
                    why.append('%d: %s' % (donor, err))
                    if 'track' in str(err) and 'starts motions' not in str(err):
                        break   # (the animation itself fails: no other donor helps)
            if tracks is None:
                why = why or ['no 2011 move starts from the same position (start class %s)' % self.start_of(mid)]
                report['skipped'].append(dict(id=mid, name=name, why=why))
                used.discard(plan[mid])
                continue
            dtracks = self.g.ymks(donor)
            # the donor's phases HCTP's move lacks (so its EXH records all have motions)
            ported = {xx for xx, yy in tracks}     # (before the loop: a kept phase keeps all its tracks)
            for (x, y), e in sorted(dtracks.items()):
                if x not in ported:
                    tracks[(x, y)] = e['data']
                    notes.append('phase %d track %d from the donor' % (x, y))
            paths = self.g.paths(donor)
            # the RR banks (a wrestler's match bank is gathered from them) load into a
            # fixed ~16,000 KiB buffer and 2011's RR00/RR01 are full to within a few KB:
            # a move added there pushed the bank's end out of the buffer - the attacker's
            # motion never loaded and he froze while the victim (MOTP) went on. All RR
            # copies go to the bank with the most room (the port's svr10_moves does the same)
            unpacked = [p for p in paths if p.startswith(('MOT/BM', 'MVMT'))] + [self.rr_bank()]
            packed = [p for p in paths if p.startswith('MOTP')]
            for (x, y), data in sorted(tracks.items()):
                fn = '%d_%d_%d.ymk' % (target, x, y)
                open(os.path.join(out, 'motions', fn), 'wb').write(data)
                n = ymks.parse_stream(data).nframes()
                for p in unpacked:
                    lines.append('motion m.pac %s %d %d %d %d motions/%s' % (p, target, x, y, n, fn))
                if packed:
                    try:
                        # a track kept from the donor unchanged: its own in-match (MOTP) copy, not a re-encoded one (2026-11-05: a dive's
                        # flight track 0_32 is not a body track - re-encoded it broke and the jump cut straight to the landing)
                        _own = [e['data'] for e in self.g.motions.get((donor, x, y), []) if e['path'].startswith('MOTP')]
                        _kept = any(e['data'] == data for e in self.g.motions.get((donor, x, y), []) if not e['path'].startswith('MOTP'))
                        pk = _own[0] if _own and _kept else convert.packed_stream(data, y, self.T11)
                    except (ValueError, IndexError, struct.error) as err:
                        notes.append('track %d/%d: no in-match packed copy (%s)' % (x, y, err))
                        continue
                    pfn = 'motp_' + fn
                    open(os.path.join(out, 'motions', pfn), 'wb').write(pk)
                    for p in packed:
                        lines.append('motion mpsp.pac %s %d %d %d %d motions/%s' % (p, target, x, y, n, pfn))
            for table, kind in ((self.g.exh, 'exh'), (self.g.mbd, 'mbd')):
                for g, rec in table.get(donor, []):
                    r = bytearray(rec)
                    struct.pack_into('<H', r, 0, target)
                    lines.append('%s %d %s' % (kind, g, r.hex()))
            for g, rec, evs in self.g.evt.get(donor, []):
                r = bytearray(rec)
                struct.pack_into('<H', r, 0, target)
                lines.append('evt %d %s %s' % (g, r.hex(), evs.hex()))
            lines += ['wazecopy %d %d' % (target, donor), 'waze %d %s' % (target, self.g.waze[donor]['flags']),
                      'wazename %d %s [HCTP]' % (target, name)]
            report['ported'].append(dict(hctp_id=mid, name=name, id=target, donor=donor,
                                         donor_name=self.g.waze[donor]['name'], tracks=sorted(tracks),
                                         notes=notes, donors_tried=why))
        added = {}
        for line in lines:
            f = line.split()
            if f and f[0] == 'motion' and f[2].startswith('MOT/RR'):
                added[f[2]] = added.get(f[2], 0) + os.path.getsize(os.path.join(out, f[7])) + 16
        for bank, n in added.items():
            stock = self._rr_size.get(bank, 0)
            report.setdefault('rr', {})[bank] = dict(stock=stock, added=n, cap=self.RR_CAP)
            if stock + n > self.RR_CAP * 0.98:
                raise ValueError('%s would hold %d bytes - over the game\'s %d-byte buffer' % (bank, stock + n, self.RR_CAP))
        open(os.path.join(out, 'pack.txt'), 'w', newline='\n').write('\n'.join(lines) + '\n')
        open(os.path.join(out, 'manifest.txt'), 'w', newline='\n').write(
            'type=moves\nid=%s\nname=%s\nauthor=hctp_porter\nversion=1.0\n' % (pack_id, pack_name))
        json.dump(report, open(os.path.join(out, 'report.json'), 'w'), indent=1)
        return report


def parse_ids(s):
    if s.startswith('@'):
        return [int(l.split()[0]) for l in open(s[1:]) if l.strip() and not l.startswith('#')]
    return [int(x) for x in s.split(',') if x.strip()]


def main(argv):
    ap = argparse.ArgumentParser(description='Here Comes the Pain -> SvR 2011 move porter')
    ap.add_argument('--hctp', default=hctp.DEFAULT_ROOT, help='HCTP extracted disc folder')
    ap.add_argument('--game', default=game11.DEFAULT_GAME, help='SvR 2011 game folder')
    sub = ap.add_subparsers(dest='cmd', required=True)
    s = sub.add_parser('survey')
    s.add_argument('--find', default='')
    s.add_argument('--all', action='store_true', help='also unnamed ids')
    i = sub.add_parser('info')
    i.add_argument('id', type=int)
    p = sub.add_parser('port')
    p.add_argument('ids')
    p.add_argument('--out')
    p.add_argument('--pack-id', default='hctp_moves')
    p.add_argument('--name', default='Here Comes the Pain moves')
    p.add_argument('--donor', default='', help='ID=DONOR,... overrides')
    p.add_argument('--install', action='store_true')
    a = ap.parse_args(argv)
    sys.stdout.reconfigure(encoding='utf-8')
    P = Porter(a.hctp, a.game)
    if a.cmd == 'survey':
        n = 0
        for mid in P.h.ids:
            if mid < 2000 or mid >= EXTRA or not any(k[2] == 0 for k in P.h.keys(mid)):
                continue
            name = P.h.name(mid)
            if not name and not a.all:
                continue
            if a.find.lower() not in name.lower():
                continue
            state = 'in 2011' if P.in_2011(mid) else 'portable'
            print('%5d  %-34s start %-4s tracks %-28s %s' % (mid, name, P.start_of(mid),
                  ' '.join('%d/%d' % t for k, t in P.source_keys(mid))[:28], state))
            n += 1
        print(n, 'moves')
    elif a.cmd == 'info':
        mid = a.id
        print(mid, P.h.name(mid), 'start class', P.start_of(mid), 'in 2011' if P.in_2011(mid) else '')
        for k, t in P.source_keys(mid):
            print('  track %d/%d (source %s) %d frames' % (t[0], t[1], k, P.h.motions[k]['frames']))
        for d in P.donor_candidates(mid)[:8]:
            print('  donor candidate %d %s' % (d, P.g.waze[d]['name']))
    elif a.cmd == 'port':
        out = a.out or os.path.join(HERE, 'out', a.pack_id)
        if os.path.isdir(out):
            shutil.rmtree(out)
        over = {int(x.split('=')[0]): int(x.split('=')[1]) for x in a.donor.split(',') if '=' in x}
        rep = P.port(parse_ids(a.ids), out, a.pack_id, a.name, over)
        for r in rep['ported']:
            print('ported %d %s -> id %d, donor %d %s' % (r['hctp_id'], r['name'], r['id'], r['donor'], r['donor_name']))
            for n in r['notes']:
                print('    ' + n)
        for r in rep['skipped']:
            print('SKIPPED %s %s: %s' % (r['id'], r.get('name', ''), r['why']))
        print('pack:', out)
        if a.install:
            dest = os.path.join(a.game, 'Mods', 'Moves', a.pack_id)
            if os.path.isdir(dest):
                shutil.rmtree(dest)
            shutil.copytree(out, dest)
            print('installed:', dest, '(the game merges it at its next start)')


if __name__ == '__main__':
    main(sys.argv[1:])
