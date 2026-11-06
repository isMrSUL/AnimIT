using System;
using System.IO;
using System.Linq;
using AnimIT.Core;

namespace AnimIT;

/// <summary>--floor game id...: the lowest joint (game y, down = +) per track over the move: where the mat is</summary>
static class FloorCheck
{
    public static int Run(string game, string[] ids)
    {
        var cat = new GameCatalog(game);
        var skel = Skeleton.Load(Path.Combine(game, "pac", "ch", "ch125.pac"));
        var comp = new Composer(cat, skel);
        foreach (var s in ids)
        {
            var m = cat.Moves[int.Parse(s)];
            foreach (var ((x, y), _) in m.Tracks.Where(t => t.Key.Y <= 1))
            {
                var t = comp.Source(m.Id, x, y);
                var low = t.Frames.Select(p => skel.Fk(p).Max(q => q.Y)).ToList();   // y down: the largest y is the lowest point
                Console.WriteLine($"{m.Id}_{x}_{y} {m.Name}: lowest joint y  frame0 {low[0]:0.00}  min {low.Min():0.00}  max {low.Max():0.00}  median {low.OrderBy(v => v).ElementAt(low.Count / 2):0.00}");
            }
        }
        return 0;
    }
}
