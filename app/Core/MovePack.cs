using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AnimIT.Core;

/// <summary>Saves a composed move into the game as a move pack (Mods\Moves\&lt;pack&gt;\pack.txt, read by the port at start-up).
/// Same rules as the proven porters (hctp_porter / k26_porter):
/// - the move takes an id that has a stock move record nobody uses (a pack can only re-use a record: wazecopy),
///   the closest one to the donor;
/// - the donor (the first block's move) gives the record (category, menu), its EXH / MBD / sound-event records
///   (id changed) and the phases the splice doesn't write;
/// - every track goes into the donor's banks: MOT/BM.. and MVMT/.. unpacked, MOTP/GAME packed (what matches load),
///   and the RR copy into the roomiest RR bank (RR00/RR01 are full: a move added there froze the attacker).</summary>
public sealed class MovePack
{
    public const int RrCap = 16384000;
    readonly GameCatalog cat;
    readonly Composer comp;
    public MovePack(GameCatalog cat, Composer comp) { this.cat = cat; this.comp = comp; }

    public sealed class Result
    {
        public int Id, Donor, Phase;
        public string Category = "";
        public string Name = "", Dir = "";
        public List<string> Notes = new();
    }

    // ops that change the motion or jump in time: only the last block may hand over to what follows
    // (79 end position and 82 end adjust re-anchor the fighter where a move ends: inside a splice they turned and moved the
    // wrestler at the join in the game - the next block then played from the moved anchor)
    static readonly HashSet<int> MotionOps = new() { 2, 3, 4, 49, 50, 51, 54, 79, 82, 88 };
    static readonly HashSet<int> JumpOps = new() { 1, 10 };

    public string PackDir(string pack) => Path.Combine(cat.Game, "Mods", "Moves", pack);

    // ---------------------------------------------------------------- misc.pac WAZA/DATA: EXH, sound events, MBD
    internal sealed class Records
    {
        public Dictionary<int, List<(int G, byte[] R)>> Exh = new(), Mbd = new();
        public Dictionary<int, List<(int G, byte[] R, byte[] Ev)>> Evt = new();
    }

    internal Records ReadRecords()
    {
        string misc = Path.Combine(cat.Game, "pac", "misc.pac");
        var (_, _, off, size) = GameCatalog.TopEntries(misc).First(e => e.Typ == "WAZA" && e.Name == "DATA");
        var blob = new byte[size];
        using (var f = File.OpenRead(misc)) { f.Position = off; f.ReadExactly(blob); }
        var kids = Pach.Read(Bpe.Unpack(blob)).ToDictionary(x => x.Id, x => x.Data);
        IEnumerable<(int G, byte[] U)> Groups(int k) => Pach.Read(Bpe.Unpack(kids[k])).Select(x => (x.Id, Bpe.Unpack(x.Data)));
        var r = new Records();
        foreach (var (g, u) in Groups(0))
        {
            if (!Bin.Tag(u, "EXH\0")) continue;
            int n = (int)Bin.U32(u, 4);
            for (int k = 0; k < n && 8 + 36 * (k + 1) <= u.Length; k++)
            {
                var rec = u.AsSpan(8 + 36 * k, 36).ToArray();
                Add(r.Exh, Bin.U16(rec, 0), (g, rec));
            }
        }
        foreach (var (g, u) in Groups(1))
        {
            if (u.Length < 8 || Bin.U32(u, 0) != 16) continue;
            int n = (int)Bin.U32(u, 4);
            if (8 + 16 * n > u.Length) continue;
            var ev = u.AsSpan(8 + 16 * n).ToArray();
            for (int k = 0; k < n; k++)
            {
                var rec = u.AsSpan(8 + 16 * k, 16).ToArray();
                int i = Bin.U16(rec, 8), c = rec[10];
                if (4 * (i + c) > ev.Length) continue;
                if (!r.Evt.TryGetValue(Bin.U16(rec, 0), out var l)) r.Evt[Bin.U16(rec, 0)] = l = new();
                l.Add((g, rec, ev.AsSpan(4 * i, 4 * c).ToArray()));
            }
        }
        foreach (var (g, u) in Groups(2))
        {
            if (!Bin.Tag(u, "MBD\0")) continue;
            int n = (int)Bin.U32(u, 4);
            for (int k = 0; k < n && 16 + 8 * k <= u.Length; k++)
            {
                var rec = u.AsSpan(8 + 8 * k, 8).ToArray();
                Add(r.Mbd, Bin.U16(rec, 0), (g, rec));
            }
        }
        // Pack records override the stock records, including temporary porter previews.
        foreach (var pack in cat.Packs)
        {
            var replaced = new HashSet<(string, int)>();
            foreach (string line in File.ReadLines(Path.Combine(pack.Dir, "pack.txt")))
            {
                var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 3 || p[0] is not ("exh" or "mbd" or "evt")) continue;
                int group = int.Parse(p[1]);
                byte[] record = Convert.FromHexString(p[2]);
                if (record.Length < 2) continue;
                int id = Bin.U16(record, 0);
                if (replaced.Add((p[0], id)))
                {
                    if (p[0] == "exh") r.Exh.Remove(id);
                    else if (p[0] == "mbd") r.Mbd.Remove(id);
                    else r.Evt.Remove(id);
                }
                if (p[0] == "exh") Add(r.Exh, id, (group, record));
                else if (p[0] == "mbd") Add(r.Mbd, id, (group, record));
                else Add(r.Evt, id, (group, record, p.Length > 3 ? Convert.FromHexString(p[3]) : Array.Empty<byte>()));
            }
        }
        return r;
    }

    static void Add<T>(Dictionary<int, List<T>> d, int k, T v)
    {
        if (!d.TryGetValue(k, out var l)) d[k] = l = new();
        l.Add(v);
    }

    static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    static byte[] WithId(byte[] rec, int id) { var r = (byte[])rec.Clone(); r[0] = (byte)id; r[1] = (byte)(id >> 8); return r; }

    // ---------------------------------------------------------------- pack.txt sections
    /// <summary>the move id a pack line belongs to (null = a comment / other line)</summary>
    internal static int? LineId(string line)
    {
        var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length < 2) return null;
        switch (f[0])
        {
            case "motion": return f.Length > 3 && int.TryParse(f[3], out int m) ? m : null;
            case "exh": case "mbd": case "evt":
                return f.Length > 2 && f[2].Length >= 4 ? Convert.ToInt32(f[2][2..4] + f[2][..2], 16) : null;
            case "wazecopy": case "waze": case "wazename": return int.TryParse(f[1], out int w) ? w : null;
        }
        return null;
    }

    /// <summary>ids used by stock motions and by every installed pack (but this one)</summary>
    HashSet<int> UsedIds(string skipDir)
    {
        var used = new HashSet<int>(cat.StockMotionIds);
        foreach (var pack in cat.Packs)
            if (!string.Equals(Path.GetFullPath(pack.Dir), Path.GetFullPath(skipDir), StringComparison.OrdinalIgnoreCase))
                foreach (string line in File.ReadLines(Path.Combine(pack.Dir, "pack.txt")))
                    if (LineId(line) is int reserved) used.Add(reserved);
        string mods = Path.Combine(cat.Game, "Mods");
        if (!Directory.Exists(mods)) return used;
        foreach (var p in Directory.EnumerateFiles(mods, "pack.txt", SearchOption.AllDirectories))
        {
            string d = Path.GetDirectoryName(p)!;
            if (string.Equals(Path.GetFullPath(d), Path.GetFullPath(skipDir), StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(Path.Combine(d, "disabled"))) continue;
            foreach (var l in File.ReadLines(p)) if (LineId(l) is int id) used.Add(id);
        }
        return used;
    }

    // ---------------------------------------------------------------- the splice's events
    /// <summary>one event of the spliced move: from block Block's lane (0 attacker, 1 receiver), a motion event (Op, its
    /// bytes) or a sound (Data = {u16 sound, u16 frame}); Base = the output frame it plays on by itself, Frame = with
    /// your timing (the block's EventShift)</summary>
    public sealed record SpliceEvent(int Block, int Lane, bool Sound, int SourceFrame, int Index, int Op, byte[] Data, int Base, int Frame)
    {
        public string Key => $"{Lane}:{(Sound ? "s" : "e")}:{SourceFrame}:{Index}";
    }

    /// <summary>every event the saved move will have, where it will play (the save writes exactly these)</summary>
    internal List<SpliceEvent> Events(IReadOnlyList<Block> blocks, Composition c, Records? recs, int[]? dropped = null)
    {
        int n = c.Frames;
        var list = new List<SpliceEvent>();
        void Add(int i, int lane, bool sound, int sf, int idx, int op, byte[] d, int at)
        {
            var e = new SpliceEvent(i, lane, sound, sf, idx, op, d, at, at);
            int sh = blocks[i].EventShift.GetValueOrDefault(e.Key);
            list.Add(e with { Frame = Math.Clamp(at + sh, 0, n - 1) });
        }
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            var (start, len) = c.Spans[i];
            bool lastBlock = i == blocks.Count - 1;
            for (int lane = 0; lane < 2; lane++)
            {
                var lr = lane == 0 ? b.Attacker : b.Receiver;
                if (lr == null) continue;
                var t = comp.Source(lr.Id, lr.X, lr.Y);
                foreach (var (sf, evs) in t.Events)
                {
                    int at;
                    if (sf < b.In) { if (i != 0) continue; at = 0; }          // the move's set-up (states, attach...) at its start
                    else if (sf > b.Out) { if (!lastBlock) continue; at = start + len - 1; }
                    else at = start + (int)Math.Round((sf - b.In) / Math.Max(0.05, b.Speed));
                    at = Math.Clamp(at, 0, n - 1);
                    for (int k = 0; k < evs.Count; k++)
                    {
                        var (op, d) = evs[k];
                        bool keep = !JumpOps.Contains(op)
                                    && (lastBlock || !MotionOps.Contains(op))
                                    && !(i > 0 && op == 92)
                                    && !(!lastBlock && (op == 80 || op == 81) && d.Length > 1 && (d[1] == 16 || d[1] == 21 || d[1] == 2))
                                    && !(sf < b.In && (op == 86 || (op >= 32 && op <= 36)));
                        if (keep) Add(i, lane, false, sf, k, op, d, at);
                        else if (dropped != null) dropped[lane]++;
                    }
                }
                // sounds (WAZA/DATA child 1): {u16 sound, u16 frame} per entry
                if (recs == null) continue;
                foreach (var (_, rec, ev) in recs.Evt.GetValueOrDefault(lr.Id) ?? new())
                {
                    if (rec[2] != lr.X || rec[3] != lr.Y) continue;
                    for (int k = 0; k + 4 <= ev.Length; k += 4)
                    {
                        int sf = Bin.U16(ev, k + 2);
                        if (sf < b.In || sf > b.Out) continue;
                        int at = Math.Clamp(start + (int)Math.Round((sf - b.In) / Math.Max(0.05, b.Speed)), start, start + len - 1);
                        Add(i, lane, true, sf, k / 4, -1, ev.AsSpan(k, 4).ToArray(), at);
                    }
                }
            }
        }
        return list;
    }

    // ---------------------------------------------------------------- the move's tracks
    /// <summary>output frames of one lane: composed poses + the source events, re-timed</summary>
    List<YmksWriter.Frame> Lane(IReadOnlyList<Block> blocks, Composition c, bool receiver, List<SpliceEvent> events)
    {
        int n = c.Frames;
        var poses = receiver ? c.Receiver : c.Attacker;
        var fr = Enumerable.Range(0, n).Select(_ => new YmksWriter.Frame()).ToList();
        var first = blocks[0];
        var src0 = receiver ? first.Receiver : first.Attacker;
        // frame 0 keeps the source's blend-in (its first key's count), then one key a frame (the stock way)
        int c0 = 0;
        if (src0 != null && first.In == 0)
        {
            var m = cat.Best(src0.Id, src0.X, src0.Y);
            if (m != null)
                foreach (var (f, rec) in Ymks.Records(cat.Data(m), out _))
                {
                    var pose = Ymks.Split(rec, m.Packed).Pose;
                    if (pose.Length == 0) continue;
                    try { c0 = (m.Packed ? Ymks.DecodePacked(pose) : Ymks.DecodeBody(pose)).FirstOrDefault()?.Count ?? 0; } catch { }
                    break;
                }
        }
        else c0 = 4;
        c0 = Math.Clamp(c0, 0, Math.Max(0, n - 3));
        Pose? last = null;
        for (int f = 0; f < n; f++)
        {
            if (poses[f] is Pose p) last = p;
            bool key = f == 0 || (f > c0 && f < n - 1);
            if (!key || last == null) continue;
            fr[f].Pose = last;
            fr[f].Count = f == 0 ? c0 : 0;
        }
        // events: each block's source events at the output frame they now play on (moved where you put them)
        foreach (var e in events)
            if (!e.Sound && e.Lane == (receiver ? 1 : 0)) fr[e.Frame].Events.Add(e.Data);
        return fr;
    }

    // ---------------------------------------------------------------- save
    public Result Save(IReadOnlyList<Block> blocks, string name, int? donorId = null, string pack = "animit_moves")
    {
        if (blocks.Count == 0) throw new InvalidOperationException("The timeline is empty.");
        // taunts (and holds) play as YMBs in the game; this writer makes YMKs tracks, which the game will not play as a taunt
        if (cat.Moves.GetValueOrDefault(blocks[0].Attacker.Id)?.Category == "Taunts" || cat.Best(blocks[0].Attacker.Id, blocks[0].Attacker.X, 0)?.Kind == "YMBs"
            || (donorId is int dd && cat.Moves.GetValueOrDefault(dd)?.Category == "Taunts"))
            throw new InvalidOperationException("Taunts can be previewed and spliced, but not saved to the game yet: the game plays taunts in the YMBs format, and AnimIT can only write normal (YMKs) moves so far.");
        var r = new Result { Name = name, Dir = PackDir(pack) };
        var first = blocks[0];
        // the donor: the move you chose (else the first clip's). The splice replaces the donor's main part (the first clip's
        // own phase when the donor is that move); the donor's other parts (approach, landing...) stay its own
        int donor = donorId ?? first.Attacker.Id;
        if (!cat.WazeIds.Contains(donor) || cat.Flags(donor) is not byte[] dflags0 || !cat.Moves.ContainsKey(donor))
            throw new InvalidOperationException($"The donor ({donor}) has no move record in the game. Choose another move type / donor.");
        int phase = donor == first.Attacker.Id ? first.Attacker.X : cat.Moves[donor].MainPhase;
        var dflags = (byte[])dflags0.Clone();
        // a grapple donor hidden from its list (byte 0 = 00): made visible (0e), as the porters do
        if (dflags[0] == 0 && dflags.Length > 3 && (dflags[3] == 4 || dflags[3] == 8))
        {
            dflags[0] = 0x0e;
            r.Notes.Add("the donor is hidden in Create-a-Moveset: the new move is made visible");
        }
        r.Category = cat.Moves[donor].Category;
        r.Phase = phase;
        r.Donor = donor;
        var c = comp.Compose(blocks);
        if (c.Frames < 2) throw new InvalidOperationException("The move is too short.");

        // the existing pack: keep everything but this move (re-saving a name replaces it)
        Directory.CreateDirectory(Path.Combine(r.Dir, "motions"));
        string packTxt = Path.Combine(r.Dir, "pack.txt");
        var old = File.Exists(packTxt) ? File.ReadAllLines(packTxt).ToList() : new List<string>();
        if (File.Exists(packTxt)) File.Copy(packTxt, packTxt + ".bak", true);   // (the pack before this save)
        int? reuse = old.Where(l => l.StartsWith("wazename ")).Select(l => l.Split(' ', 3))
                        .Where(f => f.Length == 3 && f[2] == name).Select(f => (int?)int.Parse(f[1])).FirstOrDefault();
        var used = UsedIds(r.Dir);
        foreach (var l in old) if (LineId(l) is int id && id != reuse) used.Add(id);
        if (reuse is int ri) r.Id = ri;
        else
        {
            var free = cat.WazeIds.Where(i => i >= 2000 && i < 0x4000 && !used.Contains(i)).ToList();
            if (free.Count == 0) throw new InvalidOperationException("No free move record left in the game.");
            r.Id = free.OrderBy(i => Math.Abs(i - donor)).ThenBy(i => i).First();
        }
        int id0 = r.Id;
        var keep = old.Where(l => LineId(l) != id0 && !l.StartsWith("#")).ToList();
        foreach (var f in Directory.GetFiles(Path.Combine(r.Dir, "motions"), $"*{id0}_*.ymk"))
            if (Path.GetFileName(f).StartsWith($"{id0}_") || Path.GetFileName(f).StartsWith($"motp_{id0}_")) File.Delete(f);

        // banks: the donor's (RR copies -> the roomiest RR bank)
        string rr = RoomiestRr(r.Notes);
        var lines = new List<string>();
        // Create-a-Moveset previews (single-wrestler mode) load only MOT/RR00 plus the MVMT/SPE.. preview banks; a pack's
        // RR copy is in RR03, so a move with no SPE copy shows a still preview. Hidden donors never have one (all 97 hidden
        // standing grapples: Fisherman Suplex 1 -> "[AnimIT] cutter" did not preview, 2026-11-05), nor do strikes. The
        // spliced tracks then also go into the preview bank most moves of the donor's type use (else MVMT/SPE1/15).
        string? previewBank = null;
        if (!cat.Copies(donor, phase, 0).Any(m => m.Bank.StartsWith("MVMT/SPE")))
        {
            var kind = cat.Moves[donor].Category;
            previewBank = cat.Moves.Values.Where(m => m.Pack == null && m.Category == kind && m.Phases.Any())
                .SelectMany(m => cat.Copies(m.Id, m.MainPhase, 0).Select(q => q.Bank).Where(b => b.StartsWith("MVMT/SPE")).Distinct())
                .GroupBy(b => b).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "MVMT/SPE1/15";
            r.Notes.Add($"the donor has no Create-a-Moveset preview copy: the move is also put in {previewBank} so its preview plays");
        }
        void Write(int x, int y, byte[] unpacked, byte[]? packed, int frames, int dx, int dy, bool preview = false)
        {
            string fn = $"{id0}_{x}_{y}.ymk";
            File.WriteAllBytes(Path.Combine(r.Dir, "motions", fn), unpacked);
            var banks = cat.Copies(donor, dx, dy).Where(m => m.Kind == "YMKs").Select(m => (m.Pac, m.Bank)).Distinct().ToList();
            if (banks.Count == 0)   // (the donor has no such track - a receiver for an attacker-only part: its attacker's banks)
                banks = cat.Copies(donor, dx, 0).Where(m => m.Kind == "YMKs").Select(m => (m.Pac, m.Bank)).Distinct().ToList();
            var un = banks.Where(b => b.Pac == "m.pac" && (b.Bank.StartsWith("MOT/BM") || b.Bank.StartsWith("MVMT"))).Select(b => b.Bank).ToList();
            un.Add(rr);
            if (preview && previewBank != null) un.Add(previewBank);
            foreach (var bk in un.Distinct()) lines.Add($"motion m.pac {bk} {id0} {x} {y} {frames} motions/{fn}");
            var pk = banks.Where(b => b.Bank.StartsWith("MOTP")).Select(b => b.Bank).ToList();
            if (pk.Count > 0 && packed != null)
            {
                string pfn = "motp_" + fn;
                File.WriteAllBytes(Path.Combine(r.Dir, "motions", pfn), packed);
                foreach (var bk in pk) lines.Add($"motion mpsp.pac {bk} {id0} {x} {y} {frames} motions/{pfn}");
            }
        }

        // the spliced tracks: attacker (phase, 0) and receiver (phase, 1), with every event where the timeline puts it
        var recs = ReadRecords();
        var dropped = new int[2];
        var events = Events(blocks, c, recs, dropped);
        for (int y = 0; y < 2; y++)
            if (dropped[y] > 0) r.Notes.Add($"{(y == 1 ? "receiver" : "attacker")}: {dropped[y]} event(s) left out (hand-offs, motion changes and jumps of the inner blocks)");
        int movedEv = blocks.Sum(b => b.EventShift.Count(kv => kv.Value != 0));
        if (movedEv > 0) r.Notes.Add($"{movedEv} event(s) at the frames you moved them to");
        var lanes = new List<(int Y, List<YmksWriter.Frame> F)> { (0, Lane(blocks, c, false, events)) };
        // (a receiver track only when some block has one: a strike's shown game reaction is preview only)
        if (blocks.Any(b => b.Receiver != null) && c.Receiver.Any(p => p != null)) lanes.Add((1, Lane(blocks, c, true, events)));
        foreach (var (y, fr) in lanes)
        {
            var un = YmksWriter.Track(fr, false);
            var pk = YmksWriter.Track(fr, true);
            Verify(un, false, fr, y);
            Verify(pk, true, fr, y);
            Write(phase, y, un, pk, c.Frames, phase, y, preview: true);
        }
        // the donor's other phases and tracks (its EXH records name them: a missing motion would freeze the move)
        foreach (var ((x, y), frames) in cat.Moves[donor].Tracks)
        {
            if (x == phase && (y == 0 || (y == 1 && lanes.Count > 1))) continue;
            var m = cat.Copies(donor, x, y).OrderBy(q => q.Pack == null).FirstOrDefault(q => q.Kind == "YMKs" && !q.Bank.StartsWith("MOTP"));
            var mp = cat.Copies(donor, x, y).OrderBy(q => q.Pack == null).FirstOrDefault(q => q.Bank.StartsWith("MOTP"));
            if (m == null) continue;
            // (frames from the data itself: the bank index says 0 for some stock tracks - Fisherman Suplex 1's receiver
            // phase 92 holds 92 frames, its 1-frame placeholders 90 / 91 read 0)
            var data = cat.Data(m);
            int nf = 0;
            try { Ymks.Records(data, out nf); } catch { }
            Write(x, y, data, mp != null ? cat.Data(mp) : null, nf > 0 ? nf : Math.Max(1, m.Frames), x, y);
            r.Notes.Add($"phase {x} track {y} from the donor");
        }

        // records
        foreach (var (g, rec) in recs.Exh.GetValueOrDefault(donor) ?? new()) lines.Add($"exh {g} {Hex(WithId(rec, id0))}");
        foreach (var (g, rec) in recs.Mbd.GetValueOrDefault(donor) ?? new()) lines.Add($"mbd {g} {Hex(WithId(rec, id0))}");
        // sounds (WAZA/DATA child 1): one record per (move, phase, wrestler), a list of {u16 sound, u16 frame}. A splice gets
        // every block's sounds at the frames they now play on (the donor's list alone left the later moves silent);
        // the donor's records for tracks the splice doesn't write stay as they are. (The pack merger sets index / count.)
        var sounds = new Dictionary<int, List<(int Frame, byte[] E)>>();
        foreach (var (y, _) in lanes)
        {
            var l = new List<(int, byte[])>();
            foreach (var se in events.Where(q => q.Sound && q.Lane == y))
            {
                var e = (byte[])se.Data.Clone();
                e[2] = (byte)se.Frame; e[3] = (byte)(se.Frame >> 8);
                l.Add((se.Frame, e));
            }
            sounds[y] = l.OrderBy(s => s.Item1).Take(255).ToList();
        }
        var donorEvt = recs.Evt.GetValueOrDefault(donor) ?? new();
        var wrote = new HashSet<int>();
        foreach (var (g, rec, ev) in donorEvt)
        {
            int y = rec[3];
            if (rec[2] == phase && sounds.TryGetValue(y, out var s))
            {
                if (!wrote.Add(y)) continue;
                if (s.Count > 0) lines.Add($"evt {g} {Hex(WithId(rec, id0))} {Hex(s.SelectMany(q => q.E).ToArray())}");
                continue;
            }
            lines.Add($"evt {g} {Hex(WithId(rec, id0))} {Hex(ev)}");
        }
        foreach (var (y, s) in sounds)
        {
            if (wrote.Contains(y) || s.Count == 0 || donorEvt.Count == 0) continue;
            var (g, t, _) = donorEvt[0];                       // (the donor had no record for this wrestler: same group, his key)
            var r0 = WithId(t, id0); r0[2] = (byte)phase; r0[3] = (byte)y;
            lines.Add($"evt {g} {Hex(r0)} {Hex(s.SelectMany(q => q.E).ToArray())}");
        }
        foreach (var (y, s) in sounds)
            r.Notes.Add($"{(y == 0 ? "attacker" : "receiver")} sounds: {s.Count} from {blocks.Count(b => (y == 0 ? b.Attacker : b.Receiver) != null)} block(s)" +
                        (s.Count > 0 ? $" (frames {string.Join(", ", s.Select(q => q.Frame))})" : ""));
        if (!recs.Exh.ContainsKey(donor)) r.Notes.Add("the donor has no EXH record (it may not be pickable in matches)");
        lines.Add($"wazecopy {id0} {cat.StockDonor(donor)}");
        lines.Add($"waze {id0} {Hex(dflags)}");
        lines.Add($"wazename {id0} {name}");

        // RR buffer check: stock + every installed pack's RR additions
        long add = 0;
        foreach (var l in keep.Concat(lines))
        {
            var f = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 8 && f[0] == "motion" && f[2] == rr) { var p = Path.Combine(r.Dir, f[7]); if (File.Exists(p)) add += new FileInfo(p).Length + 16; }
        }
        long others = OtherPacksIn(rr, r.Dir);
        long stock = cat.BankUse(rr);
        if (stock + add + others > RrCap * 0.98)
            throw new InvalidOperationException($"{rr} would hold {stock + add + others:N0} bytes, over the game's {RrCap:N0}-byte buffer.");
        r.Notes.Add($"{rr}: {stock + others + add:N0} of {RrCap:N0} bytes used");

        var outLines = new List<string> { "# AnimIT moves for SvR 2011 (AnimIT 2.0): spliced animations, 2011 donor records" };
        outLines.AddRange(keep.Where(l => l.Trim().Length > 0));
        outLines.AddRange(lines);
        File.WriteAllText(packTxt, string.Join("\n", outLines) + "\n");
        string manifest = Path.Combine(r.Dir, "manifest.txt");
        if (!File.Exists(manifest))
            File.WriteAllText(manifest, $"type=moves\nid={pack}\nname=AnimIT moves\nauthor=AnimIT\nversion=1.0\n");
        return r;
    }

    string RoomiestRr(List<string> notes)
    {
        var rrs = cat.Banks.Where(b => b.StartsWith("MOT/RR") && !b.StartsWith("MOT/RRBS")).Distinct().ToList();
        if (rrs.Count == 0) return "MOT/RR03";
        return rrs.OrderBy(b => cat.BankUse(b)).First();
    }

    long OtherPacksIn(string bank, string skipDir)
    {
        long n = 0;
        string mods = Path.Combine(cat.Game, "Mods");
        foreach (var p in Directory.EnumerateFiles(mods, "pack.txt", SearchOption.AllDirectories))
        {
            string d = Path.GetDirectoryName(p)!;
            if (string.Equals(Path.GetFullPath(d), Path.GetFullPath(skipDir), StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(d, "disabled"))) continue;
            foreach (var l in File.ReadLines(p))
            {
                var f = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length >= 8 && f[0] == "motion" && f[2] == bank)
                {
                    var fp = Path.Combine(d, string.Join(' ', f.Skip(7)));
                    if (File.Exists(fp)) n += new FileInfo(fp).Length + 16;
                }
            }
        }
        return n;
    }

    /// <summary>the written track reads back: same frame count, a pose on every key frame, events in place</summary>
    static void Verify(byte[] data, bool packed, List<YmksWriter.Frame> fr, int y)
    {
        var recs = Ymks.Records(data, out int n);
        if (n != fr.Count) throw new InvalidOperationException($"track {y}: wrote {n} frames, wanted {fr.Count}");
        int keys = 0;
        foreach (var (f, rec) in recs)
        {
            var (ev, pose) = Ymks.Split(rec, packed);
            if (pose.Length == 0) continue;
            var k = packed ? Ymks.DecodePacked(pose) : Ymks.DecodeBody(pose);
            if (k.Count == 0) throw new InvalidOperationException($"track {y} frame {f}: the pose does not read back");
            keys++;
        }
        if (keys != fr.Count(x => x.Pose != null)) throw new InvalidOperationException($"track {y}: {keys} poses read back of {fr.Count(x => x.Pose != null)}");
    }
}
