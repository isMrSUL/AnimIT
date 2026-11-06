using System;
using System.Collections.Generic;
using System.Linq;

namespace AnimIT.Core;

/// <summary>YMBs motions (taunts, submissions' holds): a bank of ADPCM channel tracks, unlike YMKs' key records.
/// Layout (the port's char_preview.cpp DecodeMotion, checked here on v2.1.0's banks):
///   bank: 'YMBs', T table at 0x10 (channel lists + descriptors), entry count at 0x110, 16-byte entries at 0x114
///   (y, x, u16 id, u32 data offset, u32 frames, ...), then u32 + one 20-byte header per entry (type, segments, interval),
///   then the data (offsets from 0x114 + 16 n).
///   channels (by descriptor flags): 21 bone rotations (per axis: raw s16 / 6-bit / 4-bit ADPCM, keyed 30 a second),
///   the root move (3 x s24 + 4-bit ADPCM, 60 a second), and per arm / leg an IK target (3 channels like the root) and a
///   pole angle. Rotations are ABSOLUTE (they replace the bone's own rotation, Euler z*y*x); the upper / lower limb bones
///   carry no data and are solved from the IK target and pole (two bones).</summary>
public static class Ymbs
{
    static readonly int[] StepTable = { 57, 57, 57, 57, 77, 102, 128, 153 };

    sealed class Channel { public float Scale = 1; public int[] V = Array.Empty<int>(); }

    static int Wrap16(int x) => (short)(ushort)(x & 0xffff);

    static int[] Adpcm(int init, int step, IReadOnlyList<int> codes, bool four)
    {
        var o = new int[codes.Count + 1];
        int v = init; o[0] = v;
        for (int i = 0; i < codes.Count; i++)
        {
            int c = codes[i], mag, d, idx; bool neg;
            if (four) { mag = c & 7; neg = (c & 8) != 0; d = ((mag * 2 + 1) * step) >> 3; idx = mag; }
            else { mag = c & 0x1F; neg = (c & 0x20) != 0; d = ((mag * 2 + 1) * step) >> 5; idx = Math.Min(mag >> 2, 7); }
            v = neg ? v - d : v + d;
            step = Math.Clamp((step * StepTable[idx]) >> 6, 127, 24576);
            o[i + 1] = v;
        }
        return o;
    }

    sealed class Reader
    {
        public readonly byte[] D; public int P; public bool Ok = true;
        public Reader(byte[] d, int p) { D = d; P = p; }
        public int U8(int at) { if (at < 0 || at >= D.Length) { Ok = false; return 0; } return D[at]; }
        public List<int> Nibbles(int at, int n)
        {
            var o = new List<int>(n);
            for (int i = 0; i < n; i++) { int b = U8(at + (i >> 1)); o.Add((i & 1) != 0 ? b & 15 : b >> 4); }
            return o;
        }
    }

    // translation: s24 init, step byte, 2K nibbles (K + 4 bytes)
    static Channel Ch32(Reader r, int K, float scale)
    {
        int p = r.P;
        int init = r.U8(p) | r.U8(p + 1) << 8 | r.U8(p + 2) << 16;
        if ((init & 0x800000) != 0) init -= 0x1000000;
        var c = new Channel { Scale = scale, V = Adpcm(init, r.U8(p + 3) * 127, r.Nibbles(p + 4, 2 * K), true) };
        r.P += K + 4;
        return c;
    }

    // rotation: 'r' raw s16 (2K), '6' 6-bit (K + 3), 'n' nibbles ((K + 1) / 2 + 3)
    static Channel Ch16(Reader r, int K, char mode, float scale)
    {
        int p = r.P;
        var c = new Channel { Scale = scale };
        if (mode == 'r')
        {
            c.V = new int[K];
            for (int i = 0; i < K; i++) c.V[i] = Wrap16(r.U8(p + 2 * i) | r.U8(p + 2 * i + 1) << 8);
            r.P += 2 * K;
            return c;
        }
        int init = Wrap16(r.U8(p) | r.U8(p + 1) << 8), step = r.U8(p + 2) * 127;
        if (mode == '6')
        {
            var codes = new List<int>(K);
            for (int i = 0; i < K; i++) codes.Add(r.U8(p + 3 + i));
            c.V = Adpcm(init, step, codes, false);
            r.P += K + 3;
        }
        else
        {
            c.V = Adpcm(init, step, r.Nibbles(p + 3, K), true);
            r.P += (K + 1) / 2 + 3;
        }
        for (int i = 0; i < c.V.Length; i++) c.V[i] = Wrap16(c.V[i]);
        return c;
    }

    sealed class Motion
    {
        public int Keys;
        public Channel?[] Root = new Channel?[3];
        public Channel?[,] Ik = new Channel?[4, 3];
        public Channel?[] Pole = new Channel?[4];
        public Channel?[,] Rot = new Channel?[21, 3];
        public bool[] Has = new bool[21];
    }

    static Motion? DecodeMotion(byte[] b, int id, int x, int y, out int frames)
    {
        frames = 0;
        if (b.Length < 0x114 || !Bin.Tag(b, "YMBs")) return null;
        int n = (int)Bin.U32(b, 0x110);
        int bas = 0x114 + n * 16;
        if (bas + 4 + n * 20 > b.Length) return null;
        int k = -1;
        for (int i = 0; i < n; i++)
        {
            int e = 0x114 + 16 * i;
            if (b[e] == y && b[e + 1] == x && Bin.U16(b, e + 2) == id) { k = i; break; }
        }
        if (k < 0) return null;
        int en = 0x114 + 16 * k, h = bas + 4 + 20 * k;
        int type = b[h], nseg = b[h + 1], interval = b[h + 2] != 0 ? b[h + 2] : 1;
        frames = (int)Bin.U32(b, en + 8);
        if (nseg != 0 || type > 1) return null;          // (layouts with segments: not seen in taunts)
        int K = (frames + interval - 1) / interval;
        var m = new Motion { Keys = K };
        var r = new Reader(b, bas + (int)Bin.U32(b, en + 4));
        const int T = 0x10;
        int list = T + b[T + type + 1], desc = T + b[T + b[T] + 1];
        int rot = 0, ik = 0, pole = 0;
        for (int c = 0; c < b[list]; c++)
        {
            int ds = desc + 4 * (b[list + 1 + c] - 1);
            int f = b[ds], codec = b[ds + 1];
            float scale = b[ds + 2];
            if ((f & 0x80) != 0)
            {
                if ((f & 0x40) != 0) { for (int a = 0; a < 3; a++) m.Ik[Math.Min(ik, 3), a] = Ch32(r, K, scale); ik++; }
                else { m.Pole[Math.Min(pole, 3)] = Ch16(r, K, 'n', scale); pole++; }
            }
            else if ((f & 0x40) != 0) { for (int a = 0; a < 3; a++) m.Root[a] = Ch32(r, K, scale); }
            else if ((f & 0x20) != 0) Ch16(r, K, (codec & 15) == 2 ? '6' : 'n', 1);
            else
            {
                if (rot < 21) m.Has[rot] = (f & 7) != 0;
                for (int a = 0; a < 3; a++)
                {
                    if ((f & (1 << a)) == 0) continue;
                    char mode = (codec & 0xF0) == 0x40 ? 'r' : (codec & 15) == 2 ? '6' : 'n';
                    m.Rot[Math.Min(rot, 20), a] = Ch16(r, K, mode, scale);
                }
                rot++;
            }
        }
        return r.Ok && rot == 21 && ik == 4 && pole == 4 ? m : null;
    }

    static double Key(Channel? c, double at, bool angle, double unit)
    {
        if (c == null || c.V.Length == 0) return 0;
        int n = c.V.Length;
        int i0 = Math.Clamp((int)Math.Floor(at), 0, n - 1), i1 = Math.Min(i0 + 1, n - 1);
        double f = Math.Clamp(at - i0, 0, 1);
        double a = c.V[i0], b = c.V[i1];
        if (angle) b = a + Wrap16((int)(b - a));
        return (a + (b - a) * f) * c.Scale * unit;
    }

    /// <summary>a frame whose x points along xDir and whose z is d (sign: arms +1, legs -1) without its x part</summary>
    static M3 Aim(V3 xDir, V3 d, double sign)
    {
        var x = Norm(xDir);
        var z = Norm((d - x * Dot(d, x)) * sign);
        var y = Cross(z, x);
        return new M3(x.X, y.X, z.X, x.Y, y.Y, z.Y, x.Z, y.Z, z.Z);
    }
    static double Dot(V3 a, V3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    static V3 Cross(V3 a, V3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    static V3 Norm(V3 a) { double l = a.Length; return l < 1e-12 ? a : a * (1 / l); }

    /// <summary>a YMBs track -> AnimIT poses (one a frame, 30 a second): rotations as motion Eulers over the rest pose,
    /// arms / legs solved by the two-bone IK as the game does; null if the bank doesn't hold it or the layout is unknown</summary>
    public static Track? Decode(byte[] bank, int id, int x, int y, Skeleton skel)
    {
        var m = DecodeMotion(bank, id, x, y, out int frames);
        if (m == null) return null;
        const double kAng = 2 * Math.PI / 65536.0, kMove = 1e-4;
        int interval = Math.Max(1, (int)Math.Round(frames / (double)Math.Max(1, m.Keys)));
        var tr = new Track { Id = id, X = x, Y = y, Bank = "YMBs" };
        int nj = skel.Names.Length;
        int J(string name) => skel.IndexOf(name);
        var limbs = new (int Up, int Lo, int End, double Sign, int Roll)[]
        {
            (J("l_ninoude"), J("l_kote"), J("l_te"), 1, 7), (J("r_ninoude"), J("r_kote"), J("r_te"), 1, 11),
            (J("l_momo"), J("l_sune"), J("l_ashi"), -1, -1), (J("r_momo"), J("r_sune"), J("r_ashi"), -1, -1),
        };
        for (int fr = 0; fr < frames; fr++)
        {
            double key = fr / (double)interval;
            var pose = new Pose();
            var abs = new V3[21];
            for (int c = 0; c < 21; c++)
                abs[c] = new V3(Key(m.Rot[c, 0], key, true, kAng), Key(m.Rot[c, 1], key, true, kAng), Key(m.Rot[c, 2], key, true, kAng));
            // absolute local rotations -> AnimIT's motion Euler (rest * Zyx(motion) = the absolute one)
            for (int c = 0; c < 21; c++)
            {
                if (!m.Has[c]) continue;
                int j = Array.IndexOf(skel.AnimBone, c);
                if (j < 0) continue;
                pose.Rot[c] = (skel.Rest[j].T * M3.Zyx(abs[c])).ToZyx();
            }
            pose.Root = new V3(Key(m.Root[0], 2 * key, false, kMove), Key(m.Root[1], 2 * key, false, kMove), Key(m.Root[2], 2 * key, false, kMove));
            // the limbs: two-bone IK onto the target, bent toward the pole angle
            var R = new M3[nj];
            var P = skel.Fk(pose, R);
            var rootP = P[skel.RootJoint];
            var fwd = new V3(0, 0, -1);
            for (int l = 0; l < 4; l++)
            {
                var (iu, il, ie, sign, roll) = limbs[l];
                if (iu < 0 || il < 0 || ie < 0) continue;
                int pu = skel.Parent[iu], pl = skel.Parent[il];
                if (pu < 0 || pl < 0) continue;
                var S = P[iu];
                double L1 = (P[il] - S).Length, L2 = (P[ie] - P[il]).Length;
                var target = rootP + new V3(Key(m.Ik[l, 0], 2 * key, false, kMove), Key(m.Ik[l, 1], 2 * key, false, kMove), Key(m.Ik[l, 2], 2 * key, false, kMove));
                var a = target - S;
                double dist = Math.Max(1e-4, Math.Min(a.Length, (L1 + L2) * 0.9999));
                a = Norm(a);
                var d0 = Norm(fwd - a * Dot(fwd, a));
                double th = Key(m.Pole[l], key, true, kAng);
                var d = d0 * Math.Cos(th) + Cross(a, d0) * Math.Sin(th);
                double cosA = Math.Clamp((L1 * L1 + dist * dist - L2 * L2) / (2 * L1 * dist), -1, 1), sinA = Math.Sqrt(Math.Max(0, 1 - cosA * cosA));
                var Jt = S + a * (L1 * cosA) + d * (L1 * sinA);
                var E = S + a * dist;
                // upper bone: world = Aim(J - S); local = parent^T * world; AnimIT's motion = rest^T * local
                SetLocal(skel, pose, iu, R[pu].T * Aim(Jt - S, d, sign));
                P = skel.Fk(pose, R);
                var Rl = Aim(E - Jt, d, sign);
                if (roll >= 0) Rl = Rl * M3.Zyx(new V3(abs[roll].X, 0, 0));
                SetLocal(skel, pose, il, R[pl].T * Rl);
                P = skel.Fk(pose, R);
            }
            tr.Frames.Add(pose);
        }
        return tr;
    }

    static void SetLocal(Skeleton skel, Pose pose, int joint, M3 local)
    {
        int b = skel.AnimBone[joint];
        if (b < 0 || b >= 21) return;
        pose.Rot[b] = (skel.Rest[joint].T * local).ToZyx();
    }
}
