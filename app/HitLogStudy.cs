using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AnimIT.Core;

namespace AnimIT;

/// <summary>--hitlog game hitlog.txt out.txt: the in-game hit log (research\hitlog: M = a fighter's motion changed,
/// H = the hit function counted a hit) -> per hit: the strike (attacker motion, phase, frame of the hit), the reaction the
/// receiver switched to (the next motions he plays) and where he stood / faced relative to the attacker. Then per strike:
/// do its hits always give the same reaction?</summary>
static class HitLogStudy
{
    sealed record M(uint Clk, int Slot, int Id, int Ph, int Tr);
    sealed record H(uint Clk, int Victim, int Att, int AttMove, double[] V, double[]? A, int[]? AMot, int[] VMot);

    static double[] Nums(string s) => s.Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();

    public static int Run(string game, string log, string outPath)
    {
        var cat = new GameCatalog(game);
        var ms = new List<M>(); var hs = new List<H>();
        foreach (var line in File.ReadLines(log))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 6 && f[0] == "M") ms.Add(new M(uint.Parse(f[1]), int.Parse(f[2]), int.Parse(f[3]), int.Parse(f[4]), int.Parse(f[5])));
            else if (f.Length >= 6 && f[0] == "H")
            {
                var kv = f.Skip(6).Select(x => x.Split('=')).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1]);
                hs.Add(new H(uint.Parse(f[1]), int.Parse(f[2]), int.Parse(f[3]), int.Parse(f[4]), Nums(kv["V"]),
                    kv.TryGetValue("A", out var a) ? Nums(a) : null,
                    kv.TryGetValue("amot", out var am) ? am.Split(',').Select(int.Parse).ToArray() : null,
                    kv["vmot"].Split(',').Select(int.Parse).ToArray()));
            }
        }
        var sb = new StringBuilder();
        sb.AppendLine($"{ms.Count} motion changes, {hs.Count} hits");
        // clock rate: motions that played to their end (the next change of that fighter = start + length)
        var rate = new List<double>();
        foreach (var g in ms.GroupBy(m => m.Slot))
        {
            var l = g.ToList();
            for (int i = 0; i + 1 < l.Count; i++)
                if (cat.Moves.TryGetValue(l[i].Id, out var mv) && mv.Tracks.TryGetValue((l[i].Ph, l[i].Tr), out int n) && n > 20)
                {
                    double d = l[i + 1].Clk - l[i].Clk;
                    if (d > 0) rate.Add(d / n);
                }
        }
        rate.Sort();
        double perFrame = rate.Count > 0 ? rate[rate.Count / 2] : 1;
        sb.AppendLine($"clock ticks per motion frame (median of {rate.Count} motions): {perFrame:0.000}");
        sb.AppendLine();
        var rows = new List<(int Move, int Ph, string Name, double Frame, string React, double X, double Z, double Df, string VState)>();
        foreach (var h in hs)
        {
            if (h.AMot == null || h.A == null) continue;
            // the attacker's motion start: his last change to this motion before the hit
            var start = ms.LastOrDefault(m => m.Slot == h.Att && m.Clk <= h.Clk && m.Id == h.AMot[0] && m.Ph == h.AMot[1]);
            double frame = start == null ? -1 : (h.Clk - start.Clk) / perFrame;
            // the receiver's motions right after the hit (within ~1 s)
            var after = ms.Where(m => m.Slot == h.Victim && m.Clk >= h.Clk && m.Clk <= h.Clk + 30 * perFrame).Take(3).ToList();
            string react = after.Count == 0 ? "(no change)" : string.Join(" > ", after.Select(m => $"{m.Id}_{m.Ph}_{m.Tr}@{(m.Clk - h.Clk) / perFrame:0}"));
            // receiver relative to attacker: position in the attacker's frame (y up, the attacker faces -z) and facing difference
            double fa = h.A[0], dx = h.V[1] - h.A[1], dz = h.V[3] - h.A[3];
            double c = Math.Cos(-fa), s = Math.Sin(-fa);
            double rx = dx * c + dz * s, rz = -dx * s + dz * c;
            double df = ((h.V[0] - fa) * 180 / Math.PI + 540) % 360 - 180;
            string name = cat.Names.GetValueOrDefault(h.AMot[0]) ?? "";
            rows.Add((h.AMot[0], h.AMot[1], name, frame, react, rx, rz, df, $"{h.VMot[0]}_{h.VMot[1]}_{h.VMot[2]}"));
        }
        sb.AppendLine("strike (attacker motion)                    hits | frame of hit | receiver before -> reaction (id_phase_track@frames after) | receiver at (x, z) facing");
        foreach (var g in rows.GroupBy(r => (r.Move, r.Ph)).OrderByDescending(g => g.Count()))
        {
            var reacts = g.GroupBy(r => r.React.Split(" > ")[0].Split('@')[0]).OrderByDescending(x => x.Count()).Select(x => $"{x.Key} x{x.Count()}");
            var cat0 = cat.Moves.GetValueOrDefault(g.Key.Move)?.Category ?? "?";
            sb.AppendLine($"{g.Key.Move,6}_{g.Key.Ph,-3} {g.First().Name,-26} {cat0,-16} {g.Count(),3} | frames {string.Join(",", g.Select(r => r.Frame.ToString("0")).Distinct().Take(6))} | {string.Join("  ", reacts.Take(4))}");
            foreach (var r in g.Take(4))
                sb.AppendLine($"        before {r.VState,-14} -> {r.React,-46} at ({r.X,6:0.0}, {r.Z,6:0.0}) facing {r.Df,6:0}");
        }
        // by hit type (the strike's EXH byte 0x10, phase-matched record first): the generic reaction = the receiver's motion
        // at the hit when it isn't the strike's own receiver track. One line per hit (the game counts each hit twice).
        var skel = Skeleton.Load(Path.Combine(game, "pac", "ch", "ch125.pac"));
        var recs = new MovePack(cat, new Composer(cat, skel)).ReadRecords();
        int? TypeOf(int move, int ph)
        {
            var l = recs.Exh.GetValueOrDefault(move);
            if (l == null || l.Count == 0) return null;
            var r = l.FirstOrDefault(x => x.R[2] == ph).R ?? l[0].R;
            return r[0x10];
        }
        sb.AppendLine();
        sb.AppendLine("hit type (EXH +0x10) | strikes | generic reactions (receiver motion at the hit) with the receiver's facing vs the attacker");
        var once = new List<H>();
        foreach (var h in hs) if (!once.Any(o => o.Victim == h.Victim && Math.Abs((long)o.Clk - h.Clk) <= 3)) once.Add(h);
        var gen = once.Where(h => h.AMot != null && h.A != null && h.VMot[0] != h.AMot[0]).Select(h =>
        {
            double df = ((h.V[0] - h.A![0]) * 180 / Math.PI + 540) % 360 - 180;
            return (Type: TypeOf(h.AMot![0], h.AMot[1]), Move: h.AMot[0], React: $"{h.VMot[0]}_{h.VMot[1]}_{h.VMot[2]}", Face: df);
        }).ToList();
        foreach (var g in gen.GroupBy(x => x.Type).OrderBy(g => g.Key))
            sb.AppendLine($"  type {g.Key,3} | {string.Join(", ", g.Select(x => (cat.Names.GetValueOrDefault(x.Move) ?? x.Move.ToString())).Distinct())} | " +
                          string.Join("  ", g.GroupBy(x => x.React).Select(r => $"{r.Key} x{r.Count()} (facing {string.Join("/", r.Select(x => x.Face.ToString("0")).Distinct())})")));
        File.WriteAllText(outPath, sb.ToString());
        return 0;
    }
}

/// <summary>--hitfront game log...: per strike, its EXH bytes 3..0x23 and the generic reactions of FRONT hits
/// (receiver facing the attacker within 45 degrees) - to find which strike byte picks the front reaction</summary>
static class HitFront
{
    public static int Run(string game, string[] logs)
    {
        var cat = new GameCatalog(game);
        var skel = Skeleton.Load(Path.Combine(game, "pac", "ch", "ch125.pac"));
        var recs = new MovePack(cat, new Composer(cat, skel)).ReadRecords();
        var rows = new List<(int Move, int Ph, string React, double Face)>();
        foreach (var log in logs)
        {
            var seen = new List<(uint, int)>();
            foreach (var line in File.ReadLines(log))
            {
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 6 || f[0] != "H") continue;
                uint clk = uint.Parse(f[1]); int v = int.Parse(f[2]);
                if (seen.Any(s => s.Item2 == v && Math.Abs((long)s.Item1 - clk) <= 3)) continue;
                seen.Add((clk, v));
                var kv = f.Skip(6).Select(x => x.Split('=')).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1]);
                if (!kv.ContainsKey("A") || !kv.ContainsKey("amot")) continue;
                var V = kv["V"].Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                var A = kv["A"].Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                var am = kv["amot"].Split(',').Select(int.Parse).ToArray(); var vm = kv["vmot"].Split(',').Select(int.Parse).ToArray();
                if (vm[0] == am[0]) continue;
                double df = ((V[0] - A[0]) * 180 / Math.PI + 540) % 360 - 180;
                rows.Add((am[0], am[1], $"{vm[0]}_{vm[1]}_{vm[2]}", df));
            }
        }
        foreach (var g in rows.GroupBy(r => (r.Move, r.Ph)).OrderBy(g => g.Key.Move))
        {
            var l = recs.Exh.GetValueOrDefault(g.Key.Move);
            var r = l?.FirstOrDefault(x => x.R[2] == g.Key.Ph).R ?? l?.FirstOrDefault().R;
            string hex = r == null ? "-" : Convert.ToHexString(r.AsSpan(3, 0x21));
            var front = g.Where(x => Math.Abs(x.Face) > 135).GroupBy(x => x.React).Select(x => $"{x.Key} x{x.Count()}");
            var other = g.Where(x => Math.Abs(x.Face) <= 135).GroupBy(x => x.React).Select(x => $"{x.Key}@{string.Join("/", x.Select(y => y.Face.ToString("0")))}");
            Console.WriteLine($"{g.Key.Move,6}_{g.Key.Ph} {cat.Names.GetValueOrDefault(g.Key.Move) ?? "",-24} {hex}\n        front: {string.Join("  ", front)}\n        other: {string.Join("  ", other)}");
        }
        return 0;
    }
}
