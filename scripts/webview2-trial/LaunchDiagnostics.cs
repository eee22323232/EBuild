using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

static class LaunchDiagnostics
{
    const int LogTailBytes = 32768;
    internal static readonly string[] LogNames = { "ebuild-launch.log", "latest.log" };
    static readonly Regex Ready = new Regex(@"\bGame took \d+(?:\.\d+)? seconds to start(?:\]\]></log4j:Message>)?\s*$", RegexOptions.CultureInvariant);

    sealed class LogTail {
        internal DateTime Updated;
        internal string Signal;
        internal string Summary;
    }

    static void Recognize(LogTail tail, string line) {
        if (line.Contains("[CHAT]") || tail.Signal == "ready") return;
        if (Ready.IsMatch(line)) {
            tail.Signal = "ready";
            tail.Summary = "游戏日志记录启动完成";
        } else if (line.Contains("Downloading:") && line.Contains("Minecraft-Mod-Language-Modpack-")) {
            if (tail.Signal != "language-timeout") {
                tail.Signal = "language-download";
                tail.Summary = "自动汉化模组正在下载语言包";
            }
        } else if (line.Contains("Error while downloading:") && line.Contains("SocketTimeoutException") &&
            (tail.Signal == "language-download" || tail.Signal == "language-timeout")) {
            tail.Signal = "language-timeout";
            tail.Summary = "自动汉化模组下载超时，游戏可能仍在等待后续下载";
        } else if (line.Contains("Converting:") && line.Contains("Minecraft-Mod-Language-Modpack-")) {
            tail.Signal = "language-convert";
            tail.Summary = "自动汉化模组正在转换语言包";
        } else if (line.Contains("Created:") && line.Contains("minecraft:textures/atlas/") && line.Contains(".png-atlas")) {
            tail.Signal = "render-output";
            tail.Summary = "游戏正在加载资源";
        } else if (line.Contains("Reloading ResourceManager:") ||
                   (tail.Signal != "render-output" && line.Contains("Created:") && line.Contains(".png-atlas"))) {
            tail.Signal = "resources";
            tail.Summary = "游戏正在加载资源";
        } else if ((tail.Signal == null || tail.Signal == "language-convert") &&
                   (line.Contains("ModLauncher running:") || line.Contains("Launching target 'forgeclient'") || line.Contains("Launching target 'neoforgeclient'") ||
                    line.Contains("SpongePowered MIXIN Subsystem") || line.Contains("Fabric Loader") || line.Contains("Quilt Loader"))) {
            tail.Signal = "loader";
            tail.Summary = "游戏正在初始化加载器";
        }
    }

    internal static string LogPath(string directory, string name) {
        var path = J.Inside(directory, "logs/" + name);
        J.NoLinks(path);
        return path;
    }

    static LogTail ReadLog(string directory, string name, DateTime started) {
        try {
            var file = new FileInfo(LogPath(directory, name));
            if (!file.Exists || file.Length == 0 || file.LastWriteTimeUtc < started) return null;
            var tail = new LogTail { Updated = file.LastWriteTimeUtc };
            using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                var length = (int)Math.Min(stream.Length, LogTailBytes);
                stream.Seek(-length, SeekOrigin.End);
                var partial = stream.Position != 0;
                var bytes = new byte[length];
                int read = 0;
                while (read < length) {
                    var count = stream.Read(bytes, read, length - read);
                    if (count == 0) break;
                    read += count;
                }
                var lines = Encoding.UTF8.GetString(bytes, 0, read).Split('\n');
                for (var i = partial ? 1 : 0; i < lines.Length - 1; i++) Recognize(tail, lines[i]);
            }
            return tail;
        } catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    internal static long Time(DateTime utc) { return new DateTimeOffset(utc).ToUnixTimeMilliseconds(); }

    static bool ResponsiveWindow(int pid, DateTime started) {
        try {
            using (var process = Process.GetProcessById(pid)) {
                return !process.HasExited && process.StartTime.ToUniversalTime() == started &&
                    process.MainWindowHandle != IntPtr.Zero && process.Responding;
            }
        } catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    internal static object Read(string id, string directory, DateTime started, int pid) {
        var launcher = ReadLog(directory, LogNames[0], started);
        var game = ReadLog(directory, LogNames[1], started);
        var current = game != null && (launcher == null || game.Updated > launcher.Updated) ? game : launcher;
        var recognized = current != null && current.Signal != null ? current :
            (current == game ? launcher : game);
        if (launcher != null && launcher.Signal == "ready") recognized = launcher;
        if (game != null && game.Signal == "ready") recognized = game;
        var signal = recognized == null ? null : recognized.Signal;
        var summary = recognized == null ? null : recognized.Summary;
        // Forge and vanilla can finish loading without printing a startup time.
        if (signal == "render-output" && DateTime.UtcNow - current.Updated >= TimeSpan.FromSeconds(2) && ResponsiveWindow(pid, started)) {
            signal = "window-running";
            summary = "游戏已生成渲染资源，窗口可以响应";
        }
        return J.Map("id", id, "startedAt", Time(started), "lastLogAt", current == null ? 0L : Time(current.Updated),
            "signal", signal, "summary", summary);
    }
}
