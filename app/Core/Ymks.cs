using System;
using System.Collections.Generic;

namespace AnimIT.Core;

/// <summary>One decoded pose sub-record: a key the track blends to over Count + 1 frames.</summary>
public sealed class PoseKey
{
    public int Count;
    /// <summary>root in 0.01 model units, or null</summary>
    public double[]? Root;
    /// <summary>bone (0..24) -> s16 codes per axis (x, y, z; null = not sent)</summary>
    public Dictionary<int, int?[]> Bones = new();
}

/// <summary>SvR 2011 YMKs motions (the HCTP porter's ymks.py, from the game's own readers).</summary>
public static class Ymks
{
    public static readonly string[] Bones =
    {
        "root", "koshi", "mune", "kubi", "atama",
        "l_sakotsu", "l_ninoude", "l_kote", "l_te",
        "r_sakotsu", "r_ninoude", "r_kote", "r_te",
        "l_momo", "l_sune", "l_ashi", "l_tsumasaki",
        "r_momo", "r_sune", "r_ashi", "r_tsumasaki",
    };

    // per-bone type (bit0 x, bit1 y, bit2 z; bit3 = 8-bit storage); table at 0x82DA0AF0
    static readonly int[] TypesTable = { 7, 7, 7, 14, 0x17, 6, 7, 3, 14, 6, 7, 3, 14, 7, 2, 0x17, 10, 7, 2, 0x17, 10, 7, 7, 7, 7 };
    /// <summary>mode 1 (m.pac: every value 16-bit): 10 -> 2, 14 -> 6</summary>
    public static readonly int[] TypesM1 = Array.ConvertAll(TypesTable, t => t == 10 ? 2 : t == 14 ? 6 : t);
    /// <summary>mode 3 (MOTP packed): ashi (15, 19) -> 14</summary>
    public static readonly int[] TypesPk = MakePk();
    static int[] MakePk() { var t = (int[])TypesTable.Clone(); t[15] = t[19] = 14; return t; }

    public const double Ang = 2 * Math.PI / 65536.0;
    const int PkRootBits = 12, PkAngBits = 9, PkPBits = 7, PkMBits = 5;

    // ------------------------------------------------------------------ stream
    /// <summary>b &lt; 0x60 skips b frames; 0x60 u16 = record size; b &gt; 0x60 = size b - 0x60. A record is one frame.</summary>
    public static List<(int Frame, byte[] Rec)> Records(byte[] d, out int nframes)
    {
        var o = new List<(int, byte[])>();
        int p = 0, f = 0;
        while (p < d.Length)
        {
            int b = d[p];
            if (b < 0x60) { f += b; p++; continue; }
            int sz;
            if (b == 0x60) { sz = d[p + 1] | (d[p + 2] << 8); p += 3; }
            else { sz = b - 0x60; p++; }
            if (p + sz > d.Length) throw new InvalidOperationException("record overruns motion data");
            o.Add((f, d.AsSpan(p, sz).ToArray()));
            p += sz;
            f++;
        }
        nframes = f;
        return o;
    }

    /// <summary>-> (event block, pose). unpacked: first byte bit 7 = events of (b &amp; 0x7f) bytes;
    /// packed: first byte &lt; 0x20 = events of b + 1 bytes.</summary>
    public static (byte[] Ev, byte[] Pose) Split(byte[] r, bool packed)
    {
        if (r.Length == 0) return (Array.Empty<byte>(), Array.Empty<byte>());
        int b = r[0];
        if (packed)
        {
            if (b < 0x20) return (Slice(r, 1, 1 + b), Slice(r, 2 + b, r.Length - 2 - b));
            return (Array.Empty<byte>(), r);
        }
        if ((b & 0x80) != 0)
        {
            int n = b & 0x7f;
            return (Slice(r, 1, n), Slice(r, 1 + n, r.Length - 1 - n));
        }
        return (Array.Empty<byte>(), r);
    }

    static byte[] Slice(byte[] a, int o, int n)
    {
        if (o >= a.Length || n <= 0) return Array.Empty<byte>();
        n = Math.Min(n, a.Length - o);
        return a.AsSpan(o, n).ToArray();
    }

    /// <summary>event block -> (op, bytes incl. op); each event is T[op] bytes</summary>
    public static List<(int Op, byte[] Data)> Events(byte[] ev, byte[] T)
    {
        var o = new List<(int, byte[])>();
        int p = 0;
        while (p < ev.Length)
        {
            int op = ev[p], n = T[op];
            if (n == 0 || p + n > ev.Length) break;
            o.Add((op, ev.AsSpan(p, n).ToArray()));
            p += n;
        }
        return o;
    }

    // ------------------------------------------------------------------ unpacked poses
    static string AxesOf(int t) => t switch { 2 => "y", 3 => "xy", 4 => "z", 6 => "yz", 10 => "y", 14 => "yz", _ => "xyz" };

    static int?[] ReadBone(byte[] b, ref int p, int t)
    {
        var v = new int?[3];
        if (t == 10) { v[1] = (short)(b[p] << 8); p += 1; return v; }
        if (t == 14) { v[1] = (short)(b[p] << 8); v[2] = (short)(b[p + 1] << 8); p += 2; return v; }
        foreach (char a in AxesOf(t)) { v[a - 'x'] = Bin.S16(b, p); p += 2; }
        return v;
    }

    static double[] Root16(byte[] b, int p) => new double[] { Bin.S16(b, p), Bin.S16(b, p + 2), Bin.S16(b, p + 4) };
    static double[] RootF(byte[] b, int p) => new double[] { Bin.F32(b, p), Bin.F32(b, p + 4), Bin.F32(b, p + 8) };

    /// <summary>unpacked character pose (sub_82391380) -> keys</summary>
    public static List<PoseKey> DecodeBody(byte[] pose)
    {
        var t = TypesM1;
        var subs = new List<PoseKey>();
        int p = 0, n = pose.Length;
        while (p < n - 1)
        {
            var s = new PoseKey();
            int q;
            try
            {
                int op = pose[p];
                if (op is 0x7e or 0x7f)
                {
                    s.Count = pose[p + 1];
                    if (op == 0x7e) { s.Root = Root16(pose, p + 2); q = p + 8; } else { s.Root = RootF(pose, p + 2); q = p + 14; }
                }
                else if (op is 0x7b or 0x76)
                {
                    s.Count = pose[p + 2];
                    if (op == 0x7b) { s.Root = Root16(pose, p + 3); q = p + 9; } else q = p + 7;
                }
                else if (op == 0x74)
                {
                    q = p + 1;
                    int a = pose[q++]; if (a != 0) q += 2;
                    int c = pose[q++]; if (c != 0) q += 4;
                }
                else if (op is 0x7d or 0x7c || (op >= 0x60 && op <= 0x6f))
                {
                    if (op == 0x7d) { s.Count = pose[p + 1]; s.Root = RootF(pose, p + 2); q = p + 14; }
                    else if (op == 0x7c) { s.Count = pose[p + 1]; s.Root = Root16(pose, p + 2); q = p + 8; }
                    else { s.Count = op - 0x60; s.Root = Root16(pose, p + 1); q = p + 7; }
                    for (int k = 0; k < 21; k++) s.Bones[k] = ReadBone(pose, ref q, t[k]);
                }
                else if (op is 0x7a or 0x75 || (op >= 0x50 && op <= 0x5f))
                {
                    if (op == 0x7a) { s.Count = pose[p + 1]; s.Root = RootF(pose, p + 2); q = p + 14; }
                    else if (op == 0x75) { s.Count = pose[p + 1]; s.Root = Root16(pose, p + 2); q = p + 8; }
                    else { s.Count = op - 0x50; s.Root = RootF(pose, p + 1); q = p + 13; }
                    int m = pose[q] | pose[q + 1] << 8 | pose[q + 2] << 16;
                    q += 3;
                    for (int k = 0; k < 21; k++)
                        if ((m >> k & 1) != 0) s.Bones[k] = ReadBone(pose, ref q, t[k]);
                }
                else if (op < t.Length)                    // single bone: [bone][count][data]
                {
                    s.Count = pose[p + 1];
                    q = p + 2;
                    s.Bones[op] = ReadBone(pose, ref q, t[op]);
                }
                else throw new InvalidOperationException($"unknown body sub-record op 0x{op:x2}");
            }
            catch (IndexOutOfRangeException) { q = n + 1; }
            catch (ArgumentOutOfRangeException) { q = n + 1; }
            if (q > n)
            {
                if (subs.Count == 0) throw new InvalidOperationException("body sub-record overruns the record");
                break;
            }
            subs.Add(s);
            p = q;
        }
        return subs;
    }

    // ------------------------------------------------------------------ packed poses (MOTP)
    /// <summary>LSB-first bit reader over the bytes from an offset</summary>
    sealed class Bits
    {
        readonly byte[] b; readonly int o; public int Pos;
        public Bits(byte[] b, int o, int pos) { this.b = b; this.o = o; Pos = pos; }
        public int Get(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++, Pos++)
            {
                int bi = o + (Pos >> 3);
                if (bi >= b.Length) throw new IndexOutOfRangeException();
                v |= ((b[bi] >> (Pos & 7)) & 1) << i;
            }
            return v;
        }
    }

    /// <summary>a bone type's packed fields: (axis, bits, shift, add, hibyte)</summary>
    internal static (int Axis, int Bits, int Shift, int Add, bool Hi)[] PkFields(int t)
    {
        const int N = PkAngBits, M = PkMBits, P = PkPBits;
        return t switch
        {
            4 => new[] { (2, N, 16 - N, 0, false) },
            3 => new[] { (0, N, 16 - N, 0, false), (1, N - 1, 16 - N, 0, false) },
            2 => new[] { (1, N - 1, 16 - N, 0x8000, false) },
            6 => new[] { (1, N - 2, 16 - N, 0xE000, false), (2, N - 2, 16 - N, 0xE000, false) },
            10 => new[] { (1, M, 15 - M, -0x4000, true) },
            14 => new[] { (1, M, 15 - M, 0xC000, true), (2, P - 1, 16 - P, 0xC000, true) },
            _ => new[] { (0, N, 16 - N, 0, false), (1, N, 16 - N, 0, false), (2, N, 16 - N, 0, false) },
        };
    }

    static int PkToS16(int code, (int Axis, int Bits, int Shift, int Add, bool Hi) f)
    {
        int v = ((code << f.Shift) + f.Add) & 0xffff;
        if (f.Hi) v &= 0xff00;
        return (short)v;
    }

    /// <summary>packed character pose (sub_82390990) -> keys</summary>
    public static List<PoseKey> DecodePacked(byte[] pose)
    {
        var t = TypesPk;
        var subs = new List<PoseKey>();
        int p = 0, n = pose.Length;
        while (p < n - 1)
        {
            int h = pose[p];
            var s = new PoseKey();
            if (h == 0x3f)
            {
                s.Count = pose[p + 1];
                s.Root = Root16(pose, p + 2);
                p += 8;
                subs.Add(s);
                continue;
            }
            int mask, start;
            bool hasRoot, full = false;
            if ((h & 0x80) != 0) { full = true; s.Count = h & 0x7f; mask = 0x1FFFFF; hasRoot = true; start = 8; }
            else if ((h & 0x40) != 0)
            {
                int m2 = pose[p + 3];
                mask = pose[p + 1] | pose[p + 2] << 8 | (m2 & 0x1f) << 16;
                hasRoot = (m2 & 0x20) != 0;
                s.Count = (m2 & 0xc0) | (h & 0x3f);
                start = 32;
            }
            else if ((h & 0x20) != 0) { mask = 1 << (h & 0x1f); hasRoot = false; s.Count = pose[p + 1]; start = 16; }
            else throw new InvalidOperationException($"bad packed header 0x{h:x2}");
            var br = new Bits(pose, p, start);
            if (hasRoot)
            {
                var r = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    int c = br.Get(PkRootBits);
                    r[i] = (c >= 1 << (PkRootBits - 1) ? c - (1 << PkRootBits) : c) * 2;   // (* 0.02 units when R = 12)
                }
                s.Root = r;
            }
            for (int k = 0; k < 25; k++)
            {
                if ((mask >> k & 1) == 0) continue;
                if (full && k == 3) continue;                 // kubi not sent in the full form
                var v = new int?[3];
                foreach (var f in PkFields(t[k])) v[f.Axis] = PkToS16(br.Get(f.Bits), f);
                s.Bones[k] = v;
            }
            if (full) s.Bones[3] = new int?[] { null, 0, 0 };   // the game zeroes kubi's target
            int nb = (br.Pos + 7) / 8;
            if (p + nb > n) throw new InvalidOperationException("packed sub-record overruns the record");
            p += nb;
            subs.Add(s);
        }
        return subs;
    }
}
