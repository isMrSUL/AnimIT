"""2K26 world joints -> SvR 2011 YMKs poses (21 channels + root), per actor.

Method (the inverse of MAKE MODDING FREE\\Animation editor\\testing\\tools\\retarget_svr2007.py, which took
the same 21 Yuke's bones into 2K26): each 2011 bone's world rotation = the 2K26 joint's rotation from its
rest pose, applied to the 2011 rest bone (limbs first turned so their rest directions agree). Both rigs
face the same way at rest (+X left, -Y up, -Z forward). Each channel is then solved parent first with
ANIMRE's 2011 FK (checked against the game's bone matrices: local = R_rest(quarter-turn snapped) *
Euler_zyx(key)), keeping only the axes the channel stores (knees and toes 1, neck/clavicles/wrists 2, the
forearm's bend + twist).
"""
import math
import os
import sys
from pathlib import Path

import numpy as np

ANIMRE = str(Path(__file__).resolve().parents[1] / 'animre')
sys.path[:0] = [ANIMRE]
from src.motion import pose_fk, BONES, TYPES_M1, axes_of, ANG  # noqa: E402
import viewer  # noqa: E402

# the SvR 2011 game folder (its pac\ch skeletons); the worker sets it to the game chosen in AnimIT
GAME = os.environ.get('SVR2011_GAME', '')

# 2011 channel -> 2K26 joint (retarget_svr2007.MAP)
# koshi sits ~20 cm above the pelvis, as J_Spine2 (J_Spine1 is 6 cm up): hips->chest error 11 -> 6 degrees
MAP = {'root': 'J_Hips', 'koshi': 'J_Spine2', 'mune': 'J_Chest', 'kubi': 'J_Neck', 'atama': 'J_Head',
       'l_sakotsu': 'J_Clavicle_L', 'l_ninoude': 'J_Shoulder_L', 'l_kote': 'J_Elbow_L', 'l_te': 'J_Wrist_L',
       'r_sakotsu': 'J_Clavicle_R', 'r_ninoude': 'J_Shoulder_R', 'r_kote': 'J_Elbow_R', 'r_te': 'J_Wrist_R',
       'l_momo': 'J_Leg_L', 'l_sune': 'J_Knee_L', 'l_ashi': 'J_Foot_L', 'l_tsumasaki': 'J_Toe_L',
       'r_momo': 'J_Leg_R', 'r_sune': 'J_Knee_R', 'r_ashi': 'J_Foot_R', 'r_tsumasaki': 'J_Toe_R'}
# limbs: (2011 child, 2K26 child) whose rest directions are lined up
ALIGN = {'l_ninoude': ('l_kote', 'J_Elbow_L'), 'l_kote': ('l_te', 'J_Wrist_L'),
         'r_ninoude': ('r_kote', 'J_Elbow_R'), 'r_kote': ('r_te', 'J_Wrist_R'),
         'l_momo': ('l_sune', 'J_Knee_L'), 'l_sune': ('l_ashi', 'J_Foot_L'), 'l_ashi': ('l_tsumasaki', 'J_Toe_L'),
         'r_momo': ('r_sune', 'J_Knee_R'), 'r_sune': ('r_ashi', 'J_Foot_R'), 'r_ashi': ('r_tsumasaki', 'J_Toe_R')}
BODY26 = ['J_Hips', 'J_Spine1', 'J_Spine2', 'J_Chest', 'J_Neck', 'J_Head', 'J_Shoulder_L', 'J_Elbow_L', 'J_Wrist_L',
          'J_Shoulder_R', 'J_Elbow_R', 'J_Wrist_R', 'J_Leg_L', 'J_Knee_L', 'J_Foot_L', 'J_Toe_L',
          'J_Leg_R', 'J_Knee_R', 'J_Foot_R', 'J_Toe_R']


def unit(v):
    return v / np.linalg.norm(v)


def from_to(u, v):
    """smallest rotation matrix taking unit u onto unit v"""
    c = float(np.dot(u, v))
    x = np.cross(u, v)
    s = np.linalg.norm(x)
    if s < 1e-9:
        return np.eye(3)
    k = x / s
    K = np.array([[0, -k[2], k[1]], [k[2], 0, -k[0]], [-k[1], k[0], 0]])
    return np.eye(3) + s * K + (1 - c) * K @ K


def euler_zyx(a):
    x, y, z = a
    cx, sx, cy, sy, cz, sz = math.cos(x), math.sin(x), math.cos(y), math.sin(y), math.cos(z), math.sin(z)
    return (np.array([[cz, -sz, 0], [sz, cz, 0], [0, 0, 1]]) @ np.array([[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]])
            @ np.array([[1, 0, 0], [0, cx, -sx], [0, sx, cx]]))


def solve_axes(K, allowed):
    """angles (x, y, z) on the allowed axes with Euler_zyx(angles) closest to K"""
    a = [math.atan2(K[2, 1], K[2, 2]), math.asin(max(-1.0, min(1.0, -K[2, 0]))), math.atan2(K[1, 0], K[0, 0])]
    if len(allowed) == 3:
        return a
    a = [v if i in allowed else 0.0 for i, v in enumerate(a)]
    for _ in range(8):
        for i in allowed:   # trace(E^T K) is A cos t + B sin t + C in one angle
            f = []
            for t in (0.0, math.pi / 2, math.pi):
                a[i] = t
                f.append(float(np.trace(euler_zyx(a).T @ K)))
            A, C = (f[0] - f[2]) / 2, (f[0] + f[2]) / 2
            a[i] = math.atan2(f[1] - C, A)
    return a


class Rig11:
    def __init__(self, ch='ch251.pac'):
        self.sk = viewer.model_from(Path(GAME) / 'pac' / 'ch' / ch)
        self.idx = self.sk.name_index()
        self.node = {b: self.idx[b] for b in BONES}
        self.parent = {b: self.sk.bones[self.idx[b]].parent for b in BONES}
        q = math.pi / 2
        self.snapped = {b: euler_zyx([math.floor(v / q + 0.5) * q for v in self.sk.bones[self.idx[b]].euler[:3]])
                        for b in BONES}
        rest = self.fk((0.0, 0.0, 0.0), {})
        self.rest_pos = {b: rest[self.idx[b]][0] for b in BONES}
        self.rest_rot = {b: rest[self.idx[b]][1] for b in BONES}

    def fk(self, root, rotations):
        js = pose_fk(self.sk, {'root': tuple(root), 'rotations': rotations})
        return [(np.array(j.position), np.array(j.rotation)) for j in js]


def allowed_axes(slot):
    return [i for i, a in enumerate('xyz') if a in axes_of(TYPES_M1[slot])]


def misfit(E, K):
    """0 when equal; 2 - 2 cos(angle between them)"""
    return 3.0 - float(np.trace(E.T @ K))


def solve_near(K, allowed, prev):
    """solve_axes, also started from last frame's angles; the closer fit wins, ties go to last frame's"""
    best = solve_axes(K, allowed)
    if prev is None or len(allowed) == 3:
        return best
    a = list(prev)
    for _ in range(8):
        for i in allowed:
            f = []
            for t in (0.0, math.pi / 2, math.pi):
                a[i] = t
                f.append(float(np.trace(euler_zyx(a).T @ K)))
            A, C = (f[0] - f[2]) / 2, (f[0] + f[2]) / 2
            a[i] = math.atan2(f[1] - C, A)
    return a if misfit(euler_zyx(a), K) <= misfit(euler_zyx(best), K) + 1e-6 else best


FOREARM = {7: 8, 11: 12}     # kote slot -> te slot
TWIST_STEADY = 0.3           # weight keeping the forearm twist near last frame's (stops flips)


def retarget_frame(rig11, rig26, w26, align, root11, prev=None):
    """one frame: 2K26 world joints -> ({slot: (x, y, z) radians}, 2011 world joints). prev: last frame's
    angles (continuity). 2011's hand roll lives in the forearm twist (kote x -> kote_x / kote_x2) and the
    wrist (te) bends on y/z only, while 2K26 keeps part of the roll in the wrist: the forearm twist is
    chosen so the wrist can match the 2K26 hand (fitting the wrist alone flipped it 90-120 degrees)."""
    target = {}
    for b, j in MAP.items():
        delta = w26[j][1] @ rig26.rest_world[j][1].T
        target[b] = delta @ align.get(b, np.eye(3)) @ rig11.rest_rot[b]
    rot = {}
    world = rig11.fk(root11, rot)
    for slot, b in enumerate(BONES):
        if slot in FOREARM.values() and slot in rot:
            continue
        p = rig11.parent[b]
        pr = world[p][1] if p >= 0 else np.eye(3)
        K = rig11.snapped[b].T @ pr.T @ target[b]
        rot[slot] = tuple(solve_near(K, allowed_axes(slot), prev.get(slot) if prev else None))
        if slot in FOREARM:
            te = FOREARM[slot]
            tb = BONES[te]
            bend = rot[slot][1]
            rot[slot] = (0.0, bend, 0.0)
            w0 = rig11.fk(root11, rot)[rig11.node[b]][1]          # forearm without twist
            px = prev[slot][0] if prev else None

            def cost(x):
                P = w0 @ euler_zyx((x, 0.0, 0.0))
                Kt = rig11.snapped[tb].T @ P.T @ target[tb]
                a = solve_near(Kt, allowed_axes(te), prev.get(te) if prev else None)
                c = misfit(euler_zyx(a), Kt)
                if px is not None:
                    c += TWIST_STEADY * (1 - math.cos(x - px))
                return c, a
            grid = [math.radians(d) for d in range(-180, 180, 5)]
            x = min(grid, key=lambda v: cost(v)[0])
            for step in (2.0, 0.5, 0.1):
                x = min((x + math.radians(step * k) for k in range(-3, 4)), key=lambda v: cost(v)[0])
            rot[slot] = (x, bend, 0.0)
            rot[te] = tuple(cost(x)[1])
        world = rig11.fk(root11, rot)
    return rot, world


def make_align(rig11, rig26):
    out = {}
    for b, (c11, c26) in ALIGN.items():
        j = MAP[b]
        d11 = unit(rig11.rest_pos[c11] - rig11.rest_pos[b])
        d26 = unit(rig26.rest_world[c26][0] - rig26.rest_world[j][0])
        out[b] = from_to(d11, d26)
    return out
