using System;
using System.Collections.Generic;
using System.Linq;

namespace AnimIT.Core;

/// <summary>The game's own hit reactions for strikes that carry no receiver track (People's Punch 2, Gut Punch...): the game
/// plays one of its basic reaction motions on the receiver when the hit lands. Learned from in-game captures
/// (research\hitlog: the receiver's motion at the hit, CPU matches 2026-10-23, ~100 hits): the strike's EXH byte 0x10 (hit
/// type) picks the family, the receiver's situation the variant (front / side / down), and the game varies it at runtime -
/// so these lists are the reactions seen, most common first, for a STANDING receiver hit from the FRONT (a downed one for
/// ground strikes). The hit lands on the strike's first op 87 / 109 (the impact commands), else EXH byte 0x22.</summary>
public static class HitReactions
{
    /// <summary>(id, phase, track) of reaction motions seen in game, by hit type</summary>
    static readonly Dictionary<int, (int Id, int X, int Y)[]> Front = new()
    {
        [1] = new[] { (302, 1, 0) },
        [2] = new[] { (302, 1, 0), (302, 1, 1), (301, 1, 0), (300, 1, 1), (309, 1, 0) },
        [6] = new[] { (540, 0, 0), (360, 3, 0) },
        [13] = new[] { (430, 1, 0) },
        [14] = new[] { (302, 1, 1), (307, 1, 0), (560, 0, 0) },
        [15] = new[] { (307, 1, 1) },
        [17] = new[] { (430, 0, 0), (460, 0, 0), (430, 1, 0) },
    };
    static readonly (int, int, int)[] StandingDefault = { (302, 1, 0), (301, 1, 0), (300, 1, 1) };
    static readonly (int, int, int)[] DownDefault = { (430, 0, 0), (430, 1, 0), (460, 0, 0) };

    /// <summary>the reactions to offer for a strike (most likely first); empty when the move has its own receiver</summary>
    public static IReadOnlyList<(int Id, int X, int Y)> For(int? hitType, string category)
    {
        bool down = category == "Ground";
        if (hitType is int t && Front.TryGetValue(t, out var l)) return l;
        return down ? DownDefault : StandingDefault;
    }

    /// <summary>how far ahead the game had the receiver at these hits (captures: standing ~12.5, downed ~16)</summary>
    public static double Distance(string category) => category == "Ground" ? 16.0 : 12.5;

    /// <summary>the frame the strike lands: its first impact command (op 87 / 109), else the EXH hit byte, else 40%</summary>
    public static int HitFrame(Track attacker, byte[]? exh)
    {
        foreach (var (f, evs) in attacker.Events)
            if (evs.Any(e => e.Op == 87 || e.Op == 109)) return f;
        if (exh != null && exh.Length > 0x22 && exh[0x22] > 0 && exh[0x22] < attacker.Frames.Count) return exh[0x22];
        return attacker.Frames.Count * 2 / 5;
    }
}
