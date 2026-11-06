using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AnimIT.Core;

namespace AnimIT;

static class PorterCheck
{
    /// <summary>--stagedump game names.txt: for each "group|move name" line, every move of that name: id, type, start
    /// (op 92), anchor (op 84), list flags, tracks, the attacker's first root and the stage the viewer puts it on</summary>
    /// <summary>--savecheck game "moveA;moveB" donor: splice two moves, move the first hit +5 frames, save with that donor into
    /// a throwaway pack, validate it, read its events back (the hit must be at its new frame) and delete the pack</summary>
    public static int SaveCheck(string game, string names, int donor)
    {
        var cat = new GameCatalog(game);
        var comp = new Composer(cat, Skeleton.Load(Path.Combine(game, "pac", "ch", "ch125.pac")));
        var pack = new MovePack(cat, comp);
        var ms = names.Split(';').Select(n => cat.Moves.Values.First(m => m.Pack == null && string.Equals(m.Name, n.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();
        var bl = ms.Select((m, i) => new Block
        {
            Name = m.Name, Attacker = new LaneRef(m.Id, m.MainPhase, 0, ""),
            Receiver = m.ReceiverAt(m.MainPhase) != null ? new LaneRef(m.Id, m.MainPhase, 1, "") : null,
            In = 0, Out = (m.AttackerAt(m.MainPhase) ?? 1) - 1, SourceLength = m.AttackerAt(m.MainPhase) ?? 1,
            Join = i == 0 ? JoinMode.HardCut : JoinMode.Smooth, Blend = 10, Align = i == 0 ? RootAlign.None : RootAlign.PlanarGroup,
        }).ToList();
        var c = comp.Compose(bl);
        var recs = pack.ReadRecords();
        var evs = pack.Events(bl, c, recs);
        var hit = evs.First(e => !e.Sound && e.Op is 86 or (>= 32 and <= 36));
        bl[hit.Block].EventShift[hit.Key] = 5;
        Console.WriteLine($"{evs.Count} events ({evs.Count(e => e.Sound)} sounds); hit op {hit.Op} lane {hit.Lane} from clip {hit.Block + 1} at {hit.Base} -> {hit.Base + 5}");
        string dir = pack.PackDir("animit_savecheck");
        try
        {
            var r = pack.Save(bl, "AnimIT save check", donor, "animit_savecheck");
            Console.WriteLine($"saved id {r.Id} donor {r.Donor} phase {r.Phase} list {r.Category}");
            foreach (var n in r.Notes) Console.WriteLine("  - " + n);
            foreach (var l in File.ReadLines(Path.Combine(dir, "pack.txt")).Where(l => l.StartsWith("waze"))) Console.WriteLine("  " + l);
            var cat2 = new GameCatalog(game);
            AnimationPorter.Validate(cat, dir, new[] { r.Id });
            Console.WriteLine("validate: pack OK");
            var back = PackEvents.Load(cat2, dir, r.Id, r.Phase);
            bool found = back.Events.Any(e => !e.IsSound && e.Track == hit.Lane && e.Op == hit.Op && e.Frame == hit.Base + 5);
            Console.WriteLine($"read back: {back.Events.Count} events, frames {back.Frames[0]}/{back.Frames[1]}; moved hit at {hit.Base + 5}: {(found ? "YES" : "NO")}");
            return found ? 0 : 1;
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    /// <summary>--leadincheck game "id:x;id:x;...": compose the clips (line-up Auto) and print, at each clip's first frame,
    /// the angle between where the attacker faces and where the receiver is, their distance, and how far the receiver
    /// jumps at the join</summary>
    public static int LeadInCheck(string game, string spec)
    {
        var cat = new GameCatalog(game);
        var skel = Skeleton.Load(Path.Combine(game, "pac", "ch", "ch125.pac"));
        var comp = new Composer(cat, skel);
        var bl = spec.Split(';').Select((t, i) =>
        {
            var q = t.Split(':'); int id = int.Parse(q[0]), x = int.Parse(q[1]);
            var m = cat.Moves[id];
            return new Block
            {
                Name = $"{m.Name} {x}", Attacker = new LaneRef(id, x, 0, ""), Receiver = m.ReceiverAt(x) != null ? new LaneRef(id, x, 1, "") : null,
                In = 0, Out = (m.AttackerAt(x) ?? 1) - 1, Join = i == 0 ? JoinMode.HardCut : JoinMode.Smooth, Blend = 10,
                Align = i == 0 ? RootAlign.None : RootAlign.PlanarGroup, Reaction = -1,
            };
        }).ToList();
        var c = comp.Compose(bl);
        var (A, R) = comp.Joints(c);
        for (int i = 0; i < bl.Count; i++)
        {
            int f = c.Spans[i].Start;
            foreach (int g in new[] { f, Math.Min(c.Frames - 1, f + 12) })
            {
                if (A[g] is not V3[] a || R[g] is not V3[] r) continue;
                var pa = c.Attacker[g]!;
                double h = MotionMath.Heading(pa, skel.RootRest);
                V3 d = r[0] - a[0];
                double to = Math.Atan2(d.X, d.Z), diff = Math.Abs(((to - h) * 180 / Math.PI + 540) % 360 - 180);
                string jump = g == f && f > 0 && R[f - 1] is V3[] r0 && A[f - 1] is V3[] a0 ? $"  receiver jump {(r[0] - r0[0]).Length:0.0}, attacker jump {(a[0] - a0[0]).Length:0.0}" : "";
                Console.WriteLine($"clip {i + 1} {bl[i].Name,-24} frame {g,4}: attacker faces {diff,5:0} deg off the receiver, distance {new V3(d.X, 0, d.Z).Length,5:0.0}{jump}");
            }
        }
        return 0;
    }

    /// <summary>--previewbanks game: per move type, the preview (MVMT/SPE..) banks of the stock moves' main tracks, for
    /// moves in a list and for hidden ones (grapple byte 0 = 00)</summary>
    public static int PreviewBanks(string game)
    {
        var cat = new GameCatalog(game);
        foreach (var g in cat.Moves.Values.Where(m => m.Pack == null && cat.Usable(m.Id) && m.Phases.Any()).GroupBy(m => m.Category).OrderBy(g => g.Key))
        {
            foreach (bool hidden in new[] { false, true })
            {
                var ms = g.Where(m => (cat.Flags(m.Id)![0] == 0 && cat.Flags(m.Id)![3] is 4 or 8) == hidden).ToList();
                if (ms.Count == 0) continue;
                var tally = new Dictionary<string, int>();
                int none = 0;
                foreach (var m in ms)
                {
                    var spe = cat.Copies(m.Id, m.MainPhase, 0).Select(q => q.Bank).Where(bk => bk.StartsWith("MVMT/SPE")).Distinct().ToList();
                    if (spe.Count == 0) none++;
                    foreach (var bk in spe) tally[bk] = tally.GetValueOrDefault(bk) + 1;
                }
                Console.WriteLine($"{g.Key,-22} {(hidden ? "hidden" : "listed"),-7} {ms.Count,4} moves: no preview bank {none,4}; " +
                                  string.Join(", ", tally.OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{kv.Key} {kv.Value}")));
            }
        }
        return 0;
    }

    /// <summary>--moveinfo game ids: name, type, pack, list flags and phases of each move id</summary>
    public static int MoveInfo(string game, string ids)
    {
        var cat = new GameCatalog(game);
        foreach (var id in ids.Split(',').Select(int.Parse))
        {
            var m = cat.Moves.GetValueOrDefault(id);
            var f = cat.Flags(id);
            Console.WriteLine($"{id,5} {cat.Names.GetValueOrDefault(id) ?? "-",-34} {m?.Category ?? "-",-20} pack={m?.Pack ?? "-"} usable={m?.Usable} " +
                              $"flags={(f != null ? Convert.ToHexString(f) : "-")} stockdonor={cat.StockDonor(id)} tracks={(m != null ? string.Join(" ", m.Tracks.OrderBy(k => k.Key).Select(k => $"{k.Key.Item1}_{k.Key.Item2}:{k.Value}")) : "-")}");
        }
        return 0;
    }

    /// <summary>--joincheck game "moveA;moveB": splice two moves with each join mode and print how fast the attacker's
    /// joints move around the join (the biggest jump per frame; a pop shows as a spike) and how long a pose stands still</summary>
    public static int JoinCheck(string game, string names)
    {
        var cat = new GameCatalog(game);
        var skel = Skeleton.Load(Path.Combine(game, "pac", "ch", "ch125.pac"));
        var comp = new Composer(cat, skel);
        var ms = names.Split(';').Select(n => cat.Moves.Values.First(m => string.Equals(m.Name, n.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();
        foreach (var align in new[] { RootAlign.PlanarGroup, RootAlign.None })
            foreach (var mode in new[] { JoinMode.HardCut, JoinMode.Crossfade, JoinMode.Smooth })
            {
                var bl = ms.Select((m, i) => new Block
                {
                    Name = m.Name, Attacker = new LaneRef(m.Id, m.MainPhase, 0, ""),
                    Receiver = m.ReceiverAt(m.MainPhase) != null ? new LaneRef(m.Id, m.MainPhase, 1, "") : null,
                    In = 0, Out = (m.AttackerAt(m.MainPhase) ?? 1) - 1, Join = i == 0 ? JoinMode.HardCut : mode, Blend = 10,
                    Align = i == 0 ? RootAlign.None : align,
                }).ToList();
                var c = comp.Compose(bl);
                int at = c.Spans[1].Start;
                var speeds = new List<double>();
                for (int f = Math.Max(1, at - 4); f < Math.Min(c.Frames, at + 14); f++)
                {
                    var p0 = skel.Fk(c.Attacker[f - 1]!).ToArray(); var p1 = skel.Fk(c.Attacker[f]!).ToArray();
                    speeds.Add(p0.Zip(p1, (u, v) => (u - v).Length).Max());
                }
                Console.WriteLine($"{align,-12} {mode,-9} speed per frame around the join: {string.Join(" ", speeds.Select(v => v.ToString("0.0")))}");
            }
        return 0;
    }

    /// <summary>--phases game names: each move's parts by plain name (PhaseGuide) and its "whole move" chain</summary>
    public static int Phases(string game, string names)
    {
        var cat = new GameCatalog(game);
        var comp = new Composer(cat, Skeleton.Load(Path.Combine(game, "pac", "ch", "ch125.pac")));
        foreach (var name in names.Split(';'))
            foreach (var m in name.StartsWith('~')       // ~Category: the first 4 moves of that category
                         ? cat.Moves.Values.Where(m => m.Category.Contains(name[1..], StringComparison.OrdinalIgnoreCase)).Take(4)
                         : cat.Moves.Values.Where(m => string.Equals(m.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                Func<int, Track?> tr = x => { try { return comp.Source(m.Id, x, 0); } catch { return null; } };
                Console.WriteLine($"{m.Id,5} {m.Name}  [{m.Category}]  phases {string.Join(",", m.Phases)}");
                foreach (var x in m.Phases.Where(x => PhaseGuide.Shown(m, x)))
                    Console.WriteLine($"        {x,3}: {PhaseGuide.Label(m, x, tr)}");
                Console.WriteLine($"        whole: {PhaseGuide.ChainText(m, PhaseGuide.WholeMove(m, m.MainPhase, tr), tr)}");
            }
        return 0;
    }

    public static int StageDump(string game, string names)
    {
        var cat = new GameCatalog(game);
        var skel = Skeleton.Load(Path.Combine(game, "pac", "ch", "ch125.pac"));
        var comp = new Composer(cat, skel);
        foreach (var line in File.ReadLines(names))
        {
            var p = line.Split('|', 2);
            if (p.Length < 2) continue;
            foreach (var m in cat.Moves.Values.Where(m => string.Equals(m.Name, p[1].Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                string s;
                try
                {
                    var t = comp.Source(m.Id, m.MainPhase, 0);
                    var st = MotionMath.StageOf(t, m.Category, cat.Flags(m.Id));
                    var bits = cat.Flags(m.Id) is byte[] f ? string.Join(",", Enumerable.Range(0, f.Length * 8).Where(b => (f[b / 8] >> (b % 8) & 1) != 0)) : "-";
                    var root = t.Frames.Count > 0 ? t.Frames[0].Root : new V3(0, 0, 0);
                    var ops = string.Join(" ", t.Events.Where(e => e.Frame == 0).SelectMany(e => e.Events).Select(e => $"{e.Op}:{Convert.ToHexString(e.Data)}"));
                    s = $"start={t.Start?.ToString() ?? "-"} anchor={t.Anchor?.ToString() ?? "-"} bits={bits} rec={(m.Receiver != null ? "y" : "n")} phases={string.Join(",", m.Phases)} root0=({root.X:0.0},{root.Y:0.0},{root.Z:0.0}) stage=({st.Shift.X:0},{st.Shift.Z:0},{st.Yaw * 180 / Math.PI:0}) f0[{ops}]";
                }
                catch (Exception e) { s = "ERR " + e.Message; }
                Console.WriteLine($"{p[0]} {m.Id,5} {m.Name,-28} {m.Category,-18} {(m.Pack ?? ""),-10} {s}");
            }
        }
        return 0;
    }

    /// <summary>--eventcheck game pack id work: copy a built pack to work, move its first hit +5 frames and its first
    /// sound +3, save, read back (both track formats, pack.txt) and check the poses did not change</summary>
    public static int Events(string game, string pack, int id, string work)
    {
        try
        {
            if (Directory.Exists(work)) throw new InvalidOperationException("work folder exists: " + work);
            foreach (var f in Directory.GetFiles(pack, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(work, Path.GetRelativePath(pack, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to);
            }
            var cat = new GameCatalog(game);
            var ev = PackEvents.Load(cat, work, id);
            Console.WriteLine($"loaded {ev.Events.Count} events ({ev.Events.Count(e => e.IsSound)} sounds), frames {ev.Frames[0]}/{ev.Frames[1]}");
            foreach (var e in ev.Events.Where(e => PackEvents.Describe(e).Major))
                Console.WriteLine($"   {(e.Track == 0 ? "A" : "R")} f{e.Frame,4}  {PackEvents.Describe(e).Name}");
            var hit = ev.Events.First(e => !e.IsSound && (e.Op == 86 || e.Op is >= 96 and <= 99));
            var snd = ev.Events.FirstOrDefault(e => e.IsSound);
            int hitTo = hit.Frame + 5, sndTo = snd != null ? snd.Frame + 3 : -1;
            string hitName = PackEvents.Describe(hit).Name; int hitTrack = hit.Track;
            hit.Frame = hitTo;
            if (snd != null) snd.Frame = sndTo;
            static string Poses(string dir, bool packed) => string.Join("|", Directory.GetFiles(Path.Combine(dir, "motions")).Where(f => Path.GetFileName(f).StartsWith("motp_") == packed).OrderBy(f => f)
                .Select(f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Ymks.Records(File.ReadAllBytes(f), out _).SelectMany(r => Ymks.Split(r.Rec, packed).Pose).ToArray()))));
            string before = Poses(work, false) + Poses(work, true);
            foreach (var n in ev.Save()) Console.WriteLine("note: " + n);
            var again = PackEvents.Load(cat, work, id);
            if (!again.Events.Any(e => !e.IsSound && e.Track == hitTrack && e.Frame == hitTo && PackEvents.Describe(e).Name == hitName)) throw new InvalidOperationException("moved hit not found after reload");
            if (snd != null && !again.Events.Any(e => e.IsSound && e.Frame == sndTo && e.Sound == snd.Sound)) throw new InvalidOperationException("moved sound not found after reload");
            if (again.Events.Count != ev.Events.Count) throw new InvalidOperationException($"event count changed {ev.Events.Count} -> {again.Events.Count}");
            if (before != Poses(work, false) + Poses(work, true)) throw new InvalidOperationException("poses changed");
            // the packed copy has the event at the same new frame
            var T = cat.EventSizes("MOTP/GAME/0");
            foreach (var f in Directory.GetFiles(Path.Combine(work, "motions"), $"motp_{id}_0_{hitTrack}.ymk"))
                if (!Ymks.Records(File.ReadAllBytes(f), out _).Any(r => r.Frame == hitTo && Ymks.Events(Ymks.Split(r.Rec, true).Ev, T).Any(x => x.Data.AsSpan().SequenceEqual(hit.Data))))
                    throw new InvalidOperationException("packed copy: event not at the new frame");
            AnimationPorter.Validate(cat, work, new[] { id });
            Console.WriteLine($"OK: {hitName} moved to {hitTo}" + (snd != null ? $", sound {snd.Sound} to {sndTo}" : "") + "; poses unchanged; pack validates");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("FAILED: " + e.Message); return 1; }
    }

    public static int Workflow(string fixtureGame, string hctpSource, string output)
    {
        try
        {
            var cat = new GameCatalog(fixtureGame);
            bool cancellationPassed = false;
            using (var cancel = new System.Threading.CancellationTokenSource(10))
            {
                try { AnimationPorter.List(AnimationPorter.DefaultPython, "hctp", hctpSource, cancel.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { cancellationPassed = true; }
            }
            if (!cancellationPassed) throw new InvalidOperationException("Converter cancellation failed.");
            var request = new AnimationPorter.Request { Source = "hctp", Path = hctpSource, Move = "5019", Name = "Workflow Show Stopper" };
            var first = AnimationPorter.Build(AnimationPorter.DefaultPython, cat, request, default).GetAwaiter().GetResult();
            cat.LoadPreviewPack(first.Directory);
            var second = AnimationPorter.Build(AnimationPorter.DefaultPython, cat, request, default).GetAwaiter().GetResult();
            if (first.Ids.Intersect(second.Ids).Any()) throw new InvalidOperationException("Second preview reused a reserved id.");
            var skel = Skeleton.Load(Path.Combine(fixtureGame, "pac", "ch", "ch125.pac"));
            var composer = new Composer(cat, skel);
            var m = cat.Moves[first.Ids[0]];
            int x = m.MainPhase, n = m.AttackerAt(x)!.Value;
            var block = new Block { Name = m.Name, Attacker = new(m.Id, x, 0, "test"), Receiver = new(m.Id, x, 1, "test"), In = 0, Out = n - 1, SourceLength = n };
            var saved = new MovePack(cat, composer).Save(new[] { block }, "Porter Workflow Splice", pack: "porter_workflow_splice");
            if (saved.Id == m.Id) throw new InvalidOperationException("Splice overwrote its preview source id.");
            string[] lines = File.ReadAllLines(Path.Combine(saved.Dir, "pack.txt"));
            if (!lines.Any(l => l.StartsWith("motion mpsp.pac MOTP")) || !lines.Any(l => l.StartsWith("exh ")) || !lines.Contains($"wazecopy {saved.Id} {cat.StockDonor(m.Id)}"))
                throw new InvalidOperationException("Splice lost the imported move's match data.");
            string installed = AnimationPorter.Install(fixtureGame, first);
            var reload = new GameCatalog(fixtureGame);
            if (reload.Moves[first.Ids[0]].Pack == null || !File.ReadAllBytes(Path.Combine(installed, "pack.txt")).SequenceEqual(File.ReadAllBytes(Path.Combine(first.Directory, "pack.txt")))) throw new InvalidOperationException("Installed pack read-back failed.");
            bool duplicateRejected = false;
            try { AnimationPorter.Install(fixtureGame, first); } catch (InvalidOperationException) { duplicateRejected = true; }
            if (!duplicateRejected) throw new InvalidOperationException("Duplicate installation was not rejected.");
            File.WriteAllText(output, JsonSerializer.Serialize(new { ok = true, first.Ids, secondIds = second.Ids, spliceId = saved.Id, saved.Dir, installed, duplicateRejected, cancellationPassed }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception e) { File.WriteAllText(output, JsonSerializer.Serialize(new { ok = false, error = e.ToString() })); return 1; }
    }

    public static int Run(string game, string directory, string output)
    {
        try
        {
            var cat = new GameCatalog(game);
            int[] ids = File.ReadLines(Path.Combine(directory, "pack.txt")).Where(l => l.StartsWith("wazename ")).Select(l => int.Parse(l.Split(' ')[1])).Distinct().ToArray();
            AnimationPorter.Validate(cat, directory, ids);
            cat.LoadPreviewPack(directory);
            var skel = Skeleton.Load(Path.Combine(game, "pac", "ch", "ch125.pac"));
            var composer = new Composer(cat, skel);
            int tracks = 0, frames = 0;
            double maxJointError = 0;
            var comparisons = new System.Collections.Generic.List<object>();
            foreach (int id in ids)
            {
                var move = cat.Moves[id];
                foreach (var ((phase, lane), length) in move.Tracks.Where(t => t.Key.Y < 2))
                {
                    var copies = cat.Copies(id, phase, lane).Where(c => c.Pack != null).ToList();
                    var unpacked = copies.First(c => !c.Packed);
                    var packed = copies.First(c => c.Packed);
                    var a = Track.Decode(cat.Data(unpacked), cat.T(unpacked), false, length);
                    var b = Track.Decode(cat.Data(packed), cat.T(packed), true, length);
                    double worst = 0; int worstFrame = 0, worstJoint = 0;
                    for (int f = 0; f < length; f++)
                    {
                        var aa = skel.Fk(a.Frames[f]); var bb = skel.Fk(b.Frames[f]);
                        foreach (var p in aa.Concat(bb))
                            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Z)) throw new InvalidOperationException("Nonfinite preview joint.");
                        maxJointError = Math.Max(maxJointError, aa.Zip(bb, (p, q) => (p - q).Length).Max());
                        for (int j = 0; j < aa.Length; j++)
                            if ((aa[j] - bb[j]).Length > worst) { worst = (aa[j] - bb[j]).Length; worstFrame = f; worstJoint = j; }
                    }
                    comparisons.Add(new { id, phase, lane, length, worst, worstFrame, worstJoint = skel.Names[worstJoint] });
                    tracks++; frames += length;
                }
                int phase0 = move.MainPhase, n = move.AttackerAt(phase0)!.Value;
                var block = new Block { Name = move.Name, Attacker = new(id, phase0, 0, "check"), Receiver = move.ReceiverAt(phase0) != null ? new(id, phase0, 1, "check") : null, In = 0, Out = n - 1, SourceLength = n };
                var composition = composer.Compose(new[] { block });
                _ = composer.Joints(composition);
                var records = new MovePack(cat, composer).ReadRecords();
                if (!records.Exh.ContainsKey(id)) throw new InvalidOperationException("Imported gameplay records were not loaded.");
                if (cat.StockDonor(id) == id) throw new InvalidOperationException("Imported donor record was not resolved.");
            }
            bool collisionRejected = false;
            try { AnimationPorter.Validate(cat, directory, ids); } catch (InvalidOperationException) { collisionRejected = true; }
            if (!collisionRejected) throw new InvalidOperationException("Preview id collision was not rejected.");
            File.WriteAllText(output, JsonSerializer.Serialize(new { ok = true, ids, tracks, frames, maxJointError, collisionRejected, comparisons }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception e) { File.WriteAllText(output, JsonSerializer.Serialize(new { ok = false, error = e.ToString() })); return 1; }
    }
}
