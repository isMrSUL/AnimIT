using System;
using System.IO;
using System.Linq;
using System.Text;
using AnimIT.Core;

namespace AnimIT;

/// <summary>--study game out.txt "name" ...: every track of the named moves with its start class, events and root path
/// (research for the placement of moves that aren't plain front / rear / ground grapples)</summary>
static class Study
{
    public static int Run(string game, string outPath, string[] names)
    {
        var cat = new GameCatalog(game);
        var skel = Skeleton.Load(Path.Combine(game, "pac", "ch", "ch125.pac"));
        var comp = new Composer(cat, skel);
        var sb = new StringBuilder();
        foreach (var q in names)
        {
            var hits = cat.Moves.Values.Where(m => m.Name.Equals(q, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0) hits = cat.Moves.Values.Where(m => m.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(6).ToList();
            sb.AppendLine($"=== {q}: {hits.Count} hit(s)");
            foreach (var m in hits)
            {
                sb.AppendLine($"--- move {m.Id} '{m.Name}' usable {m.Usable} pack {m.Pack ?? "-"} tracks {string.Join(" ", m.Tracks.Select(t => $"{t.Key.X}_{t.Key.Y}:{t.Value}f"))}");
                foreach (var ((x, y), _) in m.Tracks)
                {
                    Track t;
                    try { t = comp.Source(m.Id, x, y); } catch (Exception e) { sb.AppendLine($"  {x}_{y}: {e.Message}"); continue; }
                    int n = t.Frames.Count;
                    sb.AppendLine($"  track {m.Id}_{x}_{y} bank {t.Bank} packed {t.Packed} frames {n} start {t.Start?.ToString() ?? "-"}");
                    foreach (int f in new[] { 0, n / 4, n / 2, 3 * n / 4, n - 1 }.Distinct().Where(f => f >= 0 && f < n))
                    {
                        var p = t.Frames[f];
                        sb.AppendLine($"    f{f,4} root ({p.Root.X,7:0.00} {p.Root.Y,7:0.00} {p.Root.Z,7:0.00}) heading {MotionMath.Heading(p, skel.RootRest) * 180 / Math.PI,7:0.0}");
                    }
                    foreach (var (f, evs) in t.Events)
                        sb.AppendLine($"    ev f{f,4}: " + string.Join("  ", evs.Select(e => $"{e.Op}[{Convert.ToHexString(e.Data)}]")));
                }
            }
        }
        File.WriteAllText(outPath, sb.ToString());
        return 0;
    }
}
