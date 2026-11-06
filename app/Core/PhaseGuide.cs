using System;
using System.Collections.Generic;
using System.Linq;

namespace AnimIT.Core;

/// <summary>Plain names for a move's phases and the chain of phases the game plays as one move, so users don't have to
/// know SvR's phase numbers. From a survey of every stock move (2026-11-05):
/// - dives (landings 30 / 40 / 50 with a receiver): 0 jump, 30 / 40 landing on a lying opponent (he rolls off one way /
///   the other), 50 landing on a standing opponent, 112 miss;
/// - strikes, running and corner attacks: 0 the approach (attacker alone), then 10 / 20 / 25-27 the hit (with the opponent);
/// - holds (Ankle Lock 2): 0 put on, a phase ending in a loop command = the hold, the other = let go;
/// - 90-92 are mostly 1-frame placeholders (hidden); a real one is an alternative version (Batista Bomb 90);
/// - grapples with 1 / 2 / 80: variations (a loop = a struggle).</summary>
public static class PhaseGuide
{
    /// <summary>a phase worth showing: not a 1-frame placeholder</summary>
    public static bool Shown(MoveInfo m, int x) => (m.AttackerAt(x) ?? 0) > 1 || (m.ReceiverAt(x) ?? 0) > 1;

    public static bool IsDive(MoveInfo m) => m.Tracks.Keys.Any(k => k.Y == 1 && k.X is 30 or 40 or 50);

    /// <summary>a phase that ends by jumping back into itself (op 1 / 10 near its end): a hold / struggle loop</summary>
    static bool Loops(Track? t) =>
        t != null && t.Events.Any(e => e.Frame >= t.Frames.Count - 3 && e.Events.Any(ev => ev.Op is 1 or 10));

    static string Who(MoveInfo m, int x) => m.ReceiverAt(x) is int r && r > 1
        ? $"both · {Math.Max(m.AttackerAt(x) ?? 0, r)} f" : $"attacker alone · {m.AttackerAt(x) ?? 0} f";

    /// <summary>"Landing on a standing opponent  ·  both · 31 f" (the plain name, then who is in it and how long)</summary>
    public static string Label(MoveInfo m, int x, Func<int, Track?> track) => $"{Name(m, x, track)}  ·  {Who(m, x)}";

    public static string Name(MoveInfo m, int x, Func<int, Track?> track)
    {
        bool rec = (m.ReceiverAt(x) ?? 0) > 1;
        bool recAt0 = (m.ReceiverAt(0) ?? 0) > 1;
        if (IsDive(m))
            return x switch
            {
                0 => "Jump",
                30 => "Landing on a lying opponent (he rolls one way)",
                40 => "Landing on a lying opponent (he rolls the other way)",
                50 => "Landing on a standing opponent",
                112 => "Miss",
                110 or 111 => "Miss (part)",
                _ => Generic(m, x, track, rec),
            };
        if (Loops(track(x))) return rec ? "Hold (loops)" : "Loop (attacker alone)";
        if (x == 0 && m.Phases.Any(p => p != 0 && Loops(track(p)))) return "Put on the hold";
        if (x == 0) return rec ? "Main move" : m.Phases.Any(p => p != 0 && (m.ReceiverAt(p) ?? 0) > 1) ? "Approach (before the hit)" : "Main move";
        if (!recAt0 && rec && x is >= 10 and < 30)
            return x is 26 or 27 ? "Corner hit" : "Hit";
        if (x is >= 90 and <= 92) return "Alternative version";
        if (x == m.MainPhase) return "Main move";
        if (x is >= 100 and < 110 && m.Phases.Any(p => p != x && Loops(track(p)))) return "Let go of the hold";
        if (x == 112 && rec) return "Ending";
        return Generic(m, x, track, rec);
    }

    static string Generic(MoveInfo m, int x, Func<int, Track?> track, bool rec) =>
        x is >= 1 and <= 9 ? $"Variation {x}" : x == 80 ? "Extra part" : rec ? $"Part {x}" : $"Part {x} (attacker alone)";

    /// <summary>the phases the game plays one after the other for this move, given the phase the user picked:
    /// a dive = jump + the picked landing (else the standing / lying one); an approach + its hit; a hold = put on +
    /// hold + let go; anything else = just the picked phase.</summary>
    public static List<int> WholeMove(MoveInfo m, int picked, Func<int, Track?> track)
    {
        var ph = m.Phases.Where(x => Shown(m, x)).ToList();
        if (IsDive(m) && ph.Contains(0))
        {
            int land = picked is 30 or 40 or 50 or 112 && ph.Contains(picked) ? picked
                     : ph.Contains(50) ? 50 : ph.FirstOrDefault(x => x is 30 or 40, -1);
            return land < 0 ? new() { 0 } : new() { 0, land };
        }
        bool recAt0 = (m.ReceiverAt(0) ?? 0) > 1;
        if (!recAt0 && ph.Contains(0))
        {
            var hits = ph.Where(x => x is >= 10 and < 30 && (m.ReceiverAt(x) ?? 0) > 1).ToList();
            if (hits.Count > 0) return new() { 0, hits.Contains(picked) ? picked : hits[0] };
        }
        var loop = ph.FirstOrDefault(x => x != 0 && Loops(track(x)), -1);
        if (ph.Contains(0) && loop >= 0)
        {
            var chain = new List<int> { 0, loop };
            var release = ph.FirstOrDefault(x => x is >= 100 and < 110 && x != loop, -1);
            if (release >= 0) chain.Add(release);
            return chain;
        }
        return new() { picked };
    }

    /// <summary>"Jump + Landing on a standing opponent"</summary>
    public static string ChainText(MoveInfo m, List<int> chain, Func<int, Track?> track) =>
        string.Join(" + ", chain.Select(x => Name(m, x, track)));
}
