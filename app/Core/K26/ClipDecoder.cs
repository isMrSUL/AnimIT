// Recovered from Downloads/Animation/AdefStudio; see docs/animation-porter.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace AnimIT.Core.K26;

/// <summary>A joint's local transform at one frame. Rotation is in radians.</summary>
public readonly struct TransformSample(
    float translationX, float translationY, float translationZ,
    float rotationX, float rotationY, float rotationZ)
{
    public float TranslationX { get; } = translationX;
    public float TranslationY { get; } = translationY;
    public float TranslationZ { get; } = translationZ;
    public float RotationX { get; } = rotationX;
    public float RotationY { get; } = rotationY;
    public float RotationZ { get; } = rotationZ;
}

/// <summary>One decoded track: a joint, camera or object channel of a clip.</summary>
public sealed class TrackAnimation
{
    public required int Index { get; init; }
    public required ulong Hash { get; init; }
    public required string? Name { get; init; }
    public required TransformSample[] Samples { get; init; }

    /// <summary>Null when the group is constant across the whole timeline.</summary>
    public TransformGroup? TranslationGroup { get; init; }

    public TransformGroup? RotationGroup { get; init; }

    public string HashHex => Fnv1a64.ToHex(Hash);
}

/// <summary>A fully decoded clip entry.</summary>
public sealed class ClipAnimation
{
    public required ulong ClipHash { get; init; }
    public required int SampleCount { get; init; }
    public required float FrameRate { get; init; }
    public required int UsedBitCount { get; init; }
    public required IReadOnlyList<TrackAnimation> Tracks { get; init; }

    /// <summary>True when any group uses the key-reduced representation.</summary>
    public required bool IsReduced { get; init; }

    public TimeSpan Duration => TimeSpan.FromSeconds(SampleCount / (FrameRate <= 0 ? 60 : FrameRate));
}

/// <summary>
/// Decodes a clip entry's transform stream into per-frame poses. Handles both
/// the dense and the key-reduced variants.
/// </summary>
public static class ClipDecoder
{
    public static ClipAnimation Decode(ClipEntry entry, IReadOnlyDictionary<ulong, string>? trackNames = null)
    {
        EntryArrays arrays = entry.SplitArrays();
        int sampleCount = entry.SampleCount;
        int usedBitCount = entry.UsedBitCount;
        var reader = new LsbBitReader(arrays.ArrayE.Data, usedBitCount);

        var tracks = new List<TrackAnimation>(arrays.TrackIds.Count);
        Span<ChannelDescriptor> descriptors = stackalloc ChannelDescriptor[6];
        Span<float> translation = stackalloc float[3];
        Span<float> rotation = stackalloc float[3];
        bool reduced = false;

        for (int trackIndex = 0; trackIndex < arrays.TrackIds.Count; trackIndex++)
        {
            for (int channel = 0; channel < 6; channel++)
            {
                descriptors[channel] = ChannelDescriptor.Read(arrays.ArrayD, (trackIndex * 6) + channel);
            }

            TransformGroup? translationGroup = ReadGroup(reader, sampleCount, descriptors[..3]);
            TransformGroup? rotationGroup = ReadGroup(reader, sampleCount, descriptors[3..]);
            reduced |= translationGroup?.UsesReducedBlocks == true
                || rotationGroup?.UsesReducedBlocks == true;

            var samples = new TransformSample[sampleCount];
            for (int frame = 0; frame < sampleCount; frame++)
            {
                SampleGroup(translationGroup, descriptors[..3], frame, translation);
                SampleGroup(rotationGroup, descriptors[3..], frame, rotation);
                samples[frame] = new TransformSample(
                    translation[0], translation[1], translation[2],
                    rotation[0], rotation[1], rotation[2]);
            }

            ulong hash = entry.TrackHash(arrays, trackIndex);
            tracks.Add(new TrackAnimation
            {
                Index = trackIndex,
                Hash = hash,
                Name = trackNames is not null && trackNames.TryGetValue(hash, out string? resolved)
                    ? resolved
                    : null,
                Samples = samples,
                TranslationGroup = translationGroup,
                RotationGroup = rotationGroup,
            });
        }

        if (reader.Position != usedBitCount)
        {
            throw new InvalidDataException(
                $"decode consumed {reader.Position} of {usedBitCount} used bits");
        }

        return new ClipAnimation
        {
            ClipHash = entry.Hash,
            SampleCount = sampleCount,
            FrameRate = entry.FrameRate,
            UsedBitCount = usedBitCount,
            Tracks = tracks,
            IsReduced = reduced,
        };
    }

    private static TransformGroup? ReadGroup(
        LsbBitReader reader,
        int sampleCount,
        ReadOnlySpan<ChannelDescriptor> channels)
    {
        foreach (ChannelDescriptor descriptor in channels)
        {
            if (!descriptor.IsConstant)
            {
                return TransformGroupReader.Read(reader, sampleCount, channels);
            }
        }

        return null;
    }

    private static void SampleGroup(
        TransformGroup? group,
        ReadOnlySpan<ChannelDescriptor> channels,
        int frame,
        Span<float> destination)
    {
        if (group is null)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                destination[channel] = channels[channel].Minimum;
            }

            return;
        }

        group.Sample(frame, channels, destination);
    }
}
