using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AnimIT.Core;

/// <summary>Runs the recovered offline converters in a separate, cancellable process.</summary>
public static class AnimationPorter
{
    public static readonly string WorkRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimIT", "PortedMoves");
    public static string DefaultPython
    {
        get
        {
            string bundled = Path.Combine(AppContext.BaseDirectory, "Porter", "python", "python.exe");
            if (File.Exists(bundled)) return bundled;
            string cached = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "python", "python.exe");
            return File.Exists(cached) ? cached : "python";
        }
    }

    /// <summary>a source move; Kind = its move type (AEW: from the file name), Named = a real name from the game's tables</summary>
    public sealed record SourceMove(string Id, string Name, string Variant, string Kind = "", bool Named = true)
    {
        public override string ToString() => $"{Name}   ·   {Id}" + (Variant != "000" ? $" / {Variant}" : "");
    }

    public sealed class Request
    {
        public string Source = "hctp", Path = "", Move = "", Variant = "000", Name = "";
        public int Donor;
        public bool Running;
        public int[]? Damage;
    }

    public sealed record BuiltPack(string Directory, int[] Ids, string Report);
    public sealed record SourceCatalog(List<SourceMove> Moves, string Notes);

    static async Task<JsonElement> Run(string python, Dictionary<string, object?> request, CancellationToken cancel)
    {
        string worker = Path.Combine(AppContext.BaseDirectory, "Porter", "worker.py");
        if (!File.Exists(worker)) throw new InvalidOperationException("The Porter folder is missing beside AnimIT.exe. Rebuild or copy the complete application folder.");
        string job = Path.Combine(WorkRoot, "jobs", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(job);
        string input = Path.Combine(job, "request.json"), output = Path.Combine(job, "response.json");
        await File.WriteAllTextAsync(input, JsonSerializer.Serialize(request), cancel);
        var start = new ProcessStartInfo(python.Trim().Trim('"'))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(worker)!, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string arg in new[] { "-I", "-B", worker, input, output }) start.ArgumentList.Add(arg);
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Python runtime.");
        using var registration = cancel.Register(() => { try { process.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } });
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancel);
        string log = await stderr + await stdout;
        await File.WriteAllTextAsync(Path.Combine(job, "worker.log"), log, cancel);
        if (!File.Exists(output)) throw new InvalidOperationException($"The converter stopped before returning a result. Check the Python runtime (NumPy and Pillow are required). Log: {job}");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(output, cancel));
        var result = document.RootElement.Clone();
        if (!result.GetProperty("ok").GetBoolean() || process.ExitCode != 0)
            throw new InvalidOperationException((result.TryGetProperty("error", out var error) ? error.GetString() : "Conversion failed.") + $"\n\nLog: {Path.Combine(job, "worker.log")}");
        return result;
    }

    public static async Task<SourceCatalog> List(string python, string source, string path, CancellationToken cancel)
    {
        var response = await Run(python, new() { ["command"] = "list", ["source"] = source, ["path"] = path,
            ["decoder_executable"] = Path.Combine(AppContext.BaseDirectory, "AnimIT.exe") }, cancel);
        var moves = response.GetProperty("moves").EnumerateArray().Select(m => new SourceMove(m.GetProperty("id").GetString()!, m.GetProperty("name").GetString()!, m.GetProperty("variant").GetString()!,
            m.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "", !m.TryGetProperty("named", out var nm) || nm.GetBoolean())).ToList();
        return new(moves, response.TryGetProperty("notes", out var notes) ? string.Join(Environment.NewLine, notes.EnumerateArray().Select(n => n.GetString())) : "");
    }

    /// <summary>a source move as it is (both actors, 30 fps), joints by 2011 name: for the stage before building</summary>
    public sealed record SourceSample(string[] Joints, List<double[]> Attacker, List<double[]> Receiver);

    public static async Task<SourceSample> Sample(string python, string game, string source, string path, string move, string variant, CancellationToken cancel)
    {
        var response = await Run(python, new()
        {
            ["command"] = "sample", ["source"] = source, ["path"] = path, ["game"] = game, ["move"] = move, ["variant"] = variant,
            ["decoder_executable"] = Path.Combine(AppContext.BaseDirectory, "AnimIT.exe"),
        }, cancel);
        static List<double[]> Frames(JsonElement e) => e.EnumerateArray().Select(f => f.EnumerateArray().Select(v => v.GetDouble()).ToArray()).ToList();
        return new(response.GetProperty("joints").EnumerateArray().Select(j => j.GetString()!).ToArray(),
                   Frames(response.GetProperty("attacker")), Frames(response.GetProperty("receiver")));
    }

    /// <summary>one ranked donor: lower score = better; each check is ok / warn / info</summary>
    public sealed record DonorMatch(int Id, double Score, List<(string State, string Text)> Checks);
    public sealed record DonorRanking(string Source, List<DonorMatch> Donors);

    /// <summary>the best SvR 2011 donors for a source move (donor_finder.py: start, end face, ring shakes, hit, length)</summary>
    public static async Task<DonorRanking> Donors(string python, string game, string source, string path, string move, string variant, CancellationToken cancel)
    {
        var r = await Run(python, new()
        {
            ["command"] = "donors", ["source"] = source, ["path"] = path, ["game"] = game, ["move"] = move, ["variant"] = variant,
            ["limit"] = 80, ["decoder_executable"] = Path.Combine(AppContext.BaseDirectory, "AnimIT.exe"),
        }, cancel);
        var list = r.GetProperty("donors").EnumerateArray().Select(d => new DonorMatch(d.GetProperty("id").GetInt32(), d.GetProperty("score").GetDouble(),
            d.GetProperty("checks").EnumerateArray().Select(c => (c.GetProperty("state").GetString()!, c.GetProperty("text").GetString()!)).ToList())).ToList();
        return new(r.GetProperty("source").GetString() ?? "", list);
    }

    /// <summary>match a built AEW pack's events to AEW's own markers again (event_map.py) -> notes</summary>
    public static async Task<string> MapEvents(string python, string game, string path, string pack, int id, string montage, CancellationToken cancel)
    {
        var r = await Run(python, new()
        {
            ["command"] = "mapevents", ["game"] = game, ["path"] = path, ["pack"] = pack, ["id"] = id, ["move"] = montage,
        }, cancel);
        return string.Join(Environment.NewLine, r.GetProperty("notes").EnumerateArray().Select(n => n.GetString()));
    }

    public static async Task<BuiltPack> Build(string python, GameCatalog catalog, Request request, CancellationToken cancel)
    {
        var reserved = catalog.Moves.Values.Where(m => m.Pack != null).Select(m => m.Id).ToHashSet();
        foreach (var pack in catalog.Packs)
            foreach (string line in File.ReadLines(Path.Combine(pack.Dir, "pack.txt")))
                if (MovePack.LineId(line) is int id) reserved.Add(id);
        string directory = Path.Combine(WorkRoot, "animit_port_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..8]);
        var response = await Run(python, new()
        {
            ["command"] = "port", ["source"] = request.Source, ["path"] = request.Path,
            ["game"] = catalog.Game, ["move"] = request.Move, ["variant"] = request.Variant,
            ["name"] = request.Name, ["donor"] = request.Donor,
            ["running"] = request.Running, ["damage"] = request.Damage, ["out"] = directory,
            ["decoder_executable"] = Path.Combine(AppContext.BaseDirectory, "AnimIT.exe"),
            ["reserved_ids"] = reserved.ToArray(),
        }, cancel);
        int[] ids = response.GetProperty("ids").EnumerateArray().Select(i => i.GetInt32()).ToArray();
        cancel.ThrowIfCancellationRequested();
        await Task.Run(() => Validate(catalog, directory, ids), cancel);
        var notes = new List<string>();
        foreach (var report in response.GetProperty("reports").EnumerateArray())
        {
            notes.Add($"Move {report.GetProperty("id")} · donor {report.GetProperty("donor")} {report.GetProperty("donor_name").GetString()}");
            if (report.TryGetProperty("notes", out var list)) notes.AddRange(list.EnumerateArray().Select(n => n.GetString()!));
        }
        return new(directory, ids, string.Join(Environment.NewLine, notes));
    }

    static string ChildFile(string directory, string relative)
    {
        string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(directory, relative));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("A pack file points outside its folder.");
        return full;
    }

    /// <summary>Independent C# read-back, bank limits, and id conflict checks before preview/install.</summary>
    public static void Validate(GameCatalog cat, string directory, int[] ids)
    {
        if (ids.Length == 0 || ids.Distinct().Count() != ids.Length) throw new InvalidOperationException("The converter returned no unique moves.");
        var used = new HashSet<int>(cat.StockMotionIds);
        foreach (var move in cat.Moves.Values.Where(m => m.Pack != null)) used.Add(move.Id);
        // Also include metadata-only records reserved by installed packs.
        foreach (var pack in cat.Packs)
            foreach (var line in File.ReadLines(Path.Combine(pack.Dir, "pack.txt")))
                if (MovePack.LineId(line) is int id) used.Add(id);
        foreach (int id in ids)
            if (id < 2000 || id >= 16384 || !cat.WazeIds.Contains(id) || used.Contains(id))
                throw new InvalidOperationException($"Move id {id} is unavailable. Build again after loading the current game and packs.");
        var allowed = ids.ToHashSet();
        var seen = new HashSet<(int, int, int, bool)>();
        var decoded = new HashSet<(string, bool)>();
        var extra = new Dictionary<string, long>();
        foreach (string line in File.ReadLines(Path.Combine(directory, "pack.txt")))
        {
            var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0 || p[0].StartsWith('#')) continue;
            if (MovePack.LineId(line) is int lineId && !allowed.Contains(lineId)) throw new InvalidOperationException("The pack contains an unexpected move id.");
            if (p[0] != "motion") continue;
            if (p.Length < 8 || !int.TryParse(p[3], out int id) || !int.TryParse(p[4], out int x) || !int.TryParse(p[5], out int y) || !int.TryParse(p[6], out int frames) || frames < 1 || frames > 100000)
                throw new InvalidOperationException("The pack contains an invalid motion record.");
            if (!cat.Banks.Contains(p[2]) || p[1] != (p[2].StartsWith("MOTP", StringComparison.Ordinal) ? "mpsp.pac" : "m.pac")) throw new InvalidOperationException("The pack targets an unknown motion bank.");
            bool packed = p[2].StartsWith("MOTP", StringComparison.Ordinal) && y < 3;
            string file = ChildFile(directory, string.Join(' ', p.Skip(7)));
            seen.Add((id, x, y, packed));
            if (p[2].StartsWith("MOT/RR", StringComparison.Ordinal)) extra[p[2]] = extra.GetValueOrDefault(p[2]) + new FileInfo(file).Length + 16;
            if (!decoded.Add((file, packed))) continue;
            byte[] bytes = File.ReadAllBytes(file);
            var records = Ymks.Records(bytes, out int actual);
            if (actual != frames) throw new InvalidOperationException("The motion length does not match its pack record.");
            // Camera / prop tracks have their own pose formats. Check their stream framing only.
            if (y > 2) continue;
            var t = cat.BankBytes(p[2]).AsSpan(0x10, 256).ToArray();
            bool hasPose = false;
            foreach (var (_, record) in records)
            {
                var (ev, pose) = Ymks.Split(record, packed);
                int consumed = Ymks.Events(ev, t).Sum(e => e.Data.Length);
                if (consumed != ev.Length) throw new InvalidOperationException("The motion contains an incomplete or unknown event.");
                if (pose.Length == 0) continue;
                var keys = packed ? Ymks.DecodePacked(pose) : Ymks.DecodeBody(pose);
                if (keys.Count == 0) throw new InvalidOperationException("A character pose could not be decoded.");
                hasPose = true;
                if (keys.Any(k => k.Root?.Any(v => !double.IsFinite(v)) == true)) throw new InvalidOperationException("The motion contains an invalid position.");
            }
            if (!hasPose && frames > 1) throw new InvalidOperationException("A character track contains no animation poses.");
            var track = Track.Decode(bytes, t, packed, frames);
            if (track.Frames.Count != frames) throw new InvalidOperationException("The character track could not be read back.");
        }
        foreach (var (id, phase, lane, packed) in seen.Where(k => k.Item3 < 2 && !k.Item4).ToList())
            if (!seen.Contains((id, phase, lane, true))) throw new InvalidOperationException("A character track is missing its packed in-match copy.");
        if (ids.Any(id => !seen.Any(k => k.Item1 == id && k.Item3 == 0 && !k.Item4))) throw new InvalidOperationException("A ported move has no attacker track.");
        foreach (var (bank, added) in extra)
        {
            long total = cat.BankUse(bank) + added;
            foreach (var pack in cat.Packs)
                foreach (string line in File.ReadLines(Path.Combine(pack.Dir, "pack.txt")))
                {
                    var p = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 8 && p[0] == "motion" && p[2] == bank) total += new FileInfo(ChildFile(pack.Dir, string.Join(' ', p.Skip(7)))).Length + 16;
                }
            if (total > MovePack.RrCap * 0.98) throw new InvalidOperationException($"{bank} has insufficient space for this pack.");
        }
    }

    public static string Install(string game, BuiltPack pack)
    {
        if (Process.GetProcessesByName("svr2011").Length > 0) throw new InvalidOperationException("Close SvR 2011 before installing the pack.");
        // Reload from disk so another pack installed since the build cannot cause an id collision.
        Validate(new GameCatalog(game), pack.Directory, pack.Ids);
        string target = Path.Combine(game, "Mods", "Moves", Path.GetFileName(pack.Directory));
        if (System.IO.Directory.Exists(target)) throw new InvalidOperationException("This pack is already installed.");
        // Copy outside Mods first. A partial copy must never become a live pack.
        string stage = Path.Combine(game, ".animit-port-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(stage);
        foreach (string file in System.IO.Directory.EnumerateFiles(pack.Directory, "*", SearchOption.AllDirectories))
        {
            string dest = ChildFile(stage, Path.GetRelativePath(pack.Directory, file));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, false);
        }
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        System.IO.Directory.Move(stage, target);
        return target;
    }
}
