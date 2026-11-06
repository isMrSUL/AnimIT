"""Port AEW Fight Forever moves into SvR 2011 as a move pack (offline; the game is not run).

    python -I port_aew.py donors <montage> ...                       rank 2011 donors (measured start / end)
    python -I port_aew.py port "<montage>:<2011 donor>[@start]:<name>" ... [--out DIR]

montage: the AEW animation montage, e.g. 040_00014_000 (clips A_<montage>_A_* attacker, _B_* receiver).
The k26_porter does the rest exactly as for 2K26 clips (retarget onto 2011's 21 channels, placement in 2011's
move space by the donor's start class, 30 fps, donor events / records / banks): aew.py hands it the AEW joints
in its conventions.  Both AEW clips of a pair share one origin, so the receiver needs no shift.
"""
import argparse
import json
import os
import struct
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
K26 = os.path.join(os.path.dirname(HERE), 'k26')
HCTP = os.path.join(os.path.dirname(HERE), 'hctp')
sys.path[:0] = [HERE, K26, HCTP, os.path.join(HCTP, 'vendor')]
import aew  # noqa: E402
import aew_parts  # noqa: E402
import aew_notifies  # noqa: E402
import k26  # noqa: E402
import port_k26 as pk  # noqa: E402
import retarget as rt  # noqa: E402
import game11  # noqa: E402
import convert  # noqa: E402
import ymks  # noqa: E402


END_BLEND = 15   # (30 fps frames: both actors ease into the donor's last pose - the game's hand-back is smooth)


def mid_of(montage):
    """040_00014_000 -> (4000014, '000'): the porter names clips '<int id>a<variant>'"""
    a, b, v = montage.split('_')
    return int(a + b), v


def montage_of(mid, var):
    s = '%08d' % mid
    return '%s_%s_%s' % (s[:3], s[3:], var)


_cache = {}


def find_clip(name):
    i = next(k for k, c in enumerate(name) if c in 'ab')
    mid, side, var = int(name[:i]), name[i], name[i + 1:]
    return aew.find(montage_of(mid, var), side.upper())


def clip_world(rig, path):
    if path not in _cache:
        n, W = aew.clip_world(rig, path)
        for w in W:
            w['J_Root'] = w['Root']
        _cache[path] = (n, W)
    return _cache[path]


k26.find_clip = find_clip
k26.clip_world = clip_world


def pair_variants(mid):
    out = []
    for v in ('000', '010', '011', '020', '030', '040', '100'):
        try:
            aew.find(montage_of(mid, v), 'A')
            aew.find(montage_of(mid, v), 'B')
            out.append(v)
        except KeyError:
            pass
    return out


k26.pair_variants = pair_variants


# ---- donors ----
def aew_feats(rig, rig11, montage):
    mid, var = mid_of(montage)
    s = (np.linalg.norm(rig11.rest_pos['l_ashi'] - rig11.rest_pos['l_momo'])
         / np.linalg.norm(rig.rest_world['J_Foot_L'][0] - rig.rest_world['J_Leg_L'][0]))
    n, A = clip_world(rig, find_clip('%da%s' % (mid, var)))
    try:
        n2, B = clip_world(rig, find_clip('%db%s' % (mid, var)))
    except KeyError:
        B, n2 = None, n
    pel = pk.pelvis
    pa = pel(A[0])
    k = min(10, n - 1)
    sp = np.linalg.norm((pel(A[k]) - pa)[[0, 2]]) / k * 2 * s
    if B is None:
        return dict(dist=0.0, att_h=-pa[1] * s, vic_h=0.0, vic_end_h=0.0, speed=sp, frames=n // 2), s
    pv, pv_end = pel(B[0]), pel(B[min(n, n2) - 1])
    return dict(dist=float(np.linalg.norm((pa - pv)[[0, 2]]) * s), att_h=-pa[1] * s, vic_h=-pv[1] * s,
                vic_end_h=-pv_end[1] * s, speed=sp, frames=min(n, n2) // 2), s


def donors(argv):
    import donors as dn
    g = game11.Game11()
    rig, rig11 = aew.RigAEW(), rt.Rig11()
    pool = {}
    for mid, w in g.waze.items():
        if 2000 <= mid < 16384 and g.usable(mid) and mid in g.exh:
            try:
                f = dn.g11_feats(g, mid)
            except Exception:   # noqa: BLE001
                f = None
            if f:
                pool[mid] = f
    for montage in argv:
        k, s = aew_feats(rig, rig11, montage)
        print('%s: dist %.1f att_h %.1f vic_h %.1f vic_end_h %.1f speed %.2f frames %d (scale %.4f)' % (
            montage, k['dist'], k['att_h'], k['vic_h'], k['vic_end_h'], k['speed'], k['frames'], s))
        best = sorted(pool, key=lambda m: dn.score(k, pool[m]))[:12]
        for m in best:
            d = pool[m]
            print('   %5d %-34s score %4.1f  start %3s dist %4.1f vic_h %4.1f end %4.1f speed %.2f frames %d' % (
                m, g.waze[m]['name'][:34], dn.score(k, d), d['start'], d['dist'], d['vic_h'], d['vic_end_h'],
                d['speed'], d['frames']))


# ---- splice (a long repeating sell) ----
SPLICE_BLEND = 12        # 60 fps frames the remaining pose difference at a splice fades over


def rot_power(R, t):
    """the rotation R scaled to fraction t of its angle"""
    c = max(-1.0, min(1.0, (np.trace(R) - 1) / 2))
    a = np.arccos(c)
    if a < 1e-6:
        return np.eye(3)
    k = np.array([R[2, 1] - R[1, 2], R[0, 2] - R[2, 0], R[1, 0] - R[0, 1]]) / (2 * np.sin(a))
    K = np.array([[0, -k[2], k[1]], [k[2], 0, -k[0]], [-k[1], k[0], 0]])
    return np.eye(3) + np.sin(a * t) * K + (1 - np.cos(a * t)) * K @ K


def splice(rig, mid, var, win_a, win_b):
    """cut frames [a, b) out of both clips (a in win_a, b in win_b, chosen where the victim's poses match
    best); the difference left at the joint fades out over SPLICE_BLEND frames"""
    pa, pb = find_clip('%da%s' % (mid, var)), find_clip('%db%s' % (mid, var))
    A, B = clip_world(rig, pa)[1], clip_world(rig, pb)[1]
    J = rt.BODY26
    cost = lambda a, b: sum(np.linalg.norm(B[a][j][0] - B[b][j][0]) for j in J)
    a, b = min(((a, b) for a in range(*win_a) for b in range(*win_b)), key=lambda ab: cost(*ab))
    for path, W in ((pa, A), (pb, B)):
        out = W[:a] + W[b:]
        ref, src = W[a - 1], W[b - 1]
        for i in range(SPLICE_BLEND):
            k = a + i
            if k >= len(out):
                break
            t = 1 - (i + 1) / (SPLICE_BLEND + 1)
            out[k] = {j: (p + t * (ref[j][0] - src[j][0]), rot_power(ref[j][1] @ src[j][1].T, t) @ r)
                      if j in ref else (p, r) for j, (p, r) in out[k].items()}
        n = _cache[path][0] - (b - a)
        _cache[path] = (n, out)
        aew.VALID[path] = max(1, aew.VALID[path] - (b - a)) if aew.VALID[path] > a else aew.VALID[path]
    return a, b, cost(a, b) / len(J)


# ---- length / dives ----
SELL_AFTER_DOWN = 90     # 60 fps frames kept after the victim is down (AEW sells for up to 6 s more)


def move_end(rig, mid, var):
    """60 fps frame the move ends: the attacker's real length (clips jump where AEW's game takes over) or the
    victim down + SELL_AFTER_DOWN, whichever is later"""
    pa, pb = find_clip('%da%s' % (mid, var)), find_clip('%db%s' % (mid, var))
    nA, A = clip_world(rig, pa)
    nB, B = clip_world(rig, pb)
    n = min(nA, nB)
    h = [-pk.pelvis(B[f])[1] for f in range(n)]
    top = int(np.argmax(h[:aew.VALID[pb]]))
    down = next((f for f in range(top, aew.VALID[pb]) if h[f] < 30), None)
    end = n if down is None else min(n, max(aew.VALID[pa], down + SELL_AFTER_DOWN))
    return end, aew.VALID[pa], aew.VALID[pb], down


def jump_launch(takeoff):
    """the AEW jump's launch frame (60 fps): its take-off marker, else the frame its hips are highest (the top of
    the jump: Buckshot Lariat's flip over the rope; its homing marker comes after the peak, too late)"""
    marks = {n_: t_ for t_, n_, _ in reversed(aew_notifies.notifies(takeoff, 'A'))}
    for name in ('BP_DivingTakeoff_C',):
        if name in marks:
            return round(marks[name] * 60), name
    tm, tv = mid_of(takeoff)
    n, A = clip_world(aew.RigAEW(), find_clip('%da%s' % (tm, tv)))
    return int(np.argmax([-pk.pelvis(w)[1] for w in A])), 'highest point'


def jump_cut(rig, takeoff, im, v, launch):
    """the jump frame (60 fps, after the launch) whose pose best meets the landing's first pose - AEW's jump clips may
    run on (Buckshot Lariat's flip lands on its feet 3 m on), the landing starts in the air"""
    tm, tv = mid_of(takeoff)
    n, J = clip_world(rig, find_clip('%da%s' % (tm, tv)))
    n2, L = clip_world(rig, find_clip('%da%s' % (im, v)))
    target = -pk.pelvis(L[0])[1]
    window = range(min(launch + 2, len(J) - 1), min(len(J), launch + 41))     # (the fall right after the launch)
    best = min(window, key=lambda f: abs(-pk.pelvis(J[f])[1] - target))
    return best + 1


def dive_plan(g, rig, rig11, takeoff, impacts, donor, flight=True, std=None, lie=None):
    """an AEW dive -> [(phase, mid, variant, frames, solo, mirrored, face)] for a 2011 dive donor.
    AEW splits a dive as 2011 does: take-off / flight (attacker only), then impact pairs by the victim's
    state (impact montage variants: 000 standing front, 010 / 011 lying face up hit from the right / left).
    2011: phase 0 flight, 30 / 40 lying victim (rolls off to +x / -x), 50 standing victim, 112 miss (kept)."""
    dt = g.ymks(donor)
    plan = []
    im, _ = mid_of(impacts + '_000')
    if flight:
        tm, tv = mid_of(takeoff)
        launch, _ = jump_launch(takeoff)
        end = None
        land = std or (lie[0] if lie else None)
        if land:
            try:
                end = jump_cut(rig, takeoff, im, land, launch)
            except KeyError:
                end = None
        plan.append((0, tm, tv, (0, end) if end else None, True, False, 'pair'))

    def has(v):          # (an AEW dive may lack a landing: that phase stays the donor's)
        try:
            find_clip('%da%s' % (im, v))
            find_clip('%db%s' % (im, v))
            return True
        except KeyError:
            return False
    lying = [v for v in (lie if lie is not None else ('010', '011')) if has(v)]
    std = std if std is not None else '000'
    for x in sorted({x for x, y in dt if x and x < 100 and (x, 1) in dt}):
        v_tr = dt[(x, 1)]
        root = np.array(convert.first_root(v_tr['data'], v_tr['T'])) * 0.01
        if -root[1] > 3.5:
            if std and has(std):
                plan.append((x, im, std, None, False, False, 'pair'))
            continue
        if not lying:
            continue
        a_tr = dt[(x, 0)]
        disp_d = (np.array(pk.donor_root_at(a_tr, a_tr['frames'] - 1))
                  - np.array(convert.first_root(a_tr['data'], a_tr['T']))) * 0.01
        js = rig11.fk(root, pk.rt_first_rotations(v_tr))
        axd = js[rig11.idx['atama']][0] - js[rig11.idx['root']][0]
        side_d = 1 if (axd[2] * disp_d[0] - axd[0] * disp_d[2]) > 0 else -1
        best = None
        for v in lying:
            n, A = clip_world(rig, find_clip('%da%s' % (im, v)))
            n2, B = clip_world(rig, find_clip('%db%s' % (im, v)))
            ax = B[0]['J_Head'][0] - pk.pelvis(B[0])
            dp = pk.pelvis(A[n - 1]) - pk.pelvis(A[0])
            side_o = 1 if (ax[2] * dp[0] - ax[0] * dp[2]) > 0 else -1
            if side_o == side_d:
                best = (v, False)
                break
        v, mirrored = best or (lying[0], True)   # neither side matches: mirror the one AEW has
        plan.append((x, im, v, None, False, mirrored, 'victim'))
    return plan


# ---- port ----
def port(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument('moves', nargs='+')
    ap.add_argument('--out', default=os.path.join(HERE, 'out', 'aew_moves'))
    a = ap.parse_args(argv)
    g = game11.Game11()
    T11 = next(e['T'] for e in g.motions[(718, 0, 0)] if e['kind'] == b'YMKs')
    rig, rig11 = aew.RigAEW(), rt.Rig11()
    used = set(k[0] for k in g.motions)
    for root, dirs, files in os.walk(os.path.join(game11.DEFAULT_GAME, 'Mods')):
        if ('pack.txt' in files and os.path.basename(root) != os.path.basename(a.out)
                and not os.path.exists(os.path.join(root, 'disabled'))):
            for line in open(os.path.join(root, 'pack.txt'), errors='replace'):
                f = line.split()
                if f and f[0] == 'motion' and len(f) > 3:
                    used.add(int(f[3]))
                elif f and f[0] in ('waze', 'wazecopy', 'wazename') and len(f) > 1:
                    used.add(int(f[1]))
    os.makedirs(os.path.join(a.out, 'motions'), exist_ok=True)
    lines = ['# AEW Fight Forever moves for SvR 2011 (aewff_port + k26_porter): AEW animations, 2011 donor records']
    report = []
    for spec in a.moves:
        # <montage>:<donor>[@start][+pair][+run][+situp=N][+cut=..]:<name>   or a dive:  <take-off montage>/<impact montage>:<donor>:<name>
        #   +pair: turn by the pair's spacing even for a victim near the mat (a seated victim has no lying axis)
        #   +run:  a running attack - the victim stays on the donor's spot, the attacker closes the distance
        montage, donor, name = spec.split(':', 2)
        start, face = None, 'pair'
        donor, *opts = donor.split('+')
        if 'pair' in opts:
            face = 'pair-forced'
        anchor_mode = 'run' if 'run' in opts else 'attacker'
        #   +situp=N: the victim blends from the donor's lying pose into the AEW one over N (30 fps) frames
        lead_in = next((int(o[6:]) for o in opts if o.startswith('situp=')), 0)
        #   +contact: retime the donor's events around first contact, not the victim going down (long sells)
        landmark = 'contact' if 'contact' in opts else 'down'
        #   +dmg=H/B/A/L: the move's damage to head / body / arms / legs (the donor's EXH bytes 10-13 otherwise)
        dmg = next(([int(x) for x in o[4:].split('/')] for o in opts if o.startswith('dmg=')), None)
        if '@' in donor:
            donor, start = donor.split('@')
            start = int(start)
        donor = int(donor)
        if '/' in montage:
            takeoff, impacts = montage.split('/')
            # take-off '-': keep the donor's flight (AEW's take-off clips hold one pose in the air while the
            # game flies the actor - the flight path is not in them)
            std = next((o[4:] for o in opts if o.startswith('std=')), None)
            lie = next((o[4:] for o in opts if o.startswith('lie=')), None)
            plan = dive_plan(g, rig, rig11, takeoff, impacts, donor, flight=takeoff != '-', std=std,
                             lie=None if lie is None else [v for v in lie.split(',') if v])
        else:
            mid, var = mid_of(montage)
            cut = next((o[4:] for o in opts if o.startswith('cut=')), None)
            if cut:     # +cut=a0-a1/b0-b1 : splice out [a, b) of a repeating sell (60 fps frames)
                (a0, a1), (b0, b1) = [tuple(int(x) for x in w.split('-')) for w in cut.split('/')]
                sa, sb, err = splice(rig, mid, var, (a0, a1), (b0, b1))
            end, va, vb, down = move_end(rig, mid, var)
            plan = [(0, mid, var, (0, end), None, False, face)]
        tracks, notes, fk11, src = {}, [], None, None
        if '/' not in montage and cut:
            notes.append('spliced out AEW frames %d-%d (the repeating stagger; poses %.1f cm apart per joint)'
                         % (sa, sb, err))
        if '/' not in montage:
            notes.append('AEW clips: attacker real to frame %d, victim to %d, victim down at %s; move ends at %d (60 fps)'
                         % (va, vb, down, end))
        # event timing (2026-10-30, the user: the ring shook / the camera slowed at the wrong time): retime the
        # donor's events around AEW's own damage frame (the receiver montage's BP_DA notify) and the donor's hit
        # (event 86, else its first ring impact 96-99) - not around 'the hips reach the mat', which a DDT or a slam
        # reaches late
        da = [] if '/' in montage else [t_ for t_, n_, _ in aew_notifies.notifies(montage, 'B') if n_ == 'BP_DA_C']
        marks = None
        dive_fall = None
        if da:
            dt_ = g.ymks(donor)
            dev = {}
            for y_ in (0, 1):
                if (0, y_) in dt_:
                    for f_, evs_ in pk.donor_events(dt_[(0, y_)]).items():
                        for op_, b_ in evs_:
                            dev.setdefault(op_, []).append(f_)
            dhit = min(dev.get(86, [])) if dev.get(86) else min((f_ for o_ in (96, 97, 98, 99) for f_ in dev.get(o_, [])), default=None)
            if dhit is not None:
                marks = [round(da[-1] * 30), dhit]
                notes.append('events retimed on AEW damage frame %d (30 fps) <- donor hit frame %d' % tuple(marks))
        # an AEW dive with its own jump (phase 0 from AEW's take-off clip): the donor's launch (op 87, the game flies
        # him from that frame) goes on AEW's own take-off marker (BP_DivingTakeoff_C) - 2026-11-05, the user: a
        # Coffin Drop on the donor's jump looked like the donor's move (Diving Body Press), not a Coffin Drop
        if '/' in montage and takeoff != '-':
            l60, lname = jump_launch(takeoff)
            dlaunch = min((f_ for f_, evs_ in pk.donor_events(g.ymks(donor)[(0, 0)]).items()
                           if any(op_ == 87 for op_, _ in evs_)), default=None)
            if dlaunch is not None:
                marks = [round(l60 / 2), dlaunch]
                notes.append('jump: AEW launch from its %s' % lname)
                dive_fall = tuple(marks)
                notes.append('jump: the donor launch (frame %d) moved to AEW take-off frame %d (30 fps)'
                             % (marks[1], marks[0]))
        keep_mark = pk.landmark_victim_down
        if marks:
            seq = list(marks)
            pk.landmark_victim_down = lambda heights, frac=0.15: seq.pop(0) if seq else keep_mark(heights, frac)
        for ph, m_, v, fr, so, mi, fc in plan:
            t, nt, (f11, sr) = pk.port_one(g, rig, rig11, m_, name, donor, v, ph, fr, so, mi, fc, start,
                                           anchor_mode='impact' if '/' in montage and not so else anchor_mode,
                                           victim_lead_in=lead_in, landmark=landmark,
                                           part_events=aew_parts.events, end_blend=0 if (so and '/' in montage and ph == 0) else END_BLEND,
                                           fall=dive_fall if (so and ph == 0) else None)
            # (a dive's own jump: no ease into the donor's last pose - its landing follows, and the donor's jump ends in
            # ITS pose: a Coffin Drop's backward fall turned into the body press's forward one in the air, 2026-11-05)
            if mi:
                nt.insert(1, 'phase %d: AEW %s mirrored (2011 rolls off to the other side here)' % (ph, v))
            tracks.update(t)
            notes += nt
            if fk11 is None:
                fk11, src = f11, sr
        pk.landmark_victim_down = keep_mark
        notes = [n.replace('2K26', 'AEW') for n in notes]
        ported = {xx for xx, yy in tracks}     # (fixed 2026-11-05: computed in the loop, a kept phase lost its other
        dive = any(x_ in (30, 40, 50) and y_ == 1 for x_, y_ in g.ymks(donor))
        for (x, y), e in sorted(g.ymks(donor).items()):   # tracks - a dive's flight 0_32: the jump cut to the landing)
            # (a dive's jump keeps its flight-parameter track - y 11 / 12 / 32, 5-byte records, not a body - even when
            # the jump itself is AEW's)
            if (x, y) not in tracks and (x not in ported or (dive and x == 0 and y in (11, 12, 32))):
                tracks[(x, y)] = e['data']
                notes.append("phase %d track %d: the donor's" % (x, y))
        target = min((i for i in g.waze if 2000 <= i < 16384 and i not in used), key=lambda i: (abs(i - donor), i))
        used.add(target)
        paths = g.paths(donor)
        unpacked = [p for p in paths if p.startswith(('MOT/BM', 'MVMT'))] + [pk.RR_BANK]
        packed = [p for p in paths if p.startswith('MOTP')]
        for (x, y), data in sorted(tracks.items()):
            fn = '%d_%d_%d.ymk' % (target, x, y)
            open(os.path.join(a.out, 'motions', fn), 'wb').write(data)
            nfr = ymks.parse_stream(data).nframes()
            for p in unpacked:
                lines.append('motion m.pac %s %d %d %d %d motions/%s' % (p, target, x, y, nfr, fn))
            if packed:
                # a track kept from the donor unchanged: its own in-match (MOTP) copy, not a re-encoded one (2026-11-05: a dive's
                # flight track 0_32 is not a body track - re-encoded it broke and the jump cut straight to the landing)
                _own = [e['data'] for e in g.motions.get((donor, x, y), []) if e['path'].startswith('MOTP')]
                _kept = any(e['data'] == data for e in g.motions.get((donor, x, y), []) if not e['path'].startswith('MOTP'))
                pkd = _own[0] if _own and _kept else convert.packed_stream(data, y, T11)
                open(os.path.join(a.out, 'motions', 'motp_' + fn), 'wb').write(pkd)
                for p in packed:
                    lines.append('motion mpsp.pac %s %d %d %d %d motions/motp_%s' % (p, target, x, y, nfr, fn))
        for table, kind in ((g.exh, 'exh'), (g.mbd, 'mbd')):
            for grp, rec in table.get(donor, []):
                r = bytearray(rec)
                struct.pack_into('<H', r, 0, target)
                if kind == 'exh' and dmg and len(r) >= 14 and r[2] == 0:   # (the move's main record)
                    notes.append('damage head/body/arms/legs %s -> %s' % (list(r[10:14]), dmg))
                    r[10:14] = bytes(dmg)
                lines.append('%s %d %s' % (kind, grp, r.hex()))
        for grp, rec, evs in g.evt.get(donor, []):
            r = bytearray(rec)
            struct.pack_into('<H', r, 0, target)
            lines.append('evt %d %s %s' % (grp, r.hex(), evs.hex()))
        lines += ['wazecopy %d %d' % (target, donor), 'waze %d %s' % (target, g.waze[donor]['flags']),
                  'wazename %d %s [AEW]' % (target, name)]
        pk.render(os.path.join(a.out, 'preview_%d.png' % target), fk11, src, rig11)
        report.append(dict(montage=montage, name=name, id=target, donor=donor, donor_name=g.waze[donor]['name'],
                           frames=len(fk11[0]), notes=notes))
        print('%s -> %d (donor %d %s)' % (name, target, donor, g.waze[donor]['name']))
        for nt in notes:
            print('    ' + nt)
    open(os.path.join(a.out, 'pack.txt'), 'w', newline='\n').write('\n'.join(lines) + '\n')
    open(os.path.join(a.out, 'manifest.txt'), 'w', newline='\n').write(
        'type=moves\nid=%s\nname=AEW Fight Forever moves\nauthor=aewff_port\nversion=1.0\n' % os.path.basename(a.out))
    json.dump(report, open(os.path.join(a.out, 'report.json'), 'w'), indent=1)


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    {'donors': donors, 'port': port}[sys.argv[1]](sys.argv[2:])
