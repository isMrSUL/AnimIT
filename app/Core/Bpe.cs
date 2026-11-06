using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace AnimIT.Core;

/// <summary>Little helpers for the game's containers (EPAC / PACH / BPE: little-endian; JBOY: big-endian).</summary>
public static class Bin
{
    public static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d[o..]);
    public static ushort U16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d[o..]);
    public static short S16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt16LittleEndian(d[o..]);
    public static float F32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadSingleLittleEndian(d[o..]);
    public static uint U32BE(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32BigEndian(d[o..]);
    public static int I32BE(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt32BigEndian(d[o..]);
    public static float F32BE(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadSingleBigEndian(d[o..]);
    public static bool Tag(ReadOnlySpan<byte> d, string tag) =>
        d.Length >= 4 && d[0] == tag[0] && d[1] == tag[1] && d[2] == tag[2] && d[3] == tag[3];
}

/// <summary>The game's "BPE " compression (byte-pair encoding in blocks): svrfmt.bpe_decode.</summary>
public static class Bpe
{
    public static byte[] Unpack(byte[] data) => Bin.Tag(data, "BPE ") ? Decode(data) : data;

    public static byte[] Decode(byte[] src)
    {
        if (!Bin.Tag(src, "BPE ")) throw new InvalidOperationException("not BPE");
        int packed = (int)Bin.U32(src, 8), unpacked = (int)Bin.U32(src, 12);
        int pos = 16, end = 16 + packed;
        var outp = new List<byte>(unpacked);
        var left = new byte[256];
        var right = new byte[256];
        var stack = new byte[512];
        while (pos < end)
        {
            for (int i = 0; i < 256; i++) { left[i] = (byte)i; right[i] = 0; }
            int c = 0;
            int count = src[pos++];
            while (true)
            {
                if (count > 127) { c += count - 127; count = 0; }
                if (c == 256) break;
                for (int k = 0; k <= count; k++)
                {
                    left[c] = src[pos++];
                    if (c != left[c]) right[c] = src[pos++];
                    c++;
                }
                if (c == 256) break;
                count = src[pos++];
            }
            int size = src[pos] | (src[pos + 1] << 8);
            pos += 2;
            for (int i = 0; i < size; i++)
            {
                int sp = 0;
                stack[sp++] = src[pos + i];
                while (sp > 0)                   // expand pairs depth-first, left first
                {
                    byte s = stack[--sp];
                    if (left[s] == s) outp.Add(s);
                    else { stack[sp++] = right[s]; stack[sp++] = left[s]; }
                }
            }
            pos += size;
        }
        if (outp.Count != unpacked) throw new InvalidOperationException($"BPE size {outp.Count} != {unpacked}");
        return outp.ToArray();
    }
}

/// <summary>PACH: a table of (id, offset, size) children (svrfmt.pach_read).</summary>
public static class Pach
{
    public static List<(int Id, byte[] Data)> Read(byte[] data)
    {
        if (!Bin.Tag(data, "PACH")) throw new InvalidOperationException("not PACH");
        int n = (int)Bin.U32(data, 4);
        int b = 8 + 12 * n;
        var o = new List<(int, byte[])>(n);
        for (int i = 0; i < n; i++)
        {
            int id = (int)Bin.U32(data, 8 + 12 * i), off = (int)Bin.U32(data, 12 + 12 * i), sz = (int)Bin.U32(data, 16 + 12 * i);
            o.Add((id, data.AsSpan(b + off, sz).ToArray()));
        }
        return o;
    }
}
