// Recovered from Downloads/Animation/AdefStudio; see docs/animation-porter.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Buffers.Binary;

namespace AnimIT.Core.K26;

/// <summary>A contiguous slice of an entry payload holding one of the five arrays.</summary>
public readonly struct ArraySlice(int count, int elementSize, int offset, ReadOnlyMemory<byte> data)
{
    public int Count { get; } = count;
    public int ElementSize { get; } = elementSize;
    public int Offset { get; } = offset;
    public ReadOnlyMemory<byte> Data { get; } = data;

    public ReadOnlySpan<byte> Span => Data.Span;
}

/// <summary>
/// The five arrays that follow an entry's 0xA0-byte header. The layout is
/// documented in CLIPS_FORMAT_NOTES.md and holds for every entry in the archive.
/// </summary>
public sealed class EntryArrays
{
    public required ArraySlice TrackIds { get; init; }
    public required ArraySlice ArrayB { get; init; }
    public required ArraySlice ArrayC { get; init; }
    public required ArraySlice ArrayD { get; init; }
    public required ArraySlice ArrayE { get; init; }
}

/// <summary>One embedded clip inside a bank.</summary>
public sealed class ClipEntry
{
    public required int Index { get; init; }
    public required ulong Hash { get; init; }
    public required int Offset { get; init; }
    public required ReadOnlyMemory<byte> Payload { get; init; }

    public string HashHex => Fnv1a64.ToHex(Hash);
    public int Size => Payload.Length;

    private ReadOnlySpan<byte> Header => Payload.Span;

    public ushort AnimationId => BinaryPrimitives.ReadUInt16LittleEndian(Header[0x08..]);
    public float FrameRate => BinaryPrimitives.ReadSingleLittleEndian(Header[0x0C..]);
    public int TrackCount => (int)BinaryPrimitives.ReadUInt32LittleEndian(Header[0x20..]);
    public int SpecialTrackIndex => (int)BinaryPrimitives.ReadUInt32LittleEndian(Header[0x40..]);
    public int SampleCount => (int)BinaryPrimitives.ReadUInt32LittleEndian(Header[0x44..]);
    public int UsedBitCount => (int)BinaryPrimitives.ReadUInt32LittleEndian(Header[0x9C..]);

    /// <summary>Splits the payload into its five arrays, validating every boundary.</summary>
    public EntryArrays SplitArrays()
    {
        ReadOnlySpan<byte> payload = Payload.Span;
        if (payload.Length < 0xA0)
        {
            throw new InvalidDataException(
                $"entry {Index}: payload is shorter than the 0xA0 header");
        }

        int offset = 0xA0;

        ArraySlice trackIds = ReadArray(payload, 0x20, 8, "trackIds", ref offset);
        ArraySlice arrayB = ReadArray(payload, 0x38, 4, "arrayB", ref offset);

        // Array C starts on an 8-byte boundary; an odd array-B count leaves
        // four bytes of padding here.
        offset = (offset + 7) & ~7;

        ArraySlice arrayC = ReadArray(payload, 0x60, 8, "arrayC", ref offset);
        ArraySlice arrayD = ReadArray(payload, 0x78, 8, "arrayD", ref offset);
        ArraySlice arrayE = ReadArray(payload, 0x90, 8, "arrayE", ref offset);

        if (offset != payload.Length)
        {
            throw new InvalidDataException(
                $"entry {Index}: decoded arrays end at {offset}, payload ends at {payload.Length}");
        }

        return new EntryArrays
        {
            TrackIds = trackIds,
            ArrayB = arrayB,
            ArrayC = arrayC,
            ArrayD = arrayD,
            ArrayE = arrayE,
        };
    }

    private ArraySlice ReadArray(
        ReadOnlySpan<byte> payload,
        int countOffset,
        int elementSize,
        string name,
        ref int offset)
    {
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload[countOffset..]);
        long end = (long)offset + ((long)count * elementSize);
        if (count < 0 || end > payload.Length)
        {
            throw new InvalidDataException($"entry {Index}: {name} exceeds its payload");
        }

        var slice = new ArraySlice(count, elementSize, offset, Payload[offset..(int)end]);
        offset = (int)end;
        return slice;
    }

    public ulong TrackHash(EntryArrays arrays, int trackIndex) =>
        BinaryPrimitives.ReadUInt64LittleEndian(arrays.TrackIds.Span[(trackIndex * 8)..]);
}

/// <summary>
/// A `.clips` container: a 16-byte reserved header, an entry count, a table of
/// 16-byte records, then contiguous payloads.
/// </summary>
public sealed class ClipBank
{
    private ClipBank(string source, ReadOnlyMemory<byte> data, IReadOnlyList<ClipEntry> entries)
    {
        Source = source;
        Data = data;
        Entries = entries;
    }

    public string Source { get; }
    public ReadOnlyMemory<byte> Data { get; }
    public IReadOnlyList<ClipEntry> Entries { get; }

    public static ClipBank Load(string path) =>
        Parse(File.ReadAllBytes(path), path);

    public static ClipBank Parse(ReadOnlyMemory<byte> buffer, string source = "<buffer>")
    {
        ReadOnlySpan<byte> span = buffer.Span;
        if (span.Length < 24)
        {
            throw new InvalidDataException($"{source}: file is too short for a CLIPS header");
        }

        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(span[0x10..]);
        uint repeatedCount = BinaryPrimitives.ReadUInt32LittleEndian(span[0x14..]);
        if (entryCount != repeatedCount)
        {
            throw new InvalidDataException(
                $"{source}: entry counts disagree ({entryCount} != {repeatedCount})");
        }

        const int tableOffset = 0x18;
        long tableEnd = tableOffset + (long)entryCount * 16;
        if (tableEnd > span.Length)
        {
            throw new InvalidDataException($"{source}: entry table extends beyond the file");
        }

        var entries = new List<ClipEntry>((int)entryCount);
        long expectedOffset = (tableEnd + 15) & ~15L;
        for (int index = 0; index < entryCount; index++)
        {
            int record = tableOffset + index * 16;
            ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(span[record..]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(span[(record + 8)..]);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(span[(record + 12)..]);
            long end = (long)offset + size;
            if (offset < tableEnd || end > span.Length)
            {
                throw new InvalidDataException(
                    $"{source}: entry {index} has invalid range {offset}..{end}");
            }
            if (offset != expectedOffset)
                throw new InvalidDataException(
                    $"{source}: entry {index} begins at {offset}, expected contiguous offset {expectedOffset}");

            entries.Add(new ClipEntry
            {
                Index = index,
                Hash = hash,
                Offset = (int)offset,
                Payload = buffer[(int)offset..(int)end],
            });
            expectedOffset = end;
        }
        if (expectedOffset != span.Length)
            throw new InvalidDataException(
                $"{source}: final entry ends at {expectedOffset}, file ends at {span.Length}");

        return new ClipBank(source, buffer, entries);
    }

    public ClipEntry? FindByHash(ulong hash)
    {
        foreach (ClipEntry entry in Entries)
        {
            if (entry.Hash == hash)
            {
                return entry;
            }
        }

        return null;
    }
}
