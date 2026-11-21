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
        internal Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> RespondAsync;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            return RespondAsync == null ? Task.FromResult(Respond(request)) : RespondAsync(request, ct);
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
    static void CheckPackSizes(string root) {
        string digest;
        using (var sha = SHA512.Create()) digest = Network.Hex(sha.ComputeHash(Payload));
        foreach (var difference in new[] { -1, 1 }) {
            var dir = Path.Combine(root, difference.ToString());
            var target = Path.Combine(dir, "mod.jar");
            var spec = J.Map("url", "https://example.invalid/mod.jar", "sha512", digest, "size", Payload.Length + difference, "sizeHint", true);
            var handler = new Handler();
            using (var net = new Network(J.Map("proxyMode", "direct"), Path.Combine(dir, "transfers"), handler)) {
                int requests = 0;
                handler.Respond = request => {
                    if (++requests > 1) throw new HttpRequestException("fixture disconnection");
                    var partial = Response(Payload.Take(5).ToArray());
                    partial.Content.Headers.ContentLength = Payload.Length;
                    return partial;
                };
                try { net.FileTo(spec, target, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (HttpRequestException) { }
                handler.Respond = request => {
                    Require(request.Headers.Range != null && request.Headers.Range.Ranges.Single().From == 5, "Resume the modpack prefix.");
                    return Response(Payload.Skip(5).ToArray(), 5, Payload.Length);
                };
                net.FileTo(spec, target, CancellationToken.None).GetAwaiter().GetResult();
                Require(File.ReadAllBytes(target).SequenceEqual(Payload), "Accept verified modpack files when the size hint is off.");
                Require(!Network.Check(target, J.Merge(spec, J.Map("sizeHint", false))), "Ordinary downloads must enforce their declared size.");
                handler.Respond = request => Response(new byte[Payload.Length]);
                try {
                    net.FileTo(spec, Path.Combine(dir, "corrupt.jar"), CancellationToken.None).GetAwaiter().GetResult();
                    throw new Exception("A size hint must never bypass the hash check.");
                } catch (IOException) { }
            }
        }
        Console.WriteLine("PASS: modpack size hints accept verified content, including resumed transfers, while rejecting corruption.");
    }

    static void CheckSourceFallback(string root) {
        Interrupted(root);
        int mirrorRequests = 0, officialRequests = 0;
        var handler = new Handler { Respond = request => {
            if (request.RequestUri.Host == "bmclapi2.bangbang93.com") {
                mirrorRequests++;
                return new HttpResponseMessage((HttpStatusCode)429) {
                    Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(10)) }
                };
            }
            officialRequests++;
            long offset = request.Headers.Range == null ? 0 : request.Headers.Range.Ranges.Single().From.Value;
            if (officialRequests == 1) Require(offset == 5, "A change of source must resume a verified prefix.");
            return offset == 0 ? Response(Payload) : Response(Payload.Skip((int)offset).ToArray(), (int)offset, Payload.Length);
        }};
        var spec = J.Merge(DownloadSpec(), J.Map("url", "https://piston-data.mojang.com/file"));
        using (var net = new Network(J.Map("proxyMode", "direct", "concurrency", 1), Path.Combine(root, "cache/transfers"), handler)) {
            foreach (var name in new[] { "resumed.bin", "next.bin" })
                net.FileTo(spec, Path.Combine(root, name), CancellationToken.None, "https://bmclapi2.bangbang93.com/file").GetAwaiter().GetResult();
            var report = net.TransferStatus();
            Require(report.S("switch").Contains("429") && report.S("switch").Contains("Mojang"), "Report the actual source change and reason.");
            Require(Convert.ToInt64(report.At("bytes")) == Payload.Length * 2 - 5, "Speed must count only newly received bytes.");
        }
        Require(mirrorRequests == 1 && officialRequests == 2, "Avoid repeating requests to a rate-limited mirror.");
        Require(File.ReadAllBytes(Path.Combine(root, "resumed.bin")).SequenceEqual(Payload), "Verify merged downloads before publishing.");
        Console.WriteLine("PASS: mirror rate limit is bypassed across files, Range resumes on the official source, and the switch is reported.");
    }

    static async Task CheckMirrorTimeout(string root) {
        var stalled = 0;
        var handler = new Handler { RespondAsync = async (request, ct) => {
            if (request.RequestUri.Host == "mirror.invalid") {
                stalled++;
                await Task.Delay(Timeout.Infinite, ct);
            }
            return Response(Payload);
        }};
        using (var net = new Network(J.Map("proxyMode", "direct"), Path.Combine(root, "transfers"), handler))
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20))) {
            await net.FileTo(DownloadSpec(), Path.Combine(root, "file.bin"), timeout.Token, "https://mirror.invalid/file");
            Require(stalled == 1 && net.TransferStatus().S("switch").Contains("超时"), "A stalled mirror must give way to the next source.");
        }
        Console.WriteLine("PASS: source fallback cancels a stalled request before the ordinary deadline.");
    }

    static async Task CheckLoaderLists(string root) {
        var settings = J.Map("proxyMode", "direct", "downloadSource", "bmclapi");
        var minecraft = new Minecraft(root, settings);
        var requests = new System.Collections.Generic.List<Uri>();
        bool fail = false;
        var handler = new Handler { Respond = request => {
            var uri = request.RequestUri;
            requests.Add(uri);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
            if (fail || uri.AbsolutePath == "/forge/minecraft/1.20.1") {
                response.StatusCode = HttpStatusCode.ServiceUnavailable;
                response.Content = new StringContent("");
            } else if (uri.Host == "files.minecraftforge.net") {
                response.Content = new StringContent("{\"1.20.1\":[\"1.20.1-47.4.20\"]}");
            } else if (uri.AbsolutePath == "/neoforge/list/1.21.1") {
                response.Content = new StringContent("[{\"mcversion\":\"1.21.1\",\"version\":\"21.1.99\"},{\"mcversion\":\"1.21.2\",\"version\":\"21.2.3\"}]");
            } else throw new Exception("Unexpected loader endpoint: " + uri);
            return response;
        }};
        using (var net = new Network(settings, transport: handler)) {
            var ct = CancellationToken.None;
            const string mirror = "https://bmclapi2.bangbang93.com";
            const string neoFile = "/net/neoforged/neoforge/21.1.99/neoforge-21.1.99-installer.jar";
            const string fabricProfile = "/v2/versions/loader/1.20.1/0.18.4/profile/json";
            Require(minecraft.MirrorFor("https://maven.neoforged.net/releases" + neoFile) == mirror + "/maven" + neoFile &&
                minecraft.MirrorFor("https://meta.fabricmc.net" + fabricProfile) == mirror + "/fabric-meta" + fabricProfile &&
                minecraft.MirrorFor("https://maven.minecraftforge.net/net/minecraftforge/test.jar") == mirror + "/maven/net/minecraftforge/test.jar",
                "Loader profiles and installers should resolve to the configured mirror.");
            var neo = await minecraft.Loaders(net, "1.21.1", "neoforge", ct);
            Require(neo.A().Single().S("id") == "21.1.99" && requests.Count == 1 &&
                requests[0].Host == "bmclapi2.bangbang93.com", "The NeoForge list should use the configured mirror.");
            var forge = await minecraft.Loaders(net, "1.20.1", "forge", ct);
            Require(forge.A().Single().S("id") == "47.4.20" && requests.Count == 3 &&
                requests[2].Host == "files.minecraftforge.net", "An unavailable Forge mirror must fall back to the official list.");
            var cached = await new Minecraft(root, settings).Loaders(net, "1.20.1", "forge", ct);
            Require(cached.A().Single().S("id") == "47.4.20" && requests.Count == 3, "Reloading the launcher can reuse a fresh list.");
            settings["downloadSource"] = "mojang";
            Require(minecraft.MirrorFor("https://maven.neoforged.net/releases" + neoFile) == "", "Official mode never rewrites the installer URL.");
            await minecraft.Loaders(net, "1.20.1", "forge", ct);
            Require(requests.Count == 4 && requests[3].Host == "files.minecraftforge.net", "Official mode uses its own cache and source.");
            fail = true;
            var previous = await minecraft.Loaders(net, "1.20.1", "forge", ct, refresh: true);
            Require(previous.A().Single().S("id") == "47.4.20" && requests.Count == 5, "A failed explicit refresh keeps the previously validated list.");
        }
        Console.WriteLine("PASS: loader mirror, official fallback, cache reuse, source settings, and refresh recovery.");
    }

    static async Task CheckConcurrency() {
        var scheduler = new TransferScheduler(12);
        var first = scheduler.For(new Uri("https://first.invalid/"));
        var second = scheduler.For(new Uri("https://second.invalid/"));
        int active = 0, peak = 0;
        var sync = new object();
        await Task.WhenAll(Enumerable.Range(0, 48).Select(async i => {
            var host = i % 4 == 0 ? second : first;
            await scheduler.Enter(host, false, CancellationToken.None);
            lock (sync) peak = Math.Max(peak, ++active);
            await Task.Delay(20);
            lock (sync) active--;
            scheduler.Leave(host, true, "", 0, false);
        }));
        Require(peak == 12, "Idle hosts may share the configured connection budget.");
        Require(first.Limit == 12, "Fast responses should increase host concurrency up to the budget.");
        await scheduler.Enter(first, false, CancellationToken.None);
        scheduler.Leave(first, false, "rate limit", 1000, true);
        Require(first.Limit == 6 && await scheduler.Enter(first, true, CancellationToken.None) == "rate limit",
            "Rate limits reduce admission and immediately make an alternate available.");
        using (var canceled = new CancellationTokenSource(30)) {
            try {
                await scheduler.Enter(first, false, canceled.Token);
                throw new Exception("Waiting for a rate-limited source must be cancelable.");
            } catch (OperationCanceledException) { }
        }
        Console.WriteLine("PASS: global cap, per-host ramp-up, throttling, and cancellation while awaiting a source.");
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
        CheckPackSizes(Path.Combine(root, "pack-sizes"));
        CheckSourceFallback(Path.Combine(root, "source-fallback"));
        CheckMirrorTimeout(Path.Combine(root, "mirror-timeout")).GetAwaiter().GetResult();
        CheckLoaderLists(Path.Combine(root, "loader-lists")).GetAwaiter().GetResult();
        CheckConcurrency().GetAwaiter().GetResult();
        CheckStorage(Path.Combine(root, "storage"));
        CheckTasks(Path.Combine(root, "profile"));
    }
}
