using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
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
    const string Quilt = "https://meta.quiltmc.org/v3/versions/loader/";
    const string Forge = "https://maven.minecraftforge.net/net/minecraftforge/forge/";
    const string ForgeVersions = "https://files.minecraftforge.net/net/minecraftforge/forge/maven-metadata.json";
    internal static bool SupportsLoader(string loader) { return new[] { "vanilla", "fabric", "neoforge", "forge", "quilt" }.Contains(loader); }

    internal static bool SupportsForge(string version) {
        Version parsed;
        return !Version.TryParse(version, out parsed) || parsed.Major != 1 || parsed.Minor >= 13;
}
    const string Neo = "https://maven.neoforged.net/releases/net/neoforged/neoforge/";
    internal string Root;
    internal object Settings;
    readonly Dictionary<string,Tuple<string,int>> javaVersions = new Dictionary<string,Tuple<string,int>>(StringComparer.OrdinalIgnoreCase);
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
    Task Fetch(Network net, object spec, string target, CancellationToken ct, LaunchChecks checks = null) {
        var url = spec.S("url");
        return net.FileTo(spec, target, ct, url == "" ? "" : MirrorFor(url), checks);
    }
    internal async Task<object> Catalog(Network net, CancellationToken ct, bool force = false) {
        var cache = Path.Combine(Root, "versions.json");
        var preferred = Settings.S("downloadSource");
        var saved = J.Read(cache);
        var cached = saved == null ? null : J.Merge(saved, J.Map("cached", true));
        if (!force && cached != null && saved.S("preferredSource") == preferred &&
            DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < TimeSpan.FromMinutes(10)) return cached;
        var endpoints = preferred == "bmclapi" ? new[] { MirrorFor(Official), Official } : new[] { Official };
        foreach (var endpoint in endpoints) {
            try {
                var catalog = ParseCatalog(await net.Json(endpoint, ct).ConfigureAwait(false));
                var source = endpoint == Official ? "mojang" : "bmclapi";
                var result = J.Merge(catalog, J.Map("source", source, "preferredSource", preferred));
                J.Save(cache, result);
                return result;
            } catch (Exception) { if (ct.IsCancellationRequested || (endpoint == Official && cached == null)) throw; }
        }
        return cached;
    }
    internal static object ParseCatalog(object catalog) {
        var versions = catalog.A("versions").Where(x => x.S("type") == "release" || x.S("type") == "snapshot").ToArray();
        if (versions.Length == 0) throw new IOException("游戏版本清单为空。");
        return J.Map("latest", catalog.At("latest"), "versions", versions);
    }
    static Version NumericLoaderVersion(string id) {
        Version version;
        return Version.TryParse(id.Split('-','+')[0], out version) ? version : new Version();
    }

    static IEnumerable<string> LatestLoaders(IEnumerable<string> ids) {
        return ids.Where(id => Regex.IsMatch(id, @"^[0-9][a-zA-Z0-9_.+\-]{0,79}$"))
            .Distinct(StringComparer.Ordinal).OrderByDescending(NumericLoaderVersion)
            .ThenByDescending(id => !id.Contains("-")).ThenByDescending(id => id.Split('.').Last().I(null));
    }

    static Dictionary<string,object> LoaderOption(string id) { return J.Map("id", id, "stable", !id.Contains("-")); }

    static async Task<object> QuiltLoaders(Network net, string version, CancellationToken ct) {
        var response = await net.Request(Quilt + version, ct);
        if (response.Item1 == 404) return new object[0];
        if (response.Item1 != 200) throw new IOException("Quilt Meta 请求失败（HTTP " + response.Item1 + "）。");
        var entries = response.Item2.A();
        var ids = LatestLoaders(entries.Select(entry => entry.At("loader").S("version")));
        if (entries.Any(entry => entry.At("intermediary") != null)) return ids.Select(LoaderOption).ToArray();
        // An unmapped game can return generic loader entries. Only offer recent builds with real client profiles.
        var candidates = ids.Take(12).ToArray();
        var profiles = await Task.WhenAll(candidates.Select(id => net.Request(Quilt + version + "/" + Uri.EscapeDataString(id) + "/profile/json", ct)));
        return candidates.Where((id,index) => profiles[index].Item1 == 200 && profiles[index].Item2.S("inheritsFrom") == version &&
            profiles[index].Item2.S("mainClass") != "").Select(LoaderOption).ToArray();
    }

    internal async Task<object> Loaders(Network net, string version, string loader, CancellationToken ct) {
        J.Id(version);
        if (loader == "quilt") return await QuiltLoaders(net, version, ct);
        if (loader == "forge") {
            if (!SupportsForge(version)) return new object[0];
            var forgePrefix = version + "-";
            var source = await net.Json(ForgeVersions, ct);
            var versions = source.A(version).Select(value => value.S()).Where(id => id.StartsWith(forgePrefix, StringComparison.Ordinal));
            return LatestLoaders(versions.Select(id => id.Substring(forgePrefix.Length))).Select(LoaderOption).ToArray();
        }
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
    static async Task<string> ReadProcessOutput(StreamReader reader, Action<string> onLine) {
        if (onLine == null) return await reader.ReadToEndAsync().ConfigureAwait(false);
        var output = new StringBuilder();
        string line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null) {
            output.AppendLine(line);
            onLine(line);
        }
        return output.ToString();
    }
    internal static async Task<Tuple<int,string>> Run(string exe, IEnumerable<string> args, string directory, CancellationToken ct, Action<string> onLine = null) {
        using (var proc = new Process { StartInfo = new ProcessStartInfo(exe, String.Join(" ", args.Select(Quote))) {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true },
            EnableRaisingEvents = true }) {
            var done = new TaskCompletionSource<int>();
            proc.Exited += (s,e) => done.TrySetResult(proc.ExitCode);
            proc.Start();
            var output = ReadProcessOutput(proc.StandardOutput, onLine);
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
    internal async Task<string> FindJava(int major, string custom, CancellationToken ct) {
        foreach (var exe in JavaCandidates(custom).Distinct(StringComparer.OrdinalIgnoreCase)) {
            ct.ThrowIfCancellationRequested();
            var stamp = Path.IsPathRooted(exe) ? LaunchChecks.Stamp(exe) : null;
            Tuple<string,int> cached;
            int version;
            if (stamp != null && javaVersions.TryGetValue(exe, out cached) && cached.Item1 == stamp) version = cached.Item2;
            else {
                version = await JavaMajor(exe, ct);
                if (stamp != null && version > 0 && LaunchChecks.Stamp(exe) == stamp) javaVersions[exe] = Tuple.Create(stamp, version);
            }
            if (version == major) return exe;
        }
        return null;
    }
    internal async Task<string> Java(Network net, int major, string custom, Action<string,string,int,int> progress, CancellationToken ct) {
        var installed = await FindJava(major, custom, ct);
        if (installed != null) return installed;
        if (custom.Length != 0) throw new IOException("选定的 Java 版本不匹配，游戏需要 Java " + major + "。");
        return await DownloadJava(net, major, progress, ct);
    }
    internal async Task<string> DownloadJava(Network net, int major, Action<string,string,int,int> progress, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
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
    internal async Task<object> ReadBaseVersion(Network net, object instance, CancellationToken ct) {
        var version = J.Id(instance.S("version"));
        var jsonFile = J.Inside(Game, "versions/" + version + "/" + version + ".json");
        if (!File.Exists(jsonFile)) {
            var manifest = (await Catalog(net, ct)).A("versions").FirstOrDefault(x => x.S("id") == version);
            if (manifest == null) throw new IOException("无法找到 Minecraft " + version + " 的版本清单。");
            await Fetch(net, manifest, jsonFile, ct);
        }
        return J.Read(jsonFile);
    }
    internal static int RequiredJavaMajor(object baseSpec) {
        var major = baseSpec.At("javaVersion").I("majorVersion", 8);
        if (major <= 0) throw new IOException("游戏版本清单中的 Java 版本无效。");
        return major;
    }
    async Task<Prepared> Prepare(Network net, object instance, object baseSpec, string dir, object effective, string java,
        Action<string,string,int,int> progress, CancellationToken ct, bool full = false) {
        var checks = new LaunchChecks(Root, full);
        var version = J.Id(instance.S("version"));
        var asset = baseSpec.At("assetIndex");
        var indexId = J.Id(asset.S("id"));
        var index = J.Inside(Assets, "indexes/" + indexId + ".json");
        await Fetch(net, asset, index, ct, checks);
        var p = new Prepared { Base = baseSpec, Client = J.Inside(Game, "versions/" + version + "/" + version + ".jar"), Native = Path.Combine(dir, ".natives"),
            AssetsId = indexId, VirtualAssets = J.Inside(Assets, "virtual/" + indexId), Major = RequiredJavaMajor(baseSpec) };
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
            await Fetch(net, job.Spec, job.Target, ct, checks).ConfigureAwait(false);
            var done = Interlocked.Increment(ref finished);
            if (done % 40 == 0 || done == unique.Length) progress("download", "正在校验或下载游戏文件", done, unique.Length);
        }, ct);
        checks.PrepareNatives(p.Native, natives, ct);
        foreach (var dest in new[] { assets.B("virtual") ? p.VirtualAssets : null, assets.B("map_to_resources") ? Path.Combine(dir, "resources") : null }.Where(x => x != null))
            foreach (var data in assets.At("objects").Obj()) {
                ct.ThrowIfCancellationRequested();
                var hash = data.Value.S("hash");
                var output = J.Inside(dest, data.Key); Directory.CreateDirectory(Path.GetDirectoryName(output));
                File.Copy(J.Inside(Assets, "objects/" + hash.Substring(0,2) + "/" + hash), output, true);
            }
        p.Java = java ?? (full ? null : await Java(net, p.Major, effective.S("java"), progress, ct));
        if (instance.S("loader") != "vanilla") await InstallLoader(net, instance, p, effective, progress, ct, checks);
        checks.Save();
        p.Classpath.Add(p.Client);
        return p;
    }
    async Task InstallLoader(Network net, object instance, Prepared p, object effective, Action<string,string,int,int> progress, CancellationToken ct, LaunchChecks checks) {
        var loader = J.Id(instance.S("loader"));
        var version = J.Id(instance.S("version"));
        var lver = J.Id(instance.S("loaderVersion"));
        progress("download", "正在安装 " + loader, 0, 0);
        if (loader == "fabric" || loader == "quilt") {
            var json = J.Inside(Game, "versions/" + version + "/" + loader + "-" + lver + ".json");
            J.NoLinks(json);
            p.Loader = J.Read(json);
            if (p.Loader == null) {
                var endpoint = loader == "fabric" ? Fabric : Quilt;
                p.Loader = await net.Json(endpoint + version + "/" + Uri.EscapeDataString(lver) + "/profile/json", ct);
                if (p.Loader.S("mainClass") == "" || p.Loader.S("inheritsFrom") != version) throw new IOException(loader + " 启动清单无效。");
                J.Save(json, p.Loader);
            }
        } else if (loader == "forge" || loader == "neoforge") {
            if (loader == "forge" && !SupportsForge(version)) throw new IOException("当前支持 Minecraft 1.13 及以后版本的 Forge 安装器。");
            var key = (loader == "forge" ? version + "-forge-" : "neoforge-") + lver;
            var mavenVersion = loader == "forge" ? version + "-" + lver : lver;
            var source = (loader == "forge" ? Forge : Neo) + mavenVersion + "/" + loader + "-" + mavenVersion + "-installer.jar";
            p.Loader = await RunLoaderInstaller(net, p, effective, key, source, loader == "forge", progress, ct, checks);
        } else throw new IOException("此加载器尚未支持。");
        var libraries = p.Loader.A("libraries").Where(x => Allowed(x.At("rules"), false)).Select(x => {
            var artifact = x.At("downloads").At("artifact");
            var relative = artifact == null ? MavenPath(x.S("name")) : artifact.S("path");
            var origin = x.S("url") == "" ? "https://libraries.minecraft.net" : x.S("url").TrimEnd('/');
            return new FileJob { Target = J.Inside(Libraries, relative), Spec = artifact ?? J.Map("url", origin + "/" + relative,
                "size", x.At("size"), "sha1", x.At("sha1"), "sha256", x.At("sha256"), "sha512", x.At("sha512")) };
        }).ToArray();
        await Network.Pool(libraries.GroupBy(x => x.Target).Select(x => x.First()), 10, job => Fetch(net, job.Spec, job.Target, ct, checks), ct);
        var replaced = libraries.Select(x => Path.GetDirectoryName(Path.GetDirectoryName(x.Target))).ToArray();
        p.Classpath.RemoveAll(x => replaced.Contains(Path.GetDirectoryName(Path.GetDirectoryName(x)), StringComparer.OrdinalIgnoreCase));
        p.Classpath.InsertRange(0, libraries.Select(x => x.Target));
    }
    FileJob[] ForgeDependencies(string installer) {
        var files = new Dictionary<string,FileJob>(StringComparer.OrdinalIgnoreCase);
        using (var zip = ZipFile.OpenRead(installer)) {
            foreach (var name in new[] { "install_profile.json", "version.json" }) {
                var entry = zip.GetEntry(name);
                if (entry == null) continue;
                using (var reader = new StreamReader(entry.Open())) {
                    foreach (var item in J.Parse(reader.ReadToEnd()).A("libraries")) {
                        var spec = item.At("downloads").At("artifact");
                        var relative = spec.S("path");
                        if (!spec.S("url").StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                            !Regex.IsMatch(spec.S("sha1"), "^[a-fA-F0-9]{40}$") ||
                            relative == "" || zip.GetEntry("maven/" + relative) != null) continue;
                        var target = J.Inside(Libraries, relative);
                        files[target] = new FileJob { Target = target, Spec = spec };
                    }
                }
            }
        }
        return files.Values.ToArray();
    }

    async Task PrepareForgeDependencies(Network net, string installer, object effective, Action<string,string,int,int> progress, CancellationToken ct, LaunchChecks checks) {
        var jobs = ForgeDependencies(installer);
        if (jobs.Length == 0) return;
        const string message = "正在校验或下载 Forge 依赖";
        progress("download", message, 0, jobs.Length);
        int done = 0;
        var progressLock = new object();
        await Network.Pool(jobs, effective.I("concurrency", 12), async job => {
            try { await Fetch(net, job.Spec, job.Target, ct, checks).ConfigureAwait(false); }
            catch (Exception error) {
                ct.ThrowIfCancellationRequested();
                if (!(error is IOException) && !(error is HttpRequestException) && !(error is OperationCanceledException)) throw;
                // The official installer can still try its own mirror for this dependency.
            }
            lock (progressLock) {
                done++;
                if (done % 4 == 0 || done == jobs.Length) progress("download", message, done, jobs.Length);
            }
        }, ct);
    }

    async Task<object> RunLoaderInstaller(Network net, Prepared prepared, object effective, string key, string url, bool isForge,
        Action<string,string,int,int> progress, CancellationToken ct, LaunchChecks checks) {
        var json = J.Inside(Game, "versions/" + key + "/" + key + ".json");
        J.NoLinks(json);
        var cached = J.Read(json);
        if (cached.S("mainClass") != "") return cached;

        if (prepared.Java == null) prepared.Java = await FindJava(prepared.Major, effective.S("java"), ct);
        if (prepared.Java == null) throw new IOException("修复加载器需要 Java " + prepared.Major + "。请在 Java 管理中安装后重试。");
        var installer = J.Inside(Root, "cache/" + key + "-installer.jar");
        if (isForge) progress("download", "正在准备 Forge 安装器", 0, 0);
        var sha = (await net.Text(url + ".sha1", ct)).Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (sha == null || !Regex.IsMatch(sha, "^[a-fA-F0-9]{40}$")) throw new IOException("加载器安装器校验和无效。");
        await net.FileTo(J.Map("url", url, "sha1", sha), installer, ct);
        if (isForge) await PrepareForgeDependencies(net, installer, effective, progress, ct, checks);
        var profile = J.Inside(Game, "launcher_profiles.json");
        J.NoLinks(profile);
        if (!File.Exists(profile)) J.Save(profile, J.Map("profiles", J.Map()));
        var log = J.Inside(Root, "cache/" + key + "-install.log");
        J.NoLinks(log);
        try {
            Action<string> onLine = null;
            if (isForge) {
                progress("install", "正在运行 Forge 安装器", 0, 0);
                onLine = line => {
                    if (line == "Downloading libraries") progress("download", "Forge 安装器正在检查依赖", 0, 0);
                    else if (line == "Building Processor") progress("install", "正在应用 Forge 补丁", 0, 0);
                    else if (line == "Injecting profile") progress("install", "正在写入 Forge 启动配置", 0, 0);
                };
            }
            var result = await Run(prepared.Java, new[] { "-jar", installer, "--installClient", Game }, Game, ct, onLine);
            File.WriteAllText(log, result.Item2);
            var installed = J.Read(json);
            if (result.Item1 != 0 || installed.S("mainClass") == "") throw new IOException("加载器安装失败，可查看数据目录 cache 下的安装日志。");
            return installed;
        } catch {
            if (File.Exists(json)) File.Delete(json);
            throw;
        }
    }

    internal async Task Install(Network net, object instance, string dir, object effective, Action<string,string,int,int> progress, CancellationToken ct, bool full = false) {
        var baseSpec = await ReadBaseVersion(net, instance, ct);
        await Prepare(net, instance, baseSpec, dir, effective, null, progress, ct, full);
    }
    static IEnumerable<string> Arguments(object[] entries, Dictionary<string,object> replacements, bool resolution) {
        foreach (var entry in entries) {
            var values = entry is string ? new[] { entry } : !Allowed(entry.At("rules"), resolution) ? new object[0] :
                entry.At("value") is string ? new[] { entry.At("value") } : entry.A("value");
            foreach (var value in values) yield return Regex.Replace(value.S(), @"\$\{([^}]+)\}", m => replacements.S(m.Groups[1].Value));
        }
    }
    internal static bool CanQuickJoin(string version) {
        var match = Regex.Match(version, @"^(\d{1,3})\.(\d{1,3})(?:\.\d{1,3})?$");
        if (!match.Success) return false;
        var major = Int32.Parse(match.Groups[1].Value);
        return major >= 26 || (major == 1 && Int32.Parse(match.Groups[2].Value) >= 20);
    }

    internal async Task<Process> Launch(Network net, object instance, object baseSpec, string dir, object effective, object account,
        string java, Action<string,string,int,int> progress, CancellationToken ct, Action beforeStart = null, string quickJoin = "") {
        var p = await Prepare(net, instance, baseSpec, dir, effective, java, progress, ct);
        byte[] uuid;
        using (var md5 = MD5.Create()) uuid = md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" + effective.S("username")));
        uuid[6] = (byte)((uuid[6] & 15) | 48); uuid[8] = (byte)((uuid[8] & 63) | 128);
        var vars = J.Map("auth_player_name", account == null ? effective.S("username") : account.S("name"),
            "auth_uuid", account == null ? Network.Hex(uuid) : account.S("id"), "auth_access_token", account == null ? "0" : account.S("token"),
            "auth_session", account == null ? "0" : account.S("token"), "auth_xuid", account.S("xuid"), "clientid", account.S("clientId"),
            "user_type", account == null ? "legacy" : "msa", "user_properties", "{}", "version_name", instance.S("version"),
            "version_type", p.Base.S("type"), "game_directory", dir, "game_assets", p.VirtualAssets, "assets_root", Assets, "assets_index_name", p.AssetsId,
            "natives_directory", p.Native, "launcher_name", "EBuild", "launcher_version", "0.1.2", "classpath", String.Join(";", p.Classpath.Distinct()),
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
        if (quickJoin != "") {
            var exclusive = new[] { "--quickPlaySingleplayer", "--quickPlayMultiplayer", "--quickPlayRealms" };
            for (var i = game.Count - 1; i >= 0; i--) {
                if (exclusive.Contains(game[i])) {
                    if (i + 1 < game.Count && !game[i + 1].StartsWith("--", StringComparison.Ordinal)) game.RemoveAt(i + 1);
                    game.RemoveAt(i);
                } else if (exclusive.Any(key => game[i].StartsWith(key + "=", StringComparison.Ordinal))) game.RemoveAt(i);
            }
            game.AddRange(new[] { "--quickPlayMultiplayer", quickJoin });
        }
        string argsFile = null;
        if (p.Major >= 9) {
            argsFile = Path.Combine(dir, ".launch-" + Guid.NewGuid().ToString("N") + ".args");
            J.NoLinks(argsFile);
            var encoding = Encoding.Default.CodePage == 65001 ? new UTF8Encoding(false) : Encoding.Default;
            File.WriteAllLines(argsFile, jvm.Select(ArgFile), encoding);
            jvm = new List<string> { "@" + Path.GetFileName(argsFile) };
        }
        var logs = effective.B("logs");
        var log = Path.Combine(dir,"logs","ebuild-launch.log");
        if (logs) { J.NoLinks(log); Directory.CreateDirectory(Path.GetDirectoryName(log)); File.WriteAllText(log,""); }
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
        try {
            ct.ThrowIfCancellationRequested();
            if (beforeStart != null) beforeStart();
            progress("starting", "正在启动 Minecraft", 0, 0);
            proc.Start();
        } catch { if (argsFile != null) File.Delete(argsFile); proc.Dispose(); throw; }
        if (logs) { proc.BeginOutputReadLine(); proc.BeginErrorReadLine(); }
        return proc;
    }
}
