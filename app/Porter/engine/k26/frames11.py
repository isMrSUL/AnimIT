"""How 2011 places the victim's track (measured on the game's own bone matrices, 2026-10-16): each wrestler
is drawn from his own origin and facing; the victim's origin is K units ahead of the attacker's (attacker
frame -z) and his facing turned by D. Victim joint p (victim track space) in the attacker's track space:
    (0, 0, -K) + R_y(D) p
K / D per donor start class (captured: front grapple 15 / pi, whip-running 10 / pi, rear 10 / 0, ground
15 / 0, ...); any donor's pair is inferred by where its attacker's hands and feet meet the victim's body."""
import math, os, sys
import numpy as np
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path[:0] = [HERE, os.path.join(HERE, '..', 'hctp'), os.path.join(HERE, '..', 'hctp', 'vendor')]
import retarget as rt   # noqa (ANIMRE path)
from src.motion import sample_ymks

def Ry(a):
    c, s = math.cos(a), math.sin(a)
    return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]])

EFF = ['l_te', 'r_te', 'l_ashi', 'r_ashi', 'l_kote', 'r_kote', 'l_sune', 'r_sune', 'atama', 'mune', 'root']
BODY = ['atama', 'kubi', 'mune', 'koshi', 'root', 'l_momo', 'r_momo', 'l_ninoude', 'r_ninoude', 'l_sune', 'r_sune']
CANDS = [(k, d) for k in (0, 5, 10, 15, 20, 23) for d in (0.0, math.pi, math.pi / 2, -math.pi / 2)]

TORSO = ['root', 'koshi', 'mune', 'kubi', 'atama']


def infer(rig, att, vic, step=2):
    """-> ((K, D), {cand: score}) for a 2011 pair of tracks: the attacker's hands/feet reach the victim's
    body (contact) while the two torsos never pass through each other (overlap is heavily penalised)"""
    n = min(att['frames'], vic['frames'])
    A, V, AT, VT = [], [], [], []
    for f in range(0, n, step):
        sa, sv = sample_ymks(att['data'], f), sample_ymks(vic['data'], f)
        ja, jv = rig.fk(sa['root'], sa['rotations']), rig.fk(sv['root'], sv['rotations'])
        A.append(np.array([ja[rig.idx[b]][0] for b in EFF]))
        V.append(np.array([jv[rig.idx[b]][0] for b in BODY]))
        AT.append(np.array([ja[rig.idx[b]][0] for b in TORSO]))
        VT.append(np.array([jv[rig.idx[b]][0] for b in TORSO]))
    score = {}
    for K, D in CANDS:
        M = Ry(D)
        off = np.array([0, 0, -K])
        c, o = [], []
        for a, v, at, vt in zip(A, V, AT, VT):
            vv = v @ M.T + off
            vvt = vt @ M.T + off
            c.append(np.linalg.norm(a[:, None, :] - vv[None, :, :], axis=2).min())
            dt = np.linalg.norm(at[:, None, :] - vvt[None, :, :], axis=2).min()
            o.append(max(0.0, 1.6 - dt))                     # torsos closer than ~17 cm: overlap
        c = sorted(c)
        score[(K, D)] = float(np.mean(c[:max(1, len(c) * 2 // 3)])) + 6.0 * float(np.mean(o))
    best = min(score, key=score.get)
    return best, score
