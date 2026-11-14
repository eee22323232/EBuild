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
            ImportData.Run(Path.Combine(scratch, "import"));
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

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
