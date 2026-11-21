using System;
using System.IO;
using System.IO.Compression;
using System.Threading;

static class ImportData
{
    internal static void Run(string root) {
        var old = Path.Combine(root, "old");
        var copy = Path.Combine(root, "copy");
        foreach (var f in new[] { "saves/world/level.dat","mods/my-mod.jar","shaderpacks/test.zip","resourcepacks/test.zip","config/my-mod.json",
            "options.txt",".ebuild.json","versions/game.jar","runtimes/java/bin/java.exe",".natives/native.dll","logs/game.log" }) {
            var dest = J.Inside(old,f);
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            File.WriteAllText(dest,f);
        }
        Backend.ImportPlayerData(old,copy,CancellationToken.None);
        foreach (var f in new[] {"saves/world/level.dat","mods/my-mod.jar","shaderpacks/test.zip","resourcepacks/test.zip","config/my-mod.json","options.txt"})
            if (File.ReadAllText(J.Inside(copy,f)) != f) throw new IOException("Missing player data: " + f);
        foreach (var f in new[] {".ebuild.json","versions","runtimes",".natives","logs"})
            if (File.Exists(J.Inside(copy,f)) || Directory.Exists(J.Inside(copy,f))) throw new IOException("Unexpected files in import: " + f);
        if (File.ReadAllText(J.Inside(old,"saves/world/level.dat")) != "saves/world/level.dat") throw new IOException("Source was changed.");
        Console.WriteLine("PASS: player data copied without game files, Java, logs, or changes to the source.");
        CheckPackImport(root);
    }

    static void WriteManifest(string path, string entry, object manifest) {
        using (var stream = File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry(entry).Open())) writer.Write(J.Dump(manifest));
    }

    static void CheckPackImport(string root) {
        var backend = new Backend(null, Path.Combine(root, "profile"), _ => { });
        var manifest = J.Map("formatVersion", 1, "game", "minecraft", "name", "Fixture Pack",
            "dependencies", J.Map("minecraft", "1.20.1", "forge", "47.4.20"));
        foreach (var name in new[] { "fixture.mrpack", "fixture.ZIP" }) {
            var path = Path.Combine(root, name);
            WriteManifest(path, "modrinth.index.json", manifest);
            var result = backend.PreviewDroppedImport(path).GetAwaiter().GetResult();
            var instance = result.At("instance");
            if (result.S("kind") != "pack" || instance.S("name") != "Fixture Pack" ||
                instance.S("version") != "1.20.1" || instance.S("loader") != "forge" || instance.S("loaderVersion") != "47.4.20")
                throw new IOException("The launcher must preview the same Modrinth manifest in .mrpack and .zip archives.");
        }
        var other = Path.Combine(root, "other.zip");
        WriteManifest(other, "manifest.json", J.Map("manifestType", "minecraftModpack"));
        bool rejected = false;
        try { backend.PreviewDroppedImport(other).GetAwaiter().GetResult(); }
        catch (IOException error) { rejected = error.Message.Contains("modrinth.index.json"); }
        if (!rejected) throw new IOException("A non-Modrinth ZIP must not enter the Modrinth installation flow.");

        foreach (var host in new[] { "edge.forgecdn.net", "mediafilez.forgecdn.net", "cdn.modrinth.com" })
            if (Resources.PackDownloadUri("https://" + host + "/files/sample.jar") == null)
                throw new IOException("A supported pack download host was refused.");
        foreach (var url in new[] { "https://edge.forgecdn.net.example.invalid/a", "http://edge.forgecdn.net/a",
            "https://user@edge.forgecdn.net/a", "https://edge.forgecdn.net:8443/a" })
            if (Resources.PackDownloadUri(url) != null) throw new IOException("An unsupported pack download URL was accepted.");
        Console.WriteLine("PASS: Modrinth ZIP and MRPACK previews, format rejection and pack download hosts.");
    }
}
