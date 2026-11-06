using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AnimIT.Core;

namespace AnimIT;

/// <summary>--anchorfit game out.txt: moves anchored by op 84 (attacker 84 04, others 84 05) have no op 92. For every such stock
/// move, try placements of the receiver (K ahead, turned D) and score how well the attacker's hands touch the receiver's body
/// (median over frames of the closest hand-to-joint distance; a grapple's hands are on the opponent) - the placement the game
/// uses should make contact (~1-2 units) where a wrong one leaves the hands in the air.</summary>
static class AnchorFit
{
    static int? Op84(Track t) { foreach (var (f, evs) in t.Events) { if (f > 0) break; foreach (var (op, d) in evs) if (op == 84 && d.Length > 1) return d[1]; } return null; }

    public static int Run(string game, string outPath)
    {
        var cat = new GameCatalog(game);
        var skel = Skeleton.Load(Path.Combine(game, "pac", "ch", "ch125.pac"));
        var comp = new Composer(cat, skel);
        int[] hands = { skel.IndexOf("l_te"), skel.IndexOf("r_te") };
        var Ks = new[] { -10.0, -5, -2.5, 0, 2.5, 5, 10, 15 };
        var Ds = new[] { 0.0, Math.PI / 2, Math.PI, -Math.PI / 2 };
        var sb = new StringBuilder();
        var tally = new Dictionary<(double, double), int>();
        var bestDists = new List<double>();
        int n = 0;
        foreach (var m in cat.Moves.Values.Where(m => m.Pack == null && m.Usable).OrderBy(m => m.Id))
        {
            foreach (int x in m.Phases.Where(x => m.Tracks.ContainsKey((x, 1))))
            {
                Track a, r;
                try { a = comp.Source(m.Id, x, 0); r = comp.Source(m.Id, x, 1); } catch { continue; }
                if (Op84(a) != 4 || Op84(r) != 5 || a.Start != null) continue;
                int frames = Math.Min(a.Frames.Count, r.Frames.Count);
                if (frames < 10) continue;
                var A = Enumerable.Range(0, frames).Where(f => f % 3 == 0).Select(f => skel.Fk(a.Frames[f])).ToList();
                var R = Enumerable.Range(0, frames).Where(f => f % 3 == 0).Select(f => skel.Fk(r.Frames[f])).ToList();
                (double K, double D, double S) best = (0, 0, 1e9);
                var scores = new List<string>();
                foreach (var K in Ks)
                    foreach (var D in Ds)
                    {
                        var Rm = M3.Ry(D);
                        var d = new List<double>();
                        for (int k = 0; k < A.Count; k++)
                        {
                            double mn = 1e9;
                            foreach (int h in hands)
                                foreach (var q in R[k]) { var w = Rm * q + new V3(0, 0, -K); mn = Math.Min(mn, (A[k][h] - w).Length); }
                            d.Add(mn);
                        }
                        d.Sort();
                        double s = d[d.Count / 4];          // the closest quarter: contact at some point of the move
                        if (s < best.S) best = (K, D, s);
                    }
                tally[(best.K, best.D)] = tally.GetValueOrDefault((best.K, best.D)) + 1;
                bestDists.Add(best.S);
                n++;
                sb.AppendLine($"{m.Id,6} {x,3} {m.Name,-34} best K {best.K,5} D {best.D * 180 / Math.PI,5:0}  hand gap {best.S:0.00}");
            }
        }
        sb.Insert(0, $"{n} op-84 move phases\nbest placement counts:\n" + string.Join("\n", tally.OrderByDescending(t => t.Value).Select(t => $"  K {t.Key.Item1,5} D {t.Key.Item2 * 180 / Math.PI,5:0}: {t.Value}")) +
                     $"\nhand gap at best: median {bestDists.OrderBy(v => v).ElementAtOrDefault(bestDists.Count / 2):0.00}\n\n");
        File.WriteAllText(outPath, sb.ToString());
        return 0;
    }
}
