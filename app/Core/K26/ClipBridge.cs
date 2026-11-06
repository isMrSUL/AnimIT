using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AnimIT.Core.K26;

/// <summary>Offline bridge from the recovered native decoder to the Python retargeter.</summary>
public static class ClipBridge
{
    public static int CheckBank(string input, string output)
    {
        try
        {
            var bank = ClipBank.Load(input);
            int count = 0, samples = 0;
            foreach (var entry in bank.Entries)
            {
                var clip = ClipDecoder.Decode(entry);
                foreach (var track in clip.Tracks)
                    foreach (var sample in track.Samples)
                        if (!float.IsFinite(sample.TranslationX) || !float.IsFinite(sample.TranslationY) || !float.IsFinite(sample.TranslationZ) || !float.IsFinite(sample.RotationX) || !float.IsFinite(sample.RotationY) || !float.IsFinite(sample.RotationZ)) throw new InvalidDataException("Nonfinite clip sample.");
                count++; samples += clip.SampleCount;
            }
            File.WriteAllText(output, JsonSerializer.Serialize(new { ok = true, entries = count, samples, exactBitConsumption = true }));
            return 0;
        }
        catch (Exception e) { File.WriteAllText(output, JsonSerializer.Serialize(new { ok = false, error = e.ToString() })); return 1; }
    }

    public static int Run(string input, string output)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(input);
            if (bytes.Length < 0xA0) throw new InvalidDataException("The .clip header is truncated.");
            foreach (int offset in new[] { 0x20, 0x38, 0x60, 0x78, 0x90 })
                if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)) != BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4))) throw new InvalidDataException("Clip array counts disagree.");
            var entry = new ClipEntry { Index = 0, Hash = BinaryPrimitives.ReadUInt64LittleEndian(bytes), Offset = 0, Payload = bytes };
            if (entry.SampleCount < 2 || entry.SampleCount > 100000 || entry.TrackCount < 1 || entry.TrackCount > 1024) throw new InvalidDataException("Invalid clip frame or track count.");
            if (!float.IsFinite(entry.FrameRate) || Math.Abs(entry.FrameRate - 60) > 0.01) throw new InvalidDataException("This porter currently expects 2K26 clips at 60 FPS.");
            var decoded = ClipDecoder.Decode(entry);
            var bones = decoded.Tracks.Select(track => new
            {
                hash = track.Hash,
                t = track.Samples.Select(s => new[] { s.TranslationX, s.TranslationY, s.TranslationZ }).ToArray(),
                q = track.Samples.Select(s => Quaternion(s.RotationX, s.RotationY, s.RotationZ)).ToArray(),
            }).ToArray();
            File.WriteAllText(output, JsonSerializer.Serialize(new { ok = true, frames = decoded.SampleCount, fps = decoded.FrameRate, bones }));
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { ok = false, error = e.Message }));
            return 1;
        }
    }

    // The source stores the logarithm of a unit quaternion (half-angle vector).
    static double[] Quaternion(double x, double y, double z)
    {
        double n = Math.Sqrt(x * x + y * y + z * z);
        double factor = n < 1e-10 ? 1 : Math.Sin(n) / n;
        return new[] { Math.Cos(n), x * factor, y * factor, z * factor };
    }
}
