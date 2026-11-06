// Recovered from Downloads/Animation/AdefStudio; see docs/animation-porter.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace AnimIT.Core.K26;

/// <summary>One run in a group's run list.</summary>
public sealed class GroupRun
{
    public required int Type { get; init; }
    public required bool HasExplicitLength { get; init; }
    public required int StartSample { get; init; }
    public required int Length { get; init; }

    /// <summary>Selector code per reduced block, empty for other run types.</summary>
    public List<BlockSelector> Selectors { get; } = [];

    public int StoredVectorStart { get; set; }
    public int StoredVectorCount { get; set; }
}

/// <summary>
/// A reduced block's selector: the code as stored, and the ordered interior pair
/// it enumerates. The pair places the block's second and third stored vectors.
/// </summary>
public readonly struct BlockSelector(int blockIndex, uint code, int first, int second)
{
    public int BlockIndex { get; } = blockIndex;
    public uint Code { get; } = code;
    public int First { get; } = first;
    public int Second { get; } = second;
}

/// <summary>Where a requested frame lands, and how far to blend toward the next vector.</summary>
public readonly struct GroupSample(int storedVectorIndex, float blend)
{
    public int StoredVectorIndex { get; } = storedVectorIndex;
    public float Blend { get; } = blend;
}

/// <summary>
/// One decoded XYZ transform group: its run list, reduction mode, block
/// selectors and stored quantized vectors.
/// </summary>
public sealed class TransformGroup
{
    public required int SampleCount { get; init; }
    public required int VectorBitWidth { get; init; }
    public required int Mode { get; init; }
    public required int BlockSize { get; init; }
    public required bool UsesReducedBlocks { get; init; }
    public required int SelectorBitWidth { get; init; }
    public required IReadOnlyList<GroupRun> Runs { get; init; }
    public required int StoredVectorCount { get; init; }

    /// <summary>Dequantized stored vectors, three components each.</summary>
    public required float[] Vectors { get; init; }

    public int StartBit { get; init; }
    public int EndBit { get; init; }

    public bool IsConstant => StoredVectorCount == 0;

    public ReadOnlySpan<float> Vector(int index) =>
        Vectors.AsSpan(index * 3, 3);

    /// <summary>
    /// Resolves a frame to a stored vector and a blend factor toward the next one.
    /// Port of the runtime evaluator at RVA 0x155D970.
    /// </summary>
    public GroupSample? Evaluate(int frame, float subFrame = 0f)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        if (subFrame is < 0f or >= 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(subFrame), subFrame, "must be in [0, 1)");
        }

        if (StoredVectorCount == 0)
        {
            return null;
        }

        foreach (GroupRun run in Runs)
        {
            int localFrame = frame - run.StartSample;
            if (localFrame >= run.Length)
            {
                continue;
            }

            // A constant run holds one vector. Only its final sample blends on,
            // into the next run's first stored vector.
            if (run.Type != 0)
            {
                float blend = run.Length > localFrame + 1 ? 0f : subFrame;
                return new GroupSample(run.StoredVectorStart, blend);
            }

            if (!UsesReducedBlocks)
            {
                return new GroupSample(run.StoredVectorStart + localFrame, subFrame);
            }

            int blockIndex = localFrame / BlockSize;
            int blockStart = blockIndex * BlockSize;
            int blockSpan = Math.Min(BlockSize, run.Length - blockStart);
            BlockSelector selector = run.Selectors[blockIndex];

            // Keys sit at block-local 0, First and min(Second, blockSpan). The
            // last segment runs on into the next block's first key, which keeps
            // the reconstruction continuous across block boundaries.
            int middle = Math.Min(selector.Second, blockSpan);
            int localInBlock = localFrame - blockStart;

            int keyIndex;
            int numerator;
            int denominator;
            if (localInBlock < selector.First)
            {
                keyIndex = 0;
                numerator = localInBlock;
                denominator = selector.First;
            }
            else if (localInBlock < middle)
            {
                keyIndex = 1;
                numerator = localInBlock - selector.First;
                denominator = middle - selector.First;
            }
            else
            {
                keyIndex = 2;
                numerator = localInBlock - middle;
                denominator = blockSpan - middle;
            }

            return new GroupSample(
                run.StoredVectorStart + (blockIndex * 3) + keyIndex,
                (numerator + subFrame) / denominator);
        }

        return null;
    }

    /// <summary>
    /// Samples the group into <paramref name="destination"/>, matching the
    /// runtime caller at RVA 0x155DFAF: a plain lerp toward the next stored
    /// vector, skipped entirely when the blend factor is zero.
    /// </summary>
    public void Sample(
        int frame,
        ReadOnlySpan<ChannelDescriptor> descriptors,
        Span<float> destination,
        float subFrame = 0f)
    {
        GroupSample? resolved = Evaluate(frame, subFrame);
        if (resolved is not { } sample)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                destination[channel] = descriptors[channel].Minimum;
            }

            return;
        }

        ReadOnlySpan<float> first = Vector(sample.StoredVectorIndex);

        // At the group's last stored vector the runtime would read into the
        // following group's bits; clamping keeps the sample inside this group.
        bool hasNext = sample.StoredVectorIndex + 1 < StoredVectorCount;
        if (sample.Blend == 0f || !hasNext)
        {
            first.CopyTo(destination);
            return;
        }

        ReadOnlySpan<float> second = Vector(sample.StoredVectorIndex + 1);
        for (int channel = 0; channel < 3; channel++)
        {
            destination[channel] =
                first[channel] + ((second[channel] - first[channel]) * sample.Blend);
        }
    }
}
