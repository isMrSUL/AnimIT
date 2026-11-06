using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AnimIT.Core;

namespace AnimIT;

/// <summary>--placecap game log...: the logger's P lines (each fighter's facing / origin at a motion's start, ~1 s and ~2 s in)
/// -> for every paired move (attacker track 0 + receiver track 1 of the same move and phase, started together): where the
/// receiver's origin is relative to the attacker's (attacker faces -z) and how he faces, next to AnimIT's placement.</summary>
static class PlaceCapture
{
    sealed record P(uint Clk, int Slot, int Id, int Ph, int Tr, uint In, double Face, double X, double Z);

    public static int Run(string game, string[] logs)
    {
        var cat = new GameCatalog(game);
        var skel = Skeleton.Load(Path.Combine(game, "pac", "ch", "ch125.pac"));
        var comp = new Composer(cat, skel);
        var rows = new List<(int Id, int Ph, uint In, double X, double Z, double D)>();
        foreach (var log in logs)
        {
            var ps = new List<P>();
            foreach (var line in File.ReadLines(log))
            {
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 8 || f[0] != "P") continue;
                var v = f[7].Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                ps.Add(new P(uint.Parse(f[1]), int.Parse(f[2]), int.Parse(f[3]), int.Parse(f[4]), int.Parse(f[5]), uint.Parse(f[6]), v[0], v[1], v[3]));
            }
            foreach (var a in ps.Where(p => p.Tr == 0 && p.In >= 50))
            {
                // the receiver: same move and phase, track 1, the same "ticks in" mark, started within a few ticks
                var r = ps.FirstOrDefault(p => p.Tr == 1 && p.Id == a.Id && p.Ph == a.Ph && p.Slot != a.Slot && p.In == a.In && Math.Abs((long)p.Clk - a.Clk) <= 6);
                if (r == null) continue;
                double c = Math.Cos(-a.Face), s = Math.Sin(-a.Face), dx = r.X - a.X, dz = r.Z - a.Z;
                double rx = dx * c + dz * s, rz = -dx * s + dz * c;
                double d = ((r.Face - a.Face) * 180 / Math.PI + 540) % 360 - 180;
                rows.Add((a.Id, a.Ph, a.In, rx, rz, d));
            }
        }
        Console.WriteLine("move_phase  name                         type             ticks | receiver at (x, z)  facing | AnimIT now: K, D   (start)");
        foreach (var g in rows.GroupBy(r => (r.Id, r.Ph)).OrderBy(g => g.Key.Id))
        {
            var mv = cat.Moves.GetValueOrDefault(g.Key.Id);
            string start = "?"; (double K, double D) kd = (0, 0);
            try { var t = comp.Source(g.Key.Id, g.Key.Ph, 0); kd = MotionMath.PlacementOf(t); start = MotionMath.StartNameOf(t) + (t.Start is int st ? $" (op92 {st})" : t.Anchor is int an ? $" (op84 {an})" : ""); } catch { }
            foreach (var r in g.GroupBy(x => x.In).OrderBy(x => x.Key))
            {
                var l = r.ToList();
                Console.WriteLine($"{g.Key.Id,6}_{g.Key.Ph,-3} {(mv?.Name ?? ""),-28} {(mv?.Category ?? ""),-16} {r.Key,4} | ({l.Average(x => x.X),6:0.00}, {l.Average(x => x.Z),6:0.00})  {l.Average(x => x.D),6:0}  x{l.Count} | K {kd.K,4:0.#} D {kd.D * 180 / Math.PI,4:0}  {start}");
            }
        }
        return 0;
    }
}
