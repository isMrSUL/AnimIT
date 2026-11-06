// Recovered from Downloads/Animation/AdefStudio; see docs/animation-porter.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AnimIT.Core.K26;

/// <summary>
/// The 64-bit FNV-1a hash the game uses for clip names, ADEF node names and
/// skeleton joint names. Input is the case-sensitive UTF-8 encoding of the name.
/// </summary>
public static class Fnv1a64
{
    private const ulong OffsetBasis = 0xCBF29CE484222325UL;
    private const ulong Prime = 0x100000001B3UL;

    public static ulong Hash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Hash(Encoding.UTF8.GetBytes(text));
    }

    public static ulong Hash(ReadOnlySpan<byte> bytes)
    {
        ulong hash = OffsetBasis;
        foreach (byte value in bytes)
        {
            hash ^= value;
            hash *= Prime;
        }

        return hash;
    }

    /// <summary>Formats a hash the way the research notes and the UI show it.</summary>
    public static string ToHex(ulong hash) => hash.ToString("X16");

    /// <summary>
    /// Parses a hash written as 16 hex digits or as an unsigned decimal value.
    /// </summary>
    public static bool TryParse(string text, out ulong hash)
    {
        hash = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        if (trimmed.Length == 16 &&
            ulong.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber, null, out hash))
        {
            return true;
        }

        return ulong.TryParse(trimmed, out hash);
    }
}
