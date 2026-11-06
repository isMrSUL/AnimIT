using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AnimIT.Core;

namespace AnimIT;

/// <summary>--placelog game out.txt evlog...: the in-game placement logs (evlog "P92" lines: both fighters' facing +0x114,
/// origin +0x120..128, motion id / phase / track and the root node's world matrix) against the decoded tracks.
/// 1) how a fighter's root lands in the world (origin + scale * R(facing) * track root)
/// 2) where the game puts the receiver's origin relative to the attacker's, per move</summary>
static class PlaceLog
{
    sealed class Side { public double Face, X, Y, Z; public int Id, Ph, Tr; public double[] M = new double[16]; public bool HasM; }
    sealed class Line { public string Tag = ""; public int Cls; public double Time; public Side A = new(), O = new(); }

    static double F(string hex) => BitConverter.Int32BitsToSingle(int.Parse(hex, NumberStyles.HexNumber));

    static Side ParseSide(string fighter, string mot, string? m0)
    {
        var s = new Side();
        var f = fighter.Split(',');
        s.Face = F(f[0]); s.X = F(f[3]); s.Y = F(f[4]); s.Z = F(f[5]);
        var m = mot.Split(',');
        s.Id = int.Parse(m[0], NumberStyles.HexNumber); s.Ph = int.Parse(m[1], NumberStyles.HexNumber); s.Tr = int.Parse(m[2], NumberStyles.HexNumber);
        if (m0 != null) { var v = m0.Split(','); for (int k = 0; k < 16; k++) s.M[k] = F(v[k]); s.HasM = true; }
        return s;
    }

    static IEnumerable<Line> Read(string path)
    {
        foreach (var raw in File.ReadLines(path))
        {
            if (!raw.StartsWith("P92 ")) continue;
            var kv = new Dictionary<string, string>();
            var parts = raw.Split(' ');
            foreach (var p in parts) { int e = p.IndexOf('='); if (e > 0) kv[p[..e]] = p[(e + 1)..]; }
            if (!kv.ContainsKey("A_mot") || !kv.ContainsKey("O_mot")) continue;
            Line l;
            try
            {
                l = new Line
                {
                    Tag = parts[1], Cls = int.Parse(kv["cls"]), Time = double.Parse(kv["time"], CultureInfo.InvariantCulture),
                    A = ParseSide(kv["A"], kv["A_mot"], kv.GetValueOrDefault("A_m0")),
                    O = ParseSide(kv["O"], kv["O_mot"], kv.GetValueOrDefault("O_m0")),
                };
            }
            catch { continue; }
            yield return l;
        }
    }

    static readonly double SnapTime = double.Parse(Environment.GetEnvironmentVariable("SNAP_TIME") ?? "6", CultureInfo.InvariantCulture);

    public static int Run(string game, string outPath, string[] logs)
    {
        var cat = new GameCatalog(game);
        var skel = Skeleton.Load(Path.Combine(game, "pac", "ch", "ch125.pac"));
        var comp = new Composer(cat, skel);
        var lines = logs.SelectMany(Read).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"{lines.Count} lines");

        Track? Tr(Side s) { try { return comp.Source(s.Id, s.Ph, s.Tr); } catch { return null; } }
        Pose? At(Side s, double time, int shift)
        {
            var t = Tr(s);
            if (t == null || t.Frames.Count == 0) return null;
            // the player clock runs at 60 ticks a second, motions at 30 frames: frame = time / 2 - 1 (root rotation 0.7 deg median)
            int f = Math.Clamp((int)Math.Round(time * 0.5) - 1 + shift, 0, t.Frames.Count - 1);
            return t.Frames[f];
        }

        // ---- 1) single fighter: world root position - origin  vs  track root (local), per facing convention
        var samples = new List<(Side S, double Time)>();
        foreach (var l in lines) { if (l.A.HasM) samples.Add((l.A, l.Time)); if (l.O.HasM) samples.Add((l.O, l.Time)); }
        sb.AppendLine($"{samples.Count} fighter samples with a root matrix");
        (double rms, double s) best = (1e9, 0); string bestName = "";
        foreach (int shift in new[] { -1, 0 })
            foreach (int sign in new[] { 1, -1 })
                foreach (int q in new[] { 0, 1, 2, 3 })
                {
                    double num = 0, den = 0; var pairs = new List<(V3 d, V3 r)>();
                    foreach (var (s, time) in samples)
                    {
                        if (At(s, time, shift) is not Pose p) continue;
                        var d = new V3(s.M[12] - s.X, s.M[13] - s.Y, s.M[14] - s.Z);
                        var r = M3.Ry(sign * s.Face + q * Math.PI / 2) * p.Root;
                        pairs.Add((d, r));
                        num += d.X * r.X + d.Y * r.Y + d.Z * r.Z; den += r.X * r.X + r.Y * r.Y + r.Z * r.Z;
                    }
                    double sc = num / Math.Max(1e-9, den);
                    double e = Math.Sqrt(pairs.Average(pr => { var x = pr.d - pr.r * sc; return x.X * x.X + x.Y * x.Y + x.Z * x.Z; }));
                    string name = $"shift {shift} R(y, {sign:+0;-0} face + {q * 90} deg) scale {sc:0.000}";
                    sb.AppendLine($"  {name}: rms {e:0.000} over {pairs.Count}");
                    if (e < best.rms) { best = (e, sc); bestName = name; }
                }
        sb.AppendLine($"BEST: {bestName} rms {best.rms:0.000}");

        // ---- 1d) the error split: a turn about y (facing) + the rest (tilt); and receiver-relative-to-attacker
        {
            M3 Game(Side s) { var m = s.M; return new M3(m[0], m[4], m[8], m[1], m[5], m[9], m[2], m[6], m[10]); }
            M3 Ours(Side s, Pose p) => M3.Ry(s.Face) * skel.RootRest * M3.Zyx(p.Rot[0] ?? new V3(0, 0, 0));
            double Ang(M3 D) => Math.Acos(Math.Clamp((D.A + D.E + D.I - 1) / 2, -1, 1)) * 180 / Math.PI;
            var yawErr = new List<double>(); var tilt = new List<double>(); var rel = new List<(double E, int Id, int Cls)>();
            foreach (var l in lines)
            {
                if (l.Time < 15) continue;
                foreach (var s in new[] { l.A, l.O })
                {
                    if (!s.HasM || At(s, l.Time, 0) is not Pose p) continue;
                    var D = Ours(s, p) * Game(s).T;           // world-side difference: ours = D * game
                    double y = Math.Atan2(D.C - D.G, D.A + D.I) * 180 / Math.PI;
                    yawErr.Add(y);
                    tilt.Add(Ang(M3.Ry(-y * Math.PI / 180) * D));
                }
                if (l.A.HasM && l.O.HasM && l.A.Id == l.O.Id && At(l.A, l.Time, 0) is Pose pa && At(l.O, l.Time, 0) is Pose po)
                {
                    var (K, Dp) = MotionMath.Placement(l.Cls);
                    // ours, receiver relative to attacker: the receiver turned by D in the attacker's frame
                    var oa = skel.RootRest * M3.Zyx(pa.Rot[0] ?? new V3(0, 0, 0));
                    var oo = M3.Ry(Dp) * skel.RootRest * M3.Zyx(po.Rot[0] ?? new V3(0, 0, 0));
                    var ga = Game(l.A); var go = Game(l.O);
                    rel.Add((Ang((oa.T * oo).T * (ga.T * go)), l.A.Id, l.Cls));
                }
            }
            yawErr.Sort(); tilt.Sort();
            sb.AppendLine();
            sb.AppendLine($"root rotation error split: yaw part median {yawErr[yawErr.Count / 2]:0.0} (10%..90%: {yawErr[yawErr.Count / 10]:0.0} .. {yawErr[9 * yawErr.Count / 10]:0.0}),  tilt part median {tilt[tilt.Count / 2]:0.0} (90%: {tilt[9 * tilt.Count / 10]:0.0})");
            var re = rel.Select(r => r.E).OrderBy(v => v).ToList();
            sb.AppendLine($"receiver rotation RELATIVE to the attacker, game vs AnimIT: median {re[re.Count / 2]:0.0}, 10% {re[re.Count / 10]:0.0}, 90% {re[9 * re.Count / 10]:0.0} deg ({re.Count} pairs)");
            foreach (var g in rel.GroupBy(r => r.Cls).OrderBy(g => g.Key))
            {
                var v = g.Select(r => r.E).OrderBy(x => x).ToList();
                sb.AppendLine($"   op92 {g.Key,4}: median {v[v.Count / 2],6:0.0}  ({v.Count})");
            }
        }

        // ---- 1e) mirrored? local root rotation mirrored in x (S R S, S = diag(-1, 1, 1)): per sample the better of the two
        {
            var S = new M3(-1, 0, 0, 0, 1, 0, 0, 0, 1);
            M3 Game(Side s) { var m = s.M; return new M3(m[0], m[4], m[8], m[1], m[5], m[9], m[2], m[6], m[10]); }
            double Ang(M3 D) => Math.Acos(Math.Clamp((D.A + D.E + D.I - 1) / 2, -1, 1)) * 180 / Math.PI;
            var bestE = new List<double>(); int mir = 0, n = 0, pairSame = 0, pairs = 0;
            var relBest = new List<double>();
            foreach (var l in lines)
            {
                if (l.Time < 15) continue;
                bool? mA = null, mO = null;
                foreach (var (s, isA) in new[] { (l.A, true), (l.O, false) })
                {
                    if (!s.HasM || At(s, l.Time, 0) is not Pose p) continue;
                    var loc = skel.RootRest * M3.Zyx(p.Rot[0] ?? new V3(0, 0, 0));
                    double e0 = Ang((M3.Ry(s.Face) * loc).T * Game(s));
                    double e1 = Ang((M3.Ry(s.Face) * S * loc * S).T * Game(s));
                    bestE.Add(Math.Min(e0, e1)); n++;
                    if (e1 < e0) mir++;
                    if (isA) mA = e1 < e0; else mO = e1 < e0;
                }
                if (mA is bool a && mO is bool o) { pairs++; if (a == o) pairSame++; }
                if (l.A.HasM && l.O.HasM && l.A.Id == l.O.Id && At(l.A, l.Time, 0) is Pose pa && At(l.O, l.Time, 0) is Pose po)
                {
                    var (K, Dp) = MotionMath.Placement(l.Cls);
                    var la0 = skel.RootRest * M3.Zyx(pa.Rot[0] ?? new V3(0, 0, 0));
                    var lo0 = M3.Ry(Dp) * skel.RootRest * M3.Zyx(po.Rot[0] ?? new V3(0, 0, 0));
                    var g = Game(l.A).T * Game(l.O);
                    double e0 = Ang((la0.T * lo0).T * g);
                    double e1 = Ang((S * la0.T * lo0 * S).T * g);     // the whole pair mirrored
                    relBest.Add(Math.Min(e0, e1));
                }
            }
            bestE.Sort(); relBest.Sort();
            sb.AppendLine();
            sb.AppendLine($"with mirroring allowed: root rotation median {bestE[bestE.Count / 2]:0.0} (90% {bestE[9 * bestE.Count / 10]:0.0}); mirrored fits better in {mir} of {n}; attacker and receiver agree in {pairSame} of {pairs} pairs");
            sb.AppendLine($"receiver relative to attacker, pair mirrored or not: median {relBest[relBest.Count / 2]:0.0}, 90% {relBest[9 * relBest.Count / 10]:0.0} deg");
        }

        // ---- 1f) timing: frame = time * k + shift, per sample the best; how the root error falls with each mapping
        {
            M3 Game(Side s) { var m = s.M; return new M3(m[0], m[4], m[8], m[1], m[5], m[9], m[2], m[6], m[10]); }
            double Ang(M3 D) => Math.Acos(Math.Clamp((D.A + D.E + D.I - 1) / 2, -1, 1)) * 180 / Math.PI;
            sb.AppendLine();
            sb.AppendLine("timing: root rotation median error for frame = k * time + shift");
            foreach (double k in new[] { 0.5, 1.0, 2.0 })
            {
                var row = new StringBuilder($"  k {k,3}:");
                foreach (int sh in new[] { -6, -3, -2, -1, 0, 1, 2, 3, 6 })
                {
                    var errs = new List<double>();
                    foreach (var l in lines)
                    {
                        if (l.Time < 15) continue;
                        foreach (var s in new[] { l.A, l.O })
                        {
                            if (!s.HasM || Tr(s) is not Track t || t.Frames.Count == 0) continue;
                            int f = (int)Math.Round(k * l.Time) + sh;
                            if (f < 0 || f >= t.Frames.Count) continue;
                            var p = t.Frames[f];
                            errs.Add(Ang((M3.Ry(s.Face) * skel.RootRest * M3.Zyx(p.Rot[0] ?? new V3(0, 0, 0))).T * Game(s)));
                        }
                    }
                    errs.Sort();
                    row.Append(errs.Count > 0 ? $"  {sh:+0;-0}:{errs[errs.Count / 2],5:0.0}" : $"  {sh:+0;-0}:  -  ");
                }
                sb.AppendLine(row.ToString());
            }
            // how fast does the root turn in these samples (deg per frame), to see if timing could explain 19 deg
            var sp = new List<double>();
            foreach (var l in lines)
            {
                if (l.Time < 15) continue;
                foreach (var s in new[] { l.A, l.O })
                    if (Tr(s) is Track t && (int)l.Time + 1 < t.Frames.Count)
                    {
                        var a = M3.Zyx(t.Frames[(int)l.Time].Rot[0] ?? new V3(0, 0, 0)); var b = M3.Zyx(t.Frames[(int)l.Time + 1].Rot[0] ?? new V3(0, 0, 0));
                        sp.Add(Ang(a.T * b));
                    }
            }
            sp.Sort();
            if (sp.Count > 0) sb.AppendLine($"  root turn speed at the samples: median {sp[sp.Count / 2]:0.0} deg/frame, 90% {sp[9 * sp.Count / 10]:0.0}");
        }

        // ---- 1c) which composition: rest*Zyx, Zyx*rest, Zyx alone, other Euler orders; attacker / receiver apart; frame shift
        sb.AppendLine();
        sb.AppendLine("root rotation variants (median deg) [attacker | receiver]");
        var forms = new (string Name, Func<V3, M3> F)[]
        {
            ("rest*Rz*Ry*Rx", e => skel.RootRest * M3.Rz(e.Z) * M3.Ry(e.Y) * M3.Rx(e.X)),
            ("Rz*Ry*Rx*rest", e => M3.Rz(e.Z) * M3.Ry(e.Y) * M3.Rx(e.X) * skel.RootRest),
            ("rest*Rx*Ry*Rz", e => skel.RootRest * M3.Rx(e.X) * M3.Ry(e.Y) * M3.Rz(e.Z)),
            ("Rx*Ry*Rz*rest", e => M3.Rx(e.X) * M3.Ry(e.Y) * M3.Rz(e.Z) * skel.RootRest),
            ("Rz*Ry*Rx", e => M3.Rz(e.Z) * M3.Ry(e.Y) * M3.Rx(e.X)),
            ("Rx*Ry*Rz", e => M3.Rx(e.X) * M3.Ry(e.Y) * M3.Rz(e.Z)),
            ("rest*Ry*Rx*Rz", e => skel.RootRest * M3.Ry(e.Y) * M3.Rx(e.X) * M3.Rz(e.Z)),
            ("rest*Rz*Rx*Ry", e => skel.RootRest * M3.Rz(e.Z) * M3.Rx(e.X) * M3.Ry(e.Y)),
        };
        foreach (int shift in new[] { -1, 0, 1 })
            foreach (var (fname, F) in forms)
            {
                string Med(bool rec)
                {
                    var errs = new List<double>();
                    foreach (var l in lines)
                    {
                        var s = rec ? l.O : l.A;
                        if (!s.HasM || l.Time < 15 || At(s, l.Time, shift) is not Pose p) continue;
                        var m = s.M;
                        var G = new M3(m[0], m[4], m[8], m[1], m[5], m[9], m[2], m[6], m[10]);
                        var A = M3.Ry(s.Face) * F(p.Rot[0] ?? new V3(0, 0, 0));
                        var D = G.T * A;
                        errs.Add(Math.Acos(Math.Clamp((D.A + D.E + D.I - 1) / 2, -1, 1)) * 180 / Math.PI);
                    }
                    errs.Sort();
                    return errs.Count == 0 ? "-" : $"{errs[errs.Count / 2],6:0.0} ({errs.Count})";
                }
                sb.AppendLine($"  shift {shift,2} {fname,-16} {Med(false)} | {Med(true)}");
            }

        // ---- 1b) the root's ROTATION: game matrix (row vectors: columns of ours = its rows) vs Ry(+-face) * rest * Zyx(rot0)
        sb.AppendLine();
        sb.AppendLine("root rotation, game vs AnimIT (samples 15+ frames in, past the blend-in): mean / median angle between them");
        foreach (int sign in new[] { 1, -1 })
            foreach (bool transpose in new[] { false, true })
                foreach (int q in new[] { 0, 1, 2, 3 })
                {
                    var errs = new List<double>();
                    foreach (var (s, time) in samples)
                    {
                        if (time < 15 || At(s, time, 0) is not Pose p) continue;
                        var m = s.M;
                        var G = transpose ? new M3(m[0], m[1], m[2], m[4], m[5], m[6], m[8], m[9], m[10])
                                          : new M3(m[0], m[4], m[8], m[1], m[5], m[9], m[2], m[6], m[10]);
                        var A = M3.Ry(sign * s.Face + q * Math.PI / 2) * skel.RootRest * M3.Zyx(p.Rot[0] ?? new V3(0, 0, 0));
                        var D = G.T * A;
                        double tr = Math.Clamp((D.A + D.E + D.I - 1) / 2, -1, 1);
                        errs.Add(Math.Acos(tr) * 180 / Math.PI);
                    }
                    if (errs.Count == 0) continue;
                    errs.Sort();
                    sb.AppendLine($"  face sign {sign:+0;-0} {(transpose ? "rows" : "cols")} +{q * 90,3} deg: mean {errs.Average(),6:0.0}  median {errs[errs.Count / 2],6:0.0}  ({errs.Count})");
                }

        // ---- 2) paired: receiver origin in the attacker's frame, per move (id, phase)
        sb.AppendLine();
        sb.AppendLine("move  phase  cls  n  | receiver origin in attacker frame (x, z) +- sd | facing diff deg +- sd | AnimIT (K, D) from the track start");
        var groups = lines.Where(l => l.A.Id == l.O.Id && l.A.Ph == l.O.Ph && l.A.Tr == 0 && l.O.Tr == 1 && Math.Abs(l.Time - SnapTime) < 0.01)
                          .GroupBy(l => (l.A.Id, l.A.Ph));
        foreach (var g in groups.OrderBy(g => g.Key.Id))
        {
            var rel = g.Select(l =>
            {
                var d = new V3(l.O.X - l.A.X, 0, l.O.Z - l.A.Z);
                var r = M3.Ry(-l.A.Face) * d;
                double df = (l.O.Face - l.A.Face) * 180 / Math.PI;
                df = ((df + 180) % 360 + 360) % 360 - 180;
                return (r.X, r.Z, df);
            }).ToList();
            double mx = rel.Average(r => r.X), mz = rel.Average(r => r.Z);
            double mf = Math.Atan2(rel.Average(r => Math.Sin(r.df * Math.PI / 180)), rel.Average(r => Math.Cos(r.df * Math.PI / 180))) * 180 / Math.PI;
            double sx = Math.Sqrt(rel.Average(r => (r.X - mx) * (r.X - mx))), sz = Math.Sqrt(rel.Average(r => (r.Z - mz) * (r.Z - mz)));
            double sf = Math.Sqrt(rel.Average(r => { double e = ((r.df - mf + 180) % 360 + 360) % 360 - 180; return e * e; }));
            int? st = Tr(g.First().A)?.Start;
            var (K, D) = MotionMath.Placement(st);
            string name = cat.Names.GetValueOrDefault(g.Key.Id) ?? "";
            sb.AppendLine($"{g.Key.Id,5} {g.Key.Ph,4} {g.First().Cls,4} {rel.Count,3} | ({mx,7:0.00}, {mz,7:0.00}) +- ({sx:0.00}, {sz:0.00}) | {mf,7:0.0} +- {sf:0.0} | K {K} D {D * 180 / Math.PI:0}   {name}");
        }
        // ---- 3) per start value (op 92): the snap, from every paired line 5 frames in
        sb.AppendLine();
        sb.AppendLine("op92  n  moves | x (sd) | z (sd) | facing diff deg (values seen)");
        foreach (var g in lines.Where(l => l.A.Id == l.O.Id && l.A.Ph == l.O.Ph && l.A.Tr == 0 && l.O.Tr == 1 && Math.Abs(l.Time - SnapTime) < 0.01)
                               .GroupBy(l => l.Cls).OrderBy(g => g.Key))
        {
            var rel = g.Select(l =>
            {
                var r = M3.Ry(-l.A.Face) * new V3(l.O.X - l.A.X, 0, l.O.Z - l.A.Z);
                double df = (l.O.Face - l.A.Face) * 180 / Math.PI;
                return (r.X, r.Z, Df: (int)Math.Round((((df + 180) % 360 + 360) % 360 - 180) / 5) * 5);
            }).ToList();
            double mx = rel.Average(r => r.X), mz = rel.Average(r => r.Z);
            double sx = Math.Sqrt(rel.Average(r => (r.X - mx) * (r.X - mx))), sz = Math.Sqrt(rel.Average(r => (r.Z - mz) * (r.Z - mz)));
            var faces = string.Join(" ", rel.GroupBy(r => r.Df == -180 ? 180 : r.Df).OrderByDescending(x => x.Count()).Select(x => $"{x.Key}:{x.Count()}"));
            sb.AppendLine($"{g.Key,4} {rel.Count,3} {g.Select(l => l.A.Id).Distinct().Count(),4} | {mx,6:0.00} ({sx:0.00}) | {mz,7:0.00} ({sz:0.00}) | {faces}");
        }
        // ---- 4) the test that matters for the preview: the receiver's root bone in the attacker's frame, game vs AnimIT
        sb.AppendLine();
        sb.AppendLine("op92  n | attacker root err | receiver root err: AnimIT table now | flipped facing | (scale fitted from attackers)");
        var paired = lines.Where(l => l.A.Id == l.O.Id && l.A.Ph == l.O.Ph && l.A.Tr == 0 && l.O.Tr == 1 && l.A.HasM && l.O.HasM && l.Time >= 15).ToList();
        foreach (var g in paired.GroupBy(l => l.Cls).OrderBy(g => g.Key))
        {
            double ea = 0, e1 = 0, e2 = 0; int n = 0;
            foreach (var l in g)
            {
                if (At(l.A, l.Time, 0) is not Pose pa || At(l.O, l.Time, 0) is not Pose po) continue;
                var toA = M3.Ry(-l.A.Face);
                var wa = toA * new V3(l.A.M[12] - l.A.X, l.A.M[13] - l.A.Y, l.A.M[14] - l.A.Z);
                var wo = toA * new V3(l.O.M[12] - l.A.X, l.O.M[13] - l.A.Y, l.O.M[14] - l.A.Z);
                var ja = skel.Fk(pa)[skel.RootJoint] * best.s;
                var jo = skel.Fk(po)[skel.RootJoint];
                var (K, D) = MotionMath.Placement(l.Cls);
                var p1 = M3.Ry(D) * jo * best.s + new V3(0, 0, -K);
                var p2 = M3.Ry(D + Math.PI) * jo * best.s + new V3(0, 0, -K);
                double Dist(V3 a, V3 b) { var d = a - b; return Math.Sqrt(d.X * d.X + d.Z * d.Z); }
                ea += Dist(wa, ja); e1 += Dist(wo, p1); e2 += Dist(wo, p2); n++;
            }
            if (n > 0) sb.AppendLine($"{g.Key,4} {n,3} | {ea / n,8:0.00} | {e1 / n,8:0.00} | {e2 / n,8:0.00}");
        }
        File.WriteAllText(outPath, sb.ToString());
        return 0;
    }
}
