using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

static class StorageRecovery
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    sealed class Handler : HttpMessageHandler {
        internal Func<HttpRequestMessage,HttpResponseMessage> Respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            return Task.FromResult(Respond(request));
        }
    }
    static HttpResponseMessage Response(byte[] bytes, int offset = -1, long? total = null) {
        var response = new HttpResponseMessage(offset < 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes) };
        if (offset >= 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + bytes.Length - 1, total.Value);
        return response;
    }
    static readonly byte[] Payload = Encoding.ASCII.GetBytes("verified download bytes");
    static object DownloadSpec() {
        using (var sha = SHA256.Create()) return J.Map("url", "https://example.invalid/file", "sha256", Network.Hex(sha.ComputeHash(Payload)), "size", Payload.Length);
    }
    static void Fetch(string root, Handler handler, CancellationToken ct = default(CancellationToken)) {
        using (var net = new Network(J.Map("proxyMode", "direct"), Path.Combine(root, "cache/transfers"), handler))
            net.FileTo(DownloadSpec(), Path.Combine(root, "file.bin"), ct).GetAwaiter().GetResult();
    }
    static void Interrupted(string root) {
        int requests = 0;
        try {
            Fetch(root, new Handler { Respond = request => {
                requests++;
                if (requests > 1) throw new HttpRequestException("fixture disconnection");
                return Response(Payload.Take(5).ToArray());
            }});
            throw new Exception("Interrupted download should fail.");
        } catch (HttpRequestException) { }
        Require(!File.Exists(Path.Combine(root, "file.bin")), "Partial data must not replace the destination.");
        Require(new FileInfo(Directory.GetFiles(Path.Combine(root, "cache/transfers")).Single()).Length == 5, "Keep downloaded prefix.");
    }
    static void CheckTransfers(string root) {
        foreach (var behavior in new[] { "range", "ignored", "invalid", "unsatisfiable", "corrupt" }) {
            var dir = Path.Combine(root, behavior);
            Interrupted(dir);
            int requests = 0;
            Fetch(dir, new Handler { Respond = request => {
                requests++;
                var offset = request.Headers.Range == null ? 0 : request.Headers.Range.Ranges.Single().From.Value;
                if (requests == 1) {
                    Require(offset == 5, "Resume should request the stored prefix offset.");
                    if (behavior == "invalid") return Response(Payload.Skip(4).ToArray(), 4, Payload.Length);
                    if (behavior == "unsatisfiable") return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
                    if (behavior == "corrupt") return Response(Enumerable.Repeat((byte)'x', Payload.Length - 5).ToArray(), 5, Payload.Length);
                } else Require(offset == 0, "Bad range or hash should restart without mixing bytes.");
                return behavior == "range" ? Response(Payload.Skip((int)offset).ToArray(), (int)offset, Payload.Length) : Response(Payload);
            }});
            Require(File.ReadAllBytes(Path.Combine(dir, "file.bin")).SequenceEqual(Payload), "Final content must match expected hash.");
            Require(!Directory.GetFiles(Path.Combine(dir, "cache/transfers")).Any(), "Completed prefixes must be removed.");
        }
        var canceled = Path.Combine(root, "canceled");
        Interrupted(canceled);
        using (var source = new CancellationTokenSource()) {
            source.Cancel();
            try { Fetch(canceled, new Handler { Respond = request => { throw new Exception("Canceled task must not send a request."); } }, source.Token); }
            catch (OperationCanceledException) { }
            Require(new FileInfo(Directory.GetFiles(Path.Combine(canceled, "cache/transfers")).Single()).Length == 5, "Cancellation retains the prefix.");
        }
        Console.WriteLine("PASS: resume across Network lifetimes, ignored/invalid Range, 416, bad checksum and cancellation.");
    }
    static void Put(string root, string path, string content) {
        var file = J.Inside(root, path); Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllText(file, content);
    }
    static void CheckStorage(string root) {
        var id = Guid.NewGuid().ToString();
        Put(root, "cache/verified-game-files.json", "keep verification");
        Put(root, "cache/forge-install.log", "keep diagnostics");
        Put(root, "cache/pack.mrpack", "old archive");
        Put(root, "cache/transfers/prefix.part", "prefix");
        Put(root, "instances/" + id + "/mods-backup/old.jar", "old mod");
        Put(root, "instances/" + id + "/saves/world/level.dat", "player world");
        Put(root, "downloads/pending/stage/mod.jar", "pending install");
        Put(root, "game/library.jar", "shared game");
        Put(root, "runtimes/java/bin/java.exe", "shared java");
        var scan = Storage.Scan(root, new[] { id }, CancellationToken.None);
        Require(scan.Sizes["game"] > 0 && scan.Sizes["java"] > 0 && scan.Sizes["instances"] > 0 && scan.Sizes["installations"] > 0, "Report storage by category.");
        Put(root, "cache/new.zip", "new since preview");
        Put(root, "cache/transfers/prefix.part", "changed since preview");
        var result = Storage.Clean(scan, new[] { "archives", "transfers", "backups" }, CancellationToken.None);
        Require(result.I("skipped") == 1, "Changed downloads must be skipped.");
        Require(!File.Exists(J.Inside(root, "cache/pack.mrpack")) && !File.Exists(J.Inside(root, "instances/" + id + "/mods-backup/old.jar")), "Only selected old files are removed.");
        foreach (var file in new[] { "cache/verified-game-files.json", "cache/forge-install.log", "cache/new.zip", "cache/transfers/prefix.part", "instances/" + id + "/saves/world/level.dat", "downloads/pending/stage/mod.jar", "game/library.jar", "runtimes/java/bin/java.exe" })
            Require(File.Exists(J.Inside(root, file)), "Cleanup must preserve " + file);
        try { Storage.Clean(scan, new[] { "instances" }, CancellationToken.None); throw new Exception("Invalid category accepted."); }
        catch (IOException) { }
        Console.WriteLine("PASS: classified cleanup preserves verification, worlds, Java, shared files, pending installs and changed/new files.");
    }
    static void CheckTasks(string profile) {
        Backend backend = null;
        backend = new Backend(null, profile, update => {
            if (update.B("downloadsChanged")) backend.Call("task:cancel", new object[0]).GetAwaiter().GetResult();
        });
        var id = Guid.NewGuid().ToString();
        var input = J.Map("name", "recovery fixture", "version", "1.21.1", "loader", "vanilla");
        var created = backend.Call("instances:create", new object[] { input, id }).GetAwaiter().GetResult();
        Require(created == null && !backend.Instances().Any(), "Pause should not create a visible instance.");
        var jobDir = J.Inside(backend.Root, "downloads/" + id);
        Put(jobDir, "stage/kept.txt", "already downloaded");
        var taskFile = J.Inside(jobDir, "task.json");
        var record = J.Read(taskFile);
        J.Save(taskFile, J.Merge(record, J.Map("state", "active")));
        var reopened = new Backend(null, profile, update => { });
        var list = reopened.Call("downloads:list", new object[0]).GetAwaiter().GetResult().A();
        Require(list.Length == 1 && list[0].S("state") == "paused", "A crashed active install should reappear paused after reopening.");
        Require(File.ReadAllText(J.Inside(jobDir, "stage/kept.txt")) == "already downloaded", "Restore keeps staging files.");
        // Simulate the process ending between committing the instance and updating its task record.
        J.Save(J.Inside(backend.Root, "instances/" + id + "/.ebuild.json"), J.Merge(input, J.Map("installationId", id)));
        var edit = J.Map("name", "renamed recovery fixture", "overrides", J.Map("memory", 2048));
        reopened.Call("instances:edit", new object[] { id, edit }).GetAwaiter().GetResult();
        var completed = reopened.Call("downloads:resume", new object[] { id }).GetAwaiter().GetResult();
        Require(completed.S("id") == id && reopened.Instances().Count() == 1, "Recovery must not create duplicate instances.");
        Require(completed.S("name") == "renamed recovery fixture" && completed.At("overrides").I("memory") == 2048,
            "Editing an instance must preserve the link to its outstanding download record.");
        reopened.Call("downloads:remove", new object[] { id }).GetAwaiter().GetResult();
        Require(!Directory.Exists(jobDir) && reopened.Instances().Count() == 1, "Remove task without removing installed instance.");
        Console.WriteLine("PASS: pause/reopen recovery, atomic commit recovery and removal without deleting installed instances.");
    }
    internal static void Run(string root) {
        CheckTransfers(Path.Combine(root, "transfers"));
        CheckStorage(Path.Combine(root, "storage"));
        CheckTasks(Path.Combine(root, "profile"));
    }
}
