// Recovered from Downloads/Animation/AdefStudio; see docs/animation-porter.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace AnimIT.Core.K26;

/// <summary>
/// Reads one XYZ transform group out of array E. Port of the runtime layout
/// parser at RVA 0x155D700.
/// </summary>
public static class TransformGroupReader
{
    /// <summary>The runtime stores at most 15 run records per group.</summary>
    public const int MaxRuns = 15;

    /// <summary>
    /// Selector-code width per reduction mode. Matches the executable's table at
    /// RVA 0x3B01E40, which is <c>ceil(log2((m + 1) * (m + 2) / 2))</c>.
    /// </summary>
    public static int SelectorBitWidth(int mode)
    {
        if (mode is < 0 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "invalid reduction mode");
        }

        if (mode == 0)
        {
            return 0;
        }

        int pairCount = (mode + 1) * (mode + 2) / 2;
        return (int)Math.Ceiling(Math.Log2(pairCount));
    }

    /// <summary>
    /// Decodes a selector code into its ordered interior pair (a, b) with
    /// 1 &lt;= a &lt; b. Codes are ordered by increasing b, then increasing a,
    /// matching the executable's table at RVA 0x3B01F40.
    /// </summary>
    public static (int First, int Second) SelectorPair(uint code)
    {
        int second = 2;
        long remaining = code;
        while (remaining >= second - 1)
        {
            remaining -= second - 1;
            second++;
        }

        return ((int)remaining + 1, second);
    }

    /// <summary>
    /// Reads a group's run list, mode, selectors and stored vectors, advancing
    /// the reader by exactly the bits the group occupies.
    /// </summary>
    public static TransformGroup Read(
        LsbBitReader reader,
        int sampleCount,
        ReadOnlySpan<ChannelDescriptor> descriptors,
        bool decodeVectors = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleCount, 1);

        int vectorBitWidth = 0;
        foreach (ChannelDescriptor descriptor in descriptors)
        {
            vectorBitWidth += descriptor.BitWidth;
        }

        int startBit = reader.Position;
        if (vectorBitWidth == 0)
        {
            return new TransformGroup
            {
                SampleCount = sampleCount,
                VectorBitWidth = 0,
                Mode = 0,
                BlockSize = 3,
                UsesReducedBlocks = false,
                SelectorBitWidth = 0,
                Runs = [],
                StoredVectorCount = 0,
                Vectors = [],
                StartBit = startBit,
                EndBit = startBit,
            };
        }

        var runs = new List<GroupRun>();
        int coveredSamples = 0;
        while (coveredSamples < sampleCount)
        {
            int type = (int)reader.Read(1);
            bool explicitLength = reader.Read(1) != 0;
            int length = explicitLength ? (int)reader.Read(16) : sampleCount - coveredSamples;
            if (length < 1 || coveredSamples + length > sampleCount)
            {
                throw new InvalidDataException(
                    $"invalid run length {length} at sample {coveredSamples} of {sampleCount}");
            }

            runs.Add(new GroupRun
            {
                Type = type,
                HasExplicitLength = explicitLength,
                StartSample = coveredSamples,
                Length = length,
            });
            coveredSamples += length;

            if (runs.Count > MaxRuns)
            {
                throw new InvalidDataException(
                    $"run list exceeds the runtime's {MaxRuns}-record capacity");
            }
        }

        int mode = (int)reader.Read(8);
        int selectorBitWidth = SelectorBitWidth(mode);
        int blockSize = mode + 3;
        bool usesReducedBlocks = mode * vectorBitWidth > selectorBitWidth;

        int storedVectorCount = 0;
        foreach (GroupRun run in runs)
        {
            run.StoredVectorStart = storedVectorCount;
            if (run.Type != 0)
            {
                run.StoredVectorCount = 1;
            }
            else if (usesReducedBlocks)
            {
                int blockCount = (run.Length + blockSize - 1) / blockSize;
                for (int blockIndex = 0; blockIndex < blockCount; blockIndex++)
                {
                    uint code = reader.Read(selectorBitWidth);
                    (int first, int second) = SelectorPair(code);
                    if (second > mode + 2)
                    {
                        throw new InvalidDataException(
                            $"sparse selector {code} resolves outside mode {mode}");
                    }

                    run.Selectors.Add(new BlockSelector(blockIndex, code, first, second));
                }

                run.StoredVectorCount =
                    ((run.Length / blockSize) * 3) + Math.Min(run.Length % blockSize, 3);
            }
            else
            {
                run.StoredVectorCount = run.Length;
            }

            storedVectorCount += run.StoredVectorCount;
        }

        float[] vectors;
        if (decodeVectors)
        {
            vectors = new float[storedVectorCount * 3];
            int cursor = 0;
            for (int vectorIndex = 0; vectorIndex < storedVectorCount; vectorIndex++)
            {
                for (int channel = 0; channel < 3; channel++)
                {
                    ChannelDescriptor descriptor = descriptors[channel];
                    uint code = descriptor.BitWidth == 0 ? 0 : reader.Read(descriptor.BitWidth);
                    vectors[cursor++] = descriptor.Dequantize(code);
                }
            }
        }
        else
        {
            vectors = [];
            reader.Skip(storedVectorCount * vectorBitWidth);
        }

        return new TransformGroup
        {
            SampleCount = sampleCount,
            VectorBitWidth = vectorBitWidth,
            Mode = mode,
            BlockSize = blockSize,
            UsesReducedBlocks = usesReducedBlocks,
            SelectorBitWidth = selectorBitWidth,
            Runs = runs,
            StoredVectorCount = storedVectorCount,
            Vectors = vectors,
            StartBit = startBit,
            EndBit = reader.Position,
        };
    }
}
