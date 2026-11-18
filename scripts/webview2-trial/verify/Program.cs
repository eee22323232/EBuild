using System;
using System.IO;
using System.Linq;
using System.Text;

static class Program
{
    static int Main()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "EBuild-Verification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            CheckCatalog();
            CheckDownload(scratch);
            CheckLaunchCache(scratch);
            ImportData.Run(Path.Combine(scratch, "import"));
            CheckExternalInstances(Path.Combine(scratch, "external"));
            StorageRecovery.Run(Path.Combine(scratch, "storage-recovery"));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            Directory.Delete(scratch, true);
        }
    }

    static void CheckExternalInstances(string root)
    {
        var backend = new Backend(null, Path.Combine(root, "profile"), _ => { });
        var folder = Path.Combine(root, "26.3");
        var moved = Path.Combine(root, "moved", "26.3");
        var baseVersion = J.Map("id", "26.3", "mainClass", "net.minecraft.client.main.Main");
        J.Save(Path.Combine(folder, "26.3.json"), baseVersion);
        File.WriteAllText(Path.Combine(folder, "options.txt"), "original settings");

        var registered = backend.ImportExternal(folder, "").GetAwaiter().GetResult();
        var id = registered.S("id");
        Require(backend.InstancePath(id) == folder && backend.Instances().Single().B("canDeleteOriginal"),
            "The launcher should use the independent original instance directory.");
        Directory.CreateDirectory(Path.GetDirectoryName(moved));
        Directory.Move(folder, moved);
        Require(backend.Instances().Single().B("externalMissing"), "A moved instance should remain registered as missing.");
        var updated = backend.ImportExternal(moved, "", id).GetAwaiter().GetResult();
        Require(updated.S("id") == id && backend.InstancePath(id) == moved, "Relocation should preserve the instance identity.");
        backend.Call("instances:delete", new object[] { id, updated.S("name"), "remove" }).GetAwaiter().GetResult();
        Require(!backend.Instances().Any() && File.ReadAllText(Path.Combine(moved, "options.txt")) == "original settings",
            "Removing a registered instance should leave the original files in place.");

        var shared = Path.Combine(root, ".minecraft");
        const string fabricId = "fabric-loader-0.20.1-26.3";
        J.Save(Path.Combine(shared, "versions", "26.3", "26.3.json"), baseVersion);
        J.Save(Path.Combine(shared, "versions", fabricId, fabricId + ".json"),
            J.Map("id", fabricId, "inheritsFrom", "26.3", "mainClass", "net.fabricmc.loader.impl.launch.knot.KnotClient",
                "libraries", new[] { J.Map("name", "net.fabricmc:fabric-loader:0.20.1") }));
        var discovered = backend.ImportExternal(shared, "").GetAwaiter().GetResult();
        Require(discovered.A("choices").Length == 2 && !backend.Instances().Any(), "A shared folder should offer a version choice.");
        var profile = backend.ImportExternal(shared, fabricId).GetAwaiter().GetResult();
        Require(profile.S("loader") == "fabric" && !backend.Instances().Single().B("canDeleteOriginal"),
            "Shared game directories should only be eligible for removal of the launcher entry.");
        var refused = false;
        try { backend.Call("instances:delete", new object[] { profile.S("id"), profile.S("name"), "original" }).GetAwaiter().GetResult(); }
        catch (IOException error) { refused = error.Message.Contains("共享目录"); }
        Require(refused && Directory.Exists(shared), "The backend must refuse permanent deletion of a shared directory.");
    }

    static void CheckCatalog()
    {
        var manifest = J.Parse(@"{
          ""latest"": { ""release"": ""1.21.1"", ""snapshot"": ""24w40a"" },
          ""versions"": [
            { ""id"": ""24w40a"", ""type"": ""snapshot"", ""url"": ""https://example.invalid/snapshot.json"" },
            { ""id"": ""1.21.1"", ""type"": ""release"", ""sha1"": ""fixture-sha1"" },
            { ""id"": ""a1.2.6"", ""type"": ""old_alpha"" }
          ]
        }");
        var parsed = Minecraft.ParseCatalog(manifest);
        var versions = parsed.A("versions");
        Require(versions.Select(x => x.S("id")).SequenceEqual(new[] { "24w40a", "1.21.1" }),
            "The launcher should retain release and snapshot entries in manifest order.");
        Require(parsed.At("latest").S("release") == "1.21.1" &&
            versions[0].S("url") == "https://example.invalid/snapshot.json" &&
            versions[1].S("sha1") == "fixture-sha1", "Catalog lost fields used by the game installer.");
        try
        {
            Minecraft.ParseCatalog(J.Parse(@"{ ""versions"": [{ ""id"": ""a1.2.6"", ""type"": ""old_alpha"" }] }"));
            throw new InvalidOperationException("An unsupported-only catalog should be rejected.");
        }
        catch (IOException) { }
        Console.WriteLine("PASS: Minecraft catalog filters versions and preserves install metadata.");
    }

    static void CheckDownload(string scratch)
    {
        var file = Path.Combine(scratch, "download.bin");
        var spec = J.Map("size", 3, "sha256", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        Require(!Network.Check(file, spec), "A missing download must not pass validation.");
        File.WriteAllBytes(file, Encoding.ASCII.GetBytes("abc"));
        Require(Network.Check(file, spec), "A download with the expected size and hash should pass.");
        File.WriteAllBytes(file, Encoding.ASCII.GetBytes("abd"));
        Require(!Network.Check(file, spec), "Same-size corruption must fail hash validation.");
        Require(!Network.Check(file, J.Map("size", 2)), "A wrong-sized download must fail size validation.");
        Console.WriteLine("PASS: download validation rejects missing files, incorrect size and same-size corruption.");
    }

    static void CheckLaunchCache(string scratch)
    {
        var file = Path.Combine(scratch, "verified-game.bin");
        var spec = J.Map("size", 3, "sha256", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        File.WriteAllText(file, "abc", Encoding.ASCII);
        var initial = new LaunchChecks(scratch, false);
        var stamp = LaunchChecks.Stamp(file);
        Require(!initial.Matches(file, spec, stamp), "A file needs a successful verification before caching.");
        Require(Network.Check(file, spec), "Fixture checksum.");
        initial.Remember(file, spec, stamp);
        initial.Save();
        var reopened = new LaunchChecks(scratch, false);
        Require(reopened.Matches(file, spec, stamp), "A successful check can be reused across launches.");
        Require(!new LaunchChecks(scratch, true).Matches(file, spec, stamp), "Full verification must bypass cached checks.");
        Require(!reopened.Matches(file, J.Map("size", 3, "sha256", "changed"), stamp), "An expected checksum change invalidates the entry.");
        File.WriteAllText(file, "abd", Encoding.ASCII);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(-2));
        Require(!reopened.Matches(file, spec, LaunchChecks.Stamp(file)), "Same-size files with a new timestamp must be checked again.");
        File.Delete(file);
        Require(!reopened.Matches(file, spec, LaunchChecks.Stamp(file)), "Missing files must be checked again.");
        Console.WriteLine("PASS: launch cache detects changed or missing files and honors full verification.");

        File.WriteAllText(file, "abd", Encoding.ASCII);
        var damaged = J.Merge(spec, J.Map("url", "file:///test-must-require-repair"));
        using (var network = new Network(J.Map("proxyMode", "direct"))) {
            try {
                network.FileTo(damaged, file, System.Threading.CancellationToken.None, checks: new LaunchChecks(scratch, true)).GetAwaiter().GetResult();
                throw new IOException("Full verification wrongly accepted a same-size damaged file.");
            } catch (InvalidOperationException) {
                Require(File.ReadAllText(file, Encoding.ASCII) == "abd", "A failed repair must preserve the existing file.");
            }
        }
        Console.WriteLine("PASS: first verification still rejects same-size corruption.");
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
