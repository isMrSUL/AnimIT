using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnimIT.Core.K26;

/// <summary>Read-only CAK extraction through the recovered local BakeIT reader.</summary>
public static class CakeBridge
{
    const string Oodle = "oo2core_9_win64.dll";

    /// <summary>the 2K26 Oodle DLL: beside AnimIT (Porter\cake), in the chosen folder or one above it, or in the
    /// default Steam install</summary>
    static string FindOodle(string source, string helper)
    {
        var candidates = new List<string> { Path.Combine(helper, Oodle) };
        string? dir = File.Exists(source) ? Path.GetDirectoryName(source) : source;
        for (int up = 0; dir != null && up < 6; up++, dir = Path.GetDirectoryName(dir))
        {
            candidates.Add(Path.Combine(dir, Oodle));
            candidates.Add(Path.Combine(dir, "cakeview", Oodle));
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "WWE 2K26", Oodle));
        return candidates.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException(
            "Reading 2K26 .cak archives needs oo2core_9_win64.dll from your WWE 2K26 game folder (it is not included with AnimIT). " +
            "Copy it into AnimIT\\Porter\\cake, or choose a folder inside your 2K26 install.");
    }

    public static int Run(string requestFile, string responseFile)
    {
        try
        {
            using var request = JsonDocument.Parse(File.ReadAllText(requestFile));
            var root = request.RootElement;
            string source = root.GetProperty("source").GetString()!, cache = root.GetProperty("cache").GetString()!;
            int? move = root.TryGetProperty("move", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt32() : null;
            string helper = Path.Combine(AppContext.BaseDirectory, "Porter", "cake");
            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                string file = Path.Combine(helper, name.Name + ".dll");
                return File.Exists(file) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(file) : null;
            };
            // Oodle (RAD Game Tools) is proprietary and is NOT shipped with AnimIT: use the copy that comes with the
            // user's own WWE 2K26 (beside AnimIT if they copied it there, else the chosen 2K26 folder or a folder above
            // it, else the default Steam install)
            NativeLibrary.Load(FindOodle(source, helper));
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(helper, "BakeIT.Core.dll"));
            Type type = assembly.GetType("BakeIT.Core.CakeRegistryFile") ?? throw new InvalidOperationException("Missing BakeIT archive reader.");
            var open = type.GetMethods().Single(m => m.Name == "Open" && m.GetParameters().Length == 4);
            string[] archives = File.Exists(source) ? new[] { source } : Directory.GetFiles(source, "*.cak", SearchOption.AllDirectories);
            var files = new List<string>(); var warnings = new List<string>();
            // Retail archives first. Mods are useful when only mod containers survive.
            foreach (string archive in archives.OrderBy(p => !Path.GetFileName(p).StartsWith("bakedfile", StringComparison.OrdinalIgnoreCase)).ThenBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    object reader = open.Invoke(null, new object?[] { archive, null, false, true })!;
                    using var lifetime = (IDisposable)reader;
                    Type actual = reader.GetType();
                    var enumerate = actual.GetMethod("EnumerateFiles") ?? type.GetMethod("EnumerateFiles")!;
                    var extract = actual.GetMethod("ExtractEntry") ?? type.GetMethod("ExtractEntry")!;
                    string stamp = archive + "|" + File.GetLastWriteTimeUtc(archive).Ticks + "|" + new FileInfo(archive).Length;
                    string dir = Path.Combine(cache, "cak_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp)))[..20]);
                    foreach (object item in (IEnumerable)enumerate.Invoke(reader, null)!)
                    {
                        var itemType = item.GetType();
                        string virtualPath = ((string)itemType.GetProperty("Path")!.GetValue(item)!).Replace('\\', '/');
                        if (!virtualPath.EndsWith(".clips", StringComparison.OrdinalIgnoreCase) || (!virtualPath.Contains("/Acts/", StringComparison.OrdinalIgnoreCase) && !virtualPath.Contains("/CaeAdvanced/", StringComparison.OrdinalIgnoreCase))) continue;
                        var range = Regex.Match(virtualPath, @"MotionArea_(\d+)_(\d+)");
                        if (move.HasValue && (!range.Success || move < int.Parse(range.Groups[1].Value) || move >= int.Parse(range.Groups[2].Value))) continue;
                        string dest = Path.GetFullPath(Path.Combine(dir, virtualPath));
                        if (!dest.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive path escapes the cache.");
                        long size = Convert.ToInt64(itemType.GetProperty("ExpandedSize")!.GetValue(item));
                        if (!File.Exists(dest) || new FileInfo(dest).Length != size)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                            string partial = dest + ".partial";
                            using (var output = File.Create(partial))
                            {
                                extract.Invoke(reader, new[] { itemType.GetProperty("Entry")!.GetValue(item), output });
                                if (output.Length != size) throw new InvalidDataException($"{Path.GetFileName(archive)}: extracted {output.Length} bytes, expected {size}; this archive encoding is unsupported.");
                            }
                            File.Move(partial, dest, true);
                        }
                        files.Add(dest);
                    }
                }
                catch (Exception e) { warnings.Add(Path.GetFileName(archive) + ": " + (e.InnerException?.Message ?? e.Message)); }
            }
            if (files.Count == 0) throw new InvalidDataException("No readable animation banks in these archives. Choose an extracted Animation folder or .clips file. " + string.Join("\n", warnings));
            File.WriteAllText(responseFile, JsonSerializer.Serialize(new { ok = true, files, warnings }));
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(responseFile, JsonSerializer.Serialize(new { ok = false, error = e.InnerException?.Message ?? e.Message }));
            return 1;
        }
    }
}
