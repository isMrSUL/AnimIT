using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AnimIT.Core;

/// <summary>one motion: where it lives (a stock bank in m.pac / mpsp.pac, or an installed pack's file)</summary>
public sealed class MotionRef
{
    public int Id, X, Y, Frames;
    public string Kind = "YMKs", Bank = "", Pac = "";
    public string? File, Pack;
    /// <summary>a MOTP bank's character tracks (0-2) are packed - stock and pack files alike</summary>
    public bool Packed => Bank.StartsWith("MOTP", StringComparison.Ordinal) && Y < 3;
}

public sealed class MoveInfo
{
    public int Id;
    public string Name = "";
    public bool Usable;
    /// <summary>the Create-a-Moveset list it belongs to (MoveCategory)</summary>
    public string Category = MoveCategory.Other;
    public string? Pack;
    public SortedDictionary<(int X, int Y), int> Tracks = new();
    /// <summary>the phases (track x) the attacker plays: 0 for grapples; strikes / dives have more (10 hit, 30/40/50 dives, 90 miss...)</summary>
    public IEnumerable<int> Phases => Tracks.Keys.Where(k => k.Y == 0).Select(k => k.X);
    /// <summary>the phase where both performers act (a strike's receiver is in 10, not 0), else the first</summary>
    public int MainPhase => Phases.Where(x => Tracks.ContainsKey((x, 1))).Cast<int?>().FirstOrDefault() ?? Phases.Cast<int?>().FirstOrDefault() ?? 0;
    public int? AttackerAt(int x) => Tracks.TryGetValue((x, 0), out var f) ? f : null;
    public int? ReceiverAt(int x) => Tracks.TryGetValue((x, 1), out var f) ? f : null;
    public int? Attacker => AttackerAt(MainPhase);
    public int? Receiver => ReceiverAt(MainPhase);
}

/// <summary>The game's moves, read only. pac\m.pac / pac\mpsp.pac are indexed once (each bank's file offset and
/// PACH path, its entries' keys and lengths); a motion is read from the file only when it is opened. misc.pac
/// MOVS/WAZE gives names and category bits; Mods\Moves\*\pack.txt and Mods\Superstars\*\moves\pack.txt add the
/// installed packs' motions on top (a pack's motion replaces the stock one in the game).</summary>
public sealed class GameCatalog
{
    static readonly HashSet<int> Sentinel = new() { 0x7fff };

    public string Game { get; }
    string PacDir => Path.Combine(Game, "pac");
    readonly Dictionary<(int, int, int), List<MotionRef>> motions = new();
    readonly Dictionary<string, byte[]> bankT = new();
    readonly Dictionary<string, (string Pac, long Off, int Size, int[] Path)> bankLoc = new();
    readonly Dictionary<string, byte[]> bankCache = new();
    readonly Queue<string> bankOrder = new();
    public Dictionary<int, string> Names { get; } = new();
    readonly Dictionary<int, byte[]> flags = new();
    public List<(string Name, string Dir, int Motions)> Packs { get; } = new();
    readonly Dictionary<int, int> donorRecords = new();
    public int StockDonor(int id)
    {
        var seen = new HashSet<int>();
        while (donorRecords.TryGetValue(id, out int donor))
        {
            if (!seen.Add(id)) throw new InvalidOperationException("A pack contains a circular donor reference.");
            id = donor;
        }
        return id;
    }
    public Dictionary<int, MoveInfo> Moves { get; private set; } = new();

    public GameCatalog(string game)
    {
        Game = game;
        if (!System.IO.File.Exists(Path.Combine(PacDir, "m.pac")))
            throw new InvalidOperationException("No pac\\m.pac here - choose the SvR 2011 game folder.");
        Index("m.pac");
        Index("mpsp.pac");
        ReadWaze();
        ReadPacks();
        Moves = BuildMoves();
    }

    // ---------------------------------------------------------------- stock banks
    internal static List<(string Typ, string Name, long Off, int Size)> TopEntries(string path)
    {
        var head = new byte[0x4000];
        using (var f = System.IO.File.OpenRead(path)) f.ReadExactly(head);
        if (!Bin.Tag(head, "EPAC")) throw new InvalidOperationException(path + " is not an EPAC");
        var o = new List<(string, string, long, int)>();
        int p = 0x800;
        while (p < 0x4000)
        {
            if (Bin.U32(head, p) == 0) break;
            string typ = Encoding.ASCII.GetString(head, p, 4).Trim('\0', ' ');
            int cnt = (int)Bin.U32(head, p + 4) / 3;
            p += 12;
            for (int i = 0; i < cnt; i++)
            {
                string name = Encoding.ASCII.GetString(head, p, 4).Trim('\0', ' ');
                long sec = Bin.U32(head, p + 4), sz = Bin.U32(head, p + 8);
                o.Add((typ, name, 0x4000 + sec * 0x800, (int)(sz * 0x100)));
                p += 12;
            }
        }
        return o;
    }

    void Index(string pacName)
    {
        string path = Path.Combine(PacDir, pacName);
        using var f = System.IO.File.OpenRead(path);
        foreach (var (typ, name, off, size) in TopEntries(path))
        {
            var blob = new byte[size];
            f.Position = off;
            f.ReadExactly(blob);
            Walk(Bpe.Unpack(blob), typ + "/" + name, pacName, off, size, Array.Empty<int>());
        }
    }

    void Walk(byte[] raw, string path, string pac, long off, int size, int[] ids)
    {
        if (Bin.Tag(raw, "PACH"))
        {
            foreach (var (i, d) in Pach.Read(raw))
                Walk(Bpe.Unpack(d), path + "/" + i.ToString("x"), pac, off, size, ids.Append(i).ToArray());
            return;
        }
        bool ymks = Bin.Tag(raw, "YMKs");
        if (!ymks && !Bin.Tag(raw, "YMBs")) return;
        if (pac == "mpsp.pac" && !path.StartsWith("MOTP", StringComparison.Ordinal)) return;
        bankT[path] = raw.AsSpan(0x10, 0x100).ToArray();
        bankLoc[path] = (pac, off, size, ids);
        foreach (var e in BankEntries(raw))
        {
            if (Sentinel.Contains(e.Id)) continue;
            Add(new MotionRef { Id = e.Id, X = e.X, Y = e.Y, Frames = e.Frames, Kind = ymks ? "YMKs" : "YMBs", Bank = path, Pac = pac });
        }
    }

    void Add(MotionRef m)
    {
        var k = (m.Id, m.X, m.Y);
        if (!motions.TryGetValue(k, out var l)) motions[k] = l = new List<MotionRef>();
        l.Add(m);
    }

    /// <summary>a bank's entries in directory order; data = bytes up to the next distinct offset (svr10_moves)</summary>
    public static List<(int Y, int X, int Id, int Frames, byte[] Data)> BankEntries(byte[] raw)
    {
        int n = (int)Bin.U32(raw, 0x110), b = 0x114 + 16 * n;
        var ents = new List<(int Y, int X, int Id, uint Off, int Frames)>(n);
        for (int i = 0; i < n; i++)
        {
            int o = 0x114 + 16 * i;
            ents.Add((raw[o], raw[o + 1], Bin.U16(raw, o + 2), Bin.U32(raw, o + 4), (int)Bin.U32(raw, o + 8)));
        }
        var offs = ents.Select(e => e.Off).Distinct().OrderBy(x => x).ToList();
        offs.Add((uint)(raw.Length - b));
        var next = new Dictionary<uint, uint>();
        for (int i = 0; i + 1 < offs.Count; i++) next[offs[i]] = offs[i + 1];
        byte[] Cut(uint off)                      // (python-like slice: clipped to the bank, empty past its end)
        {
            long s = Math.Min(raw.Length, b + (long)off), e = Math.Min(raw.Length, b + (long)next[off]);
            return e > s ? raw.AsSpan((int)s, (int)(e - s)).ToArray() : Array.Empty<byte>();
        }
        return ents.Select(e => (e.Y, e.X, e.Id, e.Frames, Cut(e.Off))).ToList();
    }

    byte[] BankRaw(string path)
    {
        if (bankCache.TryGetValue(path, out var c)) return c;
        var (pac, off, size, ids) = bankLoc[path];
        var blob = new byte[size];
        using (var f = System.IO.File.OpenRead(Path.Combine(PacDir, pac))) { f.Position = off; f.ReadExactly(blob); }
        var raw = Bpe.Unpack(blob);
        foreach (int i in ids) raw = Bpe.Unpack(Pach.Read(raw).First(x => x.Id == i).Data);
        if (bankOrder.Count > 12) bankCache.Remove(bankOrder.Dequeue());
        bankCache[path] = raw;
        bankOrder.Enqueue(path);
        return raw;
    }

    // ---------------------------------------------------------------- names
    void ReadWaze()
    {
        var data = System.IO.File.ReadAllBytes(Path.Combine(PacDir, "misc.pac"));
        byte[]? wz = null;
        int p = 0x800;
        while (p < 0x4000 && wz == null)
        {
            if (Bin.U32(data, p) == 0) break;
            string typ = Encoding.ASCII.GetString(data, p, 4);
            int cnt = (int)Bin.U32(data, p + 4) / 3;
            p += 12;
            for (int i = 0; i < cnt; i++, p += 12)
            {
                if (typ == "MOVS" && Encoding.ASCII.GetString(data, p, 4) == "WAZE")
                {
                    long off = 0x4000 + Bin.U32(data, p + 4) * 0x800L;
                    int sz = (int)Bin.U32(data, p + 8) * 0x100;
                    wz = Bpe.Unpack(data.AsSpan((int)off, sz).ToArray());
                }
            }
        }
        if (wz == null) return;
        int st = IndexOf(wz, Encoding.ASCII.GetBytes("* test motion *")) - 0x10;
        if (st < 0) return;
        for (int o = st; o + 160 <= wz.Length; o += 160)
        {
            var r = wz.AsSpan(o, 160);
            if (!r.ContainsAnyExcept((byte)0)) continue;
            int id = Bin.U16(r, 0x90);
            if (Names.ContainsKey(id)) continue;
            var nm = r.Slice(0x10, 0x80);
            int z = nm.IndexOf((byte)0);
            Names[id] = Encoding.Latin1.GetString(z >= 0 ? nm[..z] : nm).Trim();
            flags[id] = r[..16].ToArray();
            WazeIds.Add(id);
        }
    }

    static int IndexOf(byte[] h, byte[] n) => h.AsSpan().IndexOf(n);

    /// <summary>a move players can pick in 2011: category bits set</summary>
    /// <summary>ids that have a stock move record (a pack can only re-use one of these: wazecopy)</summary>
    public HashSet<int> WazeIds { get; } = new();
    /// <summary>a move record's first 16 bytes (category bits)</summary>
    public byte[]? Flags(int id) => flags.TryGetValue(id, out var f) ? f : null;
    /// <summary>every id that has a stock motion</summary>
    public IEnumerable<int> StockMotionIds => motions.Where(kv => kv.Value.Any(m => m.Pack == null)).Select(kv => kv.Key.Item1).Distinct();
    public IEnumerable<string> Banks => bankLoc.Keys;
    /// <summary>bytes a stock bank's motions take in the game's buffer (data + 16 per entry)</summary>
    /// <summary>a stock bank's whole unpacked bytes (YMBs motions need their bank's tables)</summary>
    public byte[] BankBytes(string path) => BankRaw(path);
    public long BankUse(string path) => BankEntries(BankRaw(path)).Sum(e => (long)e.Data.Length + 16);

    public bool Usable(int id) => flags.TryGetValue(id, out var f) && f.AsSpan(0, 15).ContainsAnyExcept((byte)0);

    // ---------------------------------------------------------------- installed packs
    IEnumerable<string> PackDirs()
    {
        string mods = Path.Combine(Game, "Mods");
        var o = new List<string>();
        string moves = Path.Combine(mods, "Moves");
        if (Directory.Exists(moves)) o.AddRange(Directory.GetDirectories(moves).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        string sup = Path.Combine(mods, "Superstars");
        if (Directory.Exists(sup))
            o.AddRange(Directory.GetDirectories(sup).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(d => Path.Combine(d, "moves")));
        return o.Where(d => System.IO.File.Exists(Path.Combine(d, "pack.txt")) && !System.IO.File.Exists(Path.Combine(d, "disabled")));
    }

    void ReadPacks()
    {
        foreach (var d in PackDirs())
            ReadPack(d);
    }

    /// <summary>Add a built pack for preview without installing it or clearing the timeline.</summary>
    public void LoadPreviewPack(string directory)
    {
        if (Packs.Any(p => string.Equals(Path.GetFullPath(p.Dir), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))) return;
        ReadPack(directory);
        Moves = BuildMoves();
    }

    // what a preview pack replaced (names / flags / donor records), to put back when it is removed
    readonly Dictionary<string, (Dictionary<int, string> Names, Dictionary<int, byte[]> Flags, Dictionary<int, int> Donors)> previewUndo = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>load a built (not installed) pack for a look; <see cref="UnloadPreviewPack"/> takes it out again</summary>
    public bool LoadTemporaryPack(string directory)
    {
        string full = Path.GetFullPath(directory);
        if (Packs.Any(p => string.Equals(Path.GetFullPath(p.Dir), full, StringComparison.OrdinalIgnoreCase))) return false;
        previewUndo[full] = (new(Names), new(flags), new(donorRecords));
        ReadPack(directory);
        Moves = BuildMoves();
        return true;
    }

    public void UnloadPreviewPack(string directory)
    {
        string full = Path.GetFullPath(directory);
        if (!previewUndo.Remove(full, out var undo)) return;
        Packs.RemoveAll(p => string.Equals(Path.GetFullPath(p.Dir), full, StringComparison.OrdinalIgnoreCase));
        string root = full.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var k in motions.Keys.ToList())
        {
            motions[k].RemoveAll(m => m.File != null && m.File.StartsWith(root, StringComparison.OrdinalIgnoreCase));
            if (motions[k].Count == 0) motions.Remove(k);
        }
        Names.Clear(); foreach (var kv in undo.Names) Names[kv.Key] = kv.Value;
        flags.Clear(); foreach (var kv in undo.Flags) flags[kv.Key] = kv.Value;
        donorRecords.Clear(); foreach (var kv in undo.Donors) donorRecords[kv.Key] = kv.Value;
        Moves = BuildMoves();
    }

    void ReadPack(string d)
    {
        {
            string name = Path.GetFileName(d) == "moves" ? Path.GetFileName(Path.GetDirectoryName(d)!) : Path.GetFileName(d);
            int n = 0;
            foreach (var line in System.IO.File.ReadLines(Path.Combine(d, "pack.txt")))
            {
                var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 8 && p[0] == "motion"
                    && int.TryParse(p[3], out int id) && int.TryParse(p[4], out int x) && int.TryParse(p[5], out int y) && int.TryParse(p[6], out int fr))
                {
                    string file = Path.GetFullPath(Path.Combine(d, string.Join(' ', p.Skip(7))));
                    if (!System.IO.File.Exists(file)) continue;
                    int shown = id >= 16384 && y >= 50 ? id - 16384 : id;
                    var m = new MotionRef { Id = id, X = x, Y = y, Frames = fr, Bank = p[2], Pac = p[1], File = file, Pack = name };
                    var k = (shown, x, y);
                    if (!motions.TryGetValue(k, out var l)) motions[k] = l = new List<MotionRef>();
                    l.Add(m);
                    n++;
                }
                else if (p.Length >= 3 && p[0] == "wazename" && int.TryParse(p[1], out int wid))
                    Names[wid] = string.Join(' ', p.Skip(2));
                else if (p.Length >= 3 && p[0] == "waze" && int.TryParse(p[1], out int fid) && p[2].Length == 32)
                    flags[fid] = Convert.FromHexString(p[2]);   // (a pack move's own category bits)
                else if (p.Length >= 3 && p[0] == "wazecopy" && int.TryParse(p[1], out int target) && int.TryParse(p[2], out int donor))
                    donorRecords[target] = donor;
            }
            Packs.Add((name, d, n));
        }
    }

    // ---------------------------------------------------------------- moves
    /// <summary>the motion the game plays: a pack's over the stock one; YMKs over YMBs; a non-MOTP bank first</summary>
    public MotionRef? Best(int id, int x, int y)
    {
        if (!motions.TryGetValue((id, x, y), out var ms) || ms.Count == 0) return null;
        var packs = ms.Where(m => m.Pack != null).ToList();
        if (packs.Count > 0) return packs[^1];
        var ymks = ms.Where(m => m.Kind == "YMKs").ToList();
        if (ymks.Count == 0) ymks = ms;
        return ymks.FirstOrDefault(m => !m.Bank.StartsWith("MOTP", StringComparison.Ordinal)) ?? ymks[0];
    }

    public byte[] Data(MotionRef m)
    {
        if (m.File != null) return System.IO.File.ReadAllBytes(m.File);
        foreach (var e in BankEntries(BankRaw(m.Bank)))
            if (e.Id == m.Id && e.X == m.X && e.Y == m.Y) return e.Data;
        throw new KeyNotFoundException($"{m.Bank}: {m.Id} {m.X} {m.Y}");
    }

    public byte[] T(MotionRef m) => bankT.TryGetValue(m.Bank, out var t) ? t : bankT.Values.First();
    /// <summary>a bank's event size table (bytes per event op)</summary>
    public byte[] EventSizes(string bank) => bankT.TryGetValue(bank, out var t) ? t : bankT.Values.First();

    /// <summary>the banks a stock move is stored in</summary>
    public IEnumerable<MotionRef> Copies(int id, int x, int y) =>
        motions.TryGetValue((id, x, y), out var l) ? l : Enumerable.Empty<MotionRef>();

    Dictionary<int, MoveInfo> BuildMoves()
    {
        var o = new Dictionary<int, MoveInfo>();
        foreach (var ((id, x, y), ms) in motions)
        {
            if (y > 2 && y < 50) continue;
            var m = Best(id, x, y)!;
            if (!o.TryGetValue(id, out var mv))
                o[id] = mv = new MoveInfo { Id = id, Name = Names.TryGetValue(id, out var n) && n.Length > 0 ? n : $"Move {id}", Usable = Usable(id), Category = MoveCategory.Of(Flags(id)) };
            mv.Tracks[(x, y)] = m.Frames;
            if (m.Pack != null) mv.Pack = m.Pack;
        }
        return o.Where(kv => kv.Value.Attacker > 1).ToDictionary(kv => kv.Key, kv => kv.Value);
    }
}
