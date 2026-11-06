"""AEW Fight Forever animations -> world-space joints in the k26_porter's conventions (+X left, -Y up, -Z forward,
centimetres, 60 fps), so its retarget / placement code can take them as it takes 2K26 clips.

UE FTransform: world rotation = parent rotation * local, world position = parent position + parent rotation *
local translation (bone scales are 1 here).  The UE component axes are mapped to the porter's by the rest pose
itself (left = shoulder_L - shoulder_R, up = head - hips, forward = their cross), so no handedness is assumed.
"""
import glob
import json
import os
import sys

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import ueanim  # noqa: E402

ANIMS = os.path.join(HERE, 'extract', 'ELITE', 'Content', 'GameData', 'Resources', 'Animations', 'Ingame')
SKEL = os.path.join(os.path.dirname(HERE), 'data', 'SKEL_Elite_Roster_Base_Male.json')
FPS = 60.0


def quat_mats(q):
    """(..., 4) xyzw -> (..., 3, 3)"""
    x, y, z, w = q[..., 0], q[..., 1], q[..., 2], q[..., 3]
    return np.stack([np.stack([1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y)], -1),
                     np.stack([2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x)], -1),
                     np.stack([2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y)], -1)], -2)


def fk(parents, R, P):
    """local (F, B, 3, 3) / (F, B, 3) -> component space, parents before children"""
    WR, WP = np.empty_like(R), np.empty_like(P)
    for b, p in enumerate(parents):
        if p < 0:
            WR[:, b], WP[:, b] = R[:, b], P[:, b]
        else:
            WR[:, b] = WR[:, p] @ R[:, b]
            WP[:, b] = WP[:, p] + np.einsum('fij,fj->fi', WR[:, p], P[:, b])
    return WR, WP


class RigAEW:
    """the AEW roster skeleton: rest_world {joint: (position cm, 3x3)} in porter axes, like k26.Rig26"""

    def __init__(self):
        bones = json.load(open(SKEL))
        self.names = [b['name'] for b in bones]
        self.idx = {n: i for i, n in enumerate(self.names)}
        self.parents = [b['parent'] for b in bones]
        assert all(p < i for i, p in enumerate(self.parents))
        self.rest_q = np.array([b['rot'] for b in bones], float)
        self.rest_p = np.array([b['pos'] for b in bones], float)
        WR, WP = fk(self.parents, quat_mats(self.rest_q)[None], self.rest_p[None])
        g = lambda n: WP[0, self.idx[n]]
        left = g('J_Shoulder_L') - g('J_Shoulder_R')
        up = g('J_Head') - g('J_Hips')
        fwd = np.cross(left, up)          # (sign fixed below by the toes)
        axes = []
        for v in (left, up):
            k = int(np.argmax(np.abs(v)))
            axes.append(np.eye(3)[k] * np.sign(v[k]))
        f = np.cross(axes[0], axes[1])
        toe = g('J_Toe_L') - g('J_Foot_L')
        if np.dot(f, toe) < 0:
            f = -f
        self.M = np.array([axes[0], -axes[1], -f])     # rows: UE component -> porter (+X left, -Y up, -Z fwd)
        self.rest_world = {n: (self.M @ WP[0, i], self.M @ WR[0, i] @ self.M.T) for i, n in enumerate(self.names)}

    def world(self, rot, pos):
        """local animation (F, B, 4) xyzw / (F, B, 3) -> [ {joint: (position cm, 3x3)} per frame ], porter axes"""
        WR, WP = fk(self.parents, quat_mats(rot), pos)
        WP = WP @ self.M.T
        WR = self.M @ WR @ self.M.T
        out = []
        for f in range(len(rot)):
            w = {n: (WP[f, i], WR[f, i]) for i, n in enumerate(self.names)}
            w['J_anim_origin'] = (np.zeros(3), np.eye(3))      # both actors' anims share one origin (the actor's)
            out.append(w)
        return out


def find(montage, side):
    """A_<montage>_<A|B>_*.uasset path (montage like 040_00014_000)"""
    pat = os.path.join(ANIMS, montage[:9], 'A_%s_%s_*.uasset' % (montage, side))
    hits = sorted(glob.glob(pat))
    if not hits:
        raise KeyError('no animation %s' % pat)
    return hits[0]


CUT_JUMP = 40.0      # cm in one 60 fps frame: a hand-off when the pose then freezes (AEW's game moved the actor on)
CUT_TELEPORT = 75.0  # cm in one frame: a hand-off even if it moves on (dive impacts reach ~45 cm/frame for real)
FROZEN = 10          # frames of no motion after a jump that mark it as a hand-off
VALID = {}           # path -> frames before the first cut (the clip's real length)


def clip_world(rig, path):
    """-> (frames, world joints per frame). A clip that jumps where AEW hands the actor to its next animation
    (a joint > CUT_JUMP in one frame into a frozen pose - e.g. a downed victim jumping 1 m up and staying - or
    > CUT_TELEPORT at all) holds its last real pose from the jump on; VALID[path] = the real length."""
    out, acl = ueanim.load(path, SKEL)
    W = rig.world(out['rot'], out['pos'])
    J = ('J_Hips', 'J_Head', 'J_Wrist_L', 'J_Wrist_R', 'J_Foot_L', 'J_Foot_R')
    step = [0.0] + [max(np.linalg.norm(W[f][j][0] - W[f - 1][j][0]) for j in J) for f in range(1, len(W))]
    valid = len(W)
    for f in range(1, len(W)):
        frozen = f + 1 < len(W) and max(step[f + 1:f + 1 + FROZEN]) < 0.05
        if step[f] > CUT_TELEPORT or (step[f] > CUT_JUMP and frozen):
            valid = f
            break
    W = W[:valid] + [W[valid - 1]] * (len(W) - valid)
    VALID[path] = valid
    return out['frames'], W


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    rig = RigAEW()
    print('UE component -> porter axes:\n', rig.M)
    for n in ('Root', 'J_Hips', 'J_Head', 'J_Foot_L', 'J_Toe_L', 'J_Wrist_L', 'J_Wrist_R'):
        print('rest %-10s %s' % (n, np.round(rig.rest_world[n][0], 1)))
    mont = sys.argv[1] if len(sys.argv) > 1 else '040_00014_000'
    for side in 'AB':
        try:
            n, W = clip_world(rig, find(mont, side))
        except KeyError as e:
            print(e)
            continue
        print('%s: %d frames' % (side, n))
        for f in (0, n // 4, n // 2, 3 * n // 4, n - 1):
            w = W[f]
            print('  f%3d root %s hips %s head %s toeL %s' % (f, np.round(w['Root'][0], 0), np.round(w['J_Hips'][0], 0),
                                                              np.round(w['J_Head'][0], 0), np.round(w['J_Toe_L'][0], 0)))
