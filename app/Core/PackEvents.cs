using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AnimIT.Core;

/// <summary>The timed events of a built (not installed) move pack, for the porter's event editor: the motion events
/// in both wrestlers' phase-0 tracks (hit 86, ring shake 96-99, rumble 111, ...) and the sounds (pack.txt evt lines:
/// 4-byte {u16 sound, u16 frame}). Moving an event rewrites the m.pac track and its packed MOTP copy the same way,
/// keeping every pose byte as it is.</summary>
public sealed class PackEvents
{
    public sealed class Ev
    {
        public int Track;              // 0 attacker, 1 receiver
        public int Frame, Orig;        // where it plays now / where it was loaded
        public int Op;                 // motion event op (-1 = a sound)
        public byte[] Data = Array.Empty<byte>();
        public int Sound;              // sound id (sounds only)
        public int Line = -1;          // pack.txt line of its evt record (sounds only)
        public bool IsSound => Op < 0;
    }

    sealed class TrackFiles { public string? Unpacked, Packed; public string UBank = "", PBank = ""; public int Frames; }

    public string Dir { get; }
    public int Id { get; }
    public int[] Frames { get; } = new int[2];
    public List<Ev> Events { get; } = new();
    readonly Dictionary<int, TrackFiles> tracks = new();
    readonly GameCatalog cat;
    string[] lines = Array.Empty<string>();

    PackEvents(GameCatalog cat, string dir, int id, int phase) { this.cat = cat; Dir = dir; Id = id; Phase = phase; }
    /// <summary>the phase whose tracks / sounds are edited (0, or where a splice went: a strike donor's hit 10...)</summary>
    public int Phase { get; }

    public bool Dirty => Events.Any(e => e.Frame != e.Orig);

    public static PackEvents Load(GameCatalog cat, string dir, int id, int phase = 0)
    {
        var pe = new PackEvents(cat, dir, id, phase);
        pe.Read();
        return pe;
    }

    string Child(string rel)
    {
        string root = Path.GetFullPath(Dir) + Path.DirectorySeparatorChar, full = Path.GetFullPath(Path.Combine(Dir, rel));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("A pack file points outside its folder.");
        return full;
    }

    void Read()
    {
        Events.Clear(); tracks.Clear();
        lines = File.ReadAllLines(Path.Combine(Dir, "pack.txt"));
        foreach (var line in lines)
        {
            var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 8 || p[0] != "motion" || p[3] != Id.ToString() || p[4] != Phase.ToString() || !int.TryParse(p[5], out int y) || y > 1) continue;
            if (!tracks.TryGetValue(y, out var t)) tracks[y] = t = new TrackFiles();
            string file = Child(string.Join(' ', p.Skip(7)));
            t.Frames = int.Parse(p[6]);
            if (p[1] == "m.pac") { t.Unpacked = file; t.UBank = p[2]; }
            else if (p[1] == "mpsp.pac") { t.Packed = file; t.PBank = p[2]; }
        }
        foreach (var (y, t) in tracks)
        {
            Frames[y] = t.Frames;
            if (t.Unpacked == null) continue;
            var T = cat.EventSizes(t.UBank);
            foreach (var (f, rec) in Ymks.Records(File.ReadAllBytes(t.Unpacked), out _))
                foreach (var (op, data) in Ymks.Events(Ymks.Split(rec, false).Ev, T))
                    Events.Add(new Ev { Track = y, Frame = f, Orig = f, Op = op, Data = data });
        }
        for (int i = 0; i < lines.Length; i++)
        {
            var p = lines[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length != 4 || p[0] != "evt") continue;
            var rec = Convert.FromHexString(p[2]);
            if (rec.Length < 4 || (rec[0] | rec[1] << 8) != Id || rec[2] != Phase || rec[3] > 1) continue;
            var ev = Convert.FromHexString(p[3]);
            for (int k = 0; k + 4 <= ev.Length; k += 4)
            {
                int f = ev[k + 2] | ev[k + 3] << 8;
                Events.Add(new Ev { Track = rec[3], Frame = f, Orig = f, Op = -1, Sound = ev[k] | ev[k + 1] << 8, Line = i, Data = ev[k..(k + 4)] });
            }
        }
    }

    /// <summary>throw away unsaved moves</summary>
    public void Reload() => Read();

    /// <summary>write the moved events into the pack's track files and pack.txt</summary>
    public List<string> Save()
    {
        var notes = new List<string>();
        foreach (var (y, t) in tracks)
        {
            var moved = Events.Where(e => !e.IsSound && e.Track == y && e.Frame != e.Orig).ToList();
            if (moved.Count == 0) continue;
            if (t.Unpacked != null) notes.AddRange(Rewrite(t.Unpacked, cat.EventSizes(t.UBank), false, moved));
            if (t.Packed != null) notes.AddRange(Rewrite(t.Packed, cat.EventSizes(t.PBank), true, moved));
        }
        foreach (var grp in Events.Where(e => e.IsSound).GroupBy(e => e.Line))
        {
            if (!grp.Any(e => e.Frame != e.Orig)) continue;
            var p = lines[grp.Key].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var ev = grp.OrderBy(e => e.Frame).SelectMany(e => new[] { (byte)e.Sound, (byte)(e.Sound >> 8), (byte)e.Frame, (byte)(e.Frame >> 8) }).ToArray();
            lines[grp.Key] = $"evt {p[1]} {p[2]} {Convert.ToHexString(ev).ToLowerInvariant()}";
        }
        File.WriteAllText(Path.Combine(Dir, "pack.txt"), string.Join("\n", lines) + "\n");
        foreach (var e in Events) e.Orig = e.Frame;
        return notes;
    }

    /// <summary>one track file with the moved events at their new frames (poses kept byte for byte)</summary>
    static List<string> Rewrite(string file, byte[] T, bool packed, List<Ev> moved)
    {
        var notes = new List<string>();
        var recs = Ymks.Records(File.ReadAllBytes(file), out int n);
        var poses = new Dictionary<int, byte[]>();
        var evs = new Dictionary<int, List<byte[]>>();
        foreach (var (f, rec) in recs)
        {
            var (ev, pose) = Ymks.Split(rec, packed);
            if (pose.Length > 0) poses[f] = pose;
            evs[f] = Ymks.Events(ev, T).Select(x => x.Data).ToList();
        }
        var landed = new List<(int Frame, byte[] Data)>();
        foreach (var e in moved)
        {
            var at = evs.GetValueOrDefault(e.Orig);
            int i = at?.FindIndex(d => d.AsSpan().SequenceEqual(e.Data)) ?? -1;
            if (i < 0) { notes.Add($"{Path.GetFileName(file)}: event {e.Op} at frame {e.Orig} not found - left in place"); continue; }
            at!.RemoveAt(i);
            landed.Add((Math.Clamp(e.Frame, 0, n - 1), e.Data));
        }
        foreach (var (f, d) in landed)
        {
            if (!evs.TryGetValue(f, out var l)) evs[f] = l = new List<byte[]>();
            l.Add(d);
        }
        int limit = packed ? 0x20 : 0x7f;
        var outRecs = new List<(int, byte[])>();
        var carry = new List<byte[]>();
        for (int f = 0; f < n; f++)
        {
            var all = carry.Concat(evs.GetValueOrDefault(f) ?? new List<byte[]>()).ToList();
            carry = new List<byte[]>();
            var block = new List<byte>();
            foreach (var d in all)
                if (block.Count + d.Length <= limit) block.AddRange(d); else carry.Add(d);
            var pose = poses.GetValueOrDefault(f) ?? Array.Empty<byte>();
            if (block.Count == 0 && pose.Length == 0) continue;
            outRecs.Add((f, YmksWriter.Join(block.ToArray(), pose, packed)));
        }
        if (carry.Count > 0) throw new InvalidOperationException("Too many events on the last frames of " + Path.GetFileName(file));
        var data = YmksWriter.Stream(outRecs, n);
        // read back: same length, every pose in place, every event found
        var back = Ymks.Records(data, out int n2);
        if (n2 != n || back.Count(r => Ymks.Split(r.Rec, packed).Pose.Length > 0) != poses.Count
            || back.Sum(r => Ymks.Events(Ymks.Split(r.Rec, packed).Ev, T).Count) != evs.Values.Sum(l => l.Count))
            throw new InvalidOperationException("The rewritten track did not read back the same: " + Path.GetFileName(file));
        File.WriteAllBytes(file, data);
        return notes;
    }

    // ---------------------------------------------------------------- names
    /// <summary>a readable name, a colour and whether it is one of the main events (always shown)</summary>
    public static (string Name, string Color, bool Major) Describe(Ev e) => e.IsSound
        ? ($"Sound {e.Sound}", "#22C7E8", true)
        : e.Op switch
        {
            86 or (>= 32 and <= 36) => ("Hit (damage)", "#FF5C5C", true),
            >= 96 and <= 99 => ($"Ring shake (level {e.Op - 95})", "#F2B14C", true),
            111 => ("Controller rumble", "#B58CFF", true),
            82 or 79 => ("End position", "#82A8E4", false),
            84 => ("Onto the mat", "#82A8E4", false),
            92 => ("Start position", "#82A8E4", false),
            16 or 21 or 23 => ("Hand / limb grab", "#646E82", false),
            80 => ("State off", "#646E82", false),
            81 or 109 => ("State on", "#646E82", false),
            2 or 3 or 4 or 49 or 50 or 51 or 54 or 88 => ("Next motion", "#646E82", false),
            18 => ("Screen fade", "#646E82", false),
            64 or 72 or 73 or 76 or 77 => ("IK pin", "#646E82", false),
            67 or 74 or 229 or 68 => ("Limb lock", "#646E82", false),
            1 or 10 => ("Jump to frame", "#646E82", false),
            _ => ($"Event {e.Op}", "#646E82", false),
        };
}
