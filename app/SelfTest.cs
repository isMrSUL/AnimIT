using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AnimIT.Core;

namespace AnimIT;

/// <summary>AnimIT.exe --selftest &lt;game folder&gt; &lt;out.json&gt;: the same cases as docs\py_reference.json
/// (the engine's first, Python version, tested against the game's moves) - the C# core must give the same joints.</summary>
static class SelfTest
{
    public static int Run(string game, string outPath)
    {
        var cat = new GameCatalog(game);
        var sk = Skeleton.Load(Path.Combine(game, "pac", "ch", "ch125.pac"));
        var cp = new Composer(cat, sk);
        Block B(int id, int? outF = null, JoinMode j = JoinMode.HardCut, int blend = 0, RootAlign al = RootAlign.None)
        {
            var a = cp.Source(id, 0, 0);
            return new Block { Attacker = new LaneRef(id, 0, 0, ""), Receiver = new LaneRef(id, 0, 1, ""), In = 0,
                               Out = outF ?? a.Frames.Count - 1, Join = j, Blend = blend, Align = al };
        }
        int pk = cat.Moves.Values.Where(m => m.Pack == "hctp_finishers").Select(m => m.Id).First();
        var res = new Dictionary<string, object> { ["moves"] = cat.Moves.Count, ["packmove"] = pk };
        var cases = new (string, List<Block>)[]
        {
            ("stock", new() { B(4984) }), ("packed", new() { B(pk) }),
            ("splice", new() { B(4984, 80), B(5008, null, JoinMode.Crossfade, 8, RootAlign.FollowAttacker) }),
        };
        foreach (var (name, blocks) in cases)
        {
            var c = cp.Compose(blocks);
            var (A, R) = cp.Joints(c);
            double[] Flat(V3[]? p) => p == null ? Array.Empty<double>() : p.Take(4).SelectMany(v => new[] { v.X, v.Y, v.Z }).Select(x => Math.Round(x, 3)).ToArray();
            int f90 = Math.Min(90, c.Frames - 1);
            res[name] = new Dictionary<string, object>
            {
                ["frames"] = c.Frames, ["spans"] = c.Spans.Select(s => new[] { s.Start, s.Length }).ToArray(),
                ["a40"] = Flat(A[40]), ["r40"] = Flat(R[40]), ["a90"] = Flat(A[f90]), ["r90"] = Flat(R[f90]),
            };
        }
        File.WriteAllText(outPath, JsonSerializer.Serialize(res, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
