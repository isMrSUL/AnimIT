"""AEW Fight Forever UAnimSequence (UE 4.27 cooked zen package, ACL 2.0 bone codec) -> per-frame local bone transforms.

    python -I ueanim.py <A_*.uasset> [skeleton json]     prints a summary and writes <same>.npz
npz: rot (frames, bones, 4) xyzw, pos (frames, bones, 3) - local, every skeleton bone (untracked bones = rest pose
from the skeleton json) - plus fps, track_bones (skeleton index per ACL track), root_motion flag.

After the tagged properties (UE 4.25+): has-guid i32, raw data guid, FStripDataFlags (2 bytes), bSerializeCompressedData,
CompressedRawDataSize, TrackToSkeletonMap (i32s), compressed curve names, numBytes, bUseBulkDataForLoad, the byte stream
(the ACL compressed_tracks buffer).

ACL 2.0 (version 7) qvvf tracks, as read by ACL's decompression code:
  raw_buffer_header (size, hash), tracks_header (tag, version u16, algorithm u8, track type u8, num_tracks,
  num_samples, sample rate, misc_packed), transform_tracks_header (13 u32, offsets relative to it), segment start
  indices right after it.  Sub-track types: 2 bits per track, 16 per u32, first track in the high bits; one block of
  ceil(n/16) words per kind (rotation, translation, scale if present).  0 default, 1 constant, 2 animated.
  Constant data: rotations as drop-W x,y,z floats in SOA groups of 4 (last group not padded), then translations and
  scales as 3 floats.  Clip range data per animated sub-track: rotations SOA groups of 4 (min xxxx yyyy zzzz, extent
  xxxx yyyy zzzz, floats, last group not padded), then vectors min xyz extent xyz.
  Segment data: one bit-rate byte per variable sub-track (rotations padded to 4), align 2, segment range data
  (rotations 24 bytes per group of 4: u8 min xxxx yyyy zzzz extent xxxx yyyy zzzz; vectors 6 bytes), align 4, then
  animated_pose_bit_size bits per sample: rotations, translations, scales; each sub-track 3 components of n bits,
  big-endian bit order.  n = 0: the value is 3 x u16 in the segment range bytes (no segment range), n = 32: raw floats
  (no range reduction at all).  Otherwise v = q / (2^n - 1), then v * segment extent + min, then v * clip extent + min.
  Rotation w = sqrt(|1 - x^2 - y^2 - z^2|).
"""
import json
import math
import os
import struct
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import zenpkg


def read_sequence(path):
    """-> dict(props, tmap (track -> skeleton bone), buf (bone codec byte stream), codec (its name), ue (the
    UE codecs' own header: frames, key encoding, 3 formats, byte stream / track offset / scale offset counts))"""
    p = zenpkg.Pkg(open(path, 'rb').read())
    for e, s, end in p.export_data():
        if not e['name'].startswith('A_'):
            continue
        r = zenpkg.Reader(p, s, end)
        props = r.props()
        if r.i32():
            r.guid()
        r.guid()                       # raw data guid
        r.o += 2                       # FStripDataFlags
        if not r.i32():
            raise ValueError('no compressed data')
        r.i32()                        # CompressedRawDataSize
        tmap = [r.i32() for _ in range(r.i32())]
        curve_names = [r.name() for _ in range(r.i32())]  # FSmartName display names
        nb = r.i32()
        if r.i32():
            raise ValueError('bulk data animation not handled')
        buf = r.d[r.o:r.o + nb]
        r.o += nb
        codec = r.fstring()            # BoneCodecDDCHandle
        r.fstring()                    # curve codec path
        ncurve = r.i32()
        r.o += ncurve                  # curve bytes
        ue = None
        if 'ACL' not in codec:         # FUECompressedAnimData::SerializeCompressedData
            frames = r.i32()
            fmt = [r.u8() for _ in range(4)]
            nbytes, ntrack_off, nscale_off, scale_strip = r.i32(), r.i32(), r.i32(), r.i32()
            ue = dict(frames=frames, key_format=fmt[0], formats=fmt[1:], nbytes=nbytes, ntrack_off=ntrack_off,
                      nscale_off=nscale_off, scale_strip=scale_strip)
        return dict(props=props, tmap=tmap, buf=buf, codec=codec, ue=ue,
                    curve_names=curve_names, curve_bytes=ncurve)
    raise ValueError('no animation sequence export')


class PerTrack:
    """UE4 AnimCompress_PerTrackCompression (AKF_PerTrackCompression) - formats as UE Viewer reads them.
    Buffer: CompressedTrackOffsets (i32 pairs translation, rotation per track; -1 = identity), scale offsets,
    byte stream.  A track: u32 header (format << 28 | flags << 24 | keys), interval mins/ranges for the flagged
    components, the keys, align 4, then (flags & 8) a frame per key (u8 below 256 frames, else u16)."""

    def __init__(self, buf, ue, ntracks):
        if ue['key_format'] != 2:
            raise ValueError('UE key encoding %d not handled (only per-track)' % ue['key_format'])
        self.frames = ue['frames']
        self.nsamples = self.frames
        self.ntracks = ntracks
        n = ue['ntrack_off']
        self.offs = struct.unpack_from('<%di' % n, buf, 0)
        o = 4 * n
        self.scale_offs = struct.unpack_from('<%di' % ue['nscale_off'], buf, o)
        o += 4 * ue['nscale_off']
        self.strip = ue['scale_strip']
        self.b = buf[o:o + ue['nbytes']] + b'\0' * 16

    def track(self, off, quat):
        b = self.b
        hdr, = struct.unpack_from('<I', b, off)
        fmt, flags, nkeys = hdr >> 28, (hdr >> 24) & 15, hdr & 0xFFFFFF
        o = off + 4
        mins, rngs = [0.0] * 3, [0.0] * 3
        if fmt == 3:      # IntervalFixed32NoW
            for c in range(3):
                if flags & (1 << c):
                    mins[c], rngs[c] = struct.unpack_from('<2f', b, o)
                    o += 8
        mask = [bool(flags & (1 << c)) for c in range(3)]
        keys = []
        for _ in range(nkeys):
            if fmt == 6:            # identity
                v = [0.0, 0.0, 0.0]
            elif fmt in (0, 1):     # None / Float96NoW
                if quat:
                    if fmt == 0:
                        keys.append(list(struct.unpack_from('<4f', b, o)))
                        o += 16
                        continue
                    v = list(struct.unpack_from('<3f', b, o))
                    o += 12
                elif flags & 7:
                    v = [0.0] * 3
                    for c in range(3):
                        if mask[c]:
                            v[c], = struct.unpack_from('<f', b, o)
                            o += 4
                else:
                    v = list(struct.unpack_from('<3f', b, o))
                    o += 12
            elif fmt == 2:          # Fixed48NoW
                q = [32767] * 3 if quat else None
                v = [0.0] * 3
                for c in range(3):
                    if mask[c]:
                        u, = struct.unpack_from('<H', b, o)
                        o += 2
                        v[c] = (u - 32767) / 32767.0 if quat else float(u - 255)
            elif fmt == 3:          # IntervalFixed32NoW
                u, = struct.unpack_from('<I', b, o)
                o += 4
                if quat:   # X 11 bits (high), Y 11, Z 10 (low)
                    v = [((u >> 21) / 1023.0 - 1) * rngs[0] + mins[0], (((u >> 10) & 0x7FF) / 1023.0 - 1) * rngs[1] + mins[1],
                         ((u & 0x3FF) / 511.0 - 1) * rngs[2] + mins[2]]
                else:      # X 10 bits (low), Y 11, Z 11 (high)
                    v = [((u & 0x3FF) / 511.0 - 1) * rngs[0] + mins[0], (((u >> 10) & 0x7FF) / 1023.0 - 1) * rngs[1] + mins[1],
                         ((u >> 21) / 1023.0 - 1) * rngs[2] + mins[2]]
            elif fmt == 4 and quat:  # Fixed32NoW
                u, = struct.unpack_from('<I', b, o)
                o += 4
                v = [(u >> 21) / 1023.0 - 1, ((u >> 10) & 0x7FF) / 1023.0 - 1, (u & 0x3FF) / 511.0 - 1]
            elif fmt == 5 and quat:  # Float32NoW
                u, = struct.unpack_from('<I', b, o)
                o += 4
                X, Y, Z = u >> 21, (u >> 10) & 0x7FF, u & 0x3FF
                f = lambda bits: struct.unpack('<f', struct.pack('<I', bits & 0xFFFFFFFF))[0]
                v = [f(((((X >> 7) & 7) + 123) << 23) | (((X & 0x7F) | 32 * (X & 0xFFFFFC00)) << 16)),
                     f(((((Y >> 7) & 7) + 123) << 23) | (((Y & 0x7F) | 32 * (Y & 0xFFFFFC00)) << 16)),
                     f(((((Z >> 6) & 7) + 123) << 23) | (((Z & 0x3F) | 32 * (Z & 0xFFFFFE00)) << 17))]
            else:
                raise ValueError('per-track format %d (%s) not handled' % (fmt, 'rotation' if quat else 'vector'))
            if quat:
                wsq = 1.0 - (v[0] * v[0] + v[1] * v[1] + v[2] * v[2])
                v = v + [math.sqrt(wsq) if wsq > 0 else 0.0]
            keys.append(v)
        o += -o & 3
        if flags & 8:
            if self.frames < 256:
                times = list(b[o:o + nkeys])
            else:
                times = list(struct.unpack_from('<%dH' % nkeys, b, o))
        else:
            times = None
        return np.array(keys, float), times

    def sample(self, keys, times, quat):
        """keys at their frames -> one value per frame (linear; quaternions nlerp on the short way)"""
        n = len(keys)
        F = self.frames
        if n == 1:
            return np.repeat(keys, F, 0)
        if times is None:
            pos = np.arange(F) * (n - 1) / max(1, F - 1)
            k0 = np.minimum(np.floor(pos).astype(int), n - 2)
            a = pos - k0
        else:
            t = np.array(times, float)
            k0 = np.clip(np.searchsorted(t, np.arange(F), side='right') - 1, 0, n - 2)
            a = np.clip((np.arange(F) - t[k0]) / np.maximum(1e-9, t[k0 + 1] - t[k0]), 0, 1)
        A, B = keys[k0], keys[k0 + 1]
        if quat:
            B = np.where((np.sum(A * B, -1) < 0)[:, None], -B, B)
            v = A + (B - A) * a[:, None]
            return v / np.linalg.norm(v, axis=-1, keepdims=True)
        return A + (B - A) * a[:, None]

    def decode(self):
        F, T = self.frames, self.ntracks
        rot = np.zeros((F, T, 4))
        rot[..., 3] = 1
        trans = np.zeros((F, T, 3))
        scale = np.ones((F, T, 3))
        for t in range(T):
            to, ro = self.offs[2 * t], self.offs[2 * t + 1]
            if to >= 0:
                trans[:, t] = self.sample(*self.track(to, False), False)
            if ro >= 0:
                rot[:, t] = self.sample(*self.track(ro, True), True)
            if self.scale_offs and self.scale_offs[t * self.strip] >= 0:
                scale[:, t] = self.sample(*self.track(self.scale_offs[t * self.strip], False), False)
        return rot, trans, scale


class ACL:
    def __init__(self, buf):
        self.b = b = bytes(buf) + b'\0' * 16
        (size, hsh, tag, ver, alg, ttype, self.ntracks, self.nsamples, self.rate,
         misc) = struct.unpack_from('<3IHBB2IfI', b, 0)
        if tag != 0xAC11AC11 or ver != 7:
            raise ValueError('not an ACL 2.0 buffer (tag %X version %d)' % (tag, ver))
        if ttype != 12:
            raise ValueError('not qvvf transform tracks (%d)' % ttype)
        self.has_scale = misc & 1
        self.default_scale = (misc >> 1) & 1
        if (misc >> 8) & 1:
            raise ValueError('streaming database clips not handled')
        self.h = h = 32
        (self.nseg, self.nvar, self.nar, self.nat, self.nas, self.ncr, self.nct, self.ncs, db_off, seg_off, types_off,
         const_off, clip_off) = struct.unpack_from('<13I', b, h)
        self.seg_start = list(struct.unpack_from('<%dI' % self.nseg, b, h + 52)) if self.nseg > 1 else [0]
        self.segs = [struct.unpack_from('<4I', b, h + seg_off + 16 * i) for i in range(self.nseg)]
        nwords = (self.ntracks + 15) // 16
        kinds = 3 if self.has_scale else 2
        words = struct.unpack_from('<%dI' % (nwords * kinds), b, h + types_off)
        self.types = []
        for k in range(kinds):
            t = []
            for i in range(self.ntracks):
                w = words[k * nwords + i // 16]
                t.append((w >> ((15 - i % 16) * 2)) & 3)
            self.types.append(t)
        if not self.has_scale:
            self.types.append([0] * self.ntracks)
        for k, (nc, na) in enumerate(((self.ncr, self.nar), (self.nct, self.nat), (self.ncs, self.nas))):
            assert self.types[k].count(1) == nc and self.types[k].count(2) == na, (k, nc, na)
        # constants
        o = h + const_off
        self.const_rot = []
        for g in range(0, self.ncr, 4):
            n = min(4, self.ncr - g)
            v = struct.unpack_from('<%df' % (3 * n), b, o)
            o += 12 * n
            for j in range(n):
                self.const_rot.append(quat_w(v[j], v[n + j], v[2 * n + j]))
        self.const_trans = [struct.unpack_from('<3f', b, o + 12 * i) for i in range(self.nct)]
        o += 12 * self.nct
        self.const_scale = [struct.unpack_from('<3f', b, o + 12 * i) for i in range(self.ncs)]
        # clip range
        o = h + clip_off
        self.clip_rot = []
        for g in range(0, self.nar, 4):
            n = min(4, self.nar - g)
            v = struct.unpack_from('<%df' % (6 * n), b, o)
            o += 24 * n
            for j in range(n):
                self.clip_rot.append(([v[j], v[n + j], v[2 * n + j]], [v[3 * n + j], v[4 * n + j], v[5 * n + j]]))
        self.clip_vec = []
        for i in range(self.nat + self.nas):
            v = struct.unpack_from('<6f', b, o)
            o += 24
            self.clip_vec.append((v[:3], v[3:]))
        self.nar_pad = (self.nar + 3) & ~3

    def bits(self, base, bit_off, n):
        o = base + (bit_off >> 3)
        v = int.from_bytes(self.b[o:o + 8], 'big')
        return (v >> (64 - (bit_off & 7) - n)) & ((1 << n) - 1)

    def segment_samples(self, si):
        """-> (rotations (k, nar, 4), vectors (k, nat+nas, 3)) for every sample of segment si"""
        b, h = self.b, self.h
        pose_bits, rot_bits, trans_bits, data_off = self.segs[si]
        base = h + data_off
        fmt = b[base:base + self.nvar]
        o = base + self.nvar
        o += -o & 1                     # align 2 (the buffer itself is 16-aligned)
        rng = o
        rng_size = 6 * self.nvar if self.nseg > 1 else 0
        o = rng + rng_size
        o += -o & 3
        anim = o
        first = self.seg_start[si]
        last = self.seg_start[si + 1] if si + 1 < self.nseg else self.nsamples
        k = last - first
        rots = np.zeros((k, self.nar, 4))
        vecs = np.zeros((k, self.nat + self.nas, 3))
        for s in range(k):
            bit = s * pose_bits
            for i in range(self.nar):
                n = fmt[i]
                g, lane = (i // 4) * 24, i % 4
                rr = rng + g + lane
                if n == 0:
                    x = (b[rr] << 8) | b[rr + 4]
                    y = (b[rr + 8] << 8) | b[rr + 12]
                    z = (b[rr + 16] << 8) | b[rr + 20]
                    v = [x / 65535.0, y / 65535.0, z / 65535.0]
                    seg_r, clip_r = False, True
                elif n == 32:
                    v = [struct.unpack('>f', struct.pack('>I', self.bits(anim, bit + 32 * c, 32)))[0] for c in range(3)]
                    bit += 96
                    seg_r = clip_r = False
                else:
                    m = (1 << n) - 1
                    v = [self.bits(anim, bit + n * c, n) / m for c in range(3)]
                    bit += 3 * n
                    seg_r = clip_r = True
                if seg_r and rng_size:
                    v = [v[c] * (b[rr + 12 + 4 * c] / 255.0) + b[rr + 4 * c] / 255.0 for c in range(3)]
                if clip_r:
                    mn, ex = self.clip_rot[i]
                    v = [v[c] * ex[c] + mn[c] for c in range(3)]
                rots[s, i] = quat_w(*v)
            bit = s * pose_bits + rot_bits
            for j in range(self.nat + self.nas):
                if j == self.nat:
                    bit = s * pose_bits + rot_bits + trans_bits
                n = fmt[self.nar_pad + j]
                rr = rng + 24 * (self.nar_pad // 4) + 6 * j
                if n == 0:
                    v = [((b[rr + 2 * c] << 8) | b[rr + 2 * c + 1]) / 65535.0 for c in range(3)]
                    seg_r, clip_r = False, True
                elif n == 32:
                    v = [struct.unpack('>f', struct.pack('>I', self.bits(anim, bit + 32 * c, 32)))[0] for c in range(3)]
                    bit += 96
                    seg_r = clip_r = False
                else:
                    m = (1 << n) - 1
                    v = [self.bits(anim, bit + n * c, n) / m for c in range(3)]
                    bit += 3 * n
                    seg_r = clip_r = True
                if seg_r and rng_size:
                    v = [v[c] * (b[rr + 3 + c] / 255.0) + b[rr + c] / 255.0 for c in range(3)]
                if clip_r:
                    mn, ex = self.clip_vec[j]
                    v = [v[c] * ex[c] + mn[c] for c in range(3)]
                vecs[s, j] = v
        return rots, vecs

    def decode(self):
        """-> rot (samples, tracks, 4) xyzw, trans (samples, tracks, 3), scale (samples, tracks, 3)"""
        S, T = self.nsamples, self.ntracks
        rot = np.zeros((S, T, 4))
        rot[..., 3] = 1
        trans = np.zeros((S, T, 3))
        scale = np.full((S, T, 3), float(self.default_scale))
        ar = [i for i in range(T) if self.types[0][i] == 2]
        at = [i for i in range(T) if self.types[1][i] == 2]
        as_ = [i for i in range(T) if self.types[2][i] == 2]
        for k, i in enumerate(i for i in range(T) if self.types[0][i] == 1):
            rot[:, i] = self.const_rot[k]
        for k, i in enumerate(i for i in range(T) if self.types[1][i] == 1):
            trans[:, i] = self.const_trans[k]
        for k, i in enumerate(i for i in range(T) if self.types[2][i] == 1):
            scale[:, i] = self.const_scale[k]
        for si in range(self.nseg):
            first = self.seg_start[si]
            r, v = self.segment_samples(si)
            k = len(r)
            rot[first:first + k][:, ar] = r
            trans[first:first + k][:, at] = v[:, :self.nat]
            if as_:
                scale[first:first + k][:, as_] = v[:, self.nat:]
        return rot, trans, scale


class KeyLerp(PerTrack):
    """UE4 AnimEncoding_ConstantKeyLerp (key encoding 0) / VariableKeyLerp (1): track offsets are 4 ints a track
    (translation offset, keys, rotation offset, keys; offset -1 = none), scale offsets 2 a track. One format for all
    translations / rotations / scales (the header's 3 formats). A track of 1 key is Float96NoW; more keys: interval
    formats lead with mins[3], ranges[3] (24 bytes); keys; variable: align 4, then a frame per key (u8 below 256
    frames, else u16)."""
    SIZE = {0: 16, 1: 12, 2: 6, 3: 4, 4: 4, 5: 4, 6: 0}

    def __init__(self, buf, ue, ntracks):
        self.variable = ue['key_format'] == 1
        self.fmt_t, self.fmt_r, self.fmt_s = ue['formats']
        self.frames = ue['frames']
        self.nsamples = self.frames
        self.ntracks = ntracks
        n = ue['ntrack_off']
        self.offs = struct.unpack_from('<%di' % n, buf, 0)
        o = 4 * n
        self.scale_offs = struct.unpack_from('<%di' % ue['nscale_off'], buf, o)
        o += 4 * ue['nscale_off']
        self.strip = ue['scale_strip']
        self.b = buf[o:o + ue['nbytes']] + b'\0' * 16

    def ktrack(self, off, nkeys, fmt, quat):
        b = self.b
        o = off
        if nkeys == 1:
            fmt = 1
        mins, rngs = [0.0] * 3, [0.0] * 3
        if fmt == 3:
            mins = list(struct.unpack_from('<3f', b, o))
            rngs = list(struct.unpack_from('<3f', b, o + 12))
            o += 24
        keys = []
        for _ in range(nkeys):
            if fmt == 1:
                v = list(struct.unpack_from('<3f', b, o))
                o += 12
            elif fmt == 0:
                if quat:
                    keys.append(list(struct.unpack_from('<4f', b, o)))
                    o += 16
                    continue
                v = list(struct.unpack_from('<3f', b, o))
                o += 12
            elif fmt == 2:
                u = struct.unpack_from('<3H', b, o)
                o += 6
                v = [(x - 32767) / 32767.0 for x in u] if quat else [(x - 32767) / 32767.0 * 128.0 for x in u]
            elif fmt == 3:
                u, = struct.unpack_from('<I', b, o)
                o += 4
                if quat:
                    v = [((u >> 21) / 1023.0 - 1) * rngs[0] + mins[0], (((u >> 10) & 0x7FF) / 1023.0 - 1) * rngs[1] + mins[1],
                         ((u & 0x3FF) / 511.0 - 1) * rngs[2] + mins[2]]
                else:
                    v = [((u & 0x3FF) / 511.0 - 1) * rngs[0] + mins[0], (((u >> 10) & 0x7FF) / 1023.0 - 1) * rngs[1] + mins[1],
                         ((u >> 21) / 1023.0 - 1) * rngs[2] + mins[2]]
            elif fmt == 4 and quat:
                u, = struct.unpack_from('<I', b, o)
                o += 4
                v = [(u >> 21) / 1023.0 - 1, ((u >> 10) & 0x7FF) / 1023.0 - 1, (u & 0x3FF) / 511.0 - 1]
            elif fmt == 6:
                v = [0.0, 0.0, 0.0]
            else:
                raise ValueError('key-lerp format %d (%s) not handled' % (fmt, 'rotation' if quat else 'vector'))
            if quat:
                wsq = 1.0 - (v[0] * v[0] + v[1] * v[1] + v[2] * v[2])
                v = v + [math.sqrt(wsq) if wsq > 0 else 0.0]
            keys.append(v)
        times = None
        if self.variable and nkeys > 1:
            o += -o & 3
            times = list(b[o:o + nkeys]) if self.frames < 256 else list(struct.unpack_from('<%dH' % nkeys, b, o))
        return np.array(keys, float), times

    def decode(self):
        F, T = self.frames, self.ntracks
        rot = np.zeros((F, T, 4))
        rot[..., 3] = 1
        trans = np.zeros((F, T, 3))
        scale = np.ones((F, T, 3))
        for t in range(T):
            to, tn, ro, rn = self.offs[4 * t:4 * t + 4]
            if to >= 0 and tn > 0:
                trans[:, t] = self.sample(*self.ktrack(to, tn, self.fmt_t, False), False)
            if ro >= 0 and rn > 0:
                rot[:, t] = self.sample(*self.ktrack(ro, rn, self.fmt_r, True), True)
            if self.scale_offs and len(self.scale_offs) >= 2 * T:
                so, sn = self.scale_offs[2 * t:2 * t + 2]
                if so >= 0 and sn > 0:
                    scale[:, t] = self.sample(*self.ktrack(so, sn, self.fmt_s, False), False)
        return rot, trans, scale


def quat_w(x, y, z):
    return [x, y, z, math.sqrt(abs(1.0 - x * x - y * y - z * z))]


def load(path, skel_json=None):
    seq = read_sequence(path)
    props, tmap = seq['props'], seq['tmap']
    if seq['ue']:
        acl = (PerTrack if seq['ue']['key_format'] == 2 else KeyLerp)(seq['buf'], seq['ue'], len(tmap))
        acl.rate = (acl.frames - 1) / props['SequenceLength']
    else:
        acl = ACL(seq['buf'])
    acl.codec = seq['codec']
    rot, trans, scale = acl.decode()
    out = dict(track_bones=np.array(tmap), fps=acl.rate, frames=acl.nsamples,
               root_motion=bool(props.get('bEnableRootMotion')), track_rot=rot, track_pos=trans)
    if skel_json:
        bones = json.load(open(skel_json))
        R = np.tile(np.array([b['rot'] for b in bones], float), (acl.nsamples, 1, 1))
        P = np.tile(np.array([b['pos'] for b in bones], float), (acl.nsamples, 1, 1))
        R[:, tmap] = rot
        P[:, tmap] = trans
        out.update(rot=R, pos=P, parents=np.array([b['parent'] for b in bones]),
                   names=np.array([b['name'] for b in bones]))
    return out, acl


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    path = sys.argv[1]
    sk = sys.argv[2] if len(sys.argv) > 2 else None
    out, acl = load(path, sk)
    rot, pos = out['track_rot'], out['track_pos']
    print('%s: %d tracks, %d samples at %g fps' % (acl.codec, acl.ntracks, acl.nsamples, acl.rate))
    nrm = np.linalg.norm(rot, axis=-1)
    print('|q| min %.4f max %.4f' % (nrm.min(), nrm.max()))
    dq = np.abs(np.sum(rot[1:] * rot[:-1], axis=-1))
    print('worst frame-to-frame rotation step %.2f deg' % math.degrees(2 * math.acos(min(1, dq.min()))))
    dp = np.linalg.norm(pos[1:] - pos[:-1], axis=-1)
    print('worst frame-to-frame translation step %.2f' % dp.max())
    for t in range(min(8, acl.ntracks)):
        print('track %d bone %d  pos0 %s  posN %s' % (t, out['track_bones'][t], np.round(pos[0, t], 2),
                                                     np.round(pos[-1, t], 2)))
    np.savez_compressed(path[:-7] + '.npz', **out)
