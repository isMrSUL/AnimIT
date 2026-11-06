// Recovered from Downloads/Animation/AdefStudio; see docs/animation-porter.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace AnimIT.Core.K26;

/// <summary>
/// Array E is a little-endian, least-significant-bit-first bitstream stored in
/// 64-bit words. This mirrors the game's reader at RVA 0x155CD30.
/// </summary>
public sealed class LsbBitReader(ReadOnlyMemory<byte> words, int usedBitCount)
{
    private readonly ReadOnlyMemory<byte> _words = words;

    public int UsedBitCount { get; } = usedBitCount;

    public int Position { get; private set; }

    public uint Read(int width)
    {
        if (width is < 0 or > 31)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "unsupported bit width");
        }

        if (Position + width > UsedBitCount)
        {
            throw new InvalidDataException(
                $"bitstream overrun at bit {Position}: requested {width}, {UsedBitCount} available");
        }

        ReadOnlySpan<byte> span = _words.Span;
        uint value = 0;
        for (int index = 0; index < width; index++)
        {
            int bitPosition = Position + index;
            int bit = (span[bitPosition >> 3] >> (bitPosition & 7)) & 1;
            value |= (uint)bit << index;
        }

        Position += width;
        return value;
    }

    /// <summary>Reads without advancing, the way the runtime samples a selector code.</summary>
    public uint PeekAt(int bitPosition, int width)
    {
        int saved = Position;
        Position = bitPosition;
        try
        {
            return Read(width);
        }
        finally
        {
            Position = saved;
        }
    }

    public void Skip(int width)
    {
        if (width < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "unsupported skip width");
        }

        if (Position + width > UsedBitCount)
        {
            throw new InvalidDataException(
                $"bitstream overrun at bit {Position}: requested skip {width}, {UsedBitCount} available");
        }

        Position += width;
    }
}
