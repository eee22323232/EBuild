using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

sealed class GameProcesses
{
    internal sealed class Game {
        internal string Id, Directory, RecordDirectory, Identity, ArgumentFile;
        internal DateTime Started;
        internal Process Process;
        internal bool Stopping, ForceAvailable, Logs;
    }

    readonly Form window;
    readonly Action<string,int?> changed;
    readonly Dictionary<string,Game> games = new Dictionary<string,Game>(StringComparer.OrdinalIgnoreCase);

    internal GameProcesses(Form window, Action<string,int?> changed) {
        this.window = window;
        this.changed = changed;
    }

    internal int Count { get { return games.Count; } }
    internal bool Contains(string id) { return games.ContainsKey(id); }
    internal Game Find(string id) {
        Game game;
        return games.TryGetValue(id, out game) && !game.Stopping && !game.Process.HasExited ? game : null;
    }
    internal IEnumerable<object> Snapshot() {
        return games.Values.Select(game => (object)J.Map("id", game.Id, "state", game.Stopping ? "stopping" : "running",
            "startedAt", LaunchDiagnostics.Time(game.Started), "logs", game.Logs));
    }

    internal Task<object> Diagnose(string id) {
        Game game;
        if (!games.TryGetValue(id, out game) || !game.Logs) return Task.FromResult<object>(null);
        var pid = game.Process.Id;
        return Task.Run(() => LaunchDiagnostics.Read(game.Id, game.Directory, game.Started, pid));
    }

    static string Marker(string directory) { return J.Inside(directory, ".ebuild-running.json"); }
    static string Identity(Process process) { return process.Id + ":" + process.StartTime.ToUniversalTime().Ticks; }

    static void Persist(Game game) {
        var path = Marker(game.RecordDirectory);
        J.NoLinks(path);
        J.Save(path, J.Map("pid", game.Process.Id, "identity", game.Identity, "directory", game.Directory, "argumentFile", game.ArgumentFile,
            "logs", game.Logs));
    }

    internal void PersistAll() {
        foreach (var game in games.Values.ToArray()) {
            if (game.Process.HasExited) Finish(game);
            else Persist(game);
        }
    }

    static void DeleteArguments(string directory, string name) {
        if (!Regex.IsMatch(name, @"^\.launch-[a-f0-9]{32}\.args$")) return;
        var path = J.Inside(directory, name);
        J.NoLinks(path);
        File.Delete(path);
    }

    internal void Restore(string id, string directory, string record) {
        var marker = Marker(record);
        Process process = null;
        try {
            J.NoLinks(marker);
            var saved = J.Read(marker);
            if (saved == null) return;
            if (saved.I("pid") > 0) {
                try { process = Process.GetProcessById(saved.I("pid")); }
                catch (ArgumentException) { }
            }
            var sameDirectory = String.Equals(saved.S("directory"), directory, StringComparison.OrdinalIgnoreCase);
            if (process != null && !process.HasExited && Identity(process) == saved.S("identity") && sameDirectory &&
                new[] { "java", "javaw" }.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase)) {
                Add(id, directory, record, process, saved.S("argumentFile"), !Object.Equals(saved.At("logs"), false));
                process = null;
                return;
            }
            if (sameDirectory) DeleteArguments(directory, saved.S("argumentFile"));
            File.Delete(marker);
        } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (InvalidOperationException) { }
        catch (ArgumentException) { }
        finally { if (process != null) process.Dispose(); }
    }

    internal void Track(string id, string directory, Process process, string record) {
        if (process.HasExited) {
            var code = process.ExitCode;
            process.Dispose();
            changed(id, code);
            return;
        }
        var firstArgument = process.StartInfo.Arguments.Split(new[] { ' ' }, 2)[0].Trim('"');
        var argsFile = firstArgument.StartsWith("@", StringComparison.Ordinal) ? firstArgument.Substring(1) : "";
        var game = Add(id, directory, record, process, argsFile, process.StartInfo.RedirectStandardOutput);
        if (games.ContainsKey(id)) Persist(game);
    }

    Game Add(string id, string directory, string record, Process process, string argumentFile, bool logs) {
        var game = new Game { Id = id, Directory = directory, RecordDirectory = record, Process = process, Identity = Identity(process),
            ArgumentFile = argumentFile, Started = process.StartTime.ToUniversalTime(), Logs = logs };
        games.Add(id, game);
        process.Exited += (s,e) => {
            if (window.IsDisposed || !window.IsHandleCreated) return;
            try { window.BeginInvoke((MethodInvoker)(() => Finish(game))); }
            catch (InvalidOperationException) { }
        };
        process.EnableRaisingEvents = true;
        if (process.HasExited) Finish(game);
        else changed(id, null);
        return game;
    }

    bool IsCurrent(Game game) {
        Game current;
        return games.TryGetValue(game.Id, out current) && current == game;
    }

    void Finish(Game game) {
        if (!IsCurrent(game)) return;
        var code = game.Process.ExitCode;
        games.Remove(game.Id);
        game.Process.Dispose();
        try {
            var marker = Marker(game.RecordDirectory);
            J.NoLinks(marker);
            if (J.Read(marker).S("identity") == game.Identity) File.Delete(marker);
            DeleteArguments(game.Directory, game.ArgumentFile);
        } catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        changed(game.Id, code);
    }

    internal async Task<object> Stop(string id, string forceIdentity) {
        Game game;
        if (!games.TryGetValue(id, out game)) return null;
        if (game.Stopping) return null;
        var force = forceIdentity != "";
        if (force && (!game.ForceAvailable || forceIdentity != game.Identity)) throw new IOException("这条强制结束请求已失效。");
        if (game.Process.HasExited) { Finish(game); return true; }

        game.Stopping = true;
        changed(id, null);
        try {
            try {
                if (force) game.Process.Kill();
                else game.Process.CloseMainWindow();
            } catch (InvalidOperationException) {
                if (game.Process.HasExited) { Finish(game); return true; }
                throw;
            }
            var timer = Stopwatch.StartNew();
            while (IsCurrent(game) && timer.ElapsedMilliseconds < (force ? 5000 : 10000)) {
                if (game.Process.HasExited) { Finish(game); break; }
                await Task.Delay(200);
            }
            if (!IsCurrent(game)) return true;
            if (force) throw new IOException("游戏进程尚未退出，请稍后再试。");
            game.ForceAvailable = true;
            return J.Map("forceIdentity", game.Identity);
        } finally {
            if (IsCurrent(game)) {
                game.Stopping = false;
                changed(id, null);
            }
        }
    }
}
