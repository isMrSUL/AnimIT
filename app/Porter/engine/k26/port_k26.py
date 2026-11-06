"""Port a WWE 2K26 move into SvR 2011 as a move pack (offline; the game is not run).

    python -I port_k26.py "<2K26 id>:<variant>:<2011 donor>:<name>" ... [--out DIR]

The two 2K26 clips (<id>a<variant> attacker, <id>b<variant> receiver) are retargeted onto 2011's 21
channels (retarget.py), placed in 2011's move space and timed at 30 fps; the donor (a 2011 move of the
same kind) gives the events, move records and banks, as in the HCTP porter. A move that ends lower
than it starts (RKO 3 lands 1.5 m down: done off the apron in 2K26) is brought back to the ring floor.
Writes pack.txt, motions/, report.json and preview.png.
"""
import argparse
import json
import math
import os
import struct
import sys

import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
HCTP = os.path.join(os.path.dirname(HERE), 'hctp')
sys.path[:0] = [HERE, HCTP, os.path.join(HCTP, 'vendor')]
import k26  # noqa: E402
import frames11  # noqa: E402
import retarget as rt  # noqa: E402
import convert  # noqa: E402
import game11  # noqa: E402
import ymks  # noqa: E402

BODY_LOW = ['J_Toe_L', 'J_Toe_R', 'J_Foot_L', 'J_Foot_R', 'J_Knee_L', 'J_Knee_R', 'J_Hips', 'J_Chest', 'J_Head',
            'J_Wrist_L', 'J_Wrist_R', 'J_Elbow_L', 'J_Elbow_R', 'J_Shoulder_L', 'J_Shoulder_R']
FEET11 = ['l_ashi', 'r_ashi', 'l_tsumasaki', 'r_tsumasaki']
RR_BANK = 'MOT/RR03'
# how 2011 draws the victim's track, by the move's start class (event 92 at frame 0), measured on the game's
# bone matrices: (K, D) = victim origin K units ahead of the attacker's, facing turned D (frames11.py)
# (12 and 19 from the user's own match, 2026-10-16: Brain Chop 2 / Armbar 2, Boom Drop / Appealing Elbow Drop /
# Ballin' Elbow; the ported RKO 3, F-5 3 and The Stomp 1 measured exactly as written)
VICTIM_FRAME = {0: (15.0, math.pi), 1: (10.0, 0.0), 2: (10.0, math.pi), 10: (15.0, 0.0), 11: (15.0, math.pi),
                12: (15.0, math.pi), 19: (17.0, math.pi), 5: (0.0, 0.0),   # 5: a dive's impact (Diving Headbutt 5)
                150: (0.0, math.pi), 160: (10.0, math.pi / 2), 161: (10.0, -math.pi / 2)}
FLOOR_DOWN = 0.25     # move-space y: 2K26 toe height (0.36 units) down to stock's resting toes (~0.1)
SINK_OK = 0.25        # stock moves dip joints below y 0 too (median track 0.04, 90th pct 0.9)


def pelvis(w):
    return (w['J_Leg_L'][0] + w['J_Leg_R'][0]) / 2


def floor_correction(A, B):
    """per-frame lift (cm) bringing a move that ends lower than it starts back to the start floor"""
    final = float(np.median([A[f]['J_Root'][0][1] for f in range(max(0, len(A) - 30), len(A))]))
    if final < 20:            # same floor at the end
        return [0.0] * len(A)
    lows = [max(max(w[j][0][1] for j in BODY_LOW) for w in (A[f], B[f])) for f in range(len(A))]
    contact = -min(-lows[f] for f in range(5))      # lowest point standing, frames 0-4 (y down)
    out, run = [], 0.0
    for low in lows:
        run = min(final, max(run, low - contact))
        out.append(run)
    return out


def landmark_victim_down(heights, frac=0.15):
    """first frame the victim's hips are within frac of their total fall from standing"""
    h0, hmin = heights[0], min(heights)
    for f, h in enumerate(heights):
        if h - hmin <= frac * (h0 - hmin):
            return f
    return len(heights) - 1


def warp(frame, src_mark, dst_mark, src_n, dst_n):
    if frame <= src_mark:
        return round(frame * dst_mark / max(1, src_mark))
    return round(dst_mark + (frame - src_mark) * (dst_n - 1 - dst_mark) / max(1, src_n - 1 - src_mark))


def donor_heights(track):
    """the root height (model units, up) on every keyed frame of a 2011 track -> per frame"""
    keys = []
    for f, rec in ymks.parse_stream(track['data']).records():
        pose = ymks.split_record(rec)[1]
        if pose:
            for s in ymks.decode_body(pose)['subs']:
                if 'root' in s:
                    keys.append((f, -s['root'][1] * 0.01))
                    break
    n = track['frames']
    out = []
    for f in range(n):
        prev = max((k for k in keys if k[0] <= f), default=keys[0])
        nxt = min((k for k in keys if k[0] >= f), default=keys[-1])
        out.append(prev[1] if nxt[0] == prev[0] else prev[1] + (nxt[1] - prev[1]) * (f - prev[0]) / (nxt[0] - prev[0]))
    return out


def donor_contact(track, moved=1.0):
    """first keyed frame a 2011 victim's root has moved `moved` units from its start: the hit"""
    keys = []
    for f, rec in ymks.parse_stream(track['data']).records():
        pose = ymks.split_record(rec)[1]
        if pose:
            for s in ymks.decode_body(pose)['subs']:
                if 'root' in s:
                    keys.append((f, np.array(s['root']) * 0.01))
                    break
    return next((f for f, r in keys if np.linalg.norm(r - keys[0][1]) > moved), keys[-1][0])


def donor_events(track):
    out = {}
    for f, rec in ymks.parse_stream(track['data']).records():
        evs, ok = ymks.parse_events(ymks.split_record(rec)[0], track['T'])
        if evs:
            out[f] = evs
    return out


def build_track(poses, events, first_gap=4):
    """full 0x7d pose a frame (frame 0, then from first_gap on, as 2011's own tracks) + events"""
    n = len(poses)
    frames = [0] + list(range(first_gap, n - 1))
    recs = {}
    for i, f in enumerate(frames):
        count = (frames[i + 1] - f - 1) if i + 1 < len(frames) else max(0, n - 2 - f)
        root, rot = poses[f]
        bones = {}
        for slot in range(21):
            ax = rt.allowed_axes(slot)
            bones[slot] = {'xyz'[i]: ymks.s16(int(round(rot[slot][i] / rt.ANG)) & 0xffff) for i in ax}
        recs[f] = ymks.full_pose(count, tuple(v / 0.01 for v in root), bones)
    out = []
    for f in sorted(set(recs) | set(events)):
        out.append((f, ymks.join_record(ymks.build_events(events.get(f, [])), recs.get(f, b''))))
    return ymks.build_stream(ymks.make_stream(out, n))


def render(path, frames11, src, rig11, step=6):
    """side view (z forward -> x on screen, height up) of both 2011 skeletons (blue attacker, red
    receiver) over the scaled 2K26 joints (grey dots)"""
    chains = [['root', 'koshi', 'mune', 'kubi', 'atama'], ['mune', 'l_sakotsu', 'l_ninoude', 'l_kote', 'l_te'],
              ['mune', 'r_sakotsu', 'r_ninoude', 'r_kote', 'r_te'], ['root', 'l_momo', 'l_sune', 'l_ashi', 'l_tsumasaki'],
              ['root', 'r_momo', 'r_sune', 'r_ashi', 'r_tsumasaki']]
    sel = list(range(0, len(frames11[0]), step))
    W, H, cols = 300, 220, 6
    img = Image.new('RGB', (W * cols, H * ((len(sel) + cols - 1) // cols)), 'white')
    d = ImageDraw.Draw(img)
    S = 6.5
    cz = np.mean([fr[rig11.idx['root']][0][2] for a in frames11 for fr in a])
    for k, f in enumerate(sel):
        ox, oy = (k % cols) * W, (k // cols) * H
        d.rectangle([ox, oy, ox + W - 1, oy + H - 1], outline=(200, 200, 200))
        d.text((ox + 4, oy + 4), 'frame %d' % f, fill='black')
        gy = oy + H - 20
        d.line([ox, gy, ox + W, gy], fill=(160, 120, 60))
        P = lambda p: (ox + W / 2 + (p[2] - cz) * S, gy + p[1] * S)
        for actor in range(len(frames11)):
            for p in src[actor][f]:
                x, y = P(p)
                d.ellipse([x - 1.5, y - 1.5, x + 1.5, y + 1.5], fill=(150, 150, 150))
            js = frames11[actor][f]
            col = (30, 60, 220) if actor == 0 else (220, 40, 40)
            for ch in chains:
                pts = [P(js[rig11.idx[b]][0]) for b in ch]
                d.line(pts, fill=col, width=2)
    img.save(path)


REACH_FROM = ('J_Wrist_L', 'J_Wrist_R', 'J_Toe_L', 'J_Toe_R', 'J_Foot_L', 'J_Foot_R')
REACH_TO = ('J_Head', 'J_Neck', 'J_Chest', 'J_Hips')


def lowest(w):
    return max(w[j][0][1] for j in BODY_LOW)        # y down: the largest y is the lowest point


def smoothstep(t):
    t = min(1.0, max(0.0, t))
    return t * t * (3 - 2 * t)


EVENT_BYTES = 32     # a packed (MOTP) character event block holds at most 32 bytes a frame (convert.packed_stream)
MIN_REACH = 16       # 60 fps frames: the shortest blend from the donor's start spot to the source spacing


def spread_events(ev, nf):
    """retimed donor events can pile onto one frame past the packed limit: overflow moves on to the next
    frames in order (the last frame's overflow back onto the frames before it)"""
    out, carry = {}, []
    for f in range(nf):
        evs = carry + ev.get(f, [])
        carry = []
        if f < nf - 1:
            while len(evs) > 1 and len(ymks.build_events(evs)) > EVENT_BYTES:
                carry.insert(0, evs.pop())
        if evs:
            out[f] = evs
    f = nf - 1
    while f > 0 and f in out and len(out[f]) > 1 and len(ymks.build_events(out[f])) > EVENT_BYTES:
        out.setdefault(f - 1, []).append(out[f].pop(0))
        if len(ymks.build_events(out[f - 1])) > EVENT_BYTES:
            f -= 1
    return out


def port_one(g, rig26, rig11, mid, name, donor, var, phase=0, frames=None, solo=None, mirrored=False, face='pair',
             start=None, anchor_mode='attacker', victim_lead_in=0, landmark='down', part_events=None, end_blend=0,
             fall=None):
    """-> (tracks {(x, y): bytes}, notes, preview data). Placement by kind of move:
    standing - the attacker starts at the donor's spot and reaches the 2K26 spacing by first contact;
    running - 2011 runs the approach itself, so the 2K26 clip starts where it is as close as the donor's
      start (the run-up before is cut);
    raised start (top rope) with an attacker-only donor (a 2011 dive's flight, phase 0) - the donor's
      turnbuckle top is its floor (y 0): start there and keep the 2K26 drop below it (2011's own flight
      falls on past y 0 until the game cuts to the impact phase).
    phase / frames: the donor phase this makes and the 2K26 frame range (60 fps) it uses."""
    notes = []
    nA, A = k26.clip_world(rig26, k26.find_clip('%da%s' % (mid, var)))
    dt = g.ymks(donor)
    da, dv = dt[(phase, 0)], dt.get((phase, 1))
    if solo:
        dv = None
    solo = dv is None
    if not solo:
        nB, B0 = k26.clip_world(rig26, k26.find_clip('%db%s' % (mid, var)))
        # both clips name one shared point (J_anim_origin) in their own coordinates
        shift = A[0]['J_anim_origin'][0] - B0[0]['J_anim_origin'][0]
        B = [{j: (p + shift, r) for j, (p, r) in w.items()} for w in B0]
        n = min(nA, nB)
    else:
        B, n, shift = None, nA, np.zeros(3)
    if mirrored:
        A = k26.mirror(A, rig26)
        B = k26.mirror(B, rig26) if B else None
    if frames:
        a0, a1 = frames
        A = A[a0:min(a1, n)]
        B = B[a0:min(a1, n)] if B else None
        n = len(A)
    s = (np.linalg.norm(rig11.rest_pos['l_ashi'] - rig11.rest_pos['l_momo'])
         / np.linalg.norm(rig26.rest_world['J_Foot_L'][0] - rig26.rest_world['J_Leg_L'][0]))
    qa = np.array(convert.first_root(da['data'], da['T'])) * 0.01
    qv = np.array(convert.first_root(dv['data'], dv['T'])) * 0.01 if not solo else None
    K = D = 0.0
    MD = np.eye(3)
    if not solo:
        sc = convert.start_class(da['data'], da['T'])
        if start is not None:
            notes.append("start class %s instead of the donor's %s (where the attacker stands)" % (start, sc))
            sc = start
        if sc in VICTIM_FRAME:
            K, D = VICTIM_FRAME[sc]
            notes.append('victim frame of start class %s: origin %d units ahead, turned %.0f deg (measured)' % (sc, K, math.degrees(D)))
        else:
            K, D = VICTIM_FRAME[0]
            notes.append('victim frame of start class %s NOT MEASURED: front-grapple values used (%d, %.0f deg)'
                         % (sc, K, math.degrees(D)))
        MD = frames11.Ry(D)
        qv_sh = np.array([0.0, 0.0, -K]) + MD @ qv      # the donor's victim in the attacker's space
    notes.append('phase %d: 2K26 clips %da%s%s%s, %d frames at 60 fps; donor %d %s%s'
                 % (phase, mid, var, '' if solo else ' / %db%s (receiver at %s cm)' % (mid, var, np.round(shift, 0).tolist()),
                    ' frames %d-%d' % frames if frames else '', n, donor, g.waze[donor]['name'],
                    ' (attacker only)' if solo else ''))
    # running: start where the 2K26 pair is as close as the donor's pair
    f0 = 0
    if not solo:
        d_don = float(np.linalg.norm((qa - qv_sh)[[0, 2]]))
        dist = [float(np.linalg.norm((pelvis(A[f]) - pelvis(B[f]))[[0, 2]])) * s for f in range(n)]
        k = min(10, n - 1)
        speed = float(np.linalg.norm((pelvis(A[k]) - pelvis(A[0]))[[0, 2]])) / k * 2 * s   # units a 30 fps frame
        if dist[0] > d_don + 4 and speed > 0.6:
            f0 = next((f for f in range(n) if dist[f] <= d_don + 0.5), 0)
            notes.append('run-up cut: starts at 2K26 frame %d (%.1f units apart, as the donor; was %.1f)'
                         % (f0, dist[f0], dist[0]))
    A = A[f0:n]
    B = B[f0:n] if B else None
    n = len(A)
    # facing: 2011 runs some moves from the other side (attacker at +z) - turn the 2K26 scene so the
    # attacker comes from the donor's side
    ang = lambda v: math.atan2(v[0], v[2])
    if face == 'pair' and not solo and -qv[1] < 3.5:
        face = 'victim'       # a ground move: the victim already lies as the game laid him - match his axis
    if face == 'victim':      # a lying victim's body axis (pelvis -> head) as the donor's
        d_o = B[0]['J_Head'][0] - pelvis(B[0])
        js = rig11.fk(qv, rt_first_rotations(dv))
        d_d = MD @ (js[rig11.idx['atama']][0] - js[rig11.idx['root']][0])
    elif not solo:
        d_o, d_d = pelvis(B[0]) - pelvis(A[0]), qv_sh - qa
    else:
        d_o = pelvis(A[-1]) - pelvis(A[0])
        d_d = np.array(donor_root_at(da, da['frames'] - 1)) * 0.01 - qa
    if np.linalg.norm(d_o[[0, 2]]) * s > 0.5 and np.linalg.norm(d_d[[0, 2]]) > 0.5:
        th = ang(d_d) - ang(d_o)
        th = (th + math.pi) % (2 * math.pi) - math.pi
        if abs(th) > math.radians(5):
            c_, s_ = math.cos(th), math.sin(th)
            Ry = np.array([[c_, 0, s_], [0, 1, 0], [-s_, 0, c_]])
            piv = pelvis(B[0]) if B else pelvis(A[0])
            piv = np.array([piv[0], 0.0, piv[2]])
            turn = lambda seq: [{j: (Ry @ (p - piv) + piv, Ry @ r) for j, (p, r) in w.items()} for w in seq]
            A = turn(A)
            B = turn(B) if B else None
            notes.append("turned %.0f degrees to %s" % (math.degrees(th), "lie as the donor's victim" if face == 'victim'
                                                         else "come from the donor's side"))
    actors = [A] if solo else [A, B]
    lift = floor_correction(A, B if B else A)
    if max(lift):
        notes.append('ends %.0f cm below its start (done off the apron in 2K26): kept on the ring floor' % max(lift))
    up = np.array([0.0, 1.0, 0.0])
    ref = pelvis(A[0])
    base = np.array([ref[0], 0.0, ref[2]])
    anchor = np.array([qa[0], 0.0, qa[2]])
    raised = -pelvis(A[0])[1] * s > 12
    pa0 = (pelvis(A[0]) - base) * s + anchor
    off = [np.zeros(3)] * n
    off_a = [np.zeros(3)] * n
    take = land = 0
    if solo and raised:
        o_start = np.array([qa[0] - pa0[0], 0.0, qa[2] - pa0[2]])
        off = [o_start] * n
        notes.append("dive flight: starts on the donor's turnbuckle spot, 2K26's drop kept below it")
    elif not solo:
        reach = next((f for f in range(n) if min(np.linalg.norm(A[f][h][0] - B[f][t][0])
                                                 for h in REACH_FROM for t in REACH_TO) < 30), n // 3)
        pv0 = (pelvis(B[0]) - base) * s + anchor
        o0 = np.array([qv_sh[0] - pv0[0], 0.0, qv_sh[2] - pv0[2]])
        if anchor_mode in ('run', 'impact'):
            # the victim stays where the game left him (the donor's spot) and the attacker closes to the
            # source spacing by first contact: run - while running in; impact (a dive landing) - in the air
            # (a fixed run-in ramp outlasted AEW's knee strikes, which land at frame ~20: the knee missed)
            ramp = max(reach, MIN_REACH)
            off = [o0] * n
            off_a = [o0 * smoothstep(f / ramp) for f in range(n)]
            notes.append("%s: victim on the donor's spot, attacker closes %.1f units to the 2K26 spacing by frame %d"
                         % (anchor_mode, float(np.linalg.norm(o0)), ramp // 2))
        else:
            # (a pair already in contact at frame 0 still blends over MIN_REACH: no jump on the first frame)
            reach = max(reach, MIN_REACH)
            off = [o0 * smoothstep(1 - f / reach) for f in range(n)]
            notes.append("victim starts on the donor's spot (%.1f units off), 2K26 spacing by frame %d (first contact)"
                         % (float(np.linalg.norm(o0)), reach // 2))
    align = rt.make_align(rig11, rig26)
    frames60 = list(range(0, n, 2))
    out_poses, fk11, src = [], [], []
    for ai, act in enumerate(actors):
        prev = None
        P, F, S_ = [], [], []
        for f in frames60:
            w = act[f]
            if solo:
                o = off[f] - up * lift[f] * s                     # (a dive's flight: the attacker)
            else:
                o = (off[f] if ai == 1 else off_a[f]) - up * lift[f] * s
            p = (pelvis(w) - base) * s + anchor + o
            if ai == 1:
                # the victim's track is in his own frame: origin K ahead, turned D (2011 draws it so)
                p = MD.T @ (p - np.array([0.0, 0.0, -K]))
                w = {j: (pp, MD.T @ rr) for j, (pp, rr) in w.items()}
            rot, world = rt.retarget_frame(rig11, rig26, w, align, p, prev)
            prev = rot
            P.append([p, rot])
            F.append(world)
            S_.append([(act[f][j][0] - base) * s + anchor + o for j in rt.BODY26])
        # height: the 2K26 floor is 2011's (y 0); toes as stock's (2K26's rest 0.36 units up, stock ~0.1)
        dy = FLOOR_DOWN
        if ai == 0 and solo and raised:
            # a dive starts on the donor's turnbuckle (the games' heights differ)
            droot = np.array(convert.first_root(da['data'], da['T'])) * 0.01
            dfk = rig11.fk(droot, rt_first_rotations(da))
            dy = max(pp[1] for pp, rr in dfk) - max(pp[1] for pp, rr in F[0])
            dys = [dy] * len(P)
        else:
            dys = [dy] * len(P)
        for i in range(len(P)):
            v = np.array([0, dys[i], 0])
            P[i][0] = P[i][0] + v
            F[i] = [(pp + v, rr) for pp, rr in F[i]]
            S_[i] = [pp + v for pp in S_[i]]
        if ai == 0 and solo and raised:
            notes.append("attacker: turnbuckle top set as the donor's (%+.2f units)" % dy)
        out_poses.append(P)
        fk11.append(F)
        src.append(S_)
    nf = len(frames60)
    for ai in range(len(actors)):
        if ai == 0 and solo and raised:
            continue          # a dive's flight falls below its turnbuckle floor on purpose
        sink = [max(0.0, max(p[1] for p, r in fr) - SINK_OK) for fr in fk11[ai]]
        sm = [max(sink[max(0, i - 2):i + 3]) for i in range(nf)]
        sm = [sum(sm[max(0, i - 2):i + 3]) / len(sm[max(0, i - 2):i + 3]) for i in range(nf)]
        sm = [max(a, b) for a, b in zip(sm, sink)]
        for i in range(nf):
            v = np.array([0, sm[i], 0])
            out_poses[ai][i][0] = out_poses[ai][i][0] - v
            fk11[ai][i] = [(pp - v, rr) for pp, rr in fk11[ai][i]]
            src[ai][i] = [pp - v for pp in src[ai][i]]
        if max(sink):
            notes.append('%s: lifted off the mat on %d frames (at most %.2f units)'
                         % ('attacker' if ai == 0 else 'victim', sum(1 for v in sink if v > 0), max(sink)))
    # victim lead-in: the source victim starts in a pose 2011 cannot hand over (e.g. sitting up, where 2011's
    # victim lies) - blend from the donor victim's own first pose over the first frames instead of snapping
    if victim_lead_in and not solo:
        r0, root0 = rt_first_rotations(dv), np.array(qv)
        k = min(victim_lead_in, nf)
        for i in range(k):
            t = smoothstep((i + 1) / (k + 1))
            p, rot = out_poses[1][i]
            nr = {}
            for slot, a1 in rot.items():
                a0 = r0.get(slot, (0.0, 0.0, 0.0))
                nr[slot] = tuple(a0[c] + ((a1[c] - a0[c] + math.pi) % (2 * math.pi) - math.pi) * t for c in range(3))
            pp = root0 + (np.array(p) - root0) * t
            out_poses[1][i] = [pp, nr]
            fk11[1][i] = rig11.fk(pp, nr)
        notes.append("victim: blended from the donor's first pose over the first %d frames" % k)
    # end blend (2026-10-30, the user: ported moves did not hand back smoothly to the game's own animation): over the
    # last end_blend frames each actor eases into the donor's own last pose and place - what the game takes over from
    # (the attacker standing, the victim lying as 2011 expects) - so its next animation starts without a snap
    if end_blend:
        for ai, dtr in ((0, da), (1, None if solo else dv)):
            if dtr is None or ai >= len(out_poses):
                continue
            r_end = rt_last_rotations(dtr)
            if not r_end:
                continue
            root_end = np.array(donor_root_at(dtr, dtr['frames'] - 1)) * 0.01
            if ai == 1:   # (lying face up / face down: the donor's decides the game's next animation - report a mismatch)
                ci = rig11.idx['mune']
                up_src = chest_up(fk11[1][nf - 1][ci][1], rig11.rest_rot['mune'])
                up_don = chest_up(rig11.fk(root_end, r_end)[ci][1], rig11.rest_rot['mune'])
                notes.append('victim ends %s here, %s in the donor%s' % ('FACE UP' if up_src else 'FACE DOWN',
                             'FACE UP' if up_don else 'FACE DOWN', '' if up_src == up_don else ' - MISMATCH: pick a donor that ends so'))
            k = min(end_blend, nf // 3)
            for j in range(k):
                i = nf - k + j
                t = smoothstep((j + 1) / k)
                p, rot = out_poses[ai][i]
                nr = {}
                for slot, a1 in rot.items():
                    # (not the body's own turn: the game takes his facing from this last frame - turning him to the
                    # donor's flipped a face-down victim face up)
                    a0 = a1 if slot == 'root' else r_end.get(slot, a1)
                    nr[slot] = tuple(a1[c] + ((a0[c] - a1[c] + math.pi) % (2 * math.pi) - math.pi) * t for c in range(3))
                # (the pose and the height only: the game takes his place from the move's own last frame - the end
                # adjust events - so the AEW spot stays; easing the place too slid a victim 47 units in half a second)
                pp = np.array(p, float).copy()
                pp[1] += (root_end[1] - pp[1]) * t
                out_poses[ai][i] = [pp, nr]
                fk11[ai][i] = rig11.fk(pp, nr)
            notes.append("%s: limbs eased into the donor's last pose (body turn kept) over the last %d frames" % ('attacker' if ai == 0 else 'victim', k))
    # a dive's own jump (fall = (launch frame here, the donor's launch frame)): from the launch the height follows the
    # donor's own drop over time - the game flies the wrestler sideways only, the fall is in the jump (2026-11-05:
    # without it an AEW Coffin Drop floated at turnbuckle height); his pose stays AEW's
    if fall and solo:
        al, dl = fall
        dh = donor_heights(da)
        dn = len(dh)
        if 0 <= al < nf and 0 <= dl < dn:
            for i in range(al, nf):
                df = dl + (i - al) * (dn - 1 - dl) / max(1, nf - 1 - al)
                k0 = int(df)
                k1 = min(dn - 1, k0 + 1)
                h = dh[k0] + (dh[k1] - dh[k0]) * (df - k0)
                p, rot = out_poses[0][i]
                pp = np.array(p, float).copy()
                pp[1] -= h - dh[dl]          # (y is down: a lower height is a larger y)
                out_poses[0][i] = [pp, rot]
                fk11[0][i] = rig11.fk(pp, rot)
            notes.append('jump: from the launch (frame %d) the height follows the donor fall (%.1f units down by the end)'
                         % (al, dh[dl] - dh[-1]))
    # events: one timeline for both tracks, retimed around the landmark (victim down; a dive's landing)
    if solo:
        mark = landmark_victim_down([-(fr[rig11.idx['root']][0][1]) for fr in fk11[0]])
        dmark = landmark_victim_down(donor_heights(da))
        notes.append('landing: frame %d here, %d in the donor (events retimed around it)' % (mark, dmark))
    elif -qv[1] < 3.5:
        mark = dmark = 0           # the victim starts down (a dive's impact): events retimed evenly
        notes.append('victim already down: events retimed evenly')
    elif landmark == 'contact':
        # a strike whose victim sells long before falling: the donor's hit events belong at first contact
        mark = min(nf - 2, reach // 2)
        dmark = donor_contact(dv)
        notes.append('first contact: frame %d here, %d in the donor (events retimed around it)' % (mark, dmark))
    else:
        mark = landmark_victim_down([-(fr[rig11.idx['root']][0][1]) for fr in fk11[1]])
        dmark = landmark_victim_down(donor_heights(dv))
        notes.append('victim down: frame %d here, %d in the donor (events retimed around it)' % (mark, dmark))
    tracks = {}
    for y, dtr in ((0, da), (1, dv)):
        if dtr is None:
            continue
        ev = {}
        for f, evs in donor_events(dtr).items():
            nfm = nf - 1 if f == dtr['frames'] - 1 else min(nf - 2, warp(f, dmark, mark, da['frames'], nf))
            if start is not None and y == 0 and f == 0:
                evs = [(op, bytes([92, start & 0xff]) if op == 92 else b) for op, b in evs]
            if part_events is not None:
                evs = [(op, b) for op, b in evs if not (op in (16, 21) and len(b) >= 2 and b[1] in (1, 2))]
            ev.setdefault(nfm, []).extend(evs)
        if part_events is not None:
            for f, evs in part_events(actors[y], frames60).items():
                dst = ev.setdefault(f, [])
                if f == 0 and len(ymks.build_events(dst + evs)) > EVENT_BYTES:
                    # Keep both starting hand shapes on frame 0 when its packed event block fills up.
                    # The start-class event still leads; later donor commands may spill to frame 1.
                    pos = max((i + 1 for i, (op, _) in enumerate(dst) if op == 92), default=0)
                    dst[pos:pos] = evs
                else:
                    dst.extend(evs)
        tracks[(phase, y)] = build_track([(pp, rr) for pp, rr in out_poses[y]], spread_events(ev, nf))
    if not solo:     # the preview shows the victim where the game puts him (attacker's space)
        off = np.array([0.0, 0.0, -K])
        fk11[1] = [[(MD @ pp + off, MD @ rr) for pp, rr in fr] for fr in fk11[1]]
    return tracks, notes, (fk11, src)


def chest_up(w_chest, rest_rot):
    """True when a chest faces up (front = rest -Z turned; y down)"""
    return float((w_chest @ rest_rot.T @ np.array([0, 0, -1.0]))[1]) < 0


def dive_plan(g, rig26, rig11, mid, var, donor):
    """a 2K26 top-rope move -> [(phase, variant, frames, solo, mirrored, face)] for a 2011 dive donor.
    2011 splits a dive: phase 0 the take-off and flight (attacker only; it falls until the game cuts to an
    impact phase), phases 30/40 the impact on a lying victim (30 rolls off to +x, 40 is its mirror), 50 a
    standing victim, 112 a miss. The 2K26 pairs start in the air just before the stomp, as 2011's impact
    phases do: they become phases 30/40 (mirrored where the roll-off goes the other way); 2011's flight
    and the phases 2K26 has no pair for stay the donor's."""
    dt = g.ymks(donor)
    pairs = k26.pair_variants(mid)
    if var not in pairs:
        return None
    order = [var] + [v for v in pairs if v != var]
    plan = []
    for x in sorted({x for x, y in dt if x and x < 100 and (x, 1) in dt}):
        v_tr = dt[(x, 1)]
        root = np.array(convert.first_root(v_tr['data'], v_tr['T'])) * 0.01
        if -root[1] > 3.5:          # a standing victim: 2K26 has no such pair here
            continue
        a_tr = dt[(x, 0)]
        disp_d = (np.array(donor_root_at(a_tr, a_tr['frames'] - 1)) - np.array(convert.first_root(a_tr['data'], a_tr['T']))) * 0.01
        side_d = 1 if disp_d[0] > 0 else -1
        v = order[len(plan) % len(order)]
        n, A = k26.clip_world(rig26, k26.find_clip('%da%s' % (mid, v)))
        n2, B0 = k26.clip_world(rig26, k26.find_clip('%db%s' % (mid, v)))
        # the roll-off side seen from the victim (head direction = forward): cross product sign
        ax = B0[0]['J_Head'][0] - pelvis(B0[0])
        dp = pelvis(A[n - 1]) - pelvis(A[0])
        side_o = 1 if (ax[2] * dp[0] - ax[0] * dp[2]) > 0 else -1
        js = rig11.fk(root, rt_first_rotations(v_tr))
        axd = js[rig11.idx['atama']][0] - js[rig11.idx['root']][0]
        side_d = 1 if (axd[2] * disp_d[0] - axd[0] * disp_d[2]) > 0 else -1
        plan.append((x, v, None, False, side_o != side_d, 'victim'))
    return plan or None

def donor_root_at(track, frame):
    best = None
    for f, rec in ymks.parse_stream(track['data']).records():
        pose = ymks.split_record(rec)[1]
        if pose:
            for sub in ymks.decode_body(pose)['subs']:
                if 'root' in sub:
                    if best is None or abs(f - frame) < abs(best[0] - frame):
                        best = (f, sub['root'])
                    break
    return best[1]


def main(argv):
    """moves: "<2K26 id>:<variant>:<2011 donor>:<name>" ..."""
    ap = argparse.ArgumentParser()
    ap.add_argument('moves', nargs='+')
    ap.add_argument('--out', default=os.path.join(HERE, 'out', 'k26_moves'))
    a = ap.parse_args(argv)
    g = game11.Game11()
    T11 = next(e['T'] for e in g.motions[(718, 0, 0)] if e['kind'] == b'YMKs')
    rig26, rig11 = k26.Rig26(), rt.Rig11()
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
    lines = ['# WWE 2K26 moves for SvR 2011 (k26_porter): 2K26 animations, 2011 donor records']
    report = []
    for spec in a.moves:
        mid, var, donor, name = spec.split(':', 3)
        start = None
        if '@' in donor:              # <donor>@<start class>: the donor's records, another start position
            donor, start = donor.split('@')
            start = int(start)
        mid, donor = int(mid), int(donor)
        plan = None
        if (0, 1) not in g.ymks(donor):
            plan = dive_plan(g, rig26, rig11, mid, var, donor)
        tracks, notes, fk11, src = {}, [], None, None
        for ph, v, fr, so, mi, fc in (plan or [(0, var, None, None, False, 'pair')]):
            t, nt, (f11, sr) = port_one(g, rig26, rig11, mid, name, donor, v, ph, fr, so, mi, fc, start)
            if mi:
                nt.insert(1, 'phase %d: 2K26 %s mirrored (2011 rolls off to the other side here)' % (ph, v))
            tracks.update(t)
            notes += nt
            if fk11 is None:
                fk11, src = f11, sr
        ported = {xx for xx, yy in tracks}     # (before the loop: a kept phase keeps all its tracks)
        for (x, y), e in sorted(g.ymks(donor).items()):
            if (x, y) not in tracks and x not in ported:
                tracks[(x, y)] = e['data']        # the donor's other phases (its EXH records name them)
                notes.append('phase %d track %d: the donor\'s' % (x, y))
        target = min((i for i in g.waze if 2000 <= i < 16384 and i not in used), key=lambda i: (abs(i - donor), i))
        used.add(target)
        paths = g.paths(donor)
        unpacked = [p for p in paths if p.startswith(('MOT/BM', 'MVMT'))] + [RR_BANK]
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
                pk = _own[0] if _own and _kept else convert.packed_stream(data, y, T11)
                open(os.path.join(a.out, 'motions', 'motp_' + fn), 'wb').write(pk)
                for p in packed:
                    lines.append('motion mpsp.pac %s %d %d %d %d motions/motp_%s' % (p, target, x, y, nfr, fn))
        for table, kind in ((g.exh, 'exh'), (g.mbd, 'mbd')):
            for grp, rec in table.get(donor, []):
                r = bytearray(rec)
                struct.pack_into('<H', r, 0, target)
                lines.append('%s %d %s' % (kind, grp, r.hex()))
        for grp, rec, evs in g.evt.get(donor, []):
            r = bytearray(rec)
            struct.pack_into('<H', r, 0, target)
            lines.append('evt %d %s %s' % (grp, r.hex(), evs.hex()))
        lines += ['wazecopy %d %d' % (target, donor), 'waze %d %s' % (target, g.waze[donor]['flags']),
                  'wazename %d %s [2K26]' % (target, name)]
        render(os.path.join(a.out, 'preview_%d.png' % target), fk11, src, rig11)
        report.append(dict(move=mid, variant=var, name=name, id=target, donor=donor, donor_name=g.waze[donor]['name'],
                           frames=len(fk11[0]), notes=notes))
        print('%s -> %d (donor %d %s)' % (name, target, donor, g.waze[donor]['name']))
        for nt in notes:
            print('    ' + nt)
    open(os.path.join(a.out, 'pack.txt'), 'w', newline='\n').write('\n'.join(lines) + '\n')
    open(os.path.join(a.out, 'manifest.txt'), 'w', newline='\n').write(
        'type=moves\nid=%s\nname=WWE 2K26 moves\nauthor=k26_porter\nversion=1.0\n' % os.path.basename(a.out))
    json.dump(report, open(os.path.join(a.out, 'report.json'), 'w'), indent=1)


def rt_last_rotations(track):
    """the 21 channel angles (radians) of a 2011 track's last keyed pose"""
    rot = {}
    for f, rec in ymks.parse_stream(track['data']).records():
        pose = ymks.split_record(rec)[1]
        if pose:
            r = {}
            for s in ymks.decode_body(pose)['subs']:
                for k, vals in s.get('bones', {}).items():
                    r[k] = tuple(vals.get(ax, 0) * rt.ANG for ax in 'xyz')
            if r:
                rot = r
    return rot


def rt_first_rotations(track):
    """the 21 channel angles (radians) of a 2011 track's first pose"""
    for f, rec in ymks.parse_stream(track['data']).records():
        pose = ymks.split_record(rec)[1]
        if pose:
            rot = {}
            for s in ymks.decode_body(pose)['subs']:
                for k, vals in s.get('bones', {}).items():
                    rot[k] = tuple(vals.get(ax, 0) * rt.ANG for ax in 'xyz')
            return rot
    return {}


if __name__ == '__main__':
    main(sys.argv[1:])
