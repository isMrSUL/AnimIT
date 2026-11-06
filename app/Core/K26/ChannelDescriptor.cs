// Recovered from Downloads/Animation/AdefStudio; see docs/animation-porter.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Buffers.Binary;

namespace AnimIT.Core.K26;

/// <summary>
/// One array-D descriptor: the quantization of a single animated channel.
/// A stored code <c>q</c> dequantizes to <c>Minimum + q * 2^Exponent</c>, and a
/// zero bit width marks a constant channel whose value is the minimum.
/// </summary>
public readonly struct ChannelDescriptor
{
    public ChannelDescriptor(int index, uint control, float minimum)
    {
        Index = index;
        Control = control;
        BitWidth = (int)(control >> 8);
        Exponent = (sbyte)(control & 0xFF);
        Minimum = minimum;
    }

    public int Index { get; }
    public uint Control { get; }
    public int BitWidth { get; }
    public int Exponent { get; }
    public float Minimum { get; }

    public bool IsConstant => BitWidth == 0;

    /// <summary>
    /// The quantum, built the way the runtime does at RVA 0x155DFAF: the
    /// exponent is biased and shifted straight into an IEEE-754 float.
    /// </summary>
    public float Quantum => BitConverter.Int32BitsToSingle((Exponent + 127) << 23);

    public float Dequantize(uint code) => Minimum + (code * Quantum);

    public float Maximum => IsConstant
        ? Minimum
        : Minimum + (((1u << BitWidth) - 1) * Quantum);

    public static ChannelDescriptor Read(ArraySlice arrayD, int index)
    {
        ReadOnlySpan<byte> span = arrayD.Span[(index * 8)..];
        return new ChannelDescriptor(
            index,
            BinaryPrimitives.ReadUInt32LittleEndian(span),
            BinaryPrimitives.ReadSingleLittleEndian(span[4..]));
    }
}
