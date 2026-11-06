"""HCTP motion tracks -> SvR 2011's.

Poses: HCTP's character poses use signed-short roots and byte hand/foot axes (the
packed bone types). Its 0x50-0x5f masked form has a short root where 2011's has a
float, and so does its 0x7a. Every pose is decoded and rewritten in the forms
stock 2011 motions use - 0x7d (full), 0x50-0x5f / 0x7a (masked), 0x7f (root
only), all with float roots (across 2011's 8,132 attacker/victim tracks no other
root form occurs). The values stay the same: Double DDT (2604), the same
animation in both games, has 2011 float roots equal to HCTP's short ones.

Events (Yuke's own rules, read off the 338 tracks both games share with
identical keys):
  op 192 (HCTP only; 2011 uses its values for something else)  -> dropped
  op 16 limb 1/2 grips (target point on the partner)           -> op 21 (limb, 2011 point, 0x0004)
  op 16 limb 0 (a point on the partner)                        -> POINT_MAP ids; values < 1000 dropped
  op 87: bit 7 of its argument never set in 2011               -> cleared
  ops 80/81 (clear / set a fighter flag), from the same-frame pairs in the shared
  tracks (learn_events.py): HCTP's flags 25 and 26 never occur in 2011 (a ported
  victim that set 25 froze both fighters for the rest of the match) -> dropped;
  flag 12 -> 8 (17 of 17 pairs); 15 -> 22 (3 pairs; HCTP's ring-ins clear 15 where
  2011's clear 22)
  op 49/50/51 (the motion that follows / is started)           -> ids mapped to the ported ones
The hand-off between the two tracks differs: an HCTP victim lets go by clearing flag 26 (with the
attacker's op 192); in 2011 the attacker clears flag 16 and the victim sets 21 and clears 2 within
3 frames of it (1538 of 1965 stock moves). porter.Porter.hand_off writes 2011's (flag_frames, add_events).
Every other event must have the same size in both games' tables, or the track
is refused.

People are tracks 0-4 and 50-56; cameras (30-32) and props keep their own data.
"""
import copy
import os
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, 'vendor'))
import ymks  # noqa: E402

FLAG_DROP = {25, 26}
FLAG_MAP = {12: 8, 15: 22}
GRIP_TARGETS = {1: 3, 3: 25, 10: 2, 2: 12, 12: 1, 80: 5, 201: 203, 203: 225, 210: 201, 202: 212, 212: 202,
                280: 205}
# op 16 limb 0 (a point on the partner): HCTP numbers some points differently. Same-frame pairs in the
# 376 shared tracks (learn_points), the same for attacker and victim; values below 1000 are dropped
# by 2011 (14 of 17, 8 of 9); the other ids (1000, 1142, 1148, 1153, 1194...) are the same in both.
POINT_MAP = {1001: 1026, 1146: 1026, 1150: 1010, 1152: 1024, 1154: 1010, 1155: 1023, 1190: 1023,
             1202: 1031, 1407: 1000}


def is_person(y):
    return y <= 4 or 50 <= y <= 56


def decode_hctp_body(pose):
    p = 0
    subs = []
    while p < len(pose):
        op = pose[p]
        if op == 0x7a:      # extended-count masked form, short root
            s, q = ymks._body_sub(bytes([0x75]) + pose[p + 1:], 0, ymks.TYPES_PK)
            p += q
        elif 0x50 <= op <= 0x5f:   # masked form, short root
            s, q = ymks._body_sub(bytes([0x75, op - 0x50]) + pose[p + 1:], 0, ymks.TYPES_PK)
            p += q - 1
        else:
            s, p = ymks._body_sub(pose, p, ymks.TYPES_PK)
        if p > len(pose):
            raise ValueError('HCTP pose overruns its record')
        if 'raw' in s or 'ref' in s:
            raise ValueError('HCTP pose uses a reference form (0x%02x) not converted yet' % s['op'])
        subs.append(s)
    return dict(subs=subs, trail=b'')


def stock_forms(body, root_shift=(0.0, 0.0, 0.0)):
    for s in body['subs']:
        op = s['op']
        if 'root' in s:
            s['root'] = tuple(float(v) + d for v, d in zip(s['root'], root_shift))
            s['root_fmt'] = 'f32'
        if 0x60 <= op <= 0x6f or op == 0x7c:
            s['op'] = 0x7d
        elif op == 0x75:
            s['op'] = 0x50 + s['count'] if s['count'] < 16 else 0x7a
        elif op == 0x7e:
            s['op'] = 0x7f
    return body


def canonical(body):
    out = []
    for s in body['subs']:
        v = {k: s[k] for k in ('count', 'root') if k in s}
        if 'bones' in s:
            v['bones'] = {b: {a: ang.get(a, 0) for a in 'xyz'} for b, ang in s['bones'].items()}
        out.append(v)
    return out


def convert_track(data, y, T_src, T11, id_map, root_shift=(0.0, 0.0, 0.0)):
    """-> (2011 motion bytes, number of poses converted, motion ids it names)"""
    st = ymks.parse_stream(data)
    records = []
    poses = 0
    refs = set()
    for frame, rec in st.records():
        ev, pose = ymks.split_record(rec)
        events, ok = ymks.parse_events(ev, T_src)
        if not ok:
            raise ValueError('frame %d: an event block HCTP\'s own table does not parse' % frame)
        out = []
        for op, b in events:
            if op == 192:
                continue
            if op == 16 and b[1] in (1, 2):
                t = struct.unpack_from('<H', b, 2)[0]
                out.append((21, bytes([21, b[1]]) + struct.pack('<HH', GRIP_TARGETS.get(t, t), 4)))
                continue
            if op == 16 and b[1] == 0:
                t = struct.unpack_from('<H', b, 2)[0]
                if t < 1000:
                    continue
                out.append((16, b[:2] + struct.pack('<H', POINT_MAP.get(t, t)) + b[4:]))
                continue
            if op in (80, 81):
                if b[1] in FLAG_DROP:
                    continue
                if b[1] in FLAG_MAP:
                    out.append((op, bytes([op, FLAG_MAP[b[1]]]) + b[2:]))
                    continue
            if T11[op] != T_src[op]:
                raise ValueError('event %d is %d bytes in HCTP, %d in 2011' % (op, T_src[op], T11[op]))
            q = bytearray(b)
            if op == 87:
                q[1] &= 0x7f
            if op in (49, 50, 51):
                ref = struct.unpack_from('<H', q, 1)[0]
                if ref:
                    refs.add(ref)
                struct.pack_into('<H', q, 1, id_map.get(ref, ref))
            out.append((op, bytes(q)))
        if pose and is_person(y):
            target = stock_forms(copy.deepcopy(decode_hctp_body(pose)), root_shift)
            pose = ymks.encode_body(target)
            if canonical(ymks.decode_body(pose)) != canonical(target):
                raise ValueError('frame %d: the converted pose does not read back the same' % frame)
            poses += 1
        records.append((frame, ymks.join_record(ymks.build_events(out), pose)))
    result = ymks.build_stream(ymks.make_stream(records, st.nframes()))
    if ymks.parse_stream(result).nframes() != st.nframes():
        raise ValueError('frame count changed in conversion')
    if poses and is_person(y):
        result = resample_full(result)
    return result, poses, refs



def smooth_curve(ks, angle):
    """f -> value through the keys [(frame, value)] (frames rising): a monotone cubic (Fritsch-Carlson / Butland
    tangents). The speed is continuous at the keys (straight lines changed speed at every key), it never
    overshoots (a held pose stays held: equal neighbouring keys give a flat piece), and it passes every key
    exactly. Angles (s16, a full turn = 65536) are unwrapped the short way round first and wrapped back."""
    ks = sorted(dict(ks).items())          # one key a frame (the last one given wins)
    fr = [f for f, v in ks]
    vs = [float(ks[0][1])]
    for (fa, va), (fb, vb) in zip(ks, ks[1:]):
        d = vb - va
        if angle:
            d = (d + 32768) % 65536 - 32768
        vs.append(vs[-1] + d)
    n = len(fr)
    h = [fr[i + 1] - fr[i] for i in range(n - 1)]
    sec = [(vs[i + 1] - vs[i]) / h[i] for i in range(n - 1)]
    m = [0.0] * n
    if n > 1:
        m[0], m[-1] = sec[0], sec[-1]
    for i in range(1, n - 1):
        a, b = sec[i - 1], sec[i]
        if a * b <= 0:
            m[i] = 0.0
        else:
            w1, w2 = 2 * h[i] + h[i - 1], h[i] + 2 * h[i - 1]
            m[i] = (w1 + w2) / (w1 / a + w2 / b)
    for i in (0, n - 1):   # end tangents: no overshoot past the neighbouring key
        if n > 1:
            s = sec[0] if i == 0 else sec[-1]
            if m[i] * s <= 0:
                m[i] = 0.0
            elif abs(m[i]) > 3 * abs(s):
                m[i] = 3 * s

    def f_at(f):
        if f <= fr[0]:
            v = vs[0]
        elif f >= fr[-1]:
            v = vs[-1]
        else:
            i = 0
            while fr[i + 1] < f:
                i += 1
            t = (f - fr[i]) / h[i]
            t2, t3 = t * t, t * t * t
            v = ((2 * t3 - 3 * t2 + 1) * vs[i] + (t3 - 2 * t2 + t) * h[i] * m[i]
                 + (-2 * t3 + 3 * t2) * vs[i + 1] + (t3 - t2) * h[i] * m[i + 1])
        if angle:
            v = (v + 32768) % 65536 - 32768
        return v
    return f_at

def resample_full(data):
    """HCTP's sparse keys (single bones, masked groups, 0x7a, spans of up to 22 frames) -> the way
    2011's own tracks are stored (96% of the 2,426 base-game grapple tracks, and the port's own
    converter): a full 0x7d pose on every frame. Each channel (root x/y/z, every bone axis) is
    a smooth curve through its keys (smooth_curve: every key value kept exactly); angles the short way round
    (Stink Face 2's hips sit at 180 degrees, keys -32765 / +32756: straight it spun every 2 frames).
    KEY TIME: a record at frame f whose sub-record has count c holds the pose of frame f + c (the end of its
    span; c = the frames until the next record - 1). Proof, Final Cut (5180) hips: read at f the root moves
    5, 20, 66, 26, 25, 91 ... a frame (stop-go); read at f + c a steady 16-30 a frame. v3-v5 read keys at f: the
    motion rushed and stalled ("7 fps" / "12 fps") and attacker / victim drifted apart in time.
    v3/v4 drew straight lines between keys: the speed jumped at every key (2-5 frames apart), which the user saw
    as "7 fps" / "12 fps" (Final Cut worst: stop-go on 13-16% of frames, stock moves ~0%). Frame 0 keeps its key and the gap
    to the next key (the blend-in); from there one key a frame up to the last but one frame."""
    st = ymks.parse_stream(data)
    n = st.nframes()
    recs = st.records()
    keys = {}                                    # channel -> [(frame, value)]
    pose_frames = []
    for f, rec in recs:
        pose = ymks.split_record(rec)[1]
        if not pose:
            continue
        pose_frames.append(f)
        for s in ymks.decode_body(pose)['subs']:
            t = f + s.get('count', 0)     # the key's pose is reached at the END of its span (see above)
            if 'root' in s:
                for i, v in enumerate(s['root']):
                    keys.setdefault(('r', i), []).append((t, float(v)))
            for k, vals in s.get('bones', {}).items():
                for a in ymks.axes_of(ymks.TYPES_M1[k]):
                    keys.setdefault((k, a), []).append((t, vals.get(a, 0)))
    if not pose_frames or pose_frames[0] != 0:
        raise ValueError('no pose on frame 0')
    t0 = min(ks[0][0] for ks in keys.values())    # frame 0's key, reached after its blend-in
    first = t0 + 1
    frames = [0] + list(range(first, max(first, n - 1)))

    curves = {ch: smooth_curve(ks, ch[0] != 'r') for ch, ks in keys.items()}

    def at(ch, f):
        return curves[ch](f)
    poses = {}
    for i, f in enumerate(frames):
        count = (frames[i + 1] - f - 1) if i + 1 < len(frames) else max(0, n - 2 - f)
        root = tuple(at(('r', j), f) for j in range(3)) if ('r', 0) in keys else (0.0, 0.0, 0.0)
        bones = {}
        for k in range(21):
            bones[k] = {a: ymks.s16(int(round(at((k, a), f))) & 0xffff) for a in ymks.axes_of(ymks.TYPES_M1[k])
                        if (k, a) in keys}
        poses[f] = ymks.full_pose(count, root, bones)
    out = []
    events = {f: ymks.split_record(rec)[0] for f, rec in recs}
    for f in sorted(set(poses) | {f for f, ev in events.items() if ev}):
        out.append((f, ymks.join_record(events.get(f, b''), poses.get(f, b''))))
    result = ymks.build_stream(ymks.make_stream(out, n))
    if ymks.parse_stream(result).nframes() != n:
        raise ValueError('frame count changed in resampling')
    return result


def packed_stream(data, y, T11):
    """an unpacked 2011 motion -> the packed codec of the MOTP banks every match loads"""
    st = ymks.parse_stream(data)
    items = []
    for it in st.items:
        if it[0] == 'skip':
            items.append(it)
            continue
        ev, pose = ymks.split_record(it[1])
        if y in (0, 1, 2):
            if len(ev) > 32:
                raise ValueError('a packed character event block is at most 32 bytes')
            if pose:
                body = ymks.decode_body(pose)
                if body['trail']:
                    raise ValueError('body has undecoded trailing data')
                sub = []
                for s in body['subs']:
                    count = s.get('count', 0)
                    root = s.get('root')
                    if root is not None and any(abs(v) >= 4096 for v in root):
                        if any(abs(v) >= 32767 for v in root):
                            raise ValueError('root beyond signed 16-bit range')
                        sub.append(dict(form='root', count=count, root=tuple(round(v) for v in root)))
                        root = None
                    if 'bones' in s:
                        if len(s['bones']) == 1 and root is None:
                            n = dict(form='bone', count=count, bones=s['bones'])
                        else:
                            n = dict(form='mask', count=count, bones=s['bones'], mask=sum(1 << k for k in s['bones']))
                            if root is not None:
                                n['root'] = root
                        sub.append(n)
                    elif root is not None:
                        sub.append(dict(form='root', count=count, root=tuple(round(v) for v in root)))
                pose = ymks.encode_packed(dict(subs=sub, trail=b''))
                if ymks.decode_packed(pose)['trail']:
                    raise ValueError('packed pose does not read back')
            rec = ymks.join_record(ev, pose, packed=True)
        else:
            if pose and len(pose) == 29:   # cameras choose their codec by key size
                cam = ymks.decode_camera(pose)
                cam.pop('raw', None)
                cam['mode'] = 2
                pose = ymks.encode_camera(cam)
            rec = ymks.join_record(ev, pose)
        items.append(('rec', rec, len(rec) > 159))
    result = ymks.build_stream(ymks.Stream(items))
    if ymks.parse_stream(result).nframes() != st.nframes():
        raise ValueError('frame count changed in packing')
    return result


def start_class(data, T):
    """event 92's value at frame 0: where the move starts (0 front, 1 rear, 2, 10/11 ground...)"""
    for frame, rec in ymks.parse_stream(data).records():
        ev, _ = ymks.split_record(rec)
        for op, b in ymks.parse_events(ev, T)[0]:
            if op == 92:
                return b[1]
        return None
    return None


def first_root(data, T=None):
    for frame, rec in ymks.parse_stream(data).records():
        pose = ymks.split_record(rec)[1]
        if pose:
            try:
                body = ymks.decode_body(pose)
            except ValueError:
                continue
            for s in body['subs']:
                if 'root' in s:
                    return s['root']
    return None


def last_root(data):
    last = None
    for frame, rec in ymks.parse_stream(data).records():
        pose = ymks.split_record(rec)[1]
        if pose:
            try:
                body = ymks.decode_body(pose)
            except ValueError:
                continue
            for s in body['subs']:
                if 'root' in s:
                    last = s['root']
    return last


def flag_frames(data, T, op, flag):
    """frames where a track clears (op 80) or sets (op 81) a fighter flag"""
    out = []
    for frame, rec in ymks.parse_stream(data).records():
        for o, b in ymks.parse_events(ymks.split_record(rec)[0], T)[0]:
            if o == op and b[1] == flag:
                out.append(frame)
    return out


def add_events(data, T, frame, events):
    """insert events [(op, bytes)] at a frame (a record of its own when the frame has none)"""
    st = ymks.parse_stream(data)
    out, done = [], False
    for f, rec in st.records():
        if f == frame:
            ev, pose = ymks.split_record(rec)
            rec = ymks.join_record(ymks.build_events(ymks.parse_events(ev, T)[0] + events), pose)
            done = True
        elif f > frame and not done:
            out.append((frame, ymks.join_record(ymks.build_events(events), b'')))
            done = True
        out.append((f, rec))
    if not done:
        out.append((frame, ymks.join_record(ymks.build_events(events), b'')))
    result = ymks.build_stream(ymks.make_stream(out, st.nframes()))
    if ymks.parse_stream(result).nframes() != st.nframes():
        raise ValueError('frame count changed adding events')
    return result


def chain(data, T):
    """the last op 49 of a track (the motion that follows), or None"""
    last = None
    for frame, rec in ymks.parse_stream(data).records():
        ev, _ = ymks.split_record(rec)
        for op, b in ymks.parse_events(ev, T)[0]:
            if op == 49:
                last = struct.unpack_from('<H', b, 1)[0]
    return last


def set_chain(data, T, old, new):
    st = ymks.parse_stream(data)
    out = []
    for frame, rec in st.records():
        ev, pose = ymks.split_record(rec)
        evs = []
        for op, b in ymks.parse_events(ev, T)[0]:
            b = bytearray(b)
            if op == 49 and struct.unpack_from('<H', b, 1)[0] == old:
                struct.pack_into('<H', b, 1, new)
            evs.append((op, bytes(b)))
        out.append((frame, ymks.join_record(ymks.build_events(evs), pose)))
    return ymks.build_stream(ymks.make_stream(out, st.nframes()))
