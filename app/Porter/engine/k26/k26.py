"""WWE 2K26 move clips (Visual Concepts .clip) -> world-space joints.

Reads the user's extracted 2K26 Animation folder (read only) and reuses the verified clip decoder from
MAKE MODDING FREE\\Animation editor\\testing\\tools (262/262 clips exact; 392/400 retail clips decode, the
rest are empty). Clip names: <move id>a<nnn> attacker, <move id>b<nnn> receiver; a container
Acts/*/MotionArea_<a>_<b>.clips holds the move ids in [a, b); entries are keyed by FNV-1a 64 of the name.

2K26 conventions (research + measured): +X left, -Y up, -Z forward, centimetres, 60 fps. Rotations are
absolute local quaternions; translations are offsets from the rest local translation; frames whose
translations are masked keep the last ones (the research FK read them as 0: RKO 3's root jumped to 0 at
frame 40).
"""
import glob
import json
import os
import re
import struct
import sys

import numpy as np

ANIM = ''  # selected by the AnimIT worker
REF26 = os.path.join(os.path.dirname(os.path.dirname(__file__)), 'data', 'skeleton_ref_26.json')

FPS = 60.0


def fnv(s):
    h = 0xcbf29ce484222325
    for c in s.encode():
        h = ((h ^ c) * 0x100000001b3) & 0xffffffffffffffff
    return h


def find_clip(name):
    """clip bytes for a clip name such as 15591a000"""
    mid = int(re.match(r'\d+', name).group())
    want = fnv(name)
    for path in glob.glob(os.path.join(ANIM, 'Acts', '*', 'MotionArea_*.clips')):
        a, b = map(int, re.findall(r'_(\d+)', os.path.basename(path))[:2])
        if not a <= mid < b:
            continue
        d = open(path, 'rb').read()
        n = struct.unpack_from('<I', d, 0x10)[0]
        for i in range(n):
            h, size, off = struct.unpack_from('<QII', d, 0x18 + 16 * i)
            if h == want:
                return d[off:off + size]
    raise KeyError('clip %s not found under %s' % (name, ANIM))


def quat_mat(q):
    w, x, y, z = q
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y)],
                     [2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x)],
                     [2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y)]])


def euler_zyx(a):
    x, y, z = a
    cx, sx, cy, sy, cz, sz = np.cos(x), np.sin(x), np.cos(y), np.sin(y), np.cos(z), np.sin(z)
    Rx = np.array([[1, 0, 0], [0, cx, -sx], [0, sx, cx]])
    Ry = np.array([[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]])
    Rz = np.array([[cz, -sz, 0], [sz, cz, 0], [0, 0, 1]])
    return Rz @ Ry @ Rx


class Rig26:
    """the 2K26 model skeleton (166 joints): rest local transforms, rest world"""

    def __init__(self):
        self.sk = json.load(open(REF26))
        self.names = {fnv(n): n for n in self.sk}
        self.rest_t = {n: np.array(b['transform'][:3]) for n, b in self.sk.items()}
        self.rest_r = {n: euler_zyx(b['transform'][3:]) for n, b in self.sk.items()}
        self.rest_world = {}
        for n in self.sk:
            self._rw(n)

    def _rw(self, n):
        if n not in self.rest_world:
            p = self.sk[n]['parent']
            pp, pr = self._rw(p) if p else (np.zeros(3), np.eye(3))
            self.rest_world[n] = (pp + pr @ self.rest_t[n], pr @ self.rest_r[n])
        return self.rest_world[n]


def clip_world(rig, data):
    """-> (frames, [ {joint: (position cm, 3x3 rotation)} per frame ])"""
    import clip_decode  # loaded only for WWE 2K26, never needed by AEW
    clip = clip_decode.Clip(data)
    bones, *_ = clip_decode.decode(clip)
    named = {}
    for i, b in enumerate(bones):
        n = rig.names.get(b['hash'])
        if n:
            t, last = [], None
            for v in b['t']:
                last = v if v is not None else last
                t.append(last)
            first = next((v for v in t if v is not None), [0.0, 0.0, 0.0])
            named[n] = ([np.array(v if v is not None else first) for v in t], [quat_mat(q) for q in b['q']])
    out = []
    for f in range(clip.frames):
        W = {}

        def get(n):
            if n in W:
                return W[n]
            p = rig.sk[n]['parent'] if n in rig.sk else None
            pp, pr = get(p) if p else (np.zeros(3), np.eye(3))
            b = named.get(n)
            lt = rig.rest_t.get(n, np.zeros(3)) + (b[0][f] if b else 0)
            lr = b[1][f] if b else rig.rest_r.get(n, np.eye(3))
            W[n] = (pp + pr @ lt, pr @ lr)
            return W[n]
        for n in set(named) | set(rig.sk):
            if n in rig.sk or n in named:
                get(n)
        out.append(W)
    return clip.frames, out


def clip_names(mid):
    """every <id><a-h><nnn> clip name the move's container holds"""
    out = []
    for path in glob.glob(os.path.join(ANIM, 'Acts', '*', 'MotionArea_*.clips')):
        a, b = map(int, re.findall(r'_(\d+)', os.path.basename(path))[:2])
        if not a <= mid < b:
            continue
        d = open(path, 'rb').read()
        n = struct.unpack_from('<I', d, 0x10)[0]
        hs = {struct.unpack_from('<Q', d, 0x18 + 16 * i)[0] for i in range(n)}
        for side in 'abcdefgh':
            for k in range(100):
                nm = '%d%s%03d' % (mid, side, k)
                if fnv(nm) in hs:
                    out.append(nm)
    return out


def pair_variants(mid):
    """variants with both an attacker (a) and a receiver (b) clip"""
    names = set(clip_names(mid))
    return sorted(n[-3:] for n in names if n[len(str(mid))] == 'a' and '%db%s' % (mid, n[-3:]) in names)


MIRROR = np.diag([-1.0, 1.0, 1.0])


def mirror(frames, rig):
    """left-right mirror of world-space frames: positions x-flipped, _L/_R joints swapped, and each
    joint's rotation from its rest pose mirrored then put on the mirrored joint's rest (the joints'
    local axes are not mirror-symmetric, so mirroring the raw rotations bent the body)"""
    swap = lambda j: j[:-2] + ('_R' if j.endswith('_L') else '_L') if j.endswith(('_L', '_R')) else j
    out = []
    for w in frames:
        m = {}
        for j, (p, r) in w.items():
            k = swap(j)
            if j in rig.rest_world and k in rig.rest_world:
                delta = r @ rig.rest_world[j][1].T
                m[k] = (MIRROR @ p, MIRROR @ delta @ MIRROR @ rig.rest_world[k][1])
            else:
                m[k] = (MIRROR @ p, MIRROR @ r @ MIRROR)
        out.append(m)
    return out
