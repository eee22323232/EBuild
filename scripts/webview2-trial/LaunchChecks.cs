using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

// Reuses successful checks for unchanged launcher-managed files. Full verification bypasses the stamps.
sealed class LaunchChecks
{
    readonly string root, cacheFile;
    readonly bool full;
    readonly ConcurrentDictionary<string,object> files;
    readonly ConcurrentDictionary<string,bool> checkedDirectories = new ConcurrentDictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
    int changed;

    internal LaunchChecks(string root, bool full) {
        this.root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        this.full = full;
        cacheFile = J.Inside(root, "cache/verified-game-files.json");
        files = new ConcurrentDictionary<string,object>(ReadCache(cacheFile).Obj(), StringComparer.OrdinalIgnoreCase);
    }

    internal static string Stamp(string path) { return Stamp(new FileInfo(path)); }

    internal static string Stamp(FileInfo file) {
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0) return null;
        return String.Format(CultureInfo.InvariantCulture, "{0}:{1}:{2}", file.Length, file.LastWriteTimeUtc.Ticks, file.CreationTimeUtc.Ticks);
    }

    static string Expected(object spec) {
        var hashes = new[] { spec.S("sha512"), spec.S("sha256"), spec.S("sha1") };
        return hashes.All(x => x == "") ? null : String.Join(":", hashes) + ":" + spec.S("size");
    }

    string Key(string path) { return path.Substring(root.Length + 1); }

    internal bool Matches(string path, object spec, string stamp) {
        if (full || stamp == null) return false;
        var expected = Expected(spec);
        object previous;
        if (expected == null || !files.TryGetValue(Key(path), out previous) || previous.S() != expected + ":" + stamp) return false;
        CheckDirectory(path);
        return true;
    }

    internal void CheckDirectory(string path) {
        checkedDirectories.GetOrAdd(Path.GetDirectoryName(path), dir => { J.NoLinks(dir); return true; });
    }

    internal void Remember(string path, object spec, string stamp) {
        var expected = Expected(spec);
        if (expected == null || stamp == null || Stamp(path) != stamp) return;
        var key = Key(path);
        var current = expected + ":" + stamp;
        object previous;
        if (files.TryGetValue(key, out previous) && previous.S() == current) return;
        files[key] = current;
        Interlocked.Exchange(ref changed, 1);
    }

    internal void Save() {
        if (changed == 0) return;
        SaveCache(cacheFile, files.ToDictionary(pair => pair.Key, pair => pair.Value));
    }

    static object ReadCache(string file) {
        try { J.NoLinks(file); return J.Read(file); }
        catch (Exception error) {
            if (!(error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is InvalidOperationException)) throw;
            return null;
        }
    }

    static void SaveCache(string file, object value) {
        try { J.NoLinks(file); J.Save(file, value); }
        catch (Exception error) {
            if (!(error is IOException || error is UnauthorizedAccessException || error is InvalidOperationException)) throw;
        }
    }

    static Dictionary<string,object> NativeFiles(string path) {
        var files = J.Map();
        foreach (var file in NativeEntries(path)) files[file.Substring(path.Length + 1)] = Stamp(file);
        return files;
    }

    static IEnumerable<string> NativeEntries(string path) {
        foreach (var file in Directory.EnumerateFileSystemEntries(path)) {
            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReparsePoint) != 0) throw new IOException("原生库目录包含链接。");
            if ((attrs & FileAttributes.Directory) == 0) yield return file;
            else foreach (var child in NativeEntries(file)) yield return child;
        }
    }

    internal void PrepareNatives(string path, IList<Tuple<string,string[]>> sources, CancellationToken ct) {
        J.NoLinks(path);
        if (sources.Count == 0) { Directory.CreateDirectory(path); return; }
        var manifest = path + ".json";
        var signatures = sources.Select(source => source.Item1 + "|" + Stamp(source.Item1) + "|" + String.Join("|", source.Item2)).ToArray();
        var existing = ReadCache(manifest);
        if (!full && Directory.Exists(path) && existing.A("sources").Select(value => value.S()).SequenceEqual(signatures)) {
            var previous = existing.At("files").Obj();
            var current = NativeFiles(path);
            if (previous.Count == current.Count && current.All(pair => previous.S(pair.Key) == pair.Value.S())) return;
        }
        var temp = path + "-" + Guid.NewGuid().ToString("N");
        var backup = temp + "-old";
        Directory.CreateDirectory(temp);
        try {
            foreach (var source in sources) Network.Unzip(source.Item1, temp, ct, name => source.Item2.Any(exclude =>
                name.StartsWith(exclude, StringComparison.Ordinal)) ? null : name);
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(path)) Directory.Move(path, backup);
            try { Directory.Move(temp, path); }
            catch {
                if (Directory.Exists(backup)) Directory.Move(backup, path);
                throw;
            }
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            SaveCache(manifest, J.Map("sources", signatures, "files", NativeFiles(path)));
        } finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
    }
}
