using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;

static class ExternalInstances
{
    static readonly Dictionary<string,string> Components = new Dictionary<string,string> {
        { "net.fabricmc.fabric-loader", "fabric" }, { "org.quiltmc.quilt-loader", "quilt" },
        { "net.minecraftforge", "forge" }, { "net.neoforged", "neoforge" }
    };

    internal static bool SamePath(string left, string right) {
        return String.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ContainsPath(string parent, string path) {
        return SamePath(parent, path) || path.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    internal static string DirectoryPath(string value) {
        if (String.IsNullOrWhiteSpace(value)) throw new IOException("请选择游戏文件夹。");
        var path = Path.GetFullPath(value);
        if (SamePath(path, Path.GetPathRoot(path))) throw new IOException("请选择具体的游戏文件夹。");
        path = path.TrimEnd('\\');
        J.NoLinks(path);
        if (!Directory.Exists(path)) throw new IOException("找不到游戏文件夹，请重新定位。");
        return path;
    }

    static object Manifest(string path) {
        J.NoLinks(path);
        var info = new FileInfo(path);
        return info.Exists && info.Length <= 2 * 1024 * 1024 ? J.Read(path) : null;
    }

    static bool ValidId(string value) {
        try { J.Id(value); return true; }
        catch (InvalidOperationException) { return false; }
    }

    static object Candidate(string source, string game, string profile, string version, string loader, string loaderVersion, bool independent) {
        if (!ValidId(version) || !Minecraft.SupportsLoader(loader) ||
            (loader != "vanilla" && !ValidId(loaderVersion)) || (loader == "forge" && !Minecraft.SupportsForge(version))) return null;
        var name = Path.GetFileName(source);
        if (name.Equals(".minecraft", StringComparison.OrdinalIgnoreCase)) name = profile;
        return J.Map("name", name.Length > 80 ? name.Substring(0, 80) : name, "version", version, "loader", loader,
            "loaderVersion", loader == "vanilla" ? "" : loaderVersion, "externalPath", game, "externalSource", source,
            "externalProfile", profile, "independent", independent);
    }

    static bool IsIndependent(string directory) {
        if (File.Exists(J.Inside(directory, "launcher_profiles.json")) ||
            File.Exists(J.Inside(directory, "launcher_profiles_microsoft_store.json"))) return false;
        var versions = J.Inside(directory, "versions");
        J.NoLinks(versions);
        return !Directory.Exists(versions) || Directory.EnumerateDirectories(versions).Take(2).Count() < 2;
    }

    static object VersionCandidate(string source, string directory, bool independent) {
        var id = Path.GetFileName(directory);
        var manifest = Manifest(J.Inside(directory, id + ".json"));
        if (manifest.S("mainClass") == "") return null;
        var version = manifest.S("inheritsFrom");
        foreach (var library in manifest.A("libraries")) {
            var parts = library.S("name").Split(':');
            if (parts.Length < 3) continue;
            var key = parts[0] + "." + parts[1];
            if (key == "net.minecraftforge.forge") key = "net.minecraftforge";
            if (key == "net.neoforged.neoforge") key = "net.neoforged";
            string loader;
            if (!Components.TryGetValue(key, out loader)) continue;
            var loaderVersion = parts[2];
            if (version == "") version = manifest.S("jar");
            if (loader == "forge" && version != "" && loaderVersion.StartsWith(version + "-", StringComparison.Ordinal))
                loaderVersion = loaderVersion.Substring(version.Length + 1);
            return Candidate(source, source, id, version, loader, loaderVersion, independent);
        }
        if (version != "" || manifest.S("mainClass") != "net.minecraft.client.main.Main" || manifest.S("id") != id) return null;
        return Candidate(source, source, id, id, "vanilla", "", independent);
    }

    internal static object[] Scan(string path) {
        var source = DirectoryPath(path);
        var old = Manifest(J.Inside(source, ".ebuild.json"));
        var own = old.S("externalPath") == ""
            ? Candidate(source, source, old.S("version"), old.S("version"), old.S("loader"), old.S("loaderVersion"),
                old != null && IsIndependent(source)) : null;
        if (own != null) return new[] { own };

        var prism = Manifest(J.Inside(source, "mmc-pack.json"));
        if (prism != null) {
            var game = new[] { ".minecraft", "minecraft" }.Select(name => J.Inside(source, name)).FirstOrDefault(Directory.Exists);
            if (game == null) throw new IOException("这个实例还没有游戏文件夹。请先在原启动器里安装。");
            var parts = prism.A("components");
            var mc = parts.FirstOrDefault(part => part.S("uid") == "net.minecraft").S("version");
            var loaders = parts.Where(part => Components.ContainsKey(part.S("uid"))).ToArray();
            if (loaders.Length <= 1) {
                var spec = loaders.FirstOrDefault();
                var actual = DirectoryPath(game);
                var candidate = Candidate(source, actual, mc, mc, spec == null ? "vanilla" : Components[spec.S("uid")],
                    spec.S("version"), IsIndependent(actual));
                if (candidate != null) return new[] { candidate };
            }
            throw new IOException("此实例的版本或加载器暂不支持导入。");
        }

        var versions = J.Inside(source, "versions");
        J.NoLinks(versions);
        if (Directory.Exists(versions)) {
            var candidates = Directory.EnumerateDirectories(versions)
                .Select(dir => {
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return null;
                    try { return VersionCandidate(source, dir, false); }
                    catch (ArgumentException) { return null; }
                }).Where(spec => spec != null).OrderByDescending(spec => spec.S("externalProfile"), StringComparer.OrdinalIgnoreCase).ToArray();
            if (candidates.Length > 0) return candidates;
        }
        var standalone = VersionCandidate(source, source, IsIndependent(source));
        if (standalone != null) return new[] { standalone };
        throw new IOException("找不到可导入的 Minecraft 版本。请选择 .minecraft、版本实例或 Prism/MultiMC 实例文件夹。");
    }

    internal static bool SameVersion(object left, object right) {
        return left.S("version") == right.S("version") && left.S("loader") == right.S("loader") &&
            left.S("loaderVersion") == right.S("loaderVersion");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CommandLineToArgvW(string command, out int count);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr memory);

    static bool MatchesGameDirectory(string command, string directory) {
        if (String.IsNullOrEmpty(command)) return false;
        int count;
        var args = CommandLineToArgvW(command, out count);
        if (args == IntPtr.Zero) return false;
        try {
            for (var i = 1; i < count; i++) {
                var arg = Marshal.PtrToStringUni(Marshal.ReadIntPtr(args, i * IntPtr.Size));
                string target;
                if (arg == "--gameDir" && i + 1 < count)
                    target = Marshal.PtrToStringUni(Marshal.ReadIntPtr(args, ++i * IntPtr.Size));
                else if (arg.StartsWith("--gameDir=", StringComparison.Ordinal))
                    target = arg.Substring("--gameDir=".Length);
                else continue;
                try {
                    if (Path.IsPathRooted(target) && SamePath(Path.GetFullPath(target).TrimEnd('\\'), directory)) return true;
                } catch (ArgumentException) { }
                catch (NotSupportedException) { }
            }
            return false;
        } finally { LocalFree(args); }
    }

    internal static void EnsureNotRunning(string directory) {
        const string message = "检测到这个文件夹正在运行 Minecraft，请先关闭游戏。";
        try {
            using (var search = new ManagementObjectSearcher(
                new ManagementScope(), new ObjectQuery("SELECT CommandLine FROM Win32_Process WHERE Name='java.exe' OR Name='javaw.exe'"),
                new EnumerationOptions { Timeout = TimeSpan.FromSeconds(2), Rewindable = false }))
            using (var processes = search.Get()) {
                foreach (ManagementObject process in processes)
                    using (process) if (MatchesGameDirectory(process["CommandLine"] as string, directory)) throw new IOException(message);
            }
        } catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        catch (COMException) { }

        var saves = J.Inside(directory, "saves");
        J.NoLinks(saves);
        if (!Directory.Exists(saves)) return;
        foreach (var world in Directory.EnumerateDirectories(saves)) {
            if ((File.GetAttributes(world) & FileAttributes.ReparsePoint) != 0) continue;
            var marker = J.Inside(world, "session.lock");
            J.NoLinks(marker);
            if (!File.Exists(marker)) continue;
            try {
                using (var file = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)) {
                    file.Lock(0, 1);
                    file.Unlock(0, 1);
                }
            } catch (FileNotFoundException) { }
            catch (IOException) { throw new IOException(message); }
        }
    }

    internal static void EnsureDeleteTree(string root, System.Threading.CancellationToken ct) {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count != 0) {
            ct.ThrowIfCancellationRequested();
            foreach (var path in Directory.EnumerateFileSystemEntries(pending.Pop())) {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("原文件夹包含链接，不能从启动器永久删除。");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
            }
        }
    }
}
