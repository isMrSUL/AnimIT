using System;
using System.Collections.Generic;
using System.Linq;

namespace AnimIT.Core;

/// <summary>A move's type from its record's 16 category bytes (MOVS/WAZE +0x00: one bit per Create-a-Moveset slot list).
/// The bits were read off the game's own moves (AnimIT --bitmap) and the user's 17 test moves with known lists: 0 taunt,
/// 16 standing strong strike, 26 front grapple, 37/39 ground facing up, 50 corner grapple, 54 seated corner, 58 corner top
/// rope, 62 tree of woe, 67 groggy on the ropes, 76-79 apron, 87/88 diving vs standing / downed, 90 running grapple,
/// 92 running ground strike, 99-102 double team, 103/105 ring in. A move with several gets the most specific list first.</summary>
public static class MoveCategory
{
    static readonly (string Name, int[] Bits)[] Groups =
    {
        ("Taunts", new[] { 0 }),
        ("Ring in / out", R(103, 108)),
        ("Double team", R(99, 102)),
        ("Diving", new[] { 87, 88 }),
        ("Springboard", new[] { 68, 69, 70, 74, 75 }),
        ("Running", new[] { 83, 89, 90, 91, 92, 93, 95, 96, 97 }),
        ("Apron", new[] { 72, 73, 76, 77, 78, 79, 80, 81, 82, 84 }),
        ("Rope", R(64, 67)),
        ("Corner", R(48, 63)),
        ("Ground", R(36, 47)),
        ("Submissions", new[] { 24, 25, 28, 29, 113, 114, 115, 116, 117, 118, 119, 125 }),
        ("Reversals", new[] { 21, 22, 23, 94, 98 }),
        ("Standing strikes", new[] { 6, 7, 13, 14, 15, 16, 17, 18, 19, 20 }),
        ("Standing grapples", new[] { 26, 27, 30, 31, 32, 33, 34, 35 }),
        ("Finishers", new[] { 4, 5, 8, 9, 10, 11, 12, 126 }),
    };

    static int[] R(int a, int b) => Enumerable.Range(a, b - a + 1).ToArray();

    public const string Other = "Other";

    /// <summary>the list, in the order shown</summary>
    public static IReadOnlyList<string> Names { get; } = new[]
    {
        "Standing strikes", "Standing grapples", "Submissions", "Ground", "Corner", "Rope", "Apron", "Springboard", "Diving",
        "Running", "Double team", "Reversals", "Finishers", "Taunts", "Ring in / out", Other,
    };

    public static string Of(byte[]? flags)
    {
        if (flags == null || flags.Length < 16) return Other;
        bool Has(int b) => (flags[b / 8] >> (b % 8) & 1) != 0;
        foreach (var (name, bits) in Groups)
            if (bits.Any(Has)) return name;
        return Other;
    }
}
