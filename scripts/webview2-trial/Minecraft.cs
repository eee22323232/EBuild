using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

sealed class Minecraft
{
    const string Official = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    const string Mirror = "https://bmclapi2.bangbang93.com";
    const string Fabric = "https://meta.fabricmc.net/v2/versions/loader/";
    const string Neo = "https://maven.neoforged.net/releases/net/neoforged/neoforge/";
    internal string Root;
    internal object Settings;
    internal string Game { get { return Path.Combine(Root, "game"); } }
    internal string Libraries { get { return Path.Combine(Game, "libraries"); } }
    internal string Assets { get { return Path.Combine(Game, "assets"); } }
    internal Minecraft(string root, object settings) { Root = root; Settings = settings; }
    string MirrorFor(string url) {
        if (Settings.S("downloadSource") != "bmclapi") return "";
        var uri = Network.Https(url);
        if (uri.Host == "resources.download.minecraft.net") return Mirror + "/assets" + uri.AbsolutePath;
        if (uri.Host == "libraries.minecraft.net") return Mirror + "/maven" + uri.AbsolutePath;
        if (new[] { "piston-meta.mojang.com", "piston-data.mojang.com", "launcher.mojang.com", "launchermeta.mojang.com" }.Contains(uri.Host))
            return Mirror + uri.AbsolutePath;
        return "";
    }
    Task Fetch(Network net, object spec, string target, CancellationToken ct) {
        return net.FileTo(spec, target, ct, MirrorFor(spec.S("url")));
    }
    internal async Task<object> Catalog(Network net, CancellationToken ct) {
        var cache = Path.Combine(Root, "versions.json");
        var saved = J.Read(cache);
        if (saved != null && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < TimeSpan.FromMinutes(10)) return saved;
        foreach (var endpoint in (Settings.S("downloadSource") == "bmclapi" ? new[] { MirrorFor(Official), Official } : new[] { Official })) {
            try {
                var result = ParseCatalog(await net.Json(endpoint, ct).ConfigureAwait(false));
                J.Save(cache, result);
                return result;
            } catch (Exception) { if (ct.IsCancellationRequested || (endpoint == Official && saved == null)) throw; }
        }
        return saved;
    }
    internal static object ParseCatalog(object catalog) {
        var versions = catalog.A("versions").Where(x => x.S("type") == "release" || x.S("type") == "snapshot").ToArray();
        if (versions.Length == 0) throw new IOException("游戏版本清单为空。");
        return J.Map("latest", catalog.At("latest"), "versions", versions);
    }
    internal async Task<object> Loaders(Network net, string version, string loader, CancellationToken ct) {
        J.Id(version);
        if (loader == "fabric") return (await net.Json(Fabric + version, ct)).A().Select(x => {
            var spec = x.At("loader"); return J.Map("id", spec.S("version"), "stable", spec.B("stable"));
        }).ToArray();
        var match = Regex.Match(version, @"^(\d+)\.(\d+)(?:\.(\d+))?$");
        if (loader != "neoforge" || !match.Success) return new object[0];
        var parts = match.Groups;
        var prefix = (parts[1].Value == "1" ? parts[2].Value + "." + (parts[3].Success ? parts[3].Value : "0") :
            parts[1].Value + "." + parts[2].Value + "." + (parts[3].Success ? parts[3].Value : "0")) + ".";
        var xml = await net.Text(Neo + "maven-metadata.xml", ct);
        return Regex.Matches(xml, @"<version>([a-z0-9.-]+)</version>", RegexOptions.IgnoreCase).Cast<Match>().Reverse()
            .Select(m => m.Groups[1].Value).Where(x => x.StartsWith(prefix, StringComparison.Ordinal))
            .Select(x => J.Map("id", x, "stable", !x.Contains("-"))).ToArray();
    }
    internal static string Quote(string value) {
        return "\"" + Regex.Replace(value, @"(\\*)(""|$)", m => m.Groups[1].Value + m.Groups[1].Value + (m.Groups[2].Value.Length != 0 ? "\\\"" : "")) + "\"";
    }
    static string ArgFile(string value) { return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\""; }
    internal static async Task<Tuple<int,string>> Run(string exe, IEnumerable<string> args, string directory, CancellationToken ct) {
        using (var proc = new Process { StartInfo = new ProcessStartInfo(exe, String.Join(" ", args.Select(Quote))) {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true },
            EnableRaisingEvents = true }) {
            var done = new TaskCompletionSource<int>();
            proc.Exited += (s,e) => done.TrySetResult(proc.ExitCode);
            proc.Start();
            var output = proc.StandardOutput.ReadToEndAsync();
            var errors = proc.StandardError.ReadToEndAsync();
            using (ct.Register(() => { try { if (!proc.HasExited) proc.Kill(); } catch (InvalidOperationException) {} })) {
                var code = await done.Task.ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return Tuple.Create(code, (await output.ConfigureAwait(false)) + "\n" + (await errors.ConfigureAwait(false)));
            }
        }
    }
    internal IEnumerable<string> JavaCandidates(string custom) {
        if (!String.IsNullOrEmpty(custom)) yield return custom;
        var dir = Path.Combine(Root, "runtimes");
        if (Directory.Exists(dir)) foreach (var runtime in Directory.GetDirectories(dir)) yield return Path.Combine(runtime, "bin", "java.exe");
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!String.IsNullOrEmpty(home)) yield return Path.Combine(home, "bin", "java.exe");
        yield return "java.exe";
    }
    internal async Task<int> JavaMajor(string exe, CancellationToken ct) {
        try {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                deadline.CancelAfter(7000);
                var text = (await Run(exe, new[] { "-version" }, Root, deadline.Token)).Item2;
                var version = Regex.Match(text, @"version ""(?:1\.)?(\d+)");
                if (version.Success) return Int32.Parse(version.Groups[1].Value);
            }
        } catch (Exception e) {
            if (!(e is IOException) && !(e is OperationCanceledException) && !(e is System.ComponentModel.Win32Exception)) throw;
            ct.ThrowIfCancellationRequested();
        }
        return 0;
    }
    internal async Task<string> Java(Network net, int major, string custom, Action<string,string,int,int> progress, CancellationToken ct) {
        foreach (var exe in JavaCandidates(custom).Distinct(StringComparer.OrdinalIgnoreCase)) if (await JavaMajor(exe, ct) == major) return exe;
        if (custom.Length != 0) throw new IOException("选定的 Java 版本不匹配，游戏需要 Java " + major + "。");
        progress("java", "正在下载 Java " + major, 0, 0);
        var api = "https://api.adoptium.net/v3/assets/latest/" + major + "/hotspot?architecture=x64&image_type=jre&os=windows&vendor=eclipse";
        var pkg = (await net.Json(api, ct)).A().FirstOrDefault().At("binary").At("package");
        if (pkg.S("checksum").Length != 64) throw new IOException("未找到 Java " + major + "，请在设置中手动选择。");
        var archive = Path.Combine(Root, "cache", "java-" + major + ".zip");
        await net.FileTo(J.Map("url", pkg.S("link"), "sha256", pkg.S("checksum"), "size", pkg.At("size")), archive, ct);
        var output = Path.Combine(Root, "runtimes", "java-" + major);
        var temporary = output + "-" + Guid.NewGuid().ToString("N");
        try {
            Network.Unzip(archive, temporary, ct);
            var folder = Directory.GetDirectories(temporary).FirstOrDefault(x => File.Exists(Path.Combine(x, "bin", "java.exe")));
            if (folder == null || await JavaMajor(Path.Combine(folder, "bin", "java.exe"), ct) != major) throw new IOException("Java 解压校验失败。");
            if (Directory.Exists(output)) Directory.Delete(output, true);
            Directory.Move(folder, output);
            File.Delete(archive);
            return Path.Combine(output, "bin", "java.exe");
        } finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }
    static bool Allowed(object rules, bool resolution) {
        if (rules == null) return true;
        var allow = false;
        foreach (var rule in rules.A()) {
            var os = rule.At("os");
            if (os.At("name") != null && os.S("name") != "windows") continue;
            if (os.At("arch") != null && os.S("arch") != "x86_64" && os.S("arch") != "amd64") continue;
            if (os.At("version") != null && !Regex.IsMatch(Environment.OSVersion.Version.ToString(), os.S("version"))) continue;
            if (rule.At("features").Obj().Any(x => Object.Equals(x.Value, true) != (x.Key == "has_custom_resolution" && resolution))) continue;
            allow = rule.S("action") == "allow";
        }
        return allow;
    }
    static string MavenPath(string input) {
        var ext = input.Split('@');
        var fields = ext[0].Split(':');
        if (fields.Length < 3 || fields.Length > 4 || fields.Any(x => !Regex.IsMatch(x, @"^[a-zA-Z0-9_.+-]+$")))
            throw new IOException("无效的 Java 库坐标。");
        return fields[0].Replace('.', '/') + "/" + fields[1] + "/" + fields[2] + "/" + fields[1] + "-" + fields[2] +
            (fields.Length == 4 ? "-" + fields[3] : "") + "." + (ext.Length == 2 ? J.Id(ext[1]) : "jar");
    }
    sealed class Prepared {
        internal string Java, Client, Native, Logging, AssetsId, VirtualAssets;
        internal object Base, Loader;
        internal int Major;
        internal List<string> Classpath = new List<string>();
    }
    sealed class FileJob {
        internal object Spec;
        internal string Target;
    }
    async Task<Prepared> Prepare(Network net, object instance, string dir, object effective, Action<string,string,int,int> progress, CancellationToken ct) {
        var version = J.Id(instance.S("version"));
        var jsonFile = J.Inside(Game, "versions/" + version + "/" + version + ".json");
        if (!File.Exists(jsonFile)) {
            var manifest = (await Catalog(net, ct)).A("versions").FirstOrDefault(x => x.S("id") == version);
            if (manifest == null) throw new IOException("无法找到 Minecraft " + version + " 的版本清单。");
            await Fetch(net, manifest, jsonFile, ct);
        }
        var baseSpec = J.Read(jsonFile);
        var asset = baseSpec.At("assetIndex");
        var indexId = J.Id(asset.S("id"));
        var index = J.Inside(Assets, "indexes/" + indexId + ".json");
        await Fetch(net, asset, index, ct);
        var p = new Prepared { Base = baseSpec, Client = Path.ChangeExtension(jsonFile, "jar"), Native = Path.Combine(dir, ".natives"),
            AssetsId = indexId, VirtualAssets = J.Inside(Assets, "virtual/" + indexId), Major = baseSpec.At("javaVersion").I("majorVersion", 8) };
        var jobs = new List<FileJob>();
        var natives = new List<Tuple<string,string[]>>();
        jobs.Add(new FileJob { Spec = baseSpec.At("downloads").At("client"), Target = p.Client });
        var log = baseSpec.At("logging").At("client").At("file");
        if (log != null) {
            p.Logging = J.Inside(Assets, "log_configs/" + J.Id(log.S("id")));
            jobs.Add(new FileJob { Spec = log, Target = p.Logging });
        }
        var assets = J.Read(index);
        foreach (var data in assets.At("objects").Obj().Values) {
            var hash = data.S("hash");
            if (!Regex.IsMatch(hash, "^[0-9a-f]{40}$")) throw new IOException("资源哈希无效。");
            var relative = hash.Substring(0,2) + "/" + hash;
            jobs.Add(new FileJob { Spec = J.Map("url", "https://resources.download.minecraft.net/" + relative, "sha1", hash, "size", data.At("size")),
                Target = J.Inside(Assets, "objects/" + relative) });
        }
        foreach (var item in baseSpec.A("libraries")) {
            if (!Allowed(item.At("rules"), false)) continue;
            var downloads = item.At("downloads");
            var native = item.At("natives").S("windows").Replace("$" + "{arch}", "64");
            var specs = new[] { downloads.At("artifact"), downloads.At("classifiers").At(native) };
            for (int i = 0; i < specs.Length; i++) if (specs[i] != null) {
                var file = J.Inside(Libraries, specs[i].S("path"));
                jobs.Add(new FileJob { Target = file, Spec = specs[i] });
                if (i == 0) p.Classpath.Add(file); else natives.Add(Tuple.Create(file, item.At("extract").A("exclude").Select(x => x.S()).ToArray()));
            }
        }
        var unique = jobs.GroupBy(x => x.Target, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToArray();
        int finished = 0;
        progress("download", "正在校验或下载游戏文件", 0, unique.Length);
        await Network.Pool(unique, effective.I("concurrency", 12), async job => {
            await Fetch(net, job.Spec, job.Target, ct).ConfigureAwait(false);
            var done = Interlocked.Increment(ref finished);
            if (done % 40 == 0 || done == unique.Length) progress("download", "正在校验或下载游戏文件", done, unique.Length);
        }, ct);
        Directory.CreateDirectory(p.Native);
        foreach (var native in natives) Network.Unzip(native.Item1, p.Native, ct, n => native.Item2.Any(e => n.StartsWith(e, StringComparison.Ordinal)) ? null : n);
        foreach (var dest in new[] { assets.B("virtual") ? p.VirtualAssets : null, assets.B("map_to_resources") ? Path.Combine(dir, "resources") : null }.Where(x => x != null))
            foreach (var data in assets.At("objects").Obj()) {
                ct.ThrowIfCancellationRequested();
                var hash = data.Value.S("hash");
                var output = J.Inside(dest, data.Key); Directory.CreateDirectory(Path.GetDirectoryName(output));
                File.Copy(J.Inside(Assets, "objects/" + hash.Substring(0,2) + "/" + hash), output, true);
            }
        p.Java = await Java(net, p.Major, effective.S("java"), progress, ct);
        if (instance.S("loader") != "vanilla") await InstallLoader(net, instance, p, progress, ct);
        p.Classpath.Add(p.Client);
        return p;
    }
    async Task InstallLoader(Network net, object instance, Prepared p, Action<string,string,int,int> progress, CancellationToken ct) {
        var loader = J.Id(instance.S("loader"));
        var version = J.Id(instance.S("version"));
        var lver = J.Id(instance.S("loaderVersion"));
        progress("download", "正在安装 " + loader, 0, 0);
        if (loader == "fabric") {
            var json = J.Inside(Game, "versions/" + version + "/fabric-" + lver + ".json");
            p.Loader = J.Read(json);
            if (p.Loader == null) {
                p.Loader = await net.Json(Fabric + version + "/" + lver + "/profile/json", ct);
                if (p.Loader.S("mainClass") == "") throw new IOException("Fabric 启动清单无效。");
                J.Save(json,p.Loader);
            }
        }
        else if (loader == "neoforge") {
            var key = "neoforge-" + lver;
            var json = J.Inside(Game, "versions/" + key + "/" + key + ".json");
            if (!File.Exists(json)) {
                var installer = J.Inside(Root, "cache/" + key + "-installer.jar");
                var url = Neo + lver + "/" + key + "-installer.jar";
                var sha = (await net.Text(url + ".sha1", ct)).Trim().Split(' ')[0];
                if (!Regex.IsMatch(sha, "^[a-fA-F0-9]{40}$")) throw new IOException("NeoForge 安装器校验和无效。");
                await net.FileTo(J.Map("url", url, "sha1", sha), installer, ct);
                var profile = Path.Combine(Game, "launcher_profiles.json");
                if (!File.Exists(profile)) J.Save(profile, J.Map("profiles", J.Map()));
                var result = await Run(p.Java, new[] { "-jar", installer, "--installClient", Game }, Game, ct);
                File.WriteAllText(J.Inside(Root, "cache/" + key + "-install.log"), result.Item2);
                if (result.Item1 != 0 || !File.Exists(json)) throw new IOException("NeoForge 安装失败，可查看数据目录 cache 下的安装日志。");
            }
            p.Loader = J.Read(json);
        } else throw new IOException("此加载器尚未支持。");
        var libraries = p.Loader.A("libraries").Where(x => Allowed(x.At("rules"), false)).Select(x => {
            var artifact = x.At("downloads").At("artifact");
            var relative = artifact == null ? MavenPath(x.S("name")) : artifact.S("path");
            var origin = x.S("url") == "" ? "https://libraries.minecraft.net" : x.S("url").TrimEnd('/');
            return new FileJob { Target = J.Inside(Libraries, relative), Spec = artifact ?? J.Map("url", origin + "/" + relative,
                "size", x.At("size"), "sha1", x.At("sha1"), "sha256", x.At("sha256"), "sha512", x.At("sha512")) };
        }).ToArray();
        await Network.Pool(libraries.GroupBy(x => x.Target).Select(x => x.First()), 10, job => Fetch(net, job.Spec, job.Target, ct), ct);
        var replaced = libraries.Select(x => Path.GetDirectoryName(Path.GetDirectoryName(x.Target))).ToArray();
        p.Classpath.RemoveAll(x => replaced.Contains(Path.GetDirectoryName(Path.GetDirectoryName(x)), StringComparer.OrdinalIgnoreCase));
        p.Classpath.InsertRange(0, libraries.Select(x => x.Target));
    }
    internal async Task Install(Network net, object instance, string dir, object effective, Action<string,string,int,int> progress, CancellationToken ct) {
        await Prepare(net, instance, dir, effective, progress, ct);
    }
    static IEnumerable<string> Arguments(object[] entries, Dictionary<string,object> replacements, bool resolution) {
        foreach (var entry in entries) {
            var values = entry is string ? new[] { entry } : !Allowed(entry.At("rules"), resolution) ? new object[0] :
                entry.At("value") is string ? new[] { entry.At("value") } : entry.A("value");
            foreach (var value in values) yield return Regex.Replace(value.S(), @"\$\{([^}]+)\}", m => replacements.S(m.Groups[1].Value));
        }
    }
    internal async Task<Process> Launch(Network net, object instance, string dir, object effective, object account,
        Action<string,string,int,int> progress, CancellationToken ct) {
        var p = await Prepare(net, instance, dir, effective, progress, ct);
        byte[] uuid;
        using (var md5 = MD5.Create()) uuid = md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" + effective.S("username")));
        uuid[6] = (byte)((uuid[6] & 15) | 48); uuid[8] = (byte)((uuid[8] & 63) | 128);
        var vars = J.Map("auth_player_name", account == null ? effective.S("username") : account.S("name"),
            "auth_uuid", account == null ? Network.Hex(uuid) : account.S("id"), "auth_access_token", account == null ? "0" : account.S("token"),
            "auth_session", account == null ? "0" : account.S("token"), "auth_xuid", account.S("xuid"), "clientid", account.S("clientId"),
            "user_type", account == null ? "legacy" : "msa", "user_properties", "{}", "version_name", instance.S("version"),
            "version_type", p.Base.S("type"), "game_directory", dir, "game_assets", p.VirtualAssets, "assets_root", Assets, "assets_index_name", p.AssetsId,
            "natives_directory", p.Native, "launcher_name", "EBuild", "launcher_version", "0.1.1", "classpath", String.Join(";", p.Classpath.Distinct()),
            "classpath_separator", ";", "library_directory", Libraries, "resolution_width", effective.I("width"), "resolution_height", effective.I("height"));
        var hasSize = effective.I("width") > 0 && effective.I("height") > 0;
        var jvmBase = p.Base.At("arguments").A("jvm");
        if (jvmBase.Length == 0) jvmBase = new object[] { "-Djava.library.path=" + dir + "\\.natives", "-cp", vars.S("classpath") };
        var jvm = new List<string> { "-Xmx" + effective.I("memory",4096) + "M" };
        jvm.AddRange(Arguments(jvmBase.Concat(p.Loader.At("arguments").A("jvm")).ToArray(), vars, hasSize));
        if (effective.S("gc") != "") jvm.Add("-XX:+Use" + effective.S("gc") + "GC");
        jvm.AddRange(effective.A("jvmArgs").Select(x => x.S()));
        if (p.Logging != null) jvm.Add(p.Base.At("logging").At("client").S("argument").Replace("$" + "{path}", p.Logging));
        jvm.Add(p.Loader == null ? p.Base.S("mainClass") : p.Loader.S("mainClass"));
        var baseGame = p.Base.At("arguments").A("game");
        if (baseGame.Length == 0) baseGame = p.Base.S("minecraftArguments").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (p.Loader.S("id") != "") vars["version_name"] = p.Loader.S("id");
        var game = Arguments(baseGame.Concat(p.Loader.At("arguments").A("game")).ToArray(), vars, hasSize).ToList();
        if (hasSize && !game.Contains("--width")) game.AddRange(new[] { "--width", effective.S("width"), "--height", effective.S("height") });
        game.AddRange(effective.A("gameArgs").Select(x => x.S()));
        string argsFile = null;
        if (p.Major >= 9) {
            argsFile = Path.Combine(dir, ".launch-" + Guid.NewGuid().ToString("N") + ".args");
            var encoding = p.Major >= 18 ? new UTF8Encoding(false) : Encoding.Default;
            File.WriteAllLines(argsFile, jvm.Select(ArgFile), encoding);
            jvm = new List<string> { "@" + Path.GetFileName(argsFile) };
        }
        var logs = effective.B("logs");
        var log = Path.Combine(dir,"logs","ebuild-launch.log");
        if (logs) { Directory.CreateDirectory(Path.GetDirectoryName(log)); File.WriteAllText(log,""); }
        var proc = new Process { StartInfo = new ProcessStartInfo(p.Java, String.Join(" ", jvm.Concat(game).Select(Quote))) {
            WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = logs, RedirectStandardError = logs
        }, EnableRaisingEvents = true };
        if (argsFile != null) proc.Exited += (s,e) => { try { File.Delete(argsFile); } catch (IOException) {} };
        if (logs) {
            var mutex = new object();
            DataReceivedEventHandler append = (s,e) => { if (e.Data != null) lock (mutex) {
                var line = account == null || account.S("token") == "" ? e.Data : e.Data.Replace(account.S("token"), "[已隐藏]");
                try { File.AppendAllText(log, line + Environment.NewLine); } catch (IOException) {}
            }};
            proc.OutputDataReceived += append;
            proc.ErrorDataReceived += append;
        }
        ct.ThrowIfCancellationRequested(); progress("starting", "正在启动 Minecraft", 0, 0);
        try { proc.Start(); } catch { if (argsFile != null) File.Delete(argsFile); proc.Dispose(); throw; }
        if (logs) { proc.BeginOutputReadLine(); proc.BeginErrorReadLine(); }
        return proc;
    }
}
