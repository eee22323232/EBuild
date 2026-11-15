using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

static class ModIcons
{
    const int MaxIconBytes = 256 * 1024;
    static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    internal static Dictionary<string,object> Read(IEnumerable<string> files) {
        var icons = J.Map();
        int remaining = 2 * 1024 * 1024;
        foreach (var file in files) {
            if (remaining <= 0) break;
            if (!file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
            var png = ReadIcon(file);
            if (png == null || png.Length > remaining) continue;
            icons.Add(Path.GetFileName(file), "data:image/png;base64," + Convert.ToBase64String(png));
            remaining -= png.Length;
        }
        return icons;
    }

    static byte[] ReadIcon(string file) {
        try {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return null;
            using (var zip = ZipFile.OpenRead(file)) {
                var fabric = Text(zip.GetEntry("fabric.mod.json"));
                var icon = fabric == "" ? null : J.Parse(fabric).At("icon");
                var path = icon as string ?? icon.Obj().OrderBy(pair => Math.Abs((long)pair.Key.I(null) - 64)).Select(pair => pair.Value.S()).FirstOrDefault();
                if (String.IsNullOrEmpty(path)) {
                    var toml = Text(zip.GetEntry("META-INF/neoforge.mods.toml") ?? zip.GetEntry("META-INF/mods.toml"));
                    path = Regex.Match(toml, @"^[ \t]*logoFile[ \t]*=[ \t]*[""']([^""'\r\n]+)[""']", RegexOptions.Multiline).Groups[1].Value;
                }
                if (String.IsNullOrEmpty(path) || !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return null;
                var bytes = Bytes(zip.GetEntry(path.TrimStart('/')), MaxIconBytes);
                return bytes != null && bytes.Length >= 24 && bytes.Take(8).SequenceEqual(PngSignature) &&
                    Encoding.ASCII.GetString(bytes, 12, 4) == "IHDR" && Dimension(bytes, 16) && Dimension(bytes, 20) ? bytes : null;
            }
        } catch (IOException) { return null; }
        catch (InvalidDataException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    static bool Dimension(byte[] bytes, int offset) {
        int size = (bytes[offset] << 24) | (bytes[offset+1] << 16) | (bytes[offset+2] << 8) | bytes[offset+3];
        return size > 0 && size <= 1024;
    }

    static string Text(ZipArchiveEntry entry) {
        var bytes = Bytes(entry, 128 * 1024);
        if (bytes == null) return "";
        using (var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8)) return reader.ReadToEnd();
    }

    static byte[] Bytes(ZipArchiveEntry entry, int limit) {
        if (entry == null || entry.Length <= 0 || entry.Length > limit) return null;
        var buffer = new byte[(int)entry.Length];
        using (var input = entry.Open()) {
            for (int offset = 0; offset < buffer.Length;) {
                int count = input.Read(buffer, offset, buffer.Length - offset);
                if (count == 0) return null;
                offset += count;
            }
            return input.ReadByte() == -1 ? buffer : null;
        }
    }
}
