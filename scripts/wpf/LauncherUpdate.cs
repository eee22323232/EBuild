using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace EBuild.Native
{
    static class LauncherUpdate
    {
        internal const string Repository = "https://github.com/eee22323232/EBuild";
        const string TagPrefix = "dev.";
        const string ExecutableName = "EBuild-WPF.exe";
        const string ChecksumName = "WPF-SHA256SUMS.txt";
        const long ArchiveLimit = 32 * 1024 * 1024;
        static readonly string[] Payload = { ExecutableName + ".config", "LICENSE", ExecutableName };
        static string CurrentExecutable => Assembly.GetExecutingAssembly().Location;
        internal static string CurrentTag => TagPrefix + Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        internal static string BuildSource {
            get {
                var assembly = Assembly.GetExecutingAssembly();
                var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
                int separator = version.IndexOf('+');
                if (separator < 0 || separator == version.Length - 1) return "源码提交未知";
                string revision = version.Substring(separator + 1);
                var state = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(item => item.Key == "WorkingTreeState")?.Value;
                return "源码 " + revision.Substring(0, Math.Min(12, revision.Length)) +
                    (state == "modified" ? "（含未提交改动）" : state == "unknown" ? "（工作区状态未知）" : "");
            }
        }

        internal static Version ParseTag(string tag) {
            Version version;
            return Regex.IsMatch(tag, @"^dev\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\z") &&
                Version.TryParse(tag.Substring(TagPrefix.Length), out version) ? version : null;
        }

        static string Asset(string tag) => "EBuild-" + tag + "-WPF-Windows-x64.zip";
        static string AssetRoot(string tag) => Repository + "/releases/download/" + tag + "/";

        internal static async Task<object> Check(Network network, string suppressed, CancellationToken ct) {
            var feed = await network.Text(Repository + "/releases.atom", ct).ConfigureAwait(false);
            XNamespace atom = "http://www.w3.org/2005/Atom";
            XDocument document;
            using (var reader = XmlReader.Create(new StringReader(feed), new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024
            })) document = XDocument.Load(reader);
            if (document.Root == null || document.Root.Name != atom + "feed") throw new IOException("GitHub 返回的发布列表无效。");
            var releasePrefix = Repository + "/releases/tag/";
            var releases = document.Root.Elements(atom + "entry").Select(entry => {
                var link = entry.Elements(atom + "link").Select(item => (string)item.Attribute("href"))
                    .FirstOrDefault(href => href != null && href.StartsWith(releasePrefix, StringComparison.Ordinal)) ?? "";
                var tag = link == "" ? "" : link.Substring(releasePrefix.Length);
                return new { Tag = tag, Version = ParseTag(tag), Notes = (string)entry.Element(atom + "content") ?? "" };
            }).Where(item => item.Version != null && item.Version > ParseTag(CurrentTag)).OrderByDescending(item => item.Version);
            foreach (var release in releases) {
                if (release.Tag == suppressed) return null;
                string checksums;
                try { checksums = await network.Text(AssetRoot(release.Tag) + ChecksumName, ct).ConfigureAwait(false); }
                catch (IOException error) { if (error.Message.Contains("HTTP 404")) continue; throw; }
                var hash = ReadChecksum(checksums, Asset(release.Tag));
                var notes = Regex.Replace(release.Notes, @"</(?:p|li|h[1-6])>|<br\s*/?>", "\n", RegexOptions.IgnoreCase);
                notes = WebUtility.HtmlDecode(Regex.Replace(notes, "<[^>]+>", "")).Trim();
                return J.Map("tag", release.Tag, "sha256", hash, "notes", notes.Length > 2500 ? notes.Substring(0, 2500) + "…" : notes);
            }
            return null;
        }

        static string ReadChecksum(string text, string name) {
            var matches = Regex.Matches(text, @"^([A-Fa-f0-9]{64})[ \t]+\*?" + Regex.Escape(name) + @"[ \t]*\r?$", RegexOptions.Multiline);
            if (matches.Count != 1) throw new IOException("发布页缺少唯一的 WPF 更新包校验值。");
            return matches[0].Groups[1].Value.ToLowerInvariant();
        }

        static string Root(string profile) => J.Inside(profile, "launcher-updates");
        static string Transaction(string profile, string id) {
            if (!Regex.IsMatch(id, @"^[a-f0-9]{32}\z")) throw new IOException("更新任务标识无效。");
            var path = J.Inside(Root(profile), id);
            J.NoLinks(path);
            return path;
        }
        static string Receipt(string profile) => J.Inside(Root(profile), "result.json");
        static string Pending(string profile) => J.Inside(Root(profile), "pending.json");

        internal static string Staged(string profile) {
            var path = Pending(profile);
            J.NoLinks(path);
            var pending = J.Read(path);
            if (!Regex.IsMatch(pending.S("id"), @"^[a-f0-9]{32}\z")) return null;
            var directory = Transaction(profile, pending.S("id"));
            var plan = J.Read(J.Inside(directory, "plan.json"));
            return ParseTag(plan.S("tag")) > ParseTag(CurrentTag) && File.Exists(J.Inside(directory, "package.zip")) &&
                String.Equals(plan.S("target"), CurrentExecutable, StringComparison.OrdinalIgnoreCase) ? pending.S("id") : null;
        }

        internal static object LastResult(string profile) {
            var path = Receipt(profile);
            J.NoLinks(path);
            return J.Read(path);
        }

        internal static async Task<string> Stage(string profile, Network network, object release, CancellationToken ct) {
            var tag = release.S("tag");
            var version = ParseTag(tag);
            var hash = release.S("sha256");
            if (version == null || version <= ParseTag(CurrentTag) || !Regex.IsMatch(hash, @"^[a-f0-9]{64}\z"))
                throw new IOException("更新版本或校验值无效。");
            var id = Guid.NewGuid().ToString("N");
            var directory = Transaction(profile, id);
            var archive = J.Inside(directory, "package.zip");
            var spec = J.Map("url", AssetRoot(tag) + Asset(tag), "sha256", hash);
            await network.FileTo(spec, archive, ct, maxDownloadBytes: ArchiveLimit).ConfigureAwait(false);
            try {
                await Task.Run(() => Extract(archive, J.Inside(directory, "new"), version, ct), ct).ConfigureAwait(false);
                J.Save(J.Inside(directory, "plan.json"), J.Map("tag", tag, "hash", hash, "target", CurrentExecutable));
                J.NoLinks(Pending(profile));
                J.Save(Pending(profile), J.Map("id", id));
                return id;
            } catch {
                File.Delete(archive);
                foreach (var name in Payload) File.Delete(J.Inside(directory, "new/" + name));
                throw;
            }
        }

        static void Extract(string archive, string dest, Version version, CancellationToken ct) {
            using (var zip = ZipFile.OpenRead(archive)) {
                if (zip.Entries.Count != Payload.Length || Payload.Any(name => zip.Entries.Count(entry => entry.FullName == name) != 1))
                    throw new IOException("WPF 更新包的程序文件清单无效。");
                long total = 0;
                foreach (var entry in zip.Entries) {
                    ct.ThrowIfCancellationRequested();
                    total += entry.Length;
                    if (total > ArchiveLimit || entry.Length == 0) throw new IOException("更新包解压大小无效。");
                    var path = J.Inside(dest, entry.FullName);
                    J.NoLinks(path);
                    Directory.CreateDirectory(dest);
                    entry.ExtractToFile(path, true);
                }
            }
            var exe = J.Inside(dest, ExecutableName);
            var assembly = AssemblyName.GetAssemblyName(exe);
            if (assembly.Name != "EBuild-WPF" || assembly.Version != new Version(version.Major, version.Minor, version.Build, 0))
                throw new IOException("更新包中的 WPF 版本与发布版本不匹配。");
        }

        internal static void Start(string profile, string id) {
            var directory = Transaction(profile, id);
            var helper = J.Inside(directory, ExecutableName);
            J.NoLinks(helper);
            File.Copy(CurrentExecutable, helper, true);
            var config = CurrentExecutable + ".config";
            if (File.Exists(config)) File.Copy(config, helper + ".config", true);
            using (var owner = Process.GetCurrentProcess())
            using (var child = Run(helper, "--apply-launcher-update", id, owner.Id.ToString(), owner.StartTime.ToUniversalTime().Ticks.ToString(),
                CurrentExecutable)) { }
        }

        static Process Run(string exe, params string[] args) => Process.Start(new ProcessStartInfo(exe, String.Join(" ", args.Select(Minecraft.Quote))) {
            WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = false
        });

        internal static bool HandleWorker(string profile, string[] args) {
            if (args.Length == 0 || args[0] != "--apply-launcher-update") return false;
            int pid;
            long ticks;
            if (args.Length != 5 || !Int32.TryParse(args[2], out pid) || !Int64.TryParse(args[3], out ticks)) return true;
            var directory = Transaction(profile, args[1]);
            if (!String.Equals(CurrentExecutable, J.Inside(directory, ExecutableName), StringComparison.OrdinalIgnoreCase))
                return true;
            try { Apply(profile, directory, pid, ticks, args[1], args[4]); }
            catch (Exception error) { System.Windows.MessageBox.Show(error.GetBaseException().Message, "EBuild 更新"); }
            return true;
        }

        static void WaitForOwner(int pid, long ticks) {
            try {
                using (var owner = Process.GetProcessById(pid)) {
                    if (!owner.HasExited && owner.StartTime.ToUniversalTime().Ticks == ticks && !owner.WaitForExit(15000))
                        throw new IOException("EBuild 仍在运行，请关闭后重试更新。");
                }
            } catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }

        static void Apply(string profile, string directory, int pid, long ticks, string id, string ownerExecutable) {
            WaitForOwner(pid, ticks);
            using (var mutex = new Mutex(false, LauncherProfile.MutexName(profile))) {
                try { if (!mutex.WaitOne(0)) throw new IOException("另一个 EBuild 窗口正在运行。"); }
                catch (AbandonedMutexException) { }
                var locked = true;
                try { ApplyFiles(profile, directory, id, ownerExecutable, mutex, ref locked); }
                finally { if (locked) mutex.ReleaseMutex(); }
            }
        }

        static void ApplyFiles(string profile, string directory, string id, string ownerExecutable, Mutex mutex, ref bool locked) {
            var plan = J.Read(J.Inside(directory, "plan.json"));
            var tag = plan.S("tag");
            var version = ParseTag(tag);
            var exe = Path.GetFullPath(plan.S("target"));
            var target = Path.GetDirectoryName(exe);
            var payload = Payload.Select(name => name == ExecutableName ? Path.GetFileName(exe) :
                name == ExecutableName + ".config" ? Path.GetFileName(exe) + ".config" : name).ToArray();
            if (version == null || version <= ParseTag(CurrentTag) ||
                !String.Equals(exe, ownerExecutable, StringComparison.OrdinalIgnoreCase) || !File.Exists(exe) ||
                AssemblyName.GetAssemblyName(exe).FullName != Assembly.GetExecutingAssembly().GetName().FullName)
                throw new IOException("更新目标无效。");
            J.NoLinks(exe);
            var archive = J.Inside(directory, "package.zip");
            var previous = J.Inside(directory, "previous");
            var next = J.Inside(directory, "new");
            var ready = J.Inside(directory, "ready");
            var result = Receipt(profile);
            Process launched = null;
            var replaced = new System.Collections.Generic.List<string>();
            try {
                J.NoLinks(archive);
                var hash = plan.S("hash");
                if (!Regex.IsMatch(hash, @"^[a-f0-9]{64}\z") || !Network.Check(archive, J.Map("sha256", hash)))
                    throw new IOException("更新包校验失败。");
                Extract(archive, next, version, CancellationToken.None);
                J.NoLinks(previous);
                J.NoLinks(result);
                Directory.CreateDirectory(previous);
                foreach (var name in payload) {
                    var path = J.Inside(target, name);
                    J.NoLinks(path);
                    var backup = J.Inside(previous, name);
                    J.NoLinks(backup);
                    if (File.Exists(path)) File.Copy(path, backup, true); else File.Delete(backup);
                }
                J.NoLinks(ready);
                File.Delete(ready);
                for (int i = 0; i < payload.Length; i++) {
                    Replace(J.Inside(next, Payload[i]), J.Inside(target, payload[i]));
                    replaced.Add(payload[i]);
                }
                mutex.ReleaseMutex();
                locked = false;
                launched = Run(exe, "--launcher-update-probe", id);
                var wait = Stopwatch.StartNew();
                while (wait.Elapsed < TimeSpan.FromSeconds(40) && !launched.HasExited && !File.Exists(ready)) Thread.Sleep(100);
                if (!File.Exists(ready) || launched.HasExited) throw new IOException("新版未能打开主界面。");
                J.Save(result, J.Map("tag", tag, "success", true));
            } catch (Exception error) {
                if (launched != null && !launched.HasExited) { launched.Kill(); launched.WaitForExit(); }
                if (!locked) {
                    try { if (!mutex.WaitOne(5000)) throw new IOException("EBuild 正在运行，无法恢复，请关闭后重试。"); }
                    catch (AbandonedMutexException) { }
                    locked = true;
                }
                foreach (var name in replaced.AsEnumerable().Reverse()) {
                    var backup = J.Inside(previous, name);
                    var path = J.Inside(target, name);
                    if (File.Exists(backup)) Replace(backup, path); else File.Delete(path);
                }
                J.Save(result, J.Map("tag", tag, "failedTag", tag, "message", error.GetBaseException().Message));
                mutex.ReleaseMutex();
                locked = false;
                using (var old = Run(exe, "--launcher-update-restored")) { }
            } finally {
                if (launched != null) launched.Dispose();
                File.Delete(archive);
                foreach (var name in Payload) File.Delete(J.Inside(next, name));
            }
        }

        static void Replace(string source, string target) {
            J.NoLinks(source);
            J.NoLinks(target);
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                File.Copy(source, temporary);
                if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
            } finally { File.Delete(temporary); }
        }

        internal static void MarkReady(string profile, string id) {
            var directory = Transaction(profile, id);
            var plan = J.Read(J.Inside(directory, "plan.json"));
            if (plan.S("tag") != CurrentTag || !String.Equals(plan.S("target"), CurrentExecutable, StringComparison.OrdinalIgnoreCase))
                throw new IOException("新版与更新任务不匹配。");
            var ready = J.Inside(directory, "ready");
            J.NoLinks(ready);
            J.Save(ready, J.Map("tag", CurrentTag));
        }
    }
}
