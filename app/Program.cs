using System.Linq;
using Avalonia;

namespace AnimIT;

internal static class Program
{
    [System.STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--decodeclip") return Core.K26.ClipBridge.Run(args[1], args[2]);
        if (args.Length == 3 && args[0] == "--clipbankcheck") return Core.K26.ClipBridge.CheckBank(args[1], args[2]);
        if (args.Length == 3 && args[0] == "--extractclips") return Core.K26.CakeBridge.Run(args[1], args[2]);
        if (args.Length == 4 && args[0] == "--validatepack") { try { Core.AnimationPorter.Validate(new Core.GameCatalog(args[1]), args[2], args[3].Split(",").Select(int.Parse).ToArray()); System.Console.WriteLine("pack OK"); return 0; } catch (System.Exception e) { System.Console.WriteLine("FAILED: " + e.Message); return 1; } }
        if (args.Length == 3 && args[0] == "--joincheck") return PorterCheck.JoinCheck(args[1], args[2]);
        if (args.Length == 3 && args[0] == "--moveinfo") return PorterCheck.MoveInfo(args[1], args[2]);
        if (args.Length == 4 && args[0] == "--savecheck") return PorterCheck.SaveCheck(args[1], args[2], int.Parse(args[3]));
        if (args.Length == 3 && args[0] == "--leadincheck") return PorterCheck.LeadInCheck(args[1], args[2]);
        if (args.Length == 2 && args[0] == "--previewbanks") return PorterCheck.PreviewBanks(args[1]);
        if (args.Length >= 2 && args[0] == "--ymkframes") { foreach (var f in args.Skip(1)) { Core.Ymks.Records(System.IO.File.ReadAllBytes(f), out int n); System.Console.WriteLine($"{System.IO.Path.GetFileName(f)} {n}"); } return 0; }
        if (args.Length == 2 && args[0] == "--logo") return Render.Logo(args[1]);
        if (args.Length == 3 && args[0] == "--phases") return PorterCheck.Phases(args[1], args[2]);
        if (args.Length == 3 && args[0] == "--stagedump") return PorterCheck.StageDump(args[1], args[2]);
        if (args.Length == 5 && args[0] == "--eventcheck") return PorterCheck.Events(args[1], args[2], int.Parse(args[3]), args[4]);
        if (args.Length == 4 && args[0] == "--portercheck") return PorterCheck.Run(args[1], args[2], args[3]);
        if (args.Length == 4 && args[0] == "--porterworkflowcheck") return PorterCheck.Workflow(args[1], args[2], args[3]);
        if (args.Length == 3 && args[0] == "--renderporter") return Render.Porter(args[1], args[2]);
        if (args.Length >= 2 && args[0] == "--render") return Render.Run(args[1], args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 6, args.Length > 4 ? int.Parse(args[3]) : 1640, args.Length > 4 ? int.Parse(args[4]) : 960);
        if (args.Length >= 3 && args[0] == "--selftest") return SelfTest.Run(args[1], args[2]);
        if (args.Length >= 2 && args[0] == "--bitmap") return BitMap.Run(args[1]);
        if (args.Length >= 3 && args[0] == "--flags") return FlagDump.Run(args[1], args[2..]);
        if (args.Length >= 3 && args[0] == "--anchorfit") return AnchorFit.Run(args[1], args[2]);
        if (args.Length >= 7 && args[0] == "--ymbschan") return YmbsChan.Run(args[1], int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]), int.Parse(args[6]));
        if (args.Length >= 5 && args[0] == "--ymbsheads") return YmbsHeads.Run(args[1], int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]));
        if (args.Length >= 2 && args[0] == "--ymbsstats") return YmbsStats.Run(args[1]);
        if (args.Length >= 5 && args[0] == "--rawmotion") return RawMotion.Run(args[1], int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]), args.Length > 5 ? int.Parse(args[5]) : 512);
        if (args.Length >= 3 && args[0] == "--hitfront") return HitFront.Run(args[1], args[2..]);
        if (args.Length >= 3 && args[0] == "--exh") return ExhDump.Run(args[1], args[2..]);
        if (args.Length >= 3 && args[0] == "--flagsid") return FlagIds.Run(args[1], args[2..]);
        if (args.Length >= 3 && args[0] == "--placecap") return PlaceCapture.Run(args[1], args[2..]);
        if (args.Length >= 4 && args[0] == "--hitlog") return HitLogStudy.Run(args[1], args[2], args[3]);
        if (args.Length >= 2 && args[0] == "--banks") return BankList.Run(args[1]);
        if (args.Length >= 2 && args[0] == "--noreceiver") return NoReceiver.Run(args[1]);
        if (args.Length >= 3 && args[0] == "--thirdcheck") return ThirdCheck.Run(args[1], args[2..]);
        if (args.Length >= 2 && args[0] == "--track2") return Track2.Run(args[1]);
        if (args.Length >= 3 && args[0] == "--placecheck") return PlaceCheck.Run(args[1], args[2]);
        if (args.Length >= 4 && args[0] == "--cutcheck") return CutCheck.Run(args[1], args[2], int.Parse(args[3]));
        if (args.Length >= 4 && args[0] == "--stripinner") return StripInner.Run(args[1], args[2], int.Parse(args[3]));
        if (args.Length >= 5 && args[0] == "--motpcheck") return MotpCheck.Run(args[1], int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]));
        if (args.Length >= 3 && args[0] == "--opcensus") return OpCensus.Run(args[1], args[2], args.Length > 3 ? args[3] : null);
        if (args.Length >= 3 && args[0] == "--camtracks") return CamTracks.Run(args[1], args[2]);
        if (args.Length >= 3 && args[0] == "--opuses") return OpUses.Run(args[1], int.Parse(args[2]), args.Length > 3 ? int.Parse(args[3]) : 400);
        if (args.Length >= 5 && args[0] == "--rootpath") return RootPath.Run(args[1], int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]));
        if (args.Length >= 5 && args[0] == "--evcopies") return EvCopies.Run(args[1], int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]));
        if (args.Length >= 3 && args[0] == "--stockexport") return StockExport.Run(args[1], args[2]);
        if (args.Length >= 3 && args[0] == "--stagecheck") return StageCheck.Run(args[1], args[2]);
        if (args.Length >= 3 && args[0] == "--trackbody") return TrackBody.Run(args[1], args[2], args.Length > 3);
        if (args.Length >= 3 && args[0] == "--floor") return FloorCheck.Run(args[1], args[2..]);
        if (args.Length >= 4 && args[0] == "--joincheck") return JoinCheck.Run(args[1], args[2], args[3]);
        if (args.Length >= 4 && args[0] == "--export") return ExportCli(args[1], args[2], args[3]);
        if (args.Length >= 4 && args[0] == "--placelog") return PlaceLog.Run(args[1], args[2], args[3..]);
        if (args.Length >= 4 && args[0] == "--study") return Study.Run(args[1], args[2], args[3..]);
        if (args.Length >= 3 && args[0] == "--names") { var s = Core.Skeleton.Load(args[1]); System.IO.File.WriteAllLines(args[2], System.Linq.Enumerable.Range(0, s.Names.Length).Select(i => $"{i} {s.Names[i]} parent {s.Parent[i]} anim {s.AnimBone[i]} len {s.Offset[i].Length:0.00}")); return 0; }
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>--export game "name" id[@phase],id...: the blocks (whole moves, crossfade 8, planar) saved as a pack</summary>
    static int ExportCli(string game, string name, string ids)
    {
        var cat = new Core.GameCatalog(game);
        var skel = Core.Skeleton.Load(System.IO.Path.Combine(game, "pac", "ch", "ch125.pac"));
        var comp = new Core.Composer(cat, skel);
        var blocks = new System.Collections.Generic.List<Core.Block>();
        foreach (var s in ids.Split(','))
        {
            var p = s.Split('@');
            var m = cat.Moves[int.Parse(p[0])];
            int x = p.Length > 1 ? int.Parse(p[1]) : m.MainPhase;
            int n = m.AttackerAt(x) ?? 1;
            blocks.Add(new Core.Block
            {
                Name = m.Name, Attacker = new Core.LaneRef(m.Id, x, 0, $"{m.Id}_{x}_0"),
                Receiver = m.ReceiverAt(x) != null ? new Core.LaneRef(m.Id, x, 1, $"{m.Id}_{x}_1") : null,
                In = 0, Out = n - 1, SourceLength = n,
                Join = blocks.Count > 0 ? Core.JoinMode.Crossfade : Core.JoinMode.HardCut,
                Align = blocks.Count > 0 ? Core.RootAlign.PlanarGroup : Core.RootAlign.None,
            });
        }
        var r = new Core.MovePack(cat, comp).Save(blocks, name);
        System.Console.WriteLine($"saved move {r.Id} '{r.Name}' (donor {r.Donor}) in {r.Dir}");
        foreach (var n in r.Notes) System.Console.WriteLine("  " + n);
        // read back: the written tracks (m.pac and the packed MOTP copy) against the composition, joint by joint
        var c = comp.Compose(blocks);
        int phase = blocks[0].Attacker.X;
        var T = cat.T(cat.Best(blocks[0].Attacker.Id, phase, 0)!);
        foreach (int y in new[] { 0, 1 })
            foreach (bool packed in new[] { false, true })
            {
                string f = System.IO.Path.Combine(r.Dir, "motions", (packed ? "motp_" : "") + $"{r.Id}_{phase}_{y}.ymk");
                if (!System.IO.File.Exists(f)) continue;
                var t = Core.Track.Decode(System.IO.File.ReadAllBytes(f), T, packed, 0);
                var want = y == 0 ? c.Attacker : c.Receiver;
                double worst = 0, sum = 0; int cnt = 0, wf = 0;
                for (int k = 0; k < want.Count && k < t.Frames.Count; k++)
                {
                    if (want[k] is not Core.Pose w) continue;
                    var a = skel.Fk(w); var b = skel.Fk(t.Frames[k]);
                    double e = a.Zip(b, (p, q) => (p - q).Length).Max();
                    sum += e; cnt++; if (e > worst) { worst = e; wf = k; }
                }
                System.Console.WriteLine($"  read back {(packed ? "MOTP packed" : "m.pac      ")} track {y}: {t.Frames.Count} frames, joint error mean {sum / System.Math.Max(1, cnt):0.000} worst {worst:0.000} (frame {wf}), start {t.Start}");
            }
        return 0;
    }
}
