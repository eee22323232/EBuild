using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

sealed class LocalAppearance
{
    const int MaxBytes = 1024 * 1024;
    static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };
    readonly string folder;

    internal LocalAppearance(string profile) { folder = Path.Combine(profile, "offline-appearance"); }

    string ImagePath(string part) {
        if (part != "skin" && part != "cape") throw new IOException("请选择皮肤或披风。");
        return Path.Combine(folder, part + ".png");
    }

    string Read(string part) {
        var path = ImagePath(part);
        if (!File.Exists(path) || new FileInfo(path).Length > MaxBytes) return "";
        return "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(path));
    }

    internal object Summary() { return J.Map("skin", Read("skin"), "cape", Read("cape")); }
    internal void Remove(string part) { File.Delete(ImagePath(part)); }

    internal void Import(string part, string source) {
        var target = ImagePath(part);
        var invalid = part == "skin" ? "请使用 64 × 64 或 64 × 32 的 Minecraft PNG 皮肤。" :
            "请使用 64 × 32 的 Minecraft PNG 披风，或该尺寸的 2、4、8 倍图。";
        using (var stream = File.OpenRead(source)) {
            var header = new byte[24];
            if (stream.Length > MaxBytes || stream.Read(header, 0, header.Length) != header.Length ||
                !header.Take(8).SequenceEqual(PngSignature) || System.Text.Encoding.ASCII.GetString(header, 12, 4) != "IHDR")
                throw new IOException(invalid);
            long width = Dimension(header, 16), height = Dimension(header, 20);
            var valid = part == "skin" ? width == 64 && (height == 64 || height == 32) :
                (width == 64 || width == 128 || width == 256 || width == 512) && height == width / 2;
            if (!valid) throw new IOException(invalid);
            stream.Position = 0;
            Directory.CreateDirectory(folder);
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var image = Image.FromStream(stream, false, true)) {
                    if (image.RawFormat.Guid != ImageFormat.Png.Guid || image.Width != width || image.Height != height)
                        throw new IOException(invalid);
                    image.Save(temporary, ImageFormat.Png);
                }
                if (new FileInfo(temporary).Length > MaxBytes) throw new IOException("图片文件太大。");
                if (File.Exists(target)) File.Replace(temporary, target, null);
                else File.Move(temporary, target);
            } catch (ArgumentException) {
                throw new IOException(invalid);
            } finally { File.Delete(temporary); }
        }
    }

    static long Dimension(byte[] header, int index) {
        return ((long)header[index] << 24) | ((long)header[index + 1] << 16) | ((long)header[index + 2] << 8) | header[index + 3];
    }
}
