using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using System.Text;
using AnimIT.Core;

namespace AnimIT;

/// <summary>--joincheck game out.txt id,id: frame by frame around each join - headings, root, how far the joints move</summary>
static class JoinCheck
{
    public static int Run(string game, string outPath, string ids)
    {
        var cat = new GameCatalog(game);
        var skel = Skeleton.Load(Path.Combine(game, "pac", "ch", "ch125.pac"));
        var comp = new Composer(cat, skel);
        var blocks = ids.Split(',').Select((s, i) =>
        {
            var m = cat.Moves[int.Parse(s)]; int x = m.MainPhase, n = m.AttackerAt(x) ?? 1;
            return new Block
            {
                Name = m.Name, Attacker = new LaneRef(m.Id, x, 0, ""), Receiver = m.ReceiverAt(x) != null ? new LaneRef(m.Id, x, 1, "") : null,
                In = 0, Out = n - 1, SourceLength = n, Join = i > 0 ? JoinMode.Crossfade : JoinMode.HardCut, Align = i > 0 ? RootAlign.PlanarGroup : RootAlign.None,
            };
        }).ToList();
        if (Environment.GetEnvironmentVariable("JOIN_ATURN") is string at && blocks.Count > 1) blocks[^1].AttackerTurn = double.Parse(at);
        var c = comp.Compose(blocks);
        var (A, R) = comp.Joints(c);
        var sb = new StringBuilder();
        double D(double r) => r * 180 / Math.PI;
        for (int b = 1; b < blocks.Count; b++)
        {
            int j = c.Spans[b].Start;
            sb.AppendLine($"== join {b} at frame {j}");
            for (int f = Math.Max(1, j - 4); f < Math.Min(c.Frames, j + 14); f++)
            {
                string Lane(System.Collections.Generic.List<Pose?> L, System.Collections.Generic.List<V3[]?> P)
                {
                    if (L[f] is not Pose p || P[f] is not V3[] q || P[f - 1] is not V3[] q0) return "-";
                    double mv = q.Zip(q0, (u, v) => (u - v).Length).Max();
                    var pel = q[skel.RootJoint];
                    return $"head {D(MotionMath.Heading(p, skel.RootRest)),7:0.0} rot0 ({D(p.Rot[0]!.Value.X),6:0} {D(p.Rot[0]!.Value.Y),6:0} {D(p.Rot[0]!.Value.Z),6:0}) root ({pel.X,6:0.0} {pel.Y,6:0.0} {pel.Z,6:0.0}) maxmove {mv,5:0.00}";
                }
                string Body(System.Collections.Generic.List<V3[]?> P)
                {
                    if (P[f] is not V3[] q) return "-";
                    var h = q[skel.IndexOf("atama")]; var k = q[skel.IndexOf("koshi")];
                    double ang = Math.Atan2(h.X - k.X, h.Z - k.Z) * 180 / Math.PI;
                    return $"body {ang,7:0.0} head ({h.X,6:0.0} {h.Z,6:0.0}) hips ({k.X,6:0.0} {k.Z,6:0.0})";
                }
                sb.AppendLine($"f{f,4} A {Body(A)} | R {Body(R)}");
            }
        }
        File.WriteAllText(outPath, sb.ToString());
        return 0;
    }
}

/// <summary>--trackbody game file.ymk [packed]: the body direction (head - hips, seen from above) of a saved track, per frame</summary>
static class TrackBody
{
    public static int Run(string game, string file, bool packed)
    {
        var cat = new GameCatalog(game);
        var skel = Skeleton.Load(Path.Combine(game, "pac", "ch", "ch125.pac"));
        var T = cat.T(cat.Best(5109, 0, 0)!);
        var t = Track.Decode(File.ReadAllBytes(file), T, packed, 0);
        for (int f = 0; f < t.Frames.Count; f++)
        {
            if (f % 10 != 0 && (f < 150 || f > 200)) continue;
            var q = skel.Fk(t.Frames[f]);
            var h = q[skel.IndexOf("atama")]; var k = q[skel.IndexOf("koshi")];
            Console.WriteLine($"f{f,4} body {Math.Atan2(h.X - k.X, h.Z - k.Z) * 180 / Math.PI,7:0.0} hips ({k.X,6:0.0} {k.Z,6:0.0})");
        }
        return 0;
    }
}

/// <summary>--motpcheck game id x y: the game's own MOTP (packed) copy vs its m.pac copy of a stock track, per frame</summary>
static class MotpCheck
{
    public static int Run(string game, int id, int x, int y)
    {
        var cat = new GameCatalog(game);
        var skel = Skeleton.Load(Path.Combine(game, "pac", "ch", "ch125.pac"));
        var un = cat.Copies(id, x, y).First(m => m.Pack == null && m.Kind == "YMKs" && !m.Bank.StartsWith("MOTP"));
        var pk = cat.Copies(id, x, y).First(m => m.Pack == null && m.Bank.StartsWith("MOTP"));
        var a = Track.Decode(cat.Data(un), cat.T(un), false, un.Frames);
        var b = Track.Decode(cat.Data(pk), cat.T(pk), pk.Packed, pk.Frames);
        Console.WriteLine($"{un.Bank} vs {pk.Bank} (packed {pk.Packed}): {a.Frames.Count} / {b.Frames.Count} frames");
        for (int f = 0; f < Math.Min(a.Frames.Count, b.Frames.Count); f += 10)
        {
            var pa = skel.Fk(a.Frames[f]); var pb = skel.Fk(b.Frames[f]);
            double e = pa.Zip(pb, (p, q) => (p - q).Length).Max();
            var ra = a.Frames[f].Rot[0] ?? new V3(0, 0, 0); var rb = b.Frames[f].Rot[0] ?? new V3(0, 0, 0);
            Console.WriteLine($"f{f,4} joint err {e,6:0.00}  root rot m.pac ({ra.X * 57.3,6:0} {ra.Y * 57.3,6:0} {ra.Z * 57.3,6:0})  motp ({rb.X * 57.3,6:0} {rb.Y * 57.3,6:0} {rb.Z * 57.3,6:0})  root pos ({a.Frames[f].Root.X,5:0.0} {a.Frames[f].Root.Z,5:0.0}) / ({b.Frames[f].Root.X,5:0.0} {b.Frames[f].Root.Z,5:0.0})");
        }
        return 0;
    }
}

/// <summary>--stripinner game packdir id: removes ops 79 / 82 (end position / end adjust) that are not at a track's end
/// from a saved move's person tracks (y 0 / 1, m.pac and MOTP copies) - inside a splice they re-anchored the wrestler</summary>
static class StripInner
{
    public static int Run(string game, string dir, int id)
    {
        var cat = new GameCatalog(game);
        var T = cat.T(cat.Best(5109, 0, 0)!);
        foreach (var file in Directory.GetFiles(Path.Combine(dir, "motions"), "*.ymk"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            bool packed = name.StartsWith("motp_");
            var parts = (packed ? name[5..] : name).Split('_');
            if (parts.Length != 3 || parts[0] != id.ToString() || (parts[2] != "0" && parts[2] != "1")) continue;
            var data = File.ReadAllBytes(file);
            var recs = Ymks.Records(data, out int n);
            int removed = 0;
            var outRecs = new System.Collections.Generic.List<(int, byte[])>();
            foreach (var (f, rec) in recs)
            {
                var (ev, pose) = Ymks.Split(rec, packed);
                var evs = Ymks.Events(ev, T);
                var keep = evs.Where(e => !((e.Op == 79 || e.Op == 82) && f < n - 15)).ToList();
                removed += evs.Count - keep.Count;
                var nev = keep.SelectMany(e => e.Data).ToArray();
                if (nev.Length == 0 && pose.Length == 0) continue;
                outRecs.Add((f, YmksWriter.Join(nev, pose, packed)));
            }
            if (removed == 0) { Console.WriteLine($"{name}: nothing to remove"); continue; }
            var o = YmksWriter.Stream(outRecs, n);
            var back = Track.Decode(o, T, packed, 0);
            var orig = Track.Decode(data, T, packed, 0);
            double worst = 0;
            for (int f = 0; f < n; f++) worst = Math.Max(worst, (back.Frames[f].Root - orig.Frames[f].Root).Length);
            if (back.Frames.Count != n || worst > 1e-6) throw new InvalidOperationException($"{name}: poses changed ({worst})");
            File.WriteAllBytes(file, o);
            Console.WriteLine($"{name}: removed {removed} event(s); {n} frames, poses unchanged");
        }
        return 0;
    }
}

/// <summary>--cutcheck game id,id cutFrame: the composition with block 0 cut at cutFrame (second half = Continue) must equal
/// the uncut one on every frame (with a turn / move set on block 0 and 1 to prove those carry over too)</summary>
static class CutCheck
{
    public static int Run(string game, string ids, int cut)
    {
        var cat = new GameCatalog(game);
        var skel = Skeleton.Load(Path.Combine(game, "pac", "ch", "ch125.pac"));
        var comp = new Composer(cat, skel);
        System.Collections.Generic.List<Block> Make() => ids.Split(',').Select((s, i) =>
        {
            var m = cat.Moves[int.Parse(s)]; int x = m.MainPhase, n = m.AttackerAt(x) ?? 1;
            return new Block
            {
                Name = m.Name, Attacker = new LaneRef(m.Id, x, 0, ""), Receiver = m.ReceiverAt(x) != null ? new LaneRef(m.Id, x, 1, "") : null,
                In = 0, Out = n - 1, SourceLength = n, Join = i > 0 ? JoinMode.Crossfade : JoinMode.HardCut, Align = i > 0 ? RootAlign.PlanarGroup : RootAlign.None,
                AttackerTurn = i == 0 ? 30 : 15, AttackerX = 2, ReceiverZ = i == 0 ? -1.5 : 0,
            };
        }).ToList();
        var a = Make(); var b = Make();
        var b0 = b[0]; var b1 = b0.Clone();
        b1.In = cut; b1.Join = JoinMode.HardCut; b1.Align = RootAlign.Continue; b0.Out = cut - 1;
        b.Insert(1, b1);
        var ca = comp.Compose(a); var cb = comp.Compose(b);
        var (A1, R1) = comp.Joints(ca); var (A2, R2) = comp.Joints(cb);
        double worst = 0; int wf = 0;
        for (int f = 0; f < Math.Min(A1.Count, A2.Count); f++)
            foreach (var (p, q) in new[] { (A1[f], A2[f]), (R1[f], R2[f]) })
                if (p != null && q != null)
                {
                    double e = p.Zip(q, (u, v) => (u - v).Length).Max();
                    if (e > worst) { worst = e; wf = f; }
                }
        Console.WriteLine($"frames uncut {ca.Frames}, cut {cb.Frames}; largest joint difference {worst:0.000000} (frame {wf})");
        return 0;
    }
}

/// <summary>--flags game "name"...: the move record's 16 category bytes (and the set bits) for each named move</summary>
static class FlagDump
{
    public static int Run(string game, string[] names)
    {
        var cat = new GameCatalog(game);
        foreach (var q in names)
            foreach (var m in cat.Moves.Values.Where(m => m.Name.Equals(q, StringComparison.OrdinalIgnoreCase) && m.Pack == null))
            {
                var f = cat.Flags(m.Id);
                if (f == null) continue;
                var bits = Enumerable.Range(0, 128).Where(b => (f[b / 8] >> (b % 8) & 1) != 0);
                Console.WriteLine($"{m.Id,6} {m.Name,-28} {MoveCategory.Of(f),-18} bits {string.Join(",", bits)}");
            }
        return 0;
    }
}

/// <summary>--bitmap game: per category bit, how many stock moves and a few names</summary>
static class BitMap
{
    public static int Run(string game)
    {
        var cat = new GameCatalog(game);
        var by = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<string>>();
        foreach (var m in cat.Moves.Values.Where(m => m.Pack == null).OrderBy(m => m.Id))
        {
            var f = cat.Flags(m.Id); if (f == null) continue;
            for (int b = 0; b < 128; b++) if ((f[b / 8] >> (b % 8) & 1) != 0) { if (!by.TryGetValue(b, out var l)) by[b] = l = new(); l.Add(m.Name); }
        }
        foreach (var (b, l) in by.OrderBy(x => x.Key))
            Console.WriteLine($"bit {b,3} n {l.Count,4}: {string.Join(" | ", l.Where((_, i) => i % Math.Max(1, l.Count / 4) == 0).Take(4))}");
        return 0;
    }
}

/// <summary>--placecheck game id,id: (1) a 30 degree turn about a wrestler's clip-start spot keeps that spot; (2) pasting clip 0's
/// whole placement onto clip 1 (line-up off) gives clip 1 the same placement numbers</summary>
static class PlaceCheck
{
    public static int Run(string game, string ids)
    {
        var cat = new GameCatalog(game);
        var skel = Skeleton.Load(Path.Combine(game, "pac", "ch", "ch125.pac"));
        var comp = new Composer(cat, skel);
        var blocks = ids.Split(',').Select((s, i) =>
        {
            var m = cat.Moves[int.Parse(s)]; int x = m.MainPhase, n = m.AttackerAt(x) ?? 1;
            return new Block
            {
                Name = m.Name, Attacker = new LaneRef(m.Id, x, 0, ""), Receiver = m.ReceiverAt(x) != null ? new LaneRef(m.Id, x, 1, "") : null,
                In = 0, Out = n - 1, SourceLength = n, Join = i > 0 ? JoinMode.Crossfade : JoinMode.HardCut, Align = i > 0 ? RootAlign.PlanarGroup : RootAlign.None,
            };
        }).ToList();
        V3 Spot(Composition c, int i, int lane)
        {
            var (fa, fr) = c.Firsts[i]; var h = skel.Fk(lane == 1 && fr != null ? fr : fa)[skel.RootJoint]; return new V3(h.X, 0, h.Z);
        }
        var c1 = comp.Compose(blocks);
        foreach (int lane in new[] { 0, 1 })
        {
            var b = blocks[1]; var c0 = Spot(c1, 1, lane);
            var R = M3.Ry(30 * Math.PI / 180);
            if (lane == 0) { var s = R * new V3(b.AttackerX - c0.X, 0, b.AttackerZ - c0.Z); b.AttackerTurn += 30; b.AttackerX = c0.X + s.X; b.AttackerZ = c0.Z + s.Z; }
            else { var s = R * new V3(b.ReceiverX - c0.X, 0, b.ReceiverZ - c0.Z); b.ReceiverTurn += 30; b.ReceiverX = c0.X + s.X; b.ReceiverZ = c0.Z + s.Z; }
            var c2 = comp.Compose(blocks);
            Console.WriteLine($"turn {(lane == 0 ? "attacker" : "receiver")} 30 deg: his start spot moved {(Spot(c2, 1, lane) - c0).Length:0.0000} units");
            c1 = c2;
        }
        // paste: copy both bodies at clip 0's last frame, fit clip 1's raw first frame onto them
        var (A1, R1) = comp.Joints(c1);
        int f = c1.Spans[0].Length - 1;
        var qa = A1[f]!; var qr = R1[f]!;
        var raw = blocks.Select(x => x.Clone()).ToList();
        raw[1].Align = RootAlign.None; raw[1].AttackerTurn = raw[1].AttackerX = raw[1].AttackerZ = raw[1].ReceiverTurn = raw[1].ReceiverX = raw[1].ReceiverZ = 0;
        var cr = comp.Compose(raw);
        var (fa1, fr1) = cr.Firsts[1];
        var ta = Composer.FitPlanar(skel.Fk(fa1).Select(p => (p, 1.0)).ToList(), qa.ToList());
        var tr = Composer.FitPlanar(skel.Fk(fr1!).Select(p => (p, 1.0)).ToList(), qr.ToList());
        var t = blocks[1];
        t.Align = RootAlign.None; t.AttackerTurn = ta.Yaw * 180 / Math.PI; t.AttackerX = ta.Shift.X; t.AttackerZ = ta.Shift.Z;
        t.ReceiverTurn = tr.Yaw * 180 / Math.PI; t.ReceiverX = tr.Shift.X; t.ReceiverZ = tr.Shift.Z;
        var c3 = comp.Compose(blocks);
        var (pa, pr) = c3.Firsts[1];
        double Hip(V3[] a, V3[] b) { var x = a[skel.RootJoint] - b[skel.RootJoint]; return Math.Sqrt(x.X * x.X + x.Z * x.Z); }
        Console.WriteLine($"paste at frame {f}: clip 1 starts with the attacker's hips {Hip(skel.Fk(pa), qa):0.00} and the receiver's {Hip(skel.Fk(pr!), qr):0.00} units from the copied spots (body fit rms {ta.Rms:0.00} / {tr.Rms:0.00}); turns {t.AttackerTurn:0.0} / {t.ReceiverTurn:0.0} deg");
        return 0;
    }
}

/// <summary>--track2 game: every stock move with a track y 2, by its type, with track 2's op 84 and op 92 at frame 0</summary>
static class Track2
{
    public static int Run(string game)
    {
        var cat = new GameCatalog(game);
        var skel = Skeleton.Load(Path.Combine(game, "pac", "ch", "ch125.pac"));
        var comp = new Composer(cat, skel);
        var rows = new System.Collections.Generic.List<(string Cat, string Line)>();
        foreach (var m in cat.Moves.Values.Where(m => m.Pack == null && m.Tracks.Keys.Any(k => k.Y == 2)).OrderBy(m => m.Id))
            foreach (var x in m.Tracks.Keys.Where(k => k.Y == 2).Select(k => k.X))
            {
                Track t; try { t = comp.Source(m.Id, x, 2); } catch { continue; }
                var e0 = t.Events.Where(e => e.Frame == 0).SelectMany(e => e.Events).Select(e => e.Op).ToList();
                rows.Add((m.Category, $"{m.Id,6} {m.Name,-30} phase {x,3}  {t.Frames.Count,4} f  anchor {t.Anchor?.ToString() ?? "-"}  ops f0: {string.Join(",", e0)}"));
            }
        foreach (var g in rows.GroupBy(r => r.Cat).OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"== {g.Key}: {g.Count()}");
            foreach (var r in g.Take(8)) Console.WriteLine("   " + r.Line);
        }
        return 0;
    }
}

/// <summary>--thirdcheck game id...: does the preview show a third wrestler for these moves</summary>
static class ThirdCheck
{
    public static int Run(string game, string[] ids)
    {
        var cat = new GameCatalog(game);
        var skel = Skeleton.Load(Path.Combine(game, "pac", "ch", "ch125.pac"));
        var comp = new Composer(cat, skel);
        foreach (var s in ids)
        {
            var m = cat.Moves[int.Parse(s)]; int x = m.MainPhase, n = m.AttackerAt(x) ?? 1;
            var c = comp.Compose(new[] { new Block { Name = m.Name, Attacker = new LaneRef(m.Id, x, 0, ""), Receiver = m.ReceiverAt(x) != null ? new LaneRef(m.Id, x, 1, "") : null, In = 0, Out = n - 1, SourceLength = n } });
            Console.WriteLine($"{m.Id,6} {m.Name,-28} {m.Category,-16} third wrestler shown: {(c.Partner.Any(p => p != null) ? "yes" : "no")}");
        }
        return 0;
    }
}

/// <summary>--noreceiver game: pickable stock moves with no receiver track in any phase, by type</summary>
static class NoReceiver
{
    public static int Run(string game)
    {
        var cat = new GameCatalog(game);
        var all = cat.Moves.Values.Where(m => m.Pack == null && m.Usable).ToList();
        foreach (var g in all.GroupBy(m => m.Category).OrderBy(g => g.Key))
        {
            var none = g.Where(m => !m.Tracks.Keys.Any(k => k.Y == 1)).ToList();
            Console.WriteLine($"{g.Key,-18} {none.Count,4} of {g.Count(),4} have no receiver track   e.g. {string.Join(" | ", none.Take(4).Select(m => m.Name))}");
        }
        return 0;
    }
}

/// <summary>--banks game: every motion bank with its id range and count (stock, YMKs/YMBs)</summary>
static class BankList
{
    public static int Run(string game)
    {
        var cat = new GameCatalog(game);
        foreach (var b in cat.Banks.OrderBy(b => b))
        {
            var ids = cat.Moves.Values.SelectMany(m => m.Tracks.Keys.Select(k => (m.Id, k.X, k.Y)))
                .Where(k => cat.Copies(k.Id, k.X, k.Y).Any(c => c.Bank == b && c.Pack == null)).Select(k => k.Id).Distinct().OrderBy(i => i).ToList();
            if (ids.Count == 0) continue;
            Console.WriteLine($"{b,-18} {ids.Count,4} moves  ids {ids.First()}..{ids.Last()}  e.g. {string.Join(", ", ids.Where((_, i) => i % Math.Max(1, ids.Count / 5) == 0).Take(6).Select(i => $"{i} {cat.Names.GetValueOrDefault(i) ?? ""}"))}");
        }
        return 0;
    }
}

/// <summary>--exh game id...: each move's EXH records (36 bytes) as hex, with every u16 LE value that is a basic motion id</summary>
static class ExhDump
{
    public static int Run(string game, string[] ids)
    {
        var cat = new GameCatalog(game);
        var skel = Skeleton.Load(Path.Combine(game, "pac", "ch", "ch125.pac"));
        var recs = new MovePack(cat, new Composer(cat, skel)).ReadRecords();
        foreach (var s in ids)
        {
            int id = int.Parse(s);
            foreach (var (g, r) in recs.Exh.GetValueOrDefault(id) ?? new())
            {
                var u16 = Enumerable.Range(0, 18).Select(k => BitConverter.ToUInt16(r, 2 * k)).ToList();
                Console.WriteLine($"{id,6} {cat.Names.GetValueOrDefault(id) ?? "",-20} g{g,-3} {Convert.ToHexString(r)}  u16: {string.Join(" ", u16)}");
            }
        }
        return 0;
    }
}

/// <summary>--rawmotion game id x y [bytes]: a motion's raw data (hex) and its bank kind, for format work</summary>
static class RawMotion
{
    public static int Run(string game, int id, int x, int y, int n)
    {
        var cat = new GameCatalog(game);
        foreach (var m in cat.Copies(id, x, y).Where(c => c.Pack == null))
        {
            var d = cat.Data(m);
            Console.WriteLine($"{m.Bank} kind {m.Kind} frames {m.Frames} bytes {d.Length}");
            for (int o = 0; o < Math.Min(n, d.Length); o += 32)
                Console.WriteLine($"  {o:X4}: {Convert.ToHexString(d.AsSpan(o, Math.Min(32, d.Length - o)))}");
        }
        return 0;
    }
}

/// <summary>--ymbsstats game: size vs frames over YMBs motions (fit size = a + b * frames)</summary>
static class YmbsStats
{
    public static int Run(string game)
    {
        var cat = new GameCatalog(game);
        var pts = new List<(int F, int S, int Id)>();
        foreach (var m in cat.Moves.Values.Where(m => m.Category == "Taunts").Take(400))
            foreach (var ((x, y), fr) in m.Tracks)
            {
                var c = cat.Copies(m.Id, x, y).FirstOrDefault(q => q.Kind == "YMBs" && q.Pack == null);
                if (c == null) continue;
                pts.Add((c.Frames, cat.Data(c).Length, m.Id));
            }
        double n = pts.Count, sx = pts.Sum(p => (double)p.F), sy = pts.Sum(p => (double)p.S), sxx = pts.Sum(p => (double)p.F * p.F), sxy = pts.Sum(p => (double)p.F * p.S);
        double b = (n * sxy - sx * sy) / (n * sxx - sx * sx), a = (sy - b * sx) / n;
        Console.WriteLine($"{pts.Count} YMBs taunt tracks: size ~ {a:0.0} + {b:0.00} * frames");
        foreach (var p in pts.Take(12)) Console.WriteLine($"  {p.Id} frames {p.F} bytes {p.S}  predicted {a + b * p.F:0}");
        return 0;
    }
}

/// <summary>--ymbsheads game id x y: offsets of "?0 vv vv vv 01" headers in a YMBs motion and the gaps between them</summary>
static class YmbsHeads
{
    public static int Run(string game, int id, int x, int y)
    {
        var cat = new GameCatalog(game);
        var c = cat.Copies(id, x, y).First(q => q.Kind == "YMBs" && q.Pack == null);
        var d = cat.Data(c);
        Console.WriteLine($"{c.Bank} frames {c.Frames} bytes {d.Length}  (frames/2 = {c.Frames / 2.0})");
        var heads = new List<int>();
        for (int o = 0; o + 5 <= d.Length; o++)
            if ((d[o] & 0x0F) == 0 && d[o + 4] == 0x01) heads.Add(o);
        int last = -1;
        foreach (var h in heads)
        {
            int v = d[h + 1] | d[h + 2] << 8 | d[h + 3] << 16; if ((v & 0x800000) != 0) v -= 0x1000000;
            Console.WriteLine($"  @{h,5}  gap {(last < 0 ? 0 : h - last),4}  head {Convert.ToHexString(d.AsSpan(h, 5))}  s24 {v,9}");
            last = h;
        }
        return 0;
    }
}

/// <summary>--ymbschan game id x y offset count: one 86-byte block read several ways (cumulative sums)</summary>
static class YmbsChan
{
    public static int Run(string game, int id, int x, int y, int off, int count)
    {
        var cat = new GameCatalog(game);
        var c = cat.Copies(id, x, y).First(q => q.Kind == "YMBs" && q.Pack == null);
        var d = cat.Data(c);
        int n = c.Frames;
        for (int k = 0; k < count; k++)
        {
            int h = off + k * (n + 4);
            if (h + 5 + n - 1 > d.Length) break;
            int v = d[h + 1] | d[h + 2] << 8 | d[h + 3] << 16; if ((v & 0x800000) != 0) v -= 0x1000000;
            var data = d.AsSpan(h + 5, n - 1).ToArray();
            // (a) signed bytes as deltas
            var a = new List<int> { v }; foreach (var b in data) a.Add(a[^1] + (sbyte)b);
            // (b) two signed nibbles per byte (hi, lo) as consecutive deltas
            var bb = new List<int> { 0 }; foreach (var b in data) { int hi = b >> 4, lo = b & 15; if (hi > 7) hi -= 16; if (lo > 7) lo -= 16; bb.Add(bb[^1] + hi); bb.Add(bb[^1] + lo); }
            Console.WriteLine($"@{h} flag {d[h]:X2} v {v} hdr4 {d[h + 4]:X2}");
            Console.WriteLine($"   bytes: {Convert.ToHexString(data.AsSpan(0, Math.Min(40, data.Length)))}");
            Console.WriteLine($"   (a) s8 cum: {string.Join(" ", a.Where((_, i) => i % 6 == 0).Take(14))}");
            Console.WriteLine($"   (b) nib cum: {string.Join(" ", bb.Where((_, i) => i % 12 == 0).Take(14))}");
        }
        return 0;
    }
}

/// <summary>--flagsid game id...: category bits of moves by id</summary>
static class FlagIds
{
    public static int Run(string game, string[] ids)
    {
        var cat = new GameCatalog(game);
        foreach (var s in ids)
        {
            int id = int.Parse(s); var f = cat.Flags(id);
            var bits = f == null ? "-" : string.Join(",", Enumerable.Range(0, 128).Where(b => (f[b / 8] >> (b % 8) & 1) != 0));
            Console.WriteLine($"{id,6} {cat.Names.GetValueOrDefault(id) ?? "",-28} {MoveCategory.Of(f),-16} bits {bits}");
        }
        return 0;
    }
}

/// <summary>--stagecheck game id,id: each move put where the game starts it in the ring (MotionMath.StageOf) - both
/// wrestlers' hips on the mat at the start and the middle, and how near the ropes (the ring edge, 34 model units) they get</summary>
static class StageCheck
{
    public static int Run(string game, string ids)
    {
        var cat = new GameCatalog(game);
        var skel = Skeleton.Load(Path.Combine(game, "pac", "ch", "ch125.pac"));
        var comp = new Composer(cat, skel);
        foreach (var s in ids.Split(','))
        {
            var m = cat.Moves[int.Parse(s)]; int x = m.MainPhase, n = m.AttackerAt(x) ?? 1;
            var c = comp.Compose(new[] { new Block { Name = m.Name, Attacker = new LaneRef(m.Id, x, 0, ""), Receiver = m.ReceiverAt(x) != null ? new LaneRef(m.Id, x, 1, "") : null, In = 0, Out = n - 1, SourceLength = n } });
            var (A, R) = comp.Joints(c);
            var S = M3.Ry(c.Stage.Yaw);
            V3 St(V3 p) => S * p + c.Stage.Shift;
            string Hip(List<V3[]?> L, int f) => f < L.Count && L[f] is V3[] J ? $"({St(J[skel.RootJoint]).X,6:0.0}, {St(J[skel.RootJoint]).Y,5:0.0}, {St(J[skel.RootJoint]).Z,6:0.0})" : "     -       ";
            double Out(List<V3[]?> L) => L.Where(J => J != null).SelectMany(J => J!).Select(St).Max(p => Math.Max(Math.Abs(p.X), Math.Abs(p.Z)));
            Console.WriteLine($"{m.Id,6} {m.Name,-26} {m.Category,-10} stage ({c.Stage.Shift.X,5:0.0}, {c.Stage.Shift.Z,5:0.0}) {c.Stage.Yaw * 180 / Math.PI,4:0}deg | att hips {Hip(A, 0)} mid {Hip(A, c.Frames / 2)} | rec hips {Hip(R, 0)} mid {Hip(R, c.Frames / 2)} | furthest joint: att {Out(A),5:0.0} rec {(R.Any(J => J != null) ? Out(R) : 0),5:0.0}");
        }
        return 0;
    }
}

/// <summary>--opcensus game out.txt [opmap.json]: every event command in every stock YMKs track - how often, in how many
/// moves, on which track (0 attacker, 1 receiver, 2+ others), its byte patterns, and the reader's kind for it (call /
/// inline / skip, from the RE opmap) - the list of commands that must be proven</summary>
static class OpCensus
{
    public static int Run(string game, string outPath, string? opmap)
    {
        var cat = new GameCatalog(game);
        var kind = new Dictionary<int, string>();
        if (opmap != null && File.Exists(opmap))
            foreach (var p in System.Text.Json.JsonDocument.Parse(File.ReadAllText(opmap)).RootElement.GetProperty("ops").EnumerateObject())
                kind[int.Parse(p.Name)] = p.Value.GetProperty("kind").GetString() ?? "";
        var uses = new Dictionary<int, int>();
        var moves = new Dictionary<int, HashSet<int>>();
        var tracks = new Dictionary<int, SortedDictionary<int, int>>();
        var pats = new Dictionary<int, Dictionary<string, (int N, string Ex)>>();
        var sizes = new Dictionary<int, HashSet<int>>();
        var ys = new Dictionary<int, SortedSet<int>>();
        var named = new Dictionary<int, List<string>>();
        var trackOps = new Dictionary<int, (HashSet<int> Moves, Dictionary<int, int> Ops)>();
        int nt = 0, bad = 0;
        foreach (var mv in cat.Moves.Values)
            foreach (var (x, y) in mv.Tracks.Keys)
            {
                var m = cat.Best(mv.Id, x, y);
                if (m == null || m.Pack != null || m.Kind != "YMKs") continue;
                byte[] d, T;
                try { d = cat.Data(m); T = cat.T(m); } catch { bad++; continue; }
                nt++;
                List<(int Frame, byte[] Rec)> recs;
                try { recs = Ymks.Records(d, out _); } catch { bad++; continue; }
                foreach (var (f, rec) in recs)
                {
                    var (ev, _) = Ymks.Split(rec, m.Packed);
                    if (ev.Length == 0) continue;
                    foreach (var (op, b) in Ymks.Events(ev, T))
                    {
                        uses[op] = uses.GetValueOrDefault(op) + 1;
                        (moves.TryGetValue(op, out var s) ? s : moves[op] = new()).Add(mv.Id);
                        var tk = tracks.TryGetValue(op, out var q) ? q : tracks[op] = new();
                        tk[Math.Min(y, 3)] = tk.GetValueOrDefault(Math.Min(y, 3)) + 1;
                        (sizes.TryGetValue(op, out var z) ? z : sizes[op] = new()).Add(b.Length);
                        (ys.TryGetValue(op, out var yy) ? yy : ys[op] = new()).Add(y);
                        if (!mv.Name.StartsWith("Move ") && mv.Category != MoveCategory.Other)
                        {
                            var nl = named.TryGetValue(op, out var nn) ? nn : named[op] = new();
                            var tag = $"{mv.Id} {mv.Name} [{mv.Category}] {x}_{y}";
                            if (nl.Count < 40 && !nl.Any(s2 => s2.StartsWith(mv.Id + " "))) nl.Add(tag);
                        }
                        var to = trackOps.TryGetValue(y, out var tq) ? tq : trackOps[y] = (new(), new());
                        to.Moves.Add(mv.Id); to.Ops[op] = to.Ops.GetValueOrDefault(op) + 1;
                        var key = Convert.ToHexString(b, 1, b.Length - 1);
                        var pd = pats.TryGetValue(op, out var pp) ? pp : pats[op] = new();
                        pd[key] = (pd.GetValueOrDefault(key).N + 1, pd.GetValueOrDefault(key).Ex ?? $"{mv.Id}_{x}_{y} f{f} {mv.Name}");
                    }
                }
            }
        var sb = new StringBuilder();
        sb.AppendLine($"# {nt} stock YMKs tracks read ({bad} unreadable); {uses.Count} different commands used");
        sb.AppendLine("op  reader  bytes  uses   moves  tracks(0/1/2/3+)        patterns  top values (args hex: count  e.g.)");
        foreach (var op in uses.Keys.OrderBy(o => o))
        {
            var tk = tracks[op];
            string tks = string.Join("/", Enumerable.Range(0, 4).Select(i => tk.GetValueOrDefault(i)));
            var top = pats[op].OrderByDescending(p => p.Value.N).Take(4).Select(p => $"{(p.Key.Length == 0 ? "-" : p.Key)}:{p.Value.N} ({p.Value.Ex})");
            sb.AppendLine($"{op,3}  {kind.GetValueOrDefault(op, "?"),-6}  {string.Join(",", sizes[op]),5}  {uses[op],5}  {moves[op].Count,5}  {tks,-22}  {pats[op].Count,6}  y={string.Join(",", ys[op])}  {string.Join(" | ", top)}");
        }
        sb.AppendLine("# per track number: moves that have it, and the commands on it");
        foreach (var (y, (ms, ops)) in trackOps.OrderBy(k => k.Key))
            sb.AppendLine($"y {y,3}: {ms.Count,5} moves  e.g. {string.Join(", ", ms.Take(4))}  ops {string.Join(" ", ops.OrderByDescending(o => o.Value).Select(o => $"{o.Key}x{o.Value}"))}");
        sb.AppendLine("# named moves (Create-a-Moveset lists) per command: id name [list] track");
        foreach (var op in uses.Keys.OrderBy(o => o))
            sb.AppendLine($"{op,3}: " + string.Join("; ", named.GetValueOrDefault(op, new()).Take(12)));
        var never = Enumerable.Range(0, 230).Where(o => !uses.ContainsKey(o)).ToList();
        sb.AppendLine($"# never used in stock moves ({never.Count}): {string.Join(",", never)}");
        File.WriteAllText(outPath, sb.ToString());
        Console.WriteLine($"{uses.Count} commands used, {never.Count} never; {nt} tracks -> {outPath}");
        return 0;
    }
}

/// <summary>--camtracks game out.txt: every move with tracks 50+ (camera tracks: position + look-at per frame), by name</summary>
static class CamTracks
{
    public static int Run(string game, string outPath)
    {
        var cat = new GameCatalog(game);
        var sb = new StringBuilder();
        foreach (var mv in cat.Moves.Values.OrderBy(m => m.Id))
        {
            var cams = mv.Tracks.Keys.Where(k => k.Y >= 50).Select(k => $"{k.X}_{k.Y}").ToList();
            if (cams.Count > 0) sb.AppendLine($"{mv.Id,6} {mv.Name,-34} {mv.Category,-18} {string.Join(" ", cams)}");
        }
        File.WriteAllText(outPath, sb.ToString());
        return 0;
    }
}

/// <summary>--opuses game op [max]: each use of one event command - move, track, frame, its bytes, the track's length and
/// where the first hit (86) / ring impact (96-103) / mat (84) / state 81 events of that track are (to tie bytes to timing)</summary>
static class OpUses
{
    public static int Run(string game, int want, int max)
    {
        var cat = new GameCatalog(game);
        int n = 0;
        foreach (var mv in cat.Moves.Values.OrderBy(m => m.Id))
            foreach (var (x, y) in mv.Tracks.Keys)
            {
                var m = cat.Best(mv.Id, x, y);
                if (m == null || m.Pack != null || m.Kind != "YMKs") continue;
                var T = cat.T(m);
                var recs = Ymks.Records(cat.Data(m), out int nf);
                var ev = recs.Select(r => (r.Frame, Ops: Ymks.Events(Ymks.Split(r.Rec, m.Packed).Ev, T))).Where(e => e.Ops.Count > 0).ToList();
                int First(Func<int, bool> p) => ev.FirstOrDefault(e => e.Ops.Any(o => p(o.Op))).Frame is int f && ev.Any(e => e.Ops.Any(o => p(o.Op))) ? f : -1;
                foreach (var (f, ops) in ev)
                    foreach (var (op, b) in ops.Where(o => o.Op == want))
                    {
                        Console.WriteLine($"{mv.Id,6}_{x}_{y,-3} {mv.Name,-28} f{f,-4} {Convert.ToHexString(b, 1, b.Length - 1),-24} len {nf,4}  hit86 {First(o => o == 86),4}  impact {First(o => o >= 96 && o <= 103),4}  mat84 {First(o => o == 84),4}");
                        if (++n >= max) return 0;
                    }
            }
        return 0;
    }
}

/// <summary>--rootpath game id x y: the root's (x, y, z) per frame of one track (model units; y up is negative)</summary>
static class RootPath
{
    public static int Run(string game, int id, int x, int y)
    {
        var cat = new GameCatalog(game);
        var skel = Skeleton.Load(Path.Combine(game, "pac", "ch", "ch125.pac"));
        var t = new Composer(cat, skel).Source(id, x, y);
        for (int f = 0; f < t.Frames.Count; f++)
        {
            var r = skel.Fk(t.Frames[f])[skel.RootJoint];
            var ev = t.Events.FirstOrDefault(e => e.Frame == f).Events;
            Console.WriteLine($"f{f,-4} ({r.X,7:0.0}, {r.Y,7:0.0}, {r.Z,7:0.0})  {(ev == null ? "" : string.Join(" ", ev.Select(o => o.Op)))}");
        }
        return 0;
    }
}

/// <summary>--evcopies game id x y: the events of every stored copy of one track (banks can hold different versions)</summary>
static class EvCopies
{
    public static int Run(string game, int id, int x, int y)
    {
        var cat = new GameCatalog(game);
        foreach (var m in cat.Copies(id, x, y))
        {
            if (m.Kind != "YMKs") { Console.WriteLine($"== {m.Bank} {m.Kind}"); continue; }
            var T = cat.T(m);
            var recs = Ymks.Records(cat.Data(m), out int nf);
            var lines = recs.Select(r => (r.Frame, Ops: Ymks.Events(Ymks.Split(r.Rec, m.Packed).Ev, T))).Where(e => e.Ops.Count > 0)
                .Select(e => $"f{e.Frame}: " + string.Join(" ", e.Ops.Select(o => Convert.ToHexString(o.Data)))).ToList();
            Console.WriteLine($"== {m.Bank} packed={m.Packed} frames {nf}  ({lines.Count} event frames)");
            foreach (var l in lines) Console.WriteLine("   " + l);
        }
        return 0;
    }
}

/// <summary>--stockexport game outdir: every stock move (packs / mods left out) into outdir\&lt;type&gt;\&lt;id&gt; &lt;name&gt;\:
/// each track as its raw motion file &lt;id&gt;_&lt;phase&gt;_&lt;track&gt;.ymks (the unpacked bank copy, else the packed one, .ymbs for
/// taunts) + info.txt (name, id, type, tracks, bank, frames, events per frame); index.csv lists them all; event_sizes.txt
/// = each bank's command-size table (needed to read the events in the raw files)</summary>
static class StockExport
{
    static string Safe(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim().TrimEnd('.');
    }

    public static int Run(string game, string outDir)
    {
        var cat = new GameCatalog(game);
        Directory.CreateDirectory(outDir);
        var index = new StringBuilder("id,name,type,tracks,folder\n");
        var tables = new SortedDictionary<string, byte[]>();
        int moves = 0, files = 0, failed = 0; long bytes = 0;
        foreach (var mv in cat.Moves.Values.OrderBy(m => m.Id))
        {
            var refs = mv.Tracks.Keys.Select(k => (k, m: cat.Copies(mv.Id, k.X, k.Y).Where(c => c.Pack == null).ToList()))
                .Where(t => t.m.Count > 0).ToList();
            if (refs.Count == 0) continue;   // only in a mod pack
            string dir = Path.Combine(outDir, Safe(mv.Category), Safe($"{mv.Id} {mv.Name}"));
            Directory.CreateDirectory(dir);
            var info = new StringBuilder($"{mv.Name}\nid {mv.Id}\ntype {mv.Category}\n\n");
            foreach (var ((x, y), copies) in refs)
            {
                var m = copies.FirstOrDefault(c => c.Kind == "YMKs" && !c.Packed) ?? copies.FirstOrDefault(c => c.Kind == "YMKs") ?? copies[0];
                string file = $"{mv.Id}_{x}_{y}.{(m.Kind == "YMBs" ? "ymbs" : "ymks")}";
                byte[] d;
                try { d = cat.Data(m); }
                catch (Exception e) { info.AppendLine($"{file}: not exported ({e.Message})"); failed++; continue; }
                File.WriteAllBytes(Path.Combine(dir, file), d);
                files++; bytes += d.Length;
                info.AppendLine($"{file}  phase {x} track {y}{(y == 0 ? " (attacker)" : y == 1 ? " (receiver)" : y == 2 ? " (3rd / alt)" : y >= 50 ? " (camera)" : "")}  " +
                                $"bank {m.Bank}{(m.Packed ? " packed" : "")}  {m.Frames} frames  {d.Length} bytes  (also in: {string.Join(", ", copies.Where(c => c != m).Select(c => c.Bank))})");
                if (m.Kind != "YMKs") continue;
                var T = cat.T(m);
                tables[m.Bank] = T;
                try
                {
                    foreach (var (f, rec) in Ymks.Records(d, out _))
                    {
                        var ev = Ymks.Events(Ymks.Split(rec, m.Packed).Ev, T);
                        if (ev.Count > 0) info.AppendLine($"    f{f}: {string.Join(" ", ev.Select(o => $"[{o.Op}] {Convert.ToHexString(o.Data, 1, o.Data.Length - 1)}"))}");
                    }
                }
                catch { info.AppendLine("    (events not readable)"); }
            }
            File.WriteAllText(Path.Combine(dir, "info.txt"), info.ToString());
            index.AppendLine($"{mv.Id},\"{mv.Name.Replace("\"", "'")}\",{mv.Category},{refs.Count},\"{Path.GetRelativePath(outDir, dir)}\"");
            moves++;
        }
        File.WriteAllText(Path.Combine(outDir, "index.csv"), index.ToString());
        File.WriteAllText(Path.Combine(outDir, "event_sizes.txt"), "Each bank's event size table: byte n = size of event command n (0 = not used).\n\n" +
            string.Join("\n", tables.Select(t => $"{t.Key}: {Convert.ToHexString(t.Value)}")) + "\n");
        Console.WriteLine($"{moves} moves, {files} track files ({bytes / 1048576.0:0.0} MB), {failed} not exported -> {outDir}");
        return 0;
    }
}
