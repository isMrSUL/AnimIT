using System;
using System.Collections.Generic;

namespace AnimIT.Core;

/// <summary>A decoded track: one Pose per frame (the keys' blends), its events and where the move starts.</summary>
public sealed class Track
{
    public int Id, X, Y;
    public List<Pose> Frames = new();
    public List<(int Frame, List<(int Op, byte[] Data)> Events)> Events = new();
    public int? Start;
    /// <summary>op 84 at frame 0 (4 = the attacker of an anchored move, 5 = the others): moves with no op 92 (corner moves,
    /// double teams) share one spot</summary>
    public int? Anchor;
    public bool Packed;
    public string Bank = "";
    public byte[] T = Array.Empty<byte>();

    /// <summary>each sub-record reaches its pose in count + 1 frames; values blend linearly between keys
    /// (angles: s16 codes x 2 pi / 65536; root: 0.01 model units)</summary>
    public static Track Decode(byte[] data, byte[] T, bool packed, int nframes)
    {
        var tr = new Track { Packed = packed, T = T };
        var recs = Ymks.Records(data, out int n);
        if (nframes > 0) n = nframes;
        var keys = new List<(int F, PoseKey K)>();
        foreach (var (f, rec) in recs)
        {
            var (ev, pose) = Ymks.Split(rec, packed);
            if (ev.Length > 0)
            {
                var lst = Ymks.Events(ev, T);
                if (lst.Count > 0) tr.Events.Add((f, lst));
            }
            if (pose.Length == 0) continue;
            try
            {
                foreach (var k in packed ? Ymks.DecodePacked(pose) : Ymks.DecodeBody(pose)) keys.Add((f, k));
            }
            catch (Exception) { /* (a record the decoder doesn't know: skipped, as the porter's viewer) */ }
        }
        foreach (var (f, lst) in tr.Events)
        {
            if (f > 0) break;
            foreach (var (op, d) in lst)
                if (op == 84 && d.Length > 1 && tr.Anchor == null) tr.Anchor = d[1];
            foreach (var (op, d) in lst)
                if (op == 92 && d.Length > 1) { tr.Start = d[1]; break; }    // event 92 at frame 0: the start class
            if (tr.Start != null) break;
        }
        var curRoot = new V3(0, 0, 0);
        bool haveRoot = false;
        var cur = new V3?[21];
        var segRoot = new List<(int S, int E, V3 From, V3 To)>();
        var seg = new Dictionary<int, (int S, int E, V3 From, V3 To)>();
        int ki = 0;
        for (int f = 0; f < n; f++)
        {
            while (ki < keys.Count && keys[ki].F <= f)
            {
                var (kf, key) = keys[ki];
                int end = kf + key.Count + 1;
                if (key.Root is double[] r)
                {
                    // the first root key holds from frame 0 (the game blends in from whatever the wrestler was doing;
                    // blending from (0, 0, 0) sank every body ~6 units into the mat over the first frames)
                    var tgt = new V3(r[0] * 0.01, r[1] * 0.01, r[2] * 0.01);
                    segRoot.Add((kf, end, haveRoot ? curRoot : tgt, tgt));
                    haveRoot = true;
                }
                foreach (var (b, ax) in key.Bones)
                {
                    if (b >= 21) continue;
                    var tgt = new V3((ax[0] ?? 0) * Ymks.Ang, (ax[1] ?? 0) * Ymks.Ang, (ax[2] ?? 0) * Ymks.Ang);
                    seg[b] = (kf, end, cur[b] ?? tgt, tgt);
                }
                ki++;
            }
            foreach (var (b, (s, e, a0, a1)) in seg)
            {
                double u = f >= e ? 1.0 : Math.Max(0.0, (f - s + 1) / (double)Math.Max(1, e - s));
                cur[b] = V3.Lerp(a0, a1, u);
            }
            while (segRoot.Count > 1 && f >= segRoot[0].E) segRoot.RemoveAt(0);
            if (segRoot.Count > 0)
            {
                var (s, e, a0, a1) = segRoot[^1];
                double u = f >= e ? 1.0 : Math.Max(0.0, (f - s + 1) / (double)Math.Max(1, e - s));
                curRoot = V3.Lerp(a0, a1, u);
            }
            tr.Frames.Add(new Pose { Root = curRoot, Rot = (V3?[])cur.Clone() });
        }
        return tr;
    }
}

public static class MotionMath
{
    /// <summary>how 2011 places the victim, by the attacker's op 92 value at frame 0: his origin K units ahead of the attacker's
    /// (attacker -z), his facing turned by D. Measured in game (evlog P92 lines, both fighters' +0x114 facing / +0x120 origin;
    /// AnimIT --placelog): the game slides both origins there over ~30 frames, and at frame 31 every value lands exactly on these.
    /// (op 92 itself is not a placement command: it sets the fighter state, 822CBD00; the placement follows from that state.)</summary>
    static readonly Dictionary<int, (double K, double D)> PlacementTable = new()
    {
        [0] = (15.0, Math.PI),            // front grapple
        [1] = (10.0, 0.0),                // rear grapple
        [2] = (10.0, Math.PI),            // strikes, running and diving hits (face to face)
        [5] = (0.0, 0.0),                 // dive onto a downed opponent: previews, 21 of 23 dives on one spot, same facing
        [6] = (15.0, Math.PI),
        [10] = (15.0, 0.0), [11] = (15.0, Math.PI), [12] = (15.0, Math.PI), [13] = (15.0, 0.0),   // ground
        [150] = (0.0, Math.PI), [151] = (0.0, 0.0),
        [152] = (20.0, Math.PI),
        [155] = (0.0, Math.PI),           // apron to ring (Apron Suplex 1/2, Face Scrub...): Create-a-Moveset previews, 6 moves
        [157] = (17.0, 0.0),              // apron pull down / cut down: previews, both moves at 17.0, facing 0
        [19] = (15.0, Math.PI),            // big ground moves (People's Elbow, Handspring Splash): previewed 15.0, facing 180
        [160] = (10.0, -Math.PI / 2), [161] = (10.0, -Math.PI / 2),   // previews: 33 side ground moves at 9-12, -90
    };

    /// <summary>a track's placement: by its op 92; a move with none but op 84 04 (corner moves, double teams) puts every
    /// wrestler on the same anchor spot, the receiver facing the attacker: K 0, D 180. Proved offline on 164 stock op-84 phases
    /// (AnimIT --anchorfit: the attacker's hands touch the receiver; the best fit was K 0 / D 180 in 48, within 2.5 units of it
    /// in 37 more; median hand gap 1.2 units). Measured in game: 72 corner moves previewed in Create-a-Moveset (grapple, top
    /// rope, seated, tree of woe) all land on one spot (within 1.4 units), facing 180. (The previews place the pair like a match:
    /// the op 92 6 apron moves came out at 15.5, as the match logs say.)</summary>
    public static (double K, double D) PlacementOf(Track t) =>
        t.Start != null ? Placement(t.Start) : t.Anchor == 4 ? (0.0, Math.PI) : Placement(null);

    /// <summary>where the game starts a move in the ring: the attacker's origin (game x, z) and facing (0 = looking down -z),
    /// from Create-a-Moveset previews (both fighters' +0x114 / +0x120 at the start; the ring floor ends at 28.8). A corner move
    /// plays in the corner, an apron / rope move at the ropes; everything else in the middle. Returned in model units
    /// (world = 0.955 model), for the viewer only: the game places the saved move itself.</summary>
    public static (double Yaw, V3 Shift, double AttackerY, double ReceiverY) StageOf(Track t, string? category, byte[]? flags = null)
    {
        bool Has(int b) => flags != null && flags.Length > b / 8 && (flags[b / 8] >> (b % 8) & 1) != 0;
        // op 84 at frame 0 (the anchor) names the ring spot (2026-11-05, the user: corner / apron / rope / ring-in moves
        // played in the middle). Measured in game (hitlog P lines): op 84 00 Quick Ring Out (-0.2, 22.8) facing 180 and
        // Slide Ring Out (-22.8, -8.1) facing 90 = 22.8 in from a rope, facing it; op 84 03 Normal on the Apron 2
        // (1.5, -40.0) on the floor (y 0 vs the mat's -12), facing the ring; op 84 04 corner (-28.5, 28.5) facing 135.
        // Not measured (from the tracks' own start roots): op 84 01 = the op-00 spot facing in (the attacker's root starts
        // ~9 behind it: on the apron), rope moves = the corner rule on a rope's middle.
        const double Edge = 22.8, OnFloor = 11.8;
        double k = PlacementOf(t).K;
        double d = Math.Sqrt(0.5);
        (double X, double Z, double Face, double AY, double RY)? s =
            t.Start == null && t.Anchor == 4 && category == "Corner" ? (-28.0, 28.0, 3 * Math.PI / 4, 0, 0) :   // 72 corner moves
            t.Start == null && t.Anchor == 4 && category == "Rope" ? (0.0, 28.5, Math.PI, 0, 0) :            // the receiver in the ropes
            t.Start == null && t.Anchor == 0 ? (0.0, Edge, Math.PI, 0, 0) :      // ring out: at the ropes, facing out
            t.Start == null && t.Anchor == 1 ? (0.0, Edge, 0.0, 0, 0) :          // from the apron: the attacker stands on it, facing in
            t.Start == null && t.Anchor == 3 ? (0.0, 40.0, 0.0, OnFloor, 0) :    // from the floor: in front of the apron, facing the ring
            t.Start == 155 ? (0.0, 23.0, 0.0, 0, 0) :                     // apron to ring: both at the ropes
            t.Start == 6 ? (0.0, 7.0, Math.PI, 0, 0) :                    // to the apron / ropes: the receiver at 22-23
            t.Start == 157 ? (0.0, 40.0, 0.0, 12.0, 0) :                  // pull down: the attacker on the floor
            category == "Apron" && t.Start == 2 ? (0.0, 32.0, Math.PI, 0, OnFloor) :   // from the apron down to the floor
            category == "Corner" && t.Anchor != 4 ? (-27.0 + d * k, 27.0 - d * k, 3 * Math.PI / 4, 0, 0) :   // the receiver in the corner
            null;
        if (s is not var (x, z, f, ay, ry0)) return (0, new V3(0, 0, 0), 0, 0);
        // a run into the corner / rope (Clothesline 11, Front Dropkick 2, Jumping Knee 4 phase 0: the attacker's root goes
        // 28-42 toward the corner): start him further back so the run ends in front of the spot, not through the post
        if (t.Start == null && t.Anchor == 4 && t.Frames.Count > 0 && (category == "Corner" || category == "Rope"))
        {
            double back = Math.Max(0, 3.0 - System.Linq.Enumerable.Min(t.Frames, p => p.Root.Z)) * 0.955;
            x += Math.Sin(f) * back; z += Math.Cos(f) * back;     // (toward the ring's centre: against the facing)
        }
        // heights (game y is down): the pull-down attacker stands on the floor, 12 below the mat; a top rope grapple's
        // receiver (list bit 58, all 17 previewed; 59 = the same list from behind, not measured) sits 10 up on the turnbuckle
        double ry = ry0 != 0 ? ry0 : t.Start == null && (Has(58) || Has(59)) ? -10.0 : 0.0;
        return (f, new V3(x / 0.955, 0, z / 0.955), ay / 0.955, ry / 0.955);
    }

    public static string StartNameOf(Track t) => t.Start == null && t.Anchor == 4 ? "Anchored spot (corner / double team)" : StartName(t.Start);

    public static (double K, double D) Placement(int? start) =>
        start is int s && PlacementTable.TryGetValue(s, out var kd) ? kd : (15.0, Math.PI);

    public static string StartName(int? start) => start switch
    {
        0 => "Front grapple", 1 => "Rear grapple", 2 => "Strike / running / diving hit", 5 => "Dive onto a downed opponent", 6 => "Rope / apron",
        10 => "Ground (head)", 11 => "Ground (legs)", 12 => "Ground (side)", 13 => "Ground (head, 13)",
        150 or 151 => "Quick strike", 152 => "Far grapple", 157 => "Apron", 160 or 161 => "Ground (side, 90 deg)",
        19 => "Ground (big move)", 155 => "Apron to ring (same spot)", null => "No start position", _ => $"Start {start}",
    };

    /// <summary>a pose with the root bone turned by d about the world y axis: rest E(m') = Ry(d) rest E(m)</summary>
    public static Pose TurnRoot(Pose p, double d, M3 rest)
    {
        if (Math.Abs(d) < 1e-12) return p;
        var m = p.Rot[0] ?? new V3(0, 0, 0);
        var R = rest.T * M3.Ry(d) * rest * M3.Zyx(m);
        var o = p.Clone();
        o.Rot[0] = R.ToZyx();
        return o;
    }

    /// <summary>the root's facing about y (only differences are used)</summary>
    public static double Heading(Pose p, M3 rest)
    {
        var M = rest * M3.Zyx(p.Rot[0] ?? new V3(0, 0, 0));
        var v = M * new V3(0, 0, 1);
        if (Math.Abs(v.X) + Math.Abs(v.Z) < 0.2) v = M * new V3(1, 0, 0);
        return Math.Atan2(v.X, v.Z);
    }

    static double Wrap(double d) => ((d + Math.PI) % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI) - Math.PI;

    /// <summary>blend two poses. Bones with all three axes (root, koshi, mune, atama, upper arms, thighs, feet) turn along
    /// the shortest 3D path (quaternion slerp): a per-axis blend of two Euler triples far apart spins the body (a lying
    /// receiver's root (140, 81, 160) -> (90, -39, 91) deg swung his facing -107 -> -137 -> -51 deg over a crossfade).
    /// Bones the format stores with fewer axes keep the per-axis blend (a slerp would add an axis they can't hold).</summary>
    public static Pose Lerp(Pose a, Pose b, double u)
    {
        var o = new Pose { Root = V3.Lerp(a.Root, b.Root, u) };
        for (int k = 0; k < 21; k++)
        {
            var va = a.Rot[k] ?? b.Rot[k];
            var vb = b.Rot[k] ?? a.Rot[k];
            if (va is not V3 x || vb is not V3 y) continue;
            int t = Ymks.TypesM1[k];
            o.Rot[k] = t == 7 || t == 0x17
                ? Slerp(M3.Zyx(x), M3.Zyx(y), u).ToZyx()
                : new V3(x.X + Wrap(y.X - x.X) * u, x.Y + Wrap(y.Y - x.Y) * u, x.Z + Wrap(y.Z - x.Z) * u);
        }
        return o;
    }

    /// <summary>how pose a differs from pose b, per bone: a rotation vector (axis * angle, radians) for the bones with all
    /// three axes (a = exp(d) * b), a wrapped per-axis difference for the others; and the root's difference</summary>
    public static (V3 Root, V3?[] Rot) Diff(Pose a, Pose b)
    {
        var r = new V3?[21];
        for (int k = 0; k < 21; k++)
        {
            if (a.Rot[k] is not V3 x || b.Rot[k] is not V3 y) continue;
            int t = Ymks.TypesM1[k];
            r[k] = t == 7 || t == 0x17 ? LogRot(M3.Zyx(x) * M3.Zyx(y).T) : new V3(Wrap(x.X - y.X), Wrap(x.Y - y.Y), Wrap(x.Z - y.Z));
        }
        return (a.Root - b.Root, r);
    }

    /// <summary>pose p moved by an offset from Diff: each bone turned by its rotation vector, the root shifted</summary>
    public static Pose AddOffset(Pose p, V3 root, V3?[] rot)
    {
        var o = new Pose { Root = p.Root + root };
        for (int k = 0; k < 21; k++)
        {
            if (p.Rot[k] is not V3 x) continue;
            if (rot[k] is not V3 d) { o.Rot[k] = x; continue; }
            int t = Ymks.TypesM1[k];
            o.Rot[k] = t == 7 || t == 0x17 ? (ExpRot(d) * M3.Zyx(x)).ToZyx() : x + d;
        }
        return o;
    }

    /// <summary>"smooth" join (inertial blend): the new clip plays from its first frame, and the gap to where the old
    /// clip was - and the speed it had - fades out over `frames` frames (a quintic curve), so the
    /// bodies keep moving through the cut instead of freezing on the old last pose. prev2 / prev: the old clip's last
    /// two poses (prev2 may be null)</summary>
    public static void SmoothInto(List<Pose> l, Pose prev, Pose? prev2, int frames)
    {
        if (l.Count == 0 || frames <= 0) return;
        var x0 = Diff(prev, l[0]);
        // the speed difference: how the old clip was moving at its end minus how the new one starts
        var vo = prev2 != null ? Diff(prev, prev2) : (Root: new V3(0, 0, 0), Rot: new V3?[21]);
        var vn = l.Count > 1 ? Diff(l[1], l[0]) : (Root: new V3(0, 0, 0), Rot: new V3?[21]);
        static V3 Cap(V3 v, double m) => v.Length > m ? v * (m / v.Length) : v;
        var v0Root = Cap(vo.Root - vn.Root, 6);
        var v0 = new V3?[21];
        for (int k = 0; k < 21; k++)
            if (x0.Rot[k] != null)
                v0[k] = Cap((vo.Rot[k] ?? new V3(0, 0, 0)) - (vn.Rot[k] ?? new V3(0, 0, 0)), 0.35);
        // the gap fades on a quintic curve: it starts with the old clip's speed and ends at zero, with no speed or
        // acceleration left at the end (gentler than a spring, which does most of its work in the first frames)
        var src = new List<Pose>(l);
        double T = frames + 1;
        for (int j = 0; j < Math.Min(l.Count, frames); j++)
        {
            double u = (j + 1) / T, u3 = u * u * u;
            double kx = 1 - u3 * (10 - 15 * u + 6 * u * u), kv = T * (u - u3 * (6 - 8 * u + 3 * u * u));
            V3 X(V3 x, V3 v) => x * kx + v * kv;
            var rot = new V3?[21];
            for (int k = 0; k < 21; k++)
                if (x0.Rot[k] is V3 x) rot[k] = X(x, v0[k] ?? new V3(0, 0, 0));
            l[j] = AddOffset(src[j], X(x0.Root, v0Root), rot);
        }
    }

    static V3 LogRot(M3 m)
    {
        var (w, x, y, z) = Quat(m);
        if (w < 0) { w = -w; x = -x; y = -y; z = -z; }
        double s = Math.Sqrt(x * x + y * y + z * z);
        if (s < 1e-9) return new V3(2 * x, 2 * y, 2 * z);
        double ang = 2 * Math.Atan2(s, w);
        return new V3(x / s * ang, y / s * ang, z / s * ang);
    }

    static M3 ExpRot(V3 r)
    {
        double ang = r.Length;
        if (ang < 1e-9) return Mat((1, r.X / 2, r.Y / 2, r.Z / 2));
        double s = Math.Sin(ang / 2) / ang;
        return Mat((Math.Cos(ang / 2), r.X * s, r.Y * s, r.Z * s));
    }

    // ---- quaternions (w, x, y, z) for the blend
    static (double W, double X, double Y, double Z) Quat(M3 m)
    {
        double tr = m.A + m.E + m.I, w, x, y, z;
        if (tr > 0) { double s = Math.Sqrt(tr + 1) * 2; w = s / 4; x = (m.H - m.F) / s; y = (m.C - m.G) / s; z = (m.D - m.B) / s; }
        else if (m.A > m.E && m.A > m.I) { double s = Math.Sqrt(1 + m.A - m.E - m.I) * 2; w = (m.H - m.F) / s; x = s / 4; y = (m.B + m.D) / s; z = (m.C + m.G) / s; }
        else if (m.E > m.I) { double s = Math.Sqrt(1 + m.E - m.A - m.I) * 2; w = (m.C - m.G) / s; x = (m.B + m.D) / s; y = s / 4; z = (m.F + m.H) / s; }
        else { double s = Math.Sqrt(1 + m.I - m.A - m.E) * 2; w = (m.D - m.B) / s; x = (m.C + m.G) / s; y = (m.F + m.H) / s; z = s / 4; }
        return (w, x, y, z);
    }

    static M3 Mat((double W, double X, double Y, double Z) q)
    {
        var (w, x, y, z) = q;
        return new M3(1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y),
                      2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x),
                      2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y));
    }

    public static M3 Slerp(M3 a, M3 b, double u)
    {
        var p = Quat(a); var q = Quat(b);
        double d = p.W * q.W + p.X * q.X + p.Y * q.Y + p.Z * q.Z;
        if (d < 0) { q = (-q.W, -q.X, -q.Y, -q.Z); d = -d; }
        double ka, kb;
        if (d > 0.9995) { ka = 1 - u; kb = u; }
        else { double th = Math.Acos(Math.Min(1, d)), s = Math.Sin(th); ka = Math.Sin((1 - u) * th) / s; kb = Math.Sin(u * th) / s; }
        var r = (ka * p.W + kb * q.W, ka * p.X + kb * q.X, ka * p.Y + kb * q.Y, ka * p.Z + kb * q.Z);
        double n = Math.Sqrt(r.Item1 * r.Item1 + r.Item2 * r.Item2 + r.Item3 * r.Item3 + r.Item4 * r.Item4);
        return Mat((r.Item1 / n, r.Item2 / n, r.Item3 / n, r.Item4 / n));
    }

    public static V3[] PlaceVictim(V3[] P, int? start) => PlaceVictim(P, Placement(start));

    public static V3[] PlaceVictim(V3[] P, (double K, double D) kd)
    {
        var (K, D) = kd;
        var R = M3.Ry(D);
        var o = new V3[P.Length];
        for (int i = 0; i < P.Length; i++) o[i] = R * P[i] + new V3(0, 0, -K);
        return o;
    }
}
