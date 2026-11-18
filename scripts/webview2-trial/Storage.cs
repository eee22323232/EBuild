using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

static class Storage
{
    internal sealed class Entry { internal string Path, Category, Stamp; internal long Bytes; }
    internal sealed class Snapshot {
        internal string Root, Token = Guid.NewGuid().ToString("N");
        internal DateTime Created = DateTime.UtcNow;
        internal List<Entry> Cleanable = new List<Entry>();
        internal Dictionary<string,long> Sizes = new Dictionary<string,long>();
        internal int Skipped;
        internal object Report() {
            return J.Map("token", Token, "root", Root, "total", Sizes.Values.Sum(), "skipped", Skipped,
                "categories", Sizes.Select(pair => J.Map("id", pair.Key, "bytes", pair.Value,
                    "cleanable", new[] { "archives", "transfers", "backups" }.Contains(pair.Key))).ToArray());
        }
    }
    static string Category(string relative, HashSet<string> instances) {
        var p = relative.Replace('\\', '/').Split('/');
        if (p[0] == "cache") {
            if (p.Length > 2 && p[1] == "transfers") return "transfers";
            if (p.Length == 2 && new[] { ".zip", ".jar", ".mrpack" }.Contains(Path.GetExtension(p[1]).ToLowerInvariant())) return "archives";
            return "metadata";
        }
        if (p[0] == "instances") {
            if (p.Length > 3 && instances.Contains(p[1]) && p[2] == "mods-backup") return "backups";
            return "instances";
        }
        if (p[0] == "game") return "game";
        if (p[0] == "runtimes") return "java";
        if (p[0] == "downloads") return "installations";
        return "metadata";
    }
    static void Walk(string dir, Action<FileInfo> visit, Action skip, CancellationToken ct) {
        try {
            J.NoLinks(dir);
            if (!Directory.Exists(dir)) return;
            foreach (var name in Directory.EnumerateFileSystemEntries(dir)) {
                ct.ThrowIfCancellationRequested();
                try {
                    var attributes = File.GetAttributes(name);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { skip(); continue; }
                    if ((attributes & FileAttributes.Directory) != 0) Walk(name, visit, skip, ct);
                    else visit(new FileInfo(name));
                } catch (IOException) { skip(); } catch (UnauthorizedAccessException) { skip(); }
            }
        } catch (IOException) { skip(); } catch (UnauthorizedAccessException) { skip(); }
    }
    internal static Snapshot Scan(string root, IEnumerable<string> instanceIds, CancellationToken ct) {
        var snapshot = new Snapshot { Root = Path.GetFullPath(root).TrimEnd('\\', '/') };
        foreach (var key in new[] { "game", "java", "instances", "archives", "transfers", "backups", "installations", "metadata" }) snapshot.Sizes[key] = 0;
        var instances = new HashSet<string>(instanceIds, StringComparer.OrdinalIgnoreCase);
        Walk(snapshot.Root, file => {
            var category = Category(file.FullName.Substring(snapshot.Root.Length + 1), instances);
            snapshot.Sizes[category] += file.Length;
            if (new[] { "archives", "transfers", "backups" }.Contains(category)) snapshot.Cleanable.Add(new Entry {
                Path = file.FullName, Bytes = file.Length, Category = category, Stamp = LaunchChecks.Stamp(file)
            });
        }, () => snapshot.Skipped++, ct);
        return snapshot;
    }
    internal static long Size(string dir, CancellationToken ct) {
        long size = 0;
        Walk(dir, file => size += file.Length, () => { }, ct);
        return size;
    }
    internal static object Clean(Snapshot snapshot, string[] categories, CancellationToken ct) {
        if (categories.Length == 0 || categories.Any(x => !new[] { "archives", "transfers", "backups" }.Contains(x)))
            throw new IOException("请选择可清理的项目。");
        long freed = 0;
        int skipped = 0;
        foreach (var entry in snapshot.Cleanable.Where(x => categories.Contains(x.Category))) {
            ct.ThrowIfCancellationRequested();
            try {
                var path = J.Inside(snapshot.Root, entry.Path.Substring(snapshot.Root.Length + 1));
                J.NoLinks(path);
                // Only remove the exact files shown in the preview. Newly changed files are left alone.
                if (LaunchChecks.Stamp(path) != entry.Stamp) { skipped++; continue; }
                File.Delete(path);
                freed += entry.Bytes;
            } catch (IOException) { skipped++; } catch (UnauthorizedAccessException) { skipped++; }
        }
        return J.Map("freed", freed, "skipped", skipped);
    }
}

sealed partial class Backend
{
    Storage.Snapshot storageSnapshot;
    async Task<object> StorageReport() {
        var root = Root;
        var ids = StoredInstances().Where(x => x.S("externalPath") == "").Select(x => x.S("id")).ToArray();
        var scan = await Task.Run(() => Storage.Scan(root, ids, CancellationToken.None));
        if (root != Root) throw new IOException("数据目录已切换，请重新统计。");
        storageSnapshot = scan;
        return J.Merge(scan.Report(), J.Map("programBytes", await Task.Run(() => Storage.Size(AppDomain.CurrentDomain.BaseDirectory, CancellationToken.None)),
            "webBytes", await Task.Run(() => Storage.Size(Path.Combine(Path.GetDirectoryName(settingsFile), "WebView2"), CancellationToken.None))));
    }
    Task<object> CleanStorage(string token, string[] categories) {
        var scan = storageSnapshot;
        if (scan == null || scan.Token != token || scan.Root != Root.TrimEnd('\\', '/') || DateTime.UtcNow - scan.Created > TimeSpan.FromMinutes(15))
            throw new IOException("空间统计已过期，请刷新后再清理。");
        if (HasGameWork) throw new IOException("请先结束游戏和排队任务，再清理文件。");
        return Job((n,p,t) => Task.Run<object>(() => {
            J.NoLinks(scan.Root);
            if (File.ReadAllText(J.Inside(scan.Root, TrialRootMarker)) != "EBuild C#") throw new IOException("游戏数据目录标识无效。");
            storageSnapshot = null;
            p("cleaning", "正在清理所选文件", 0, 0);
            return Storage.Clean(scan, categories, t);
        }, t));
    }
}
