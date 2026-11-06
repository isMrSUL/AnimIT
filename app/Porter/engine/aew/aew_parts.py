"""Choose SvR 2011 hand part motions from AEW's animated finger joints.

The signature file was measured from MOT/BM03/63 in the stock 2011 m.pac.
Only the twelve non-thumb finger joints are compared. Face animation is not
present in the AEW move clips, so the donor's face events remain in place.
"""
import json
import math
import os
import struct
import sys
from functools import lru_cache

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import aew  # noqa: E402

HOLD_FRAMES = 4
BLEND_FRAMES = 4
FINGERS = ('Index', 'Middle', 'Ring', 'Pinky')


@lru_cache(maxsize=1)
def _data():
    with open(os.path.join(HERE, 'aew_hand_signatures.json'), encoding='utf-8') as f:
        d = json.load(f)
    rig = aew.RigAEW()
    parents = {}
    rest_local = {}
    for side in ('R', 'L'):
        for finger in FINGERS:
            for joint in (1, 2, 3):
                name = 'J_%sF%d_%s' % (finger, joint, side)
                parent = rig.names[rig.parents[rig.idx[name]]]
                parents[name] = parent
                rest_local[name] = rig.rest_world[parent][1].T @ rig.rest_world[name][1]
    shapes = {int(mid): tuple(values) for mid, values in d['signatures'].items()}
    ids = {1: tuple(sorted(d['right_base_ids'])),
           2: tuple(sorted(mid + 200 for mid in d['right_base_ids']))}
    assert all(len(shapes[mid]) == 12 for side in ids.values() for mid in side)
    return rig.M, parents, rest_local, shapes, ids


def finger_signature(world_frame, limb):
    """Twelve signed 2011 z-angle bytes, from the AEW finger changes from rest.

    `limb` is the 2011 event limb: 1 right, 2 left. The right hand uses
    positive 2011 curl z; the left uses negative 2011 curl z.
    """
    M, parents, rest_local, _, _ = _data()
    side, sign = ('R', -1) if limb == 1 else ('L', 1)
    out = []
    for finger in FINGERS:
        for joint in (1, 2, 3):
            name = 'J_%sF%d_%s' % (finger, joint, side)
            parent = parents[name]
            local = world_frame[parent][1].T @ world_frame[name][1]
            delta_ue = M.T @ (local @ rest_local[name].T) @ M
            angle = math.atan2(delta_ue[1, 0], delta_ue[0, 0])
            byte = round(sign * angle * 128 / math.pi)
            out.append(max(-127, min(127, byte)))
    return tuple(out)


def chosen_ids(actor_world_frames, frames60, limb):
    """One selected motion ID for each 30 fps output frame.

    A new nearest shape must persist for four frames, and the old shape must
    have played for at least four frames, before changing. Equal distances
    choose the lower motion ID. Source frames may be cropped or mirrored.
    """
    _, _, _, shapes, ids = _data()
    result = []
    current = pending = None
    streak = last_change = 0
    for out_frame, source_frame in enumerate(frames60):
        finger = finger_signature(actor_world_frames[source_frame], limb)
        nearest = min(ids[limb], key=lambda mid: (sum((a - b) ** 2 for a, b in zip(finger, shapes[mid])), mid))
        if out_frame == 0:
            current = nearest
            last_change = 0
        else:
            if nearest == pending:
                streak += 1
            else:
                pending, streak = nearest, 1
            if nearest != current and streak >= HOLD_FRAMES and out_frame - last_change >= HOLD_FRAMES:
                current = nearest
                last_change = out_frame
        result.append(current)
    return result


def events(actor_world_frames, frames60):
    """Return `{output_frame: [(21, event_bytes), ...]}` for port_k26.port_one.

    The caller removes donor limb 1/2 events and merges this map before its
    existing 32-byte event spreading. The donor's face events stay untouched.
    """
    out = {}
    for limb in (1, 2):
        previous = None
        for frame, mid in enumerate(chosen_ids(actor_world_frames, frames60, limb)):
            if mid != previous:
                out.setdefault(frame, []).append((21, struct.pack('<BBHH', 21, limb, mid, BLEND_FRAMES)))
                previous = mid
    return out


if __name__ == '__main__':
    import sys

    montage = sys.argv[1] if len(sys.argv) > 1 else '040_00014_000'
    rig = aew.RigAEW()
    for side in ('A', 'B'):
        path = aew.find(montage, side)
        n, world = aew.clip_world(rig, path)
        source_frames = list(range(0, n, 2))
        ev = events(world, source_frames)
        print('%s %s: %d source frames, %d hand events' % (montage, side, n, sum(map(len, ev.values()))))
        for frame in sorted(ev):
            print('  %3d: %s' % (frame, [(b[1], struct.unpack_from('<H', b, 2)[0]) for _, b in ev[frame]]))
        for frame in (0, 18, 36, min(len(source_frames) - 1, 90)):
            print('  curl frame %3d: R %s L %s' % (frame,
                  finger_signature(world[source_frames[frame]], 1),
                  finger_signature(world[source_frames[frame]], 2)))
