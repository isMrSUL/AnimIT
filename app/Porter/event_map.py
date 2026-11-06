"""Match a ported move's events to AEW's own markers (montage notifies, both actors).

    BP_DA_C        the attack lands      -> the hit events (86, 32-36) go on these frames (order kept)
    BP_RingShake_C AEW shakes the ring   -> ring shakes (96-99) on exactly these frames: extra donor shakes are
                                            removed, missing ones added (the donor's level, on its track)
    BP_PlayVib_C / BP_MotionEnd_C        -> shown in AnimIT's event editor as guides only
Rumbles (111) and sounds within NEAR frames of a moved hit / shake move with it (a slam's thud stays on the slam).
The m.pac tracks are rewritten (poses kept byte for byte) and their packed MOTP copies made again from them,
exactly as the porter makes them. The markers are saved as aew_marks.json for the editor's guides.
"""
import json
import struct
from pathlib import Path

HIT = {86, 32, 33, 34, 35, 36}
SHAKE = {96, 97, 98, 99}
NEAR = 6
NAMES = {'BP_DA_C': 'hit', 'BP_RingShake_C': 'shake', 'BP_PlayVib_C': 'rumble', 'BP_MotionEnd_C': 'end'}


def _dedup(v):
    out = []
    for f in sorted(v):
        if not out or f - out[-1] > 3:
            out.append(f)
    return out


def aew_marks(montage):
    """AEW's markers on the 30 fps frames of the move (both actors' montages)"""
    import aew_notifies
    out = {k: [] for k in NAMES.values()}
    for side in 'BA':
        for t, name, _ in aew_notifies.notifies(montage, side):
            if name in NAMES:
                out[NAMES[name]].append(round(t * 30))
    return {k: _dedup(v) for k, v in out.items()}


def _align(frames, marks):
    """each event frame (in order) -> the nearest mark not used yet and not before the previous one; None = none left"""
    out, j = [], 0
    for f in frames:
        best = None
        for k in range(j, len(marks)):
            if best is None or abs(marks[k] - f) < abs(marks[best] - f):
                best = k
        if best is None:
            out.append(None)
        else:
            out.append(marks[best])
            j = best + 1
    return out


def _read(data, T):
    import ymks
    st = ymks.parse_stream(data)
    poses, evs = {}, []
    for f, rec in st.records():
        ev, pose = ymks.split_record(rec)
        if pose:
            poses[f] = pose
        for i, (op, b) in enumerate(ymks.parse_events(ev, T)[0]):
            if op is None:
                raise ValueError('an event block the porter cannot read')
            evs.append(dict(f=f, op=op, b=b, order=(f, i)))
    return st.nframes(), poses, evs


def _write(n, poses, evs):
    import ymks
    at = {}
    for e in sorted(evs, key=lambda e: e['order']):
        at.setdefault(e['f'], []).append(e['b'])
    recs, carry = [], []
    for f in range(n):
        block, nxt = b'', []
        for b in carry + at.get(f, []):
            if len(block) + len(b) <= 32:      # (the packed copy's limit, so both formats keep the same frames)
                block += b
            else:
                nxt.append(b)
        carry = nxt
        pose = poses.get(f, b'')
        if block or pose:
            recs.append((f, ymks.join_record(block, pose)))
    if carry:
        raise ValueError('too many events on the last frames')
    return ymks.build_stream(ymks.make_stream(recs, n))


def map_events(out, lines, mid, montage, T11):
    """-> (pack lines, notes). out: the pack folder; lines: its pack.txt lines (sounds are changed in them)"""
    import convert
    marks = aew_marks(montage)
    (Path(out) / 'aew_marks.json').write_text(json.dumps(dict(montage=montage, **marks)), encoding='utf-8')
    files = {}
    for l in lines:
        f = l.split()
        if f and f[0] == 'motion' and len(f) > 7 and f[3] == str(mid) and f[4] == '0' and f[5] in ('0', '1'):
            files.setdefault(int(f[5]), {})['m' if f[1] == 'm.pac' else 'p'] = Path(out) / ' '.join(f[7:])
    tracks = {}
    for y, fl in files.items():
        if 'm' in fl:
            tracks[y] = _read(fl['m'].read_bytes(), T11)
    notes = []
    if not marks['hit'] and not marks['shake']:
        return lines, ['AEW markers: none for the hit or ring shake in %s - events kept as retimed' % montage]
    evs = [dict(e, y=y) for y, (_, _, l) in tracks.items() for e in l]
    nf = max((t[0] for t in tracks.values()), default=1)
    anchors = []                       # (old frame, new frame) of moved hits / shakes

    def move(e, to):
        to = max(0, min(tracks[e['y']][0] - 2, to))
        anchors.append((e['f'], to))
        if to != e['f']:
            e['f'] = to
            e['order'] = (to, 99)
    hits = sorted((e for e in evs if e['op'] in HIT), key=lambda e: e['f'])
    if marks['hit'] and hits:
        was = [e['f'] for e in hits]
        last = 0
        for e, to in zip(hits, _align(was, marks['hit'])):
            if to is None:
                to = e['f'] + last
            else:
                last = to - e['f']
            move(e, to)
        notes.append('AEW markers: hit on frame %s (was %s)' % (', '.join(str(e['f']) for e in hits), ', '.join(map(str, was))))
    shakes = sorted((e for e in evs if e['op'] in SHAKE), key=lambda e: e['f'])
    # no ring-shake marker: AEW shakes the ring with the damage itself - the donor's shakes go on the hits (extra
    # ones, like German Suplex 5's three, are removed; none are added)
    shake_marks = marks['shake'] or (marks['hit'] if shakes else [])
    if shake_marks:
        src = 'ring shake' if marks['shake'] else 'damage (no ring-shake marker)'
        groups = []                      # one impact = the shakes of both actors within 3 frames
        for e in shakes:
            if groups and e['f'] - groups[-1][0]['f'] <= 3:
                groups[-1].append(e)
            else:
                groups.append([e])
        was = [g[0]['f'] for g in groups]
        removed = 0
        for g, to in zip(groups, _align(was, shake_marks)):
            for e in g:
                if to is None:
                    evs.remove(e)
                else:
                    move(e, to)
            removed += to is None
        added = 0
        if marks['shake']:               # AEW shakes the ring where the donor did not: add the donor's kind of shake
            tpl = shakes[0] if shakes else dict(y=0, op=97, b=bytes([97]))
            have = {e['f'] for e in evs if e['op'] in SHAKE}
            for m in marks['shake']:
                if tpl['y'] not in tracks:
                    break
                m = max(0, min(tracks[tpl['y']][0] - 2, m))
                if not any(abs(m - h) <= 1 for h in have):
                    evs.append(dict(f=m, op=tpl['op'], b=tpl['b'], y=tpl['y'], order=(m, 98)))
                    have.add(m)
                    added += 1
        now = sorted({e['f'] for e in evs if e['op'] in SHAKE})
        notes.append('AEW markers (%s): ring shake on frame %s (donor had %s%s%s)' % (
            src, ', '.join(map(str, now)) or 'none', ', '.join(map(str, was)) or 'none',
            '; %d extra removed' % removed if removed else '', '; %d added' % added if added else ''))
    # rumbles: each wrestler's pad rumbles on AEW's own rumble markers (order kept, none added or removed)
    if marks['rumble']:
        for y in tracks:
            rum = sorted((e for e in evs if e['op'] == 111 and e['y'] == y), key=lambda e: e['f'])
            for e, to in zip(rum, _align([e['f'] for e in rum], marks['rumble'])):
                if to is not None and to != e['f']:
                    e['f'] = max(0, min(tracks[y][0] - 2, to))
                    e['order'] = (e['f'], 97)
        notes.append('AEW markers: controller rumbles on AEW frames %s' % ', '.join(map(str, marks['rumble'])))

    def follow(f):
        best = min(anchors, key=lambda a: abs(a[0] - f), default=None)
        return f + best[1] - best[0] if best and abs(best[0] - f) <= NEAR else f
    if anchors and not marks['rumble']:
        for e in evs:
            if e['op'] == 111:
                to = max(0, min(tracks[e['y']][0] - 2, follow(e['f'])))
                if to != e['f']:
                    e['f'], e['order'] = to, (to, 97)
    # write both formats of each track
    for y, (n, poses, _) in tracks.items():
        data = _write(n, poses, [e for e in evs if e['y'] == y])
        files[y]['m'].write_bytes(data)
        if 'p' in files[y]:
            files[y]['p'].write_bytes(convert.packed_stream(data, y, T11))
    # sounds near a moved hit / shake follow it
    outl, moved = [], 0
    for l in lines:
        f = l.split()
        if anchors and f and f[0] == 'evt' and len(f) == 4:
            rec, ev = bytes.fromhex(f[2]), bytes.fromhex(f[3])
            if struct.unpack_from('<H', rec, 0)[0] == mid and rec[2] == 0 and rec[3] in tracks:
                items = []
                for k in range(0, len(ev) - 3, 4):
                    snd, fr = struct.unpack_from('<HH', ev, k)
                    to = max(0, min(tracks[rec[3]][0] - 1, follow(fr)))
                    moved += to != fr
                    items.append((to, snd))
                l = 'evt %s %s %s' % (f[1], f[2], b''.join(struct.pack('<HH', s, t) for t, s in sorted(items)).hex())
        outl.append(l)
    if moved:
        notes.append('AEW markers: %d sound(s) moved with the hit / ring shake they belong to' % moved)
    return outl, notes
