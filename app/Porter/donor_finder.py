"""Rank SvR 2011 donors for one source move (AEW / 2K26), with the checks that decide a good port.

The rules come from the AEWFF pack (2026-10-30, all seen in game by the user):
  - same start: front grapple / from behind / opponent down (event 92 at frame 0),
  - the receiver ends the same way (face up / face down) - else he flips when the move hands back,
  - one ring shake (events 96-99) - more shake the ring at wrong times,
  - hit time and length close (less retiming / stretching), running and turnbuckle moves alike.
Every SvR move is measured once per game version and cached (donors_<hash>.json in AnimIT's work folder).
"""
import hashlib
import json
import math
from pathlib import Path

import numpy as np

VERSION = 4
ROPE_Y = {14, 15, 17, 18}
START = {0: 'front', 2: 'front', 1: 'behind', 10: 'ground', 11: 'ground', 12: 'ground', 19: 'ground', 5: 'dive',
         160: 'side', 161: 'side'}
LABEL = {'front': 'from the front', 'behind': 'from behind', 'ground': 'with the opponent down', 'side': 'from the side',
         'dive': 'as a dive', 'other': 'another way'}


def post(h):
    return 'lying' if h < 3.5 else 'bent' if h < 6.5 else 'standing'


# ---------------------------------------------------------------- SvR 2011 moves
def _roots(track, ymks):
    out = []
    for f, rec in ymks.parse_stream(track['data']).records():
        pose = ymks.split_record(rec)[1]
        if pose:
            try:
                body = ymks.decode_body(pose)
            except ValueError:
                continue
            for s in body['subs']:
                if 'root' in s:
                    out.append((f, np.array(s['root'], float) * 0.01))
                    break
    return out


def donor_feats(g, rig11, mid):
    import convert
    import ymks
    import port_k26 as pk
    t = g.ymks(mid)
    a, v = t.get((0, 0)), t.get((0, 1))
    if not a or not v or any(y in ROPE_Y for _, y in t):
        return None
    ra, rv = _roots(a, ymks), _roots(v, ymks)
    if not ra or not rv:
        return None
    sc = convert.start_class(a['data'], a['T'])
    vic_h = float(-rv[0][1][1])
    start = 'ground' if post(vic_h) == 'lying' and sc not in (5,) else START.get(sc, 'other')
    near = [k for k in ra if k[0] <= 5]
    speed = float(np.linalg.norm((near[-1][1] - near[0][1])[[0, 2]]) / max(1, near[-1][0] - near[0][0])) if len(near) > 1 else 0.0
    face_up = None
    r_end = pk.rt_last_rotations(v)
    if r_end:
        fk = rig11.fk(rv[-1][1], r_end)
        face_up = bool(pk.chest_up(fk[rig11.idx['mune']][1], rig11.rest_rot['mune']))
    ev = {}
    for tr in (a, v):
        for f, evs in pk.donor_events(tr).items():
            for op, _ in evs:
                ev.setdefault(op, []).append(f)
    shakes = sorted(f for o in (96, 97, 98, 99) for f in ev.get(o, []))
    merged = [f for i, f in enumerate(shakes) if i == 0 or f - shakes[i - 1] > 3]   # (both actors carry the same one)
    return dict(start=start, sc=sc, att_h=float(-ra[0][1][1]), vic_h=vic_h, vic_end_h=float(-rv[-1][1][1]),
                face_up=face_up, speed=speed, dist=float(np.linalg.norm((ra[0][1] - rv[0][1])[[0, 2]])),
                hit=min(ev[86]) if ev.get(86) else (merged[0] if merged else None), shakes=len(merged),
                frames=int(a['frames']), hidden=not int(g.waze[mid]['flags'][:2], 16),
                grapple=g.waze[mid]['flags'][6:8] in ('04', '08'), unused=not int(g.waze[mid]['flags'][:30], 16))


def pool(g, rig11, game, cache_dir):
    pac = Path(game) / 'pac'
    stamp = '|'.join('%s:%d:%d' % (f.name, f.stat().st_size, f.stat().st_mtime_ns)
                     for f in (pac / 'm.pac', pac / 'misc.pac', pac / 'mpsp.pac') if f.exists())
    cache = Path(cache_dir) / ('donors_%s.json' % hashlib.sha256(('%s|%d' % (stamp, VERSION)).encode()).hexdigest()[:16])
    if cache.is_file():
        return {int(k): v for k, v in json.loads(cache.read_text(encoding='utf-8')).items()}
    out = {}
    for mid in g.waze:
        if 2000 <= mid < 16384 and g.usable(mid) and mid in g.exh:
            try:
                f = donor_feats(g, rig11, mid)
            except Exception:   # noqa: BLE001  (a move the measures can't read is just left out)
                f = None
            if f:
                out[mid] = f
    cache.parent.mkdir(parents=True, exist_ok=True)
    cache.write_text(json.dumps(out), encoding='utf-8')
    return out


# ---------------------------------------------------------------- the source move
def _fwd(w, joint, rest):
    v = w[joint][1] @ rest.T @ np.array([0, 0, -1.0])
    h = np.array([v[0], v[2]])
    n = np.linalg.norm(h)
    return h / n if n > 1e-6 else h


def source_feats(rig, rig11, A, B, end, hit):
    """A / B: world joints per 60 fps frame (porter axes, cm); end: 60 fps frame the move ends; hit: 30 fps or None"""
    import port_k26 as pk
    s = (np.linalg.norm(rig11.rest_pos['l_ashi'] - rig11.rest_pos['l_momo'])
         / np.linalg.norm(rig.rest_world['J_Foot_L'][0] - rig.rest_world['J_Leg_L'][0]))
    pel = pk.pelvis
    pa = pel(A[0])
    k = min(10, len(A) - 1)
    speed = float(np.linalg.norm((pel(A[k]) - pa)[[0, 2]]) / max(1, k) * 2 * s)
    f = dict(att_h=float(-pa[1] * s), speed=speed, frames=max(1, end // 2), hit=hit, dist=0.0)
    if B is None:
        f.update(start='other', vic_h=0.0, vic_end_h=0.0, face_up=None)
        return f
    pv = pel(B[0])
    e = min(end, len(B)) - 1
    f['vic_h'] = float(-pv[1] * s)
    f['vic_end_h'] = float(-pel(B[e])[1] * s)
    f['face_up'] = bool(pk.chest_up(B[e]['J_Chest'][1], rig.rest_world['J_Chest'][1]))
    f['dist'] = float(np.linalg.norm((pa - pv)[[0, 2]]) * s)
    if post(f['vic_h']) == 'lying':
        f['start'] = 'ground'
    else:
        d = float(np.dot(_fwd(A[0], 'J_Hips', rig.rest_world['J_Hips'][1]), _fwd(B[0], 'J_Hips', rig.rest_world['J_Hips'][1])))
        f['start'] = 'behind' if d > 0.3 else 'front' if d < -0.3 else 'side'
    return f


def end_text(h, up):
    p = post(h)
    return ('lying face up' if up else 'lying face down') if p == 'lying' and up is not None else p


# ---------------------------------------------------------------- ranking
def rank(src, pool_, limit=60):
    rows = []
    for mid, d in pool_.items():
        if d.get('unused'):
            continue      # (in no move-set list: unused - worker.py)
        checks, score = [], 0.0

        def ok(t):
            checks.append(dict(state='ok', text=t))

        def warn(t, w):
            nonlocal score
            checks.append(dict(state='warn', text=t))
            score += w

        if src['start'] == d['start']:
            ok('Starts %s, like the source' % LABEL[src['start']])
        elif src['start'] in ('side', 'other') or d['start'] in ('side', 'other'):
            warn('Starts %s; the source starts %s' % (LABEL[d['start']], LABEL[src['start']]), 3)
        else:
            warn('Starts %s; the source starts %s' % (LABEL[d['start']], LABEL[src['start']]), 10)
        run_s, run_d = src['speed'] > 0.6, d['speed'] > 0.6
        if run_s != run_d:
            warn('Attacker %s; the source %s' % ('runs in' if run_d else 'stands', 'runs in' if run_s else 'stands'), 5)
        if (src['att_h'] > 12) != (d['att_h'] > 12):
            warn('Attacker starts %s' % ('up high (turnbuckle)' if d['att_h'] > 12 else 'on the mat'), 6)
        if src.get('face_up') is not None or src['vic_end_h']:
            es, ed = end_text(src['vic_end_h'], src['face_up']), end_text(d['vic_end_h'], d['face_up'])
            if es == ed:
                ok('Receiver ends %s, like the source' % es)
            elif post(src['vic_end_h']) == 'lying' and post(d['vic_end_h']) == 'lying':
                warn('Receiver ends %s; the source ends %s (he would flip)' % (ed, es), 8)
            else:
                warn('Receiver ends %s; the source ends %s' % (ed, es), 5)
        n = d['shakes']
        if n <= 1:
            ok('One ring shake' if n == 1 else 'No ring shake')
        else:
            warn('%d ring shakes (extra shakes at wrong times)' % n, min(6, 2 * (n - 1)))
        if src.get('hit') is not None and d.get('hit') is not None:
            diff = abs(src['hit'] - d['hit'])
            if diff <= 12:
                ok('Hit at frame %d (source %d)' % (d['hit'], src['hit']))
            else:
                warn('Hit at frame %d; the source hits at %d (events get moved)' % (d['hit'], src['hit']), min(3, diff / 20))
        ratio = d['frames'] / max(1, src['frames'])
        if 0.67 <= ratio <= 1.5:
            ok('Similar length (%d frames, source %d)' % (d['frames'], src['frames']))
        else:
            warn('%s (%d frames, source %d)' % ('Much longer' if ratio > 1 else 'Much shorter', d['frames'], src['frames']),
                 min(3, abs(math.log(ratio)) * 3))
        if d['hidden'] and d.get('grapple'):
            warn('Hidden in the game (AnimIT makes it visible; a visible donor is safer)', 1.5)
        score += min(2, abs(src['dist'] - d['dist']) / 4)
        rows.append(dict(id=mid, score=round(score, 2), checks=checks))
    rows.sort(key=lambda r: (r['score'], r['id']))
    return rows[:limit]


def summary(src):
    parts = ['starts %s' % LABEL[src['start']]]
    if src['vic_end_h'] or src.get('face_up') is not None:
        parts.append('receiver ends %s' % end_text(src['vic_end_h'], src['face_up']))
    if src.get('hit') is not None:
        parts.append('hit at frame %d' % src['hit'])
    parts.append('%d frames' % src['frames'])
    if src['speed'] > 0.6:
        parts.append('running')
    if src['att_h'] > 12:
        parts.append('from up high')
    return ' · '.join(parts)
