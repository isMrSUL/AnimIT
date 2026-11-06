"""Rank SvR 2011 dives as donors for an AEW dive (the donor's jump is kept, AEW's landings are ported).

A SvR dive: phase 0 = the jump / flight (attacker only; the game flies him to the opponent), 30 / 40 = landing on a
lying opponent (rolls off to +x / -x), 50 = landing on a standing one, 112 = the miss. A donor without the landing the
opponent needs makes the dive miss (2026-11-05, the user: ported Coffin Drops "don't hit the opponent").
"""
import hashlib
import json
import math
from pathlib import Path

import numpy as np

from donor_finder import VERSION, _roots, post


def dive_feats(g, mid, rig11=None):
    import convert
    import ymks
    t = g.ymks(mid)
    if (0, 0) not in t or not any(x in (30, 40, 50) and y == 1 for x, y in t):
        return None
    a = t[(0, 0)]
    r0 = convert.first_root(a['data'], a['T'])
    paths = g.paths(mid)
    family = ('top rope' if 'MVMT/CORN/f' in paths else 'springboard' if 'MVMT/ROPE/11' in paths
              else 'apron' if 'MVMT/APRO/12' in paths else 'standing')
    flags = g.waze[mid]['flags']
    # where an apron dive goes (the Apron Springboard list, flags byte 9): 0x04 into the ring, 0x08 to ringside
    # (2026-11-05, the user: Buckshot Lariat on Springboard Bulldog 2 - byte 9 0x08 - sat in "to ringside")
    b9 = int(flags[18:20], 16)
    f = dict(lying=(30, 1) in t or (40, 1) in t, standing=(50, 1) in t, att_h=float(-r0[1] * 0.01) if r0 else 0.0,
             hidden=not int(flags[:2], 16) and flags[6:8] in ('04', '08'), unused=not int(flags[:30], 16),
             family=family, direction='ring' if b9 & 0x04 else 'ringside' if b9 & 0x08 else None)
    for key, x in (('lie', 30), ('stand', 50)):
        if (x, 0) in t:
            ra = _roots(t[(x, 0)], ymks)
            f[key + '_frames'] = int(t[(x, 0)]['frames'])
            f[key + '_end_h'] = float(-ra[-1][1][1]) if ra else 0.0
        if (x, 1) in t and rig11 is not None:      # how the opponent ends (face up / down: the game goes on from it)
            import port_k26 as pk
            v = t[(x, 1)]
            r_end = pk.rt_last_rotations(v)
            if r_end:
                root = np.array(pk.donor_root_at(v, v['frames'] - 1)) * 0.01
                f[key + '_vic_up'] = bool(pk.chest_up(rig11.fk(root, r_end)[rig11.idx['mune']][1], rig11.rest_rot['mune']))
    return f


def pool(g, game, cache_dir, rig11=None):
    pac = Path(game) / 'pac'
    stamp = '|'.join('%s:%d:%d' % (f.name, f.stat().st_size, f.stat().st_mtime_ns)
                     for f in (pac / 'm.pac', pac / 'misc.pac') if f.exists())
    cache = Path(cache_dir) / ('dives4_%s.json' % hashlib.sha256(('%s|%d' % (stamp, VERSION)).encode()).hexdigest()[:16])
    if cache.is_file():
        return {int(k): v for k, v in json.loads(cache.read_text(encoding='utf-8')).items()}
    out = {}
    for mid in g.waze:
        if 2000 <= mid < 16384 and g.usable(mid) and mid in g.exh:
            try:
                f = dive_feats(g, mid, rig11)
            except Exception:   # noqa: BLE001
                f = None
            if f:
                out[mid] = f
    cache.parent.mkdir(parents=True, exist_ok=True)
    cache.write_text(json.dumps(out), encoding='utf-8')
    return out


def source(rig, rig11, group, has, std='000', lie=('010', '011'), family=None, direction=None):
    """the AEW landings: which exist, the attacker's end posture and length (the lying landing first);
    family: where AEW's jump starts (top rope / springboard / apron) - the donor's jump family should match"""
    import aew
    import port_k26 as pk
    s = (np.linalg.norm(rig11.rest_pos['l_ashi'] - rig11.rest_pos['l_momo'])
         / np.linalg.norm(rig.rest_world['J_Foot_L'][0] - rig.rest_world['J_Leg_L'][0]))
    lie = [v for v in lie if has(v)]
    src = dict(lying=bool(lie), standing=bool(std) and has(std), family=family, direction=direction)
    for key, v in (('lie', lie[0] if lie else None), ('stand', std if src['standing'] else None)):
        if v:
            path = aew.find('%s_%s' % (group, v), 'A')
            n, A = aew.clip_world(rig, path)
            valid = aew.VALID.get(path, len(A))
            src[key + '_frames'] = max(1, valid // 2)
            src[key + '_end_h'] = float(-pk.pelvis(A[valid - 1])[1] * s)
            try:                                   # how AEW's opponent ends
                pb = aew.find('%s_%s' % (group, v), 'B')
                nB, B = aew.clip_world(rig, pb)
                eB = aew.VALID.get(pb, len(B)) - 1
                src[key + '_vic_up'] = bool(pk.chest_up(B[eB]['J_Chest'][1], rig.rest_world['J_Chest'][1]))
            except KeyError:
                pass
    return src


def rank(src, pool_, limit=60):
    rows = []
    for mid, d in pool_.items():
        if d.get('unused'):
            continue
        checks, score = [], 0.0

        def ok(t):
            checks.append(dict(state='ok', text=t))

        def warn(t, w):
            nonlocal score
            checks.append(dict(state='warn', text=t))
            score += w
        if src.get('family'):
            fam = d.get('family')
            if fam == src['family']:
                ok('Jumps from the %s, like the source' % fam)
            else:
                warn('Jumps from the %s; the source from the %s' % (fam, src['family']), 8)
        if src.get('direction') and d.get('direction'):
            where = {'ring': 'into the ring', 'ringside': 'to ringside'}
            if d['direction'] == src['direction']:
                ok('Goes %s, like the source' % where[d['direction']])
            else:
                warn('Goes %s; the source goes %s (wrong move-set list)' % (where[d['direction']], where[src['direction']]), 8)
        if src['lying']:
            if d['lying']:
                ok('Lands on a lying opponent, like the source')
            else:
                warn('No landing on a lying opponent: the dive MISSES him', 12)
        if src['standing']:
            if d['standing']:
                ok('Lands on a standing opponent, like the source')
            else:
                warn('No landing on a standing opponent (misses him)', 4)
        for k2, what in (('stand', 'standing'), ('lie', 'lying')):
            if src.get(k2 + '_vic_up') is not None and d.get(k2 + '_vic_up') is not None:
                es = 'face up' if src[k2 + '_vic_up'] else 'face down'
                ed = 'face up' if d[k2 + '_vic_up'] else 'face down'
                if es == ed:
                    ok('Opponent ends %s after the %s landing, like the source' % (ed, what))
                else:
                    warn('Opponent ends %s after the %s landing; the source %s (he would flip)' % (ed, what, es), 6)
        key = 'lie' if src['lying'] and d['lying'] else 'stand'
        if key + '_end_h' in src and key + '_end_h' in d:
            es, ed = post(src[key + '_end_h']), post(d[key + '_end_h'])
            if es == ed:
                ok('Attacker ends %s, like the source' % es)
            else:
                warn('Attacker ends %s; the source ends %s' % (ed, es), 3)
            ratio = d[key + '_frames'] / max(1, src[key + '_frames'])
            score += min(1.0, abs(math.log(ratio)))      # (closer landing length first)
            if 0.6 <= ratio <= 1.7:
                ok('Similar landing (%d frames, source %d)' % (d[key + '_frames'], src[key + '_frames']))
            else:
                warn('Landing %s (%d frames, source %d)' % ('much longer' if ratio > 1 else 'much shorter',
                                                            d[key + '_frames'], src[key + '_frames']),
                     min(2, abs(math.log(ratio)) * 2))
        if d['att_h'] < 6 and src.get('family') in (None, 'top rope'):
            warn('Jumps from low (not the top rope)', 1)
        if d['hidden']:
            warn('Hidden in the game (AnimIT makes it visible; a visible donor is safer)', 1.5)
        rows.append(dict(id=mid, score=round(score, 2), checks=checks))
    rows.sort(key=lambda r: (r['score'], r['id']))
    return rows[:limit]


def summary(src):
    parts = ['dive: the donor jumps, AEW lands']
    if src['lying']:
        parts.append('on a lying opponent (attacker ends %s)' % post(src.get('lie_end_h', 0)))
    if src['standing']:
        parts.append('on a standing one')
    return ' · '.join(parts)
