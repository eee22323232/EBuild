using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

static class LanPackFiles
{
    static readonly string[] QuestRoots = { "config/ftbquests/quests", "config/ftbquests/normal" };
    static readonly string[] ScriptRoots = { "scripts", "kubejs/startup_scripts", "kubejs/server_scripts", "kubejs/client_scripts", "kubejs/data", "kubejs/assets" };
    static readonly string[] ConfigExtensions = { ".cfg", ".conf", ".ini", ".json", ".json5", ".jsonc", ".nbt", ".snbt", ".properties", ".toml", ".txt", ".xml", ".yaml", ".yml" };
    static readonly string[] ContentExtensions = { ".cjs", ".csv", ".fsh", ".glsl", ".jem", ".jpm", ".js", ".lang", ".mcfunction", ".mcmeta", ".mjs", ".ogg", ".png", ".ts", ".ttf", ".vsh", ".webp", ".zn", ".zs", ".zip" };
    const string Devices = @"^(con|prn|aux|nul|conin\$|conout\$|com[1-9¹²³]|lpt[1-9¹²³])( *\.|$)";

    static bool Under(string path, string folder) { return path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase); }

    internal static string Group(string path) {
        if (path.Length == 0 || path.Length > 240 || path.IndexOf('\\') >= 0 || path.Split('/').Any(part =>
                part.Length == 0 || part.StartsWith(".", StringComparison.Ordinal) || part.EndsWith(".", StringComparison.Ordinal) ||
                part.Trim() != part || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                Regex.IsMatch(part, Devices, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))) return "";
        var extension = Path.GetExtension(path);
        var config = ConfigExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        var content = ContentExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        if (Under(path, "mods") && path.Count(ch => ch == '/') == 1 && extension.Equals(".jar", StringComparison.OrdinalIgnoreCase)) return "模组";
        if (QuestRoots.Any(root => Under(path, root))) return config || content ? "任务书" : "";
        if (ScriptRoots.Any(root => Under(path, root))) return config || content ? "脚本和数据" : "";
        if (Under(path, "resourcepacks")) return config || content ? "资源包" : "";
        if (config && (Under(path, "config") || Under(path, "defaultconfigs"))) return "配置";
        return "";
    }

    internal static string ConfigPath(string instance, string file) {
        var prefix = Path.GetFullPath(instance).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(file);
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("请选择当前实例中的配置文件。");
        var path = full.Substring(prefix.Length).Replace('\\', '/');
        if (Group(path) != "配置") throw new IOException("请选择 config 或 defaultconfigs 下的配置文件。任务书可单独勾选。");
        J.NoLinks(full);
        return path;
    }

    internal static IEnumerable<string> List(string instance, object choice, CancellationToken ct) {
        foreach (var mod in Files(instance, "mods", false, "模组", ct)) yield return mod;
        if (choice.B("quests"))
            foreach (var root in QuestRoots)
                foreach (var file in Files(instance, root, true, "任务书", ct)) yield return file;
        if (choice.B("scripts"))
            foreach (var root in ScriptRoots)
                foreach (var file in Files(instance, root, true, "脚本和数据", ct)) yield return file;
        if (choice.B("resources"))
            foreach (var file in Files(instance, "resourcepacks", true, "资源包", ct)) yield return file;
        foreach (var file in choice.A("configs")) yield return ConfigPath(instance, file.S());
    }

    static IEnumerable<string> Files(string instance, string path, bool recurse, string group, CancellationToken ct) {
        var folder = J.Inside(instance, path);
        if (!Directory.Exists(folder)) yield break;
        J.NoLinks(folder);
        foreach (var entry in Directory.EnumerateFileSystemEntries(folder)) {
            ct.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
            var relative = path + "/" + Path.GetFileName(entry);
            if ((attributes & FileAttributes.Directory) != 0) {
                if (recurse && relative.Length < 240 && !Path.GetFileName(entry).StartsWith(".", StringComparison.Ordinal))
                    foreach (var child in Files(instance, relative, true, group, ct)) yield return child;
            } else if (Group(relative) == group) yield return relative;
        }
    }

    internal static object Original(object spec) {
        Uri url;
        long size;
        if (!Uri.TryCreate(spec.S("url"), UriKind.Absolute, out url) || url.Scheme != "https" ||
                url.IdnHost != "cdn.modrinth.com" || !url.IsDefaultPort || url.UserInfo != "" || url.Query != "" || url.Fragment != "" ||
                !Regex.IsMatch(url.AbsolutePath, @"^/data/[a-zA-Z0-9]+/versions/[a-zA-Z0-9]+/[^/]+\.mrpack$", RegexOptions.IgnoreCase) ||
                !Regex.IsMatch(spec.S("sha512"), "^[a-fA-F0-9]{128}$") ||
                !long.TryParse(spec.S("size"), out size) || size <= 0 || size > LanModShare.MaxBytes) return null;
        return J.Map("url", url.AbsoluteUri, "sha512", spec.S("sha512"), "size", size);
    }
}
