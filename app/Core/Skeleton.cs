using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AnimIT.Core;

public readonly struct V3
{
    public readonly double X, Y, Z;
    public V3(double x, double y, double z) { X = x; Y = y; Z = z; }
    public static V3 operator +(V3 a, V3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static V3 operator -(V3 a, V3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static V3 operator *(V3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static V3 Lerp(V3 a, V3 b, double u) => a + (b - a) * u;
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
}

/// <summary>3 x 3 rotation, row-major</summary>
public readonly struct M3
{
    public readonly double A, B, C, D, E, F, G, H, I;
    public M3(double a, double b, double c, double d, double e, double f, double g, double h, double i)
    { A = a; B = b; C = c; D = d; E = e; F = f; G = g; H = h; I = i; }
    public static readonly M3 Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);
    public static M3 operator *(M3 m, M3 n) => new(
        m.A * n.A + m.B * n.D + m.C * n.G, m.A * n.B + m.B * n.E + m.C * n.H, m.A * n.C + m.B * n.F + m.C * n.I,
        m.D * n.A + m.E * n.D + m.F * n.G, m.D * n.B + m.E * n.E + m.F * n.H, m.D * n.C + m.E * n.F + m.F * n.I,
        m.G * n.A + m.H * n.D + m.I * n.G, m.G * n.B + m.H * n.E + m.I * n.H, m.G * n.C + m.H * n.F + m.I * n.I);
    public static V3 operator *(M3 m, V3 v) => new(m.A * v.X + m.B * v.Y + m.C * v.Z, m.D * v.X + m.E * v.Y + m.F * v.Z, m.G * v.X + m.H * v.Y + m.I * v.Z);
    public M3 T => new(A, D, G, B, E, H, C, F, I);                      // (rotations: inverse = transpose)
    public static M3 Rx(double a) { double c = Math.Cos(a), s = Math.Sin(a); return new(1, 0, 0, 0, c, -s, 0, s, c); }
    public static M3 Ry(double a) { double c = Math.Cos(a), s = Math.Sin(a); return new(c, 0, s, 0, 1, 0, -s, 0, c); }
    public static M3 Rz(double a) { double c = Math.Cos(a), s = Math.Sin(a); return new(c, -s, 0, s, c, 0, 0, 0, 1); }
    /// <summary>the JBOY's order: R = Rz(z) Ry(y) Rx(x)</summary>
    public static M3 Zyx(V3 e) => Rz(e.Z) * Ry(e.Y) * Rx(e.X);
    /// <summary>R = Rz(c) Ry(b) Rx(a) -> (a, b, c)</summary>
    public V3 ToZyx()
    {
        double b = -Math.Asin(Math.Clamp(G, -1.0, 1.0));
        return new V3(Math.Atan2(H, I), b, Math.Atan2(D, A));
    }
}

/// <summary>a frame of one track: root (model units) and the 21 animated bones' Euler angles (radians); null = the
/// bone keeps its rest pose (no key reached it yet)</summary>
public sealed class Pose
{
    public V3 Root;
    public V3?[] Rot = new V3?[21];
    public Pose Clone() => new() { Root = Root, Rot = (V3?[])Rot.Clone() };
}

/// <summary>A SvR 2011 wrestler's skeleton from pac\ch\chNNN.pac (EPK8 -> PACH resource 0 = the JBOY body: bone
/// table at JBOY +0x28, 0x50-byte entries: name, local translation +16, rest Euler +32, parent +0x30). A joint's
/// rotation is its rest Euler times the motion's.</summary>
public sealed class Skeleton
{
    public string[] Names = Array.Empty<string>();
    public int[] Parent = Array.Empty<int>();
    public V3[] Offset = Array.Empty<V3>();
    public M3[] Rest = Array.Empty<M3>();
    public int[] AnimBone = Array.Empty<int>();          // joint -> anim bone 0..20, or -1
    public int RootJoint;
    public M3 RootRest => Rest[RootJoint];

    public static Skeleton Load(string chPac)
    {
        var data = File.ReadAllBytes(chPac);
        if (!Bin.Tag(data, "EPK8")) throw new InvalidOperationException("not a character pac (EPK8)");
        int pach = Bin.Tag(data.AsSpan(0x4000), "PACH") ? 0x4000 : data.AsSpan().IndexOf("PACH"u8);
        if (pach < 0) throw new InvalidOperationException("no PACH in the character pac");
        int count = (int)Bin.U32(data, pach + 4), b = pach + 8 + 12 * count;
        byte[]? jboy = null;
        for (int i = 0; i < count && jboy == null; i++)
        {
            int id = (int)Bin.U32(data, pach + 8 + 12 * i), off = (int)Bin.U32(data, pach + 12 + 12 * i), sz = (int)Bin.U32(data, pach + 16 + 12 * i);
            if (id == 0) jboy = Bpe.Unpack(data.AsSpan(b + off, sz).ToArray());
        }
        if (jboy == null || !Bin.Tag(jboy, "JBOY")) throw new InvalidOperationException("the character pac has no JBOY body");
        int nb = (int)Bin.U32BE(jboy, 0x20), tab = (int)Bin.U32BE(jboy, 0x28);
        var s = new Skeleton
        {
            Names = new string[nb], Parent = new int[nb], Offset = new V3[nb], Rest = new M3[nb], AnimBone = new int[nb],
        };
        var animIndex = new Dictionary<string, int>();
        for (int k = 0; k < Ymks.Bones.Length; k++) animIndex[Ymks.Bones[k]] = k;
        for (int k = 0; k < nb; k++)
        {
            int e = tab + 8 + 0x50 * k;
            int z = Array.IndexOf(jboy, (byte)0, e, 16);
            s.Names[k] = Encoding.Latin1.GetString(jboy, e, (z < 0 ? e + 16 : z) - e);
            int par = Bin.I32BE(jboy, e + 0x30);
            s.Parent[k] = par >= 0 && par < k ? par : -1;
            s.Offset[k] = new V3(Bin.F32BE(jboy, e + 16), Bin.F32BE(jboy, e + 20), Bin.F32BE(jboy, e + 24));
            double Nz(float f) => float.IsNaN(f) ? 0.0 : f;
            s.Rest[k] = M3.Zyx(new V3(Nz(Bin.F32BE(jboy, e + 32)), Nz(Bin.F32BE(jboy, e + 36)), Nz(Bin.F32BE(jboy, e + 40))));
            s.AnimBone[k] = animIndex.TryGetValue(s.Names[k], out int a) ? a : -1;
            if (s.AnimBone[k] == 0) s.RootJoint = k;
        }
        return s;
    }

    public int IndexOf(string name) => Array.IndexOf(Names, name);

    /// <summary>world joint positions (and rotations) of a pose</summary>
    public V3[] Fk(Pose p, M3[]? rotOut = null)
    {
        int n = Names.Length;
        var R = rotOut ?? new M3[n];
        var P = new V3[n];
        for (int i = 0; i < n; i++)
        {
            int k = AnimBone[i];
            var Ri = k >= 0 && p.Rot[k] is V3 e ? Rest[i] * M3.Zyx(e) : Rest[i];
            var t = i == RootJoint ? p.Root : Offset[i];
            int par = Parent[i];
            if (par < 0) { R[i] = Ri; P[i] = t; }
            else { R[i] = R[par] * Ri; P[i] = P[par] + R[par] * t; }
        }
        return P;
    }
}
