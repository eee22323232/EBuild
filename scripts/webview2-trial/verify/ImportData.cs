using System;
using System.IO;
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
    }
}
