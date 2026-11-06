using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AnimIT.Core;

/// <summary>Writing YMKs tracks: the inverse of <see cref="Ymks"/> (rules of the porters' proven ymks.py writer).
/// m.pac tracks get a full 0x7d pose a frame (how 96% of 2011's own grapple tracks are stored); the MOTP copy every
/// match loads uses the packed bit codec.</summary>
public static class YmksWriter
{
    /// <summary>one frame of a track to write: its pose (null = none on this frame), the blend count and its events</summary>
    public sealed class Frame
    {
        public Pose? Pose;
        public int Count;
        public List<byte[]> Events = new();
    }

    // ------------------------------------------------------------------ stream
    /// <summary>(frame, record) sorted -> bytes: skips in 0x5f chunks, records sized 0x60+n (n &lt;= 0x9f) or 0x60 u16</summary>
    public static byte[] Stream(IEnumerable<(int Frame, byte[] Rec)> records, int nframes)
    {
        var o = new MemoryStream();
        int f = 0;
        void Skip(int gap) { while (gap > 0) { int n = Math.Min(gap, 0x5f); o.WriteByte((byte)n); gap -= n; } }
        foreach (var (fr, r) in records)
        {
            if (fr < f) throw new InvalidOperationException("records out of order");
            Skip(fr - f);
            if (r.Length > 0x9f || r.Length == 0) { o.WriteByte(0x60); o.WriteByte((byte)(r.Length & 0xff)); o.WriteByte((byte)(r.Length >> 8)); }
            else o.WriteByte((byte)(0x60 + r.Length));
            o.Write(r);
            f = fr + 1;
        }
        Skip(nframes - f);
        return o.ToArray();
    }

    internal static byte[] Join(byte[] ev, byte[] pose, bool packed)
    {
        if (ev.Length == 0) return pose;
        if (packed)
        {
            if (ev.Length > 0x20) throw new InvalidOperationException("a packed event block is at most 32 bytes");
            return new[] { (byte)(ev.Length - 1) }.Concat(ev).Concat(pose).ToArray();
        }
        if (ev.Length > 0x7f) throw new InvalidOperationException("an event block is at most 127 bytes");
        return new[] { (byte)(0x80 | ev.Length) }.Concat(ev).Concat(pose).ToArray();
    }

    // ------------------------------------------------------------------ poses
    /// <summary>radians -> the game's s16 angle code</summary>
    static int Code(double a) => (short)((int)Math.Round(a / Ymks.Ang) & 0xffff);

    static int Axis(V3? v, int axis) => v is V3 x ? Code(axis == 0 ? x.X : axis == 1 ? x.Y : x.Z) : 0;

    /// <summary>the axes a bone type carries unpacked, in storage order (0 x, 1 y, 2 z)</summary>
    static int[] Axes(int t) => t switch { 2 => new[] { 1 }, 3 => new[] { 0, 1 }, 4 => new[] { 2 }, 6 => new[] { 1, 2 }, _ => new[] { 0, 1, 2 } };

    /// <summary>a full 0x7d pose: count, f32 root (0.01 units), all 21 bones in the m.pac widths</summary>
    public static byte[] FullPose(Pose p, int count)
    {
        var o = new BinaryWriter(new MemoryStream());
        o.Write((byte)0x7d); o.Write((byte)Math.Clamp(count, 0, 255));
        o.Write((float)(p.Root.X / 0.01)); o.Write((float)(p.Root.Y / 0.01)); o.Write((float)(p.Root.Z / 0.01));
        for (int k = 0; k < 21; k++)
            foreach (int a in Axes(Ymks.TypesM1[k])) o.Write((short)Axis(p.Rot[k], a));
        o.Flush();
        return ((MemoryStream)o.BaseStream).ToArray();
    }

    sealed class BitWriter
    {
        public System.Numerics.BigInteger V; public int Pos;
        public void Put(int x, int n)
        {
            if (x < 0 || x >= 1 << n) throw new ArgumentOutOfRangeException(nameof(x));
            V |= new System.Numerics.BigInteger(x) << Pos; Pos += n;
        }
        public byte[] Bytes(int nb) { var b = V.ToByteArray(isUnsigned: true); Array.Resize(ref b, Math.Max(nb, b.Length)); return b[..nb]; }
    }

    /// <summary>s16 angle code -> a packed field (the stock encoder truncates; out of range clamps to the nearer end)</summary>
    static int PkCode(int val, (int Axis, int Bits, int Shift, int Add, bool Hi) f)
    {
        int u = (val - f.Add) & 0xffff;
        int c = u >> f.Shift, hi = (1 << f.Bits) - 1;
        if (c > hi) { int top = (hi + 1) << f.Shift; c = (u - top) < (0x10000 - u) ? hi : 0; }
        return c;
    }

    /// <summary>the packed (MOTP) pose: a masked sub-record with all 21 bones (+ root when it fits 12 bits x 0.02);
    /// a root beyond that goes in its own 0x3f sub-record first</summary>
    public static byte[] PackedPose(Pose p, int count)
    {
        var o = new List<byte>();
        double[] root = { p.Root.X / 0.01, p.Root.Y / 0.01, p.Root.Z / 0.01 };
        bool rootInMask = root.All(v => Math.Abs(v) < 4096);
        if (!rootInMask)
        {
            if (root.Any(v => Math.Abs(v) >= 32767)) throw new InvalidOperationException("the root is beyond the packed range");
            o.Add(0x3f); o.Add((byte)count);
            foreach (var v in root) o.AddRange(BitConverter.GetBytes((short)Math.Round(v)));
        }
        const int mask = 0x1FFFFF;
        var hdr = new byte[] { (byte)(0x40 | (count & 0x3f)), mask & 0xff, (mask >> 8) & 0xff, (byte)(((mask >> 16) & 0x1f) | (rootInMask ? 0x20 : 0) | (count & 0xc0)) };
        var bw = new BitWriter { Pos = 32 };
        if (rootInMask)
            foreach (var v in root)
            {
                int c = Math.Clamp((int)(v / 2), -2048, 2047);   // stock truncates toward zero
                bw.Put(c & 0xfff, 12);
            }
        for (int k = 0; k < 21; k++)
            foreach (var f in Ymks.PkFields(Ymks.TypesPk[k]))
                bw.Put(PkCode(Axis(p.Rot[k], f.Axis), f), f.Bits);
        int nb = (bw.Pos + 7) / 8;
        var body = bw.Bytes(nb);
        o.AddRange(hdr);
        o.AddRange(body[hdr.Length..]);
        return o.ToArray();
    }

    // ------------------------------------------------------------------ tracks
    /// <summary>frames -> an m.pac track (unpacked) or its MOTP copy (packed). Event blocks that don't fit one record
    /// (127 / 32 bytes) carry over to the next frames.</summary>
    public static byte[] Track(IReadOnlyList<Frame> frames, bool packed)
    {
        int limit = packed ? 0x20 : 0x7f;
        var recs = new List<(int, byte[])>();
        var carry = new List<byte[]>();
        for (int f = 0; f < frames.Count; f++)
        {
            var evs = carry.Concat(frames[f].Events).ToList();
            carry = new List<byte[]>();
            var block = new List<byte>();
            foreach (var e in evs)
                if (block.Count + e.Length <= limit) block.AddRange(e); else carry.Add(e);
            byte[] pose = frames[f].Pose is Pose p ? (packed ? PackedPose(p, frames[f].Count) : FullPose(p, frames[f].Count)) : Array.Empty<byte>();
            if (block.Count == 0 && pose.Length == 0) continue;
            recs.Add((f, Join(block.ToArray(), pose, packed)));
        }
        if (carry.Count > 0) throw new InvalidOperationException("events left over after the last frame");
        return Stream(recs, frames.Count);
    }
}
