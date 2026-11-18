using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

sealed partial class Backend
{
    readonly Form window;
    readonly Action<object> notify;
    readonly string settingsFile, defaultRoot;
    readonly Accounts accounts;
    readonly LocalAppearance appearance;
    readonly Minecraft minecraft;
    readonly Resources resources;
    const string TrialRootMarker = ".ebuild-csharp-data";
    Dictionary<string,object> settings;
    CancellationTokenSource task;
    readonly GameProcesses games;
    readonly LanRooms multiplayer;
    readonly List<LaunchRequest> launchQueue = new List<LaunchRequest>();
    LaunchRequest preparing;
    string deleting;
    internal bool Leaving { get; private set; }

    sealed class LaunchRequest { internal string Id, Room; internal int ApprovedJava; internal DateTime Started; }
    internal Backend(Form owner, string profile, Action<object> status) {
        window = owner; notify = status;
        Directory.CreateDirectory(profile);
        defaultRoot = Path.Combine(profile, "游戏数据");
        settingsFile = Path.Combine(profile, "settings.json");
        settings = J.Merge(J.Map("username", "Player", "accountMode", "offline", "offlineSkinModel", "classic", "selected", "", "dataRoot", "",
            "memory", 4096, "java", "", "width", 0, "height", 0, "afterLaunch", "keep",
            "theme", "system", "motion", "on", "concurrency", 12, "downloadSource", "bmclapi", "proxyMode", "system", "proxy", "", "lanNode", "",
            "gc", "", "jvmArgs", new object[0], "gameArgs", new object[0], "logs", true), J.Read(settingsFile));
        accounts = new Accounts(profile);
        appearance = new LocalAppearance(profile);
        minecraft = new Minecraft(settings.S("dataRoot") == "" ? defaultRoot : settings.S("dataRoot"), settings);
        InitRoot(minecraft.Root);
        resources = new Resources(this);
        games = new GameProcesses(window, GameChanged);
        multiplayer = new LanRooms(profile, () => settings, notify);
        RestoreGames();
    }
    static void InitRoot(string root) {
        J.NoLinks(root);
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root,TrialRootMarker);
        if (File.Exists(marker) && File.ReadAllText(marker) == "EBuild C#") return;
        if (Directory.EnumerateFileSystemEntries(root).Any()) throw new IOException("请选空目录，或用「导入旧实例副本」复制已有实例。");
        File.WriteAllText(marker,"EBuild C#");
    }
    internal string Root { get { return minecraft.Root; } }
    string InstanceRecord(string id) { return J.Inside(Root, "instances/" + J.Id(id)); }
    internal string InstancePath(string id) {
        var external = Instance(id).S("externalPath");
        return external == "" ? InstanceRecord(id) : ExternalInstances.DirectoryPath(external);
    }
    IEnumerable<object> StoredInstances() {
        var root = Path.Combine(Root, "instances");
        if (!Directory.Exists(root)) return Enumerable.Empty<object>();
        return Directory.GetDirectories(root).Where(x => !Path.GetFileName(x).StartsWith(".") && (File.GetAttributes(x) & FileAttributes.ReparsePoint) == 0)
            .Select(x => new { Path = x, Spec = J.Read(Path.Combine(x, ".ebuild.json")) })
            .Where(x => x.Spec != null).Select(x => (object)J.Merge(x.Spec, J.Map("id", Path.GetFileName(x.Path)))).OrderBy(x => x.S("name"));
    }
    internal IEnumerable<object> Instances() {
        var stored = StoredInstances().ToArray();
        return stored.Select(instance => {
            var path = instance.S("externalPath");
            return path == "" ? instance : J.Merge(instance, J.Map("externalMissing", !Directory.Exists(path),
                "canDeleteOriginal", Directory.Exists(path) && instance.B("independent") && !SharesDirectory(instance, stored)));
        });
    }
    static bool SharesDirectory(object instance, IEnumerable<object> stored) {
        var path = instance.S("externalPath");
        return stored.Any(other => !SameInstance(other.S("id"), instance.S("id")) && other.S("externalPath") != "" &&
            (ExternalInstances.ContainsPath(path, other.S("externalPath")) || ExternalInstances.ContainsPath(other.S("externalPath"), path)));
    }
    internal object Instance(string id) {
        var file = Path.Combine(InstanceRecord(id), ".ebuild.json");
        J.NoLinks(file);
        var value = J.Read(file);
        if (value == null) throw new IOException("实例不存在。");
        return J.Merge(value, J.Map("id", id));
    }
    object Save(object delta) {
        var next = J.Merge(settings, delta);
        var node = next.S("lanNode").Trim();
        next["lanNode"] = node == "" ? "" : LanTunnel.NormalizeNode(node);
        if (!Regex.IsMatch(next.S("username"), "^[a-zA-Z0-9_]{3,16}$")) throw new IOException("离线档案名须为 3–16 位字母、数字或下划线。");
        if (!new[] { "classic", "slim" }.Contains(next.S("offlineSkinModel"))) throw new IOException("请选择经典或纤细模型。");
        if (!new[] {"system","manual","direct"}.Contains(next.S("proxyMode"))) throw new IOException("代理设置无效。");
        if (next.S("proxyMode") == "manual") {
            Uri proxy;
            if (!Uri.TryCreate(next.S("proxy"), UriKind.Absolute, out proxy) || !new[] {"http","https"}.Contains(proxy.Scheme))
                throw new IOException("请输入有效的 HTTP 或 HTTPS 代理地址。");
        }
        if (!new[] {"offline","microsoft"}.Contains(next.S("accountMode")) || !new[] {"system","light","dark"}.Contains(next.S("theme")) ||
            !new[] {"system","on","off"}.Contains(next.S("motion")) ||
            !new[] {"bmclapi","mojang"}.Contains(next.S("downloadSource")) || next.I("concurrency") < 1 || next.I("concurrency") > 32) throw new IOException("全局设置无效。");
        CheckOverrides(next);
        J.Save(settingsFile,next);
        settings = next; minecraft.Settings = next;
        return settings;
    }
    static readonly string[] GameKeys = {"java","memory","width","height","afterLaunch","gc","jvmArgs","gameArgs","logs"};
    static Dictionary<string,object> CheckOverrides(object values) {
        var x = values.Obj().Where(p => GameKeys.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        if (x.ContainsKey("memory") && (x.I("memory") < 1024 || x.I("memory") > 65536)) throw new IOException("内存必须为 1024–65536 MB。");
        foreach (var n in new[] {"width","height"}) if (x.ContainsKey(n) && (x.I(n) < 0 || x.I(n) > 16384)) throw new IOException("分辨率无效。");
        foreach (var n in new[] {"jvmArgs","gameArgs"}) if (x.A(n).Length > 80 || x.A(n).Any(a => a.S().Length > 1024 || Regex.IsMatch(a.S(), @"[\r\n\x00]")))
            throw new IOException("启动参数无效。");
        if (x.S("java") != "" && (!Path.IsPathRooted(x.S("java")) || !Regex.IsMatch(Path.GetFileName(x.S("java")), @"^javaw?\.exe$",RegexOptions.IgnoreCase)))
            throw new IOException("请选择 java.exe 或 javaw.exe。");
        if (!new[] {"","G1","Z","Parallel","Serial"}.Contains(x.S("gc"))) throw new IOException("垃圾回收器设置无效。");
        return x;
    }
    static Dictionary<string,object> Spec(object input) {
        var name = input.S("name").Trim();
        if (name.Length == 0 || name.Length > 80) throw new IOException("实例名称需为 1–80 字符。");
        var loader = input.S("loader");
        if (!Minecraft.SupportsLoader(loader)) throw new IOException("不支持该加载器。");
        return J.Map("name",name,"version",J.Id(input.S("version")),"loader",loader,
            "loaderVersion",loader == "vanilla" ? "" : J.Id(input.S("loaderVersion")),
            "overrides", CheckOverrides(input.At("overrides")));
    }
    internal async Task<object> Add(object input, Network net, Action<string,string,int,int> progress, CancellationToken ct, Func<string,Task> populate = null, bool install = true, string downloadId = "") {
        return await AddRecord(Spec(input), net, progress, ct, populate, install, downloadId);
    }
    async Task<object> AddRecord(object spec, Network net, Action<string,string,int,int> progress, CancellationToken ct, Func<string,Task> populate, bool install, string downloadId = "") {
        var id = downloadId == "" ? Guid.NewGuid().ToString() : DownloadId(downloadId);
        var final = InstanceRecord(id);
        var stage = downloadId == "" ? Path.Combine(Path.GetDirectoryName(final), "." + id) : J.Inside(DownloadDirectory(id), "stage");
        J.NoLinks(stage); J.NoLinks(final);
        if (downloadId != "") spec = J.Merge(spec, J.Map("installationId", id));
        Directory.CreateDirectory(stage);
        try {
            if (populate != null) await populate(stage);
            if (install) {
                var effective = J.Merge(settings,spec.At("overrides"));
                await Task.Run(() => minecraft.Install(net,spec,stage,effective,progress,ct),ct);
            }
            ct.ThrowIfCancellationRequested();
            J.Save(Path.Combine(stage,".ebuild.json"),spec);
            Directory.CreateDirectory(Path.GetDirectoryName(final));
            Directory.Move(stage,final);
            Save(J.Map("selected",id));
            return J.Merge(spec,J.Map("id",id));
        } finally { if (downloadId == "" && Directory.Exists(stage)) Directory.Delete(stage,true); }
    }
    void Status(string phase, string message, string kind = null, int done = 0, int total = 0) {
        notify(J.Map("phase",phase,"message",message,"kind",kind,"done",done,"total",total));
    }
    internal async Task<object> Job(Func<Network,Action<string,string,int,int>,CancellationToken,Task<object>> work, string kind = null) {
        if (task != null || Leaving) throw new IOException("请等待当前任务结束。");
        using (var source = new CancellationTokenSource())
        using (var net = new Network(settings, J.Inside(Root, "cache/transfers"))) {
            task = source;
            try {
                var result = await work(net,(phase,message,done,total) => Status(phase,message,kind,done,total),source.Token);
                Status("idle","就绪",kind);
                return result;
            } catch (OperationCanceledException) { if (!source.IsCancellationRequested) throw; Status("idle",kind == "game-install" ? "安装已暂停，进度已保留" : "任务已取消",kind); return null; }
            catch (Exception e) { Status("error",e.Message,kind); throw; }
            finally { task = null; ContinueLaunches(); }
        }
    }
    internal string Folder(string title, string initial = "") {
        using (var dialog = new FolderBrowserDialog { Description = title, SelectedPath = initial, ShowNewFolderButton = true })
            return dialog.ShowDialog(window) == DialogResult.OK ? dialog.SelectedPath : "";
    }
    string ChooseFile(string title, string filter) {
        using (var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true, Multiselect = false })
            return dialog.ShowDialog(window) == DialogResult.OK ? dialog.FileName : "";
    }
    static void CopyChecked(string source, string target, CancellationToken ct) {
        J.NoLinks(source); ct.ThrowIfCancellationRequested();
        if (Directory.Exists(source)) {
            Directory.CreateDirectory(target);
            foreach (var entry in Directory.EnumerateFileSystemEntries(source)) CopyChecked(entry, J.Inside(target,Path.GetFileName(entry)),ct);
        } else if (File.Exists(source)) {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            using (var input = File.OpenRead(source))
            using (var copy = new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                var buffer = new byte[128 * 1024];
                int length;
                while ((length = input.Read(buffer,0,buffer.Length)) != 0) { ct.ThrowIfCancellationRequested(); copy.Write(buffer,0,length); }
            }
        }
    }
    internal static void ImportPlayerData(string source, string stage, CancellationToken ct) {
        foreach (var name in new[] {"saves","mods","shaderpacks","resourcepacks","config","screenshots","options.txt","servers.dat","servers.dat_old","optionsof.txt","optionsshaders.txt"})
            CopyChecked(J.Inside(source,name),J.Inside(stage,name),ct);
    }
    static object Open(string path) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); return true; }
    static object Web(string url) {
        Uri uri;
        if (!Uri.TryCreate(url,UriKind.Absolute,out uri) || !new[] {"https","http"}.Contains(uri.Scheme) || uri.UserInfo.Length != 0) throw new IOException("链接无效。");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); return true;
    }
    async Task<object> Start(LaunchRequest request, Network net, Action<string,string,int,int> progress, CancellationToken ct) {
        var id = request.Id;
        var instance = Instance(id);
        var dir = InstancePath(id);
        var room = request.Room == "" ? null : multiplayer.Guest(request.Room);
        if (room != null && (!room.Invite.Matches(instance) || !Minecraft.CanQuickJoin(instance.S("version"))))
            throw new IOException("这个实例不能直接加入房间，请选择匹配实例或复制游戏地址。");
        Action verifyExternal = null;
        if (instance.S("externalPath") != "") {
            if (StoredInstances().Any(other => games.Contains(other.S("id")) &&
                ExternalInstances.SamePath(other.S("externalPath"), dir)))
                throw new IOException("检测到这个文件夹正在运行 Minecraft，请先关闭游戏。");
            verifyExternal = () => ExternalInstances.EnsureNotRunning(dir);
            await Task.Run(verifyExternal, ct);
        }
        var effective = J.Merge(settings, instance.At("overrides"));
        progress("java", "正在检查游戏所需的 Java", 0, 0);
        var baseSpec = await minecraft.ReadBaseVersion(net, instance, ct);
        var major = Minecraft.RequiredJavaMajor(baseSpec);
        var java = await minecraft.FindJava(major, effective.S("java"), ct);
        if (java == null) {
            if (request.ApprovedJava != major) return J.Map("requiredJava", major);
            java = await minecraft.DownloadJava(net, major, progress, ct);
        }
        object account = null;
        if (effective.S("accountMode") == "microsoft") {
            progress("account", "正在验证微软账号", 0, 0);
            account = await accounts.ForLaunch(net,ct);
        }
        Action beforeStart = () => {
            if (verifyExternal != null) verifyExternal();
            if (room != null) multiplayer.Guest(room.Id);
        };
        var process = await Task.Run(() => minecraft.Launch(net, instance, baseSpec, dir, effective, account, java, progress, ct,
            beforeStart, room == null ? "" : room.Address), ct);
        games.Track(id, dir, process, InstanceRecord(id));
        if (!Leaving && effective.S("afterLaunch") == "minimize") window.WindowState = FormWindowState.Minimized;
        return true;
    }

    internal bool HasGameWork { get { return games.Count > 0 || preparing != null || launchQueue.Count > 0; } }
    internal bool HasLanWork { get { return multiplayer.HasWork; } }

    object GameSnapshot() {
        var active = games.Snapshot().ToList();
        if (preparing != null && !games.Contains(preparing.Id))
            active.Add(J.Map("id", preparing.Id, "state", "preparing", "startedAt", LaunchDiagnostics.Time(preparing.Started)));
        active.AddRange(launchQueue.Select(request => (object)J.Map("id", request.Id, "state", "queued")));
        return active.ToArray();
    }

    void SendGames() { notify(J.Map("games", GameSnapshot())); }

    void GameChanged(string id, int? exitCode) {
        if (exitCode.HasValue) multiplayer.GameEnded(id);
        SendGames();
        if (exitCode.HasValue) notify(J.Map("gameExit", id, "code", exitCode.Value));
    }

    object OpenGameLog(string id) {
        var directory = InstancePath(id);
        var file = LaunchDiagnostics.LogNames.Select(name => new FileInfo(LaunchDiagnostics.LogPath(directory, name)))
            .Where(info => info.Exists && info.Length > 0).OrderByDescending(info => info.LastWriteTimeUtc).FirstOrDefault();
        if (file == null) throw new IOException("这个实例还没有启动日志。");
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe"), "\"" + file.FullName + "\"") {
            UseShellExecute = false
        });
        return true;
    }

    void RestoreGames() {
        foreach (var instance in StoredInstances()) {
            var id = instance.S("id");
            var record = InstanceRecord(id);
            var dir = instance.S("externalPath");
            games.Restore(id, dir == "" ? record : dir, record);
        }
    }

    static bool SameInstance(string left, string right) { return String.Equals(left, right, StringComparison.OrdinalIgnoreCase); }

    internal void RequireIdleInstance(string id) {
        if (games.Contains(id) || SameInstance(deleting, id) || (preparing != null && SameInstance(preparing.Id, id)) || launchQueue.Any(request => SameInstance(request.Id, id)))
            throw new IOException("请先停止该实例或取消它的启动任务。");
        var marker = J.Inside(InstanceRecord(id), ".ebuild-running.json");
        J.NoLinks(marker);
        if (File.Exists(marker)) throw new IOException("无法确认该实例已经退出。请重新打开启动器后再试。");
    }

    void ContinueLaunches() {
        if (task != null || preparing != null || window == null || window.IsDisposed || !window.IsHandleCreated) return;
        if (Leaving) window.BeginInvoke((MethodInvoker)(() => window.Close()));
        else if (launchQueue.Count != 0) window.BeginInvoke((MethodInvoker)PrepareNext);
    }

    async void PrepareNext() {
        if (Leaving || task != null || preparing != null || launchQueue.Count == 0) return;
        var request = launchQueue[0];
        launchQueue.RemoveAt(0);
        request.Started = DateTime.UtcNow;
        preparing = request;
        SendGames();
        try {
            var result = await Job((n,p,t) => Start(request, n, p, t), "game-launch");
            if (!Leaving && result.I("requiredJava") > 0) notify(J.Map("launchJava", request.Id, "major", result.I("requiredJava"), "room", request.Room));
        } catch (Exception error) {
            if (!Leaving) notify(J.Map("launchError", request.Id, "message", error.Message));
        } finally {
            preparing = null;
            SendGames();
            ContinueLaunches();
        }
    }

    void CheckExternalLocation(string directory) {
        var paths = new[] { Root, Path.GetDirectoryName(settingsFile), AppDomain.CurrentDomain.BaseDirectory };
        if (paths.Any(path => ExternalInstances.ContainsPath(Path.GetFullPath(path).TrimEnd('\\'), directory) ||
            ExternalInstances.ContainsPath(directory, Path.GetFullPath(path).TrimEnd('\\'))))
            throw new IOException("请选择独立于当前 EBuild 数据和程序的原游戏文件夹。");
    }

    object[] InspectExternal(string path) {
        var source = ExternalInstances.DirectoryPath(path);
        CheckExternalLocation(source);
        return ExternalInstances.Scan(source);
    }

    internal Task<object> PreviewDroppedImport(string path) {
        return Task.Run<object>(() => {
            if (Directory.Exists(path)) {
                var candidates = InspectExternal(path);
                return J.Map("kind", "folder", "source", candidates[0].S("externalSource"), "choices", candidates);
            }
            if (File.Exists(path) && String.Equals(Path.GetExtension(path), ".mrpack", StringComparison.OrdinalIgnoreCase)) {
                var pack = PackFile(path);
                return J.Map("kind", "pack", "source", pack, "instance", Spec(Resources.PackSpec(Resources.ReadPackIndex(pack))));
            }
            throw new IOException("请拖入 Minecraft 游戏文件夹或 .mrpack 整合包。");
        });
    }

    static string PackFile(string path) {
        var pack = Path.GetFullPath(path);
        J.NoLinks(pack);
        if (!File.Exists(pack) || !String.Equals(Path.GetExtension(pack), ".mrpack", StringComparison.OrdinalIgnoreCase))
            throw new IOException("请选择有效的 Modrinth .mrpack 整合包。");
        return pack;
    }

    internal async Task<object> ImportExternal(string path, string profileId, string relocateId = "") {
        return await Job(async (n,p,t) => {
            var candidates = await Task.Run(() => InspectExternal(path), t);
            if (relocateId != "") {
                RequireIdleInstance(relocateId);
                var old = Instance(relocateId);
                if (old.S("externalPath") == "") throw new IOException("只有外部实例支持重新定位。");
                var matches = candidates.Where(candidate => ExternalInstances.SameVersion(old, candidate)).ToArray();
                var replacement = matches.FirstOrDefault(c => c.S("externalProfile") == old.S("externalProfile"));
                if (replacement == null && matches.Length == 1) replacement = matches[0];
                if (replacement == null) throw new IOException("所选文件夹中没有唯一匹配当前实例的版本和加载器。");
                EnsureUniqueExternal(replacement, relocateId);
                var updated = J.Merge(old, ExternalFields(replacement));
                updated.Remove("id");
                J.Save(J.Inside(InstanceRecord(relocateId), ".ebuild.json"), updated);
                resources.Reset();
                return J.Merge(updated, J.Map("id", relocateId));
            }
            if (profileId == "" && candidates.Length > 1)
                return J.Map("choices", candidates, "source", candidates[0].S("externalSource"));
            var selected = profileId == "" ? candidates[0] : candidates.FirstOrDefault(c => c.S("externalProfile") == profileId);
            if (selected == null) throw new IOException("该版本已无法识别，请重新选择文件夹。");
            EnsureUniqueExternal(selected, "");
            return await AddRecord(J.Merge(Spec(selected), ExternalFields(selected)), n, p, t, null, false);
        });
    }

    static object ExternalFields(object instance) {
        return J.Map("externalPath", instance.S("externalPath"), "externalSource", instance.S("externalSource"),
            "externalProfile", instance.S("externalProfile"), "independent", instance.B("independent"));
    }

    void EnsureUniqueExternal(object candidate, string except) {
        if (StoredInstances().Any(other => !SameInstance(other.S("id"), except) &&
            ExternalInstances.SamePath(other.S("externalPath"), candidate.S("externalPath")) &&
            other.S("externalProfile") == candidate.S("externalProfile")))
            throw new IOException("这个文件夹里的该版本已经导入。");
    }

    async Task<object> DeleteInstance(string id, string confirmedName, string mode) {
        RequireIdleInstance(id);
        var instance = Instance(id);
        var external = instance.S("externalPath") != "";
        var removeOnly = external && mode == "remove";
        if ((external && !removeOnly && mode != "original") || (!external && mode != ""))
            throw new IOException("删除方式无效。");
        if (confirmedName != instance.S("name")) throw new IOException("请输入与实例名称完全一致的内容。");
        var path = InstanceRecord(id);
        return await Job(async (n,p,t) => {
            deleting = id;
            try {
                p("deleting", (removeOnly ? "正在移除 " : "正在删除 ") + instance.S("name"), 0, 0);
                var shared = external && !removeOnly && SharesDirectory(instance, StoredInstances());
                await Task.Run(() => {
                    if (external && !removeOnly) {
                        var original = ExternalInstances.DirectoryPath(instance.S("externalPath"));
                        var states = InspectExternal(instance.S("externalSource"));
                        if (!instance.B("independent") || shared || states.Length != 1 || !states[0].B("independent") ||
                            !ExternalInstances.SamePath(states[0].S("externalPath"), original) ||
                            !ExternalInstances.SameVersion(states[0], instance))
                            throw new IOException("这是共享目录或原游戏结构已变更。请使用「移除实例」。");
                        CheckExternalLocation(original);
                        ExternalInstances.EnsureNotRunning(original);
                        ExternalInstances.EnsureDeleteTree(original, t);
                        t.ThrowIfCancellationRequested();
                        J.NoLinks(original);
                        DeleteDirectory(original);
                    }
                    J.NoLinks(path);
                    if (!external || removeOnly) t.ThrowIfCancellationRequested();
                    try { DeleteDirectory(path); }
                    catch {
                        var manifest = J.Inside(path, ".ebuild.json");
                        if (Directory.Exists(path) && !File.Exists(manifest))
                            J.Save(manifest, external ? J.Merge(Spec(instance), ExternalFields(instance)) : Spec(instance));
                        throw;
                    }
                }, t);
                if (SameInstance(settings.S("selected"), id)) Save(J.Map("selected", Instances().FirstOrDefault().S("id")));
                resources.Reset();
                return true;
            } finally { deleting = null; }
        });
    }

    static void DeleteDirectory(string path) {
        var stage = J.Inside(Path.GetDirectoryName(path), ".delete-" + Guid.NewGuid().ToString("N"));
        Directory.Move(path, stage);
        try { Directory.Delete(stage, true); }
        catch { Directory.Move(stage, path); throw; }
    }
    internal async Task<object> Call(string channel, object[] a) {
        string id = a.Arg(0).S();
        switch (channel) {
            case "settings:get": return J.Merge(settings,J.Map("currentRoot",Root,"defaultRoot",defaultRoot,"version","dev.0.1.2 · WebView2 + C#"));
            case "settings:save": return Save(a.Arg(0).Obj().Where(x => settings.ContainsKey(x.Key) && x.Key != "selected" && x.Key != "dataRoot").ToDictionary(x=>x.Key,x=>x.Value));
            case "instances:list": return Instances().ToArray();
            case "instances:select": Instance(id); return Save(J.Map("selected",id));
            case "instances:folder": Instance(id); return Open(InstancePath(id));
            case "instances:mods-folder": Instance(id); return Open(Path.Combine(InstancePath(id),"mods"));
            case "instances:resources":
                Instance(id);
                var requestedFolder = a.Arg(1).S();
                var instancePath = InstancePath(id);
                return await Task.Run(() => Resources.Folders.Values.Where(n => requestedFolder == "" || requestedFolder == n).Select(n => {
                    var dir = J.Inside(instancePath, n);
                    J.NoLinks(dir);
                    var files = Directory.Exists(dir) ? Directory.GetFiles(dir) : new string[0];
                    return J.Map("folder", n, "directory", dir, "files", files.Select(Path.GetFileName).ToArray(),
                        "icons", n == Resources.Folders["mod"] ? ModIcons.Read(files) : J.Map());
                }).ToArray());
            case "instances:edit":
                var existing = Instance(id);
                var edited = Spec(J.Merge(existing,J.Map("name",a.Arg(1).S("name"),"overrides",a.Arg(1).At("overrides"))));
                if (existing.S("externalPath") != "") edited = J.Merge(edited, ExternalFields(existing));
                J.Save(Path.Combine(InstanceRecord(id),".ebuild.json"),edited);
                return J.Merge(edited,J.Map("id",id));
            case "instances:create": return await CreateDownload(a.Arg(0), a.Arg(1).S());
            case "downloads:list": return DownloadList();
            case "downloads:resume": return await ResumeDownload(id);
            case "downloads:remove": return await RemoveDownload(id);
            case "data:storage": return await StorageReport();
            case "data:clean": return await CleanStorage(id, a.Arg(1).A().Select(x => x.S()).ToArray());
            case "instances:import":
                var given = Folder("选择包含 .ebuild.json 的旧实例文件夹");
                if (given == "") return null;
                J.NoLinks(given);
                var source = J.Read(Path.Combine(given,".ebuild.json"));
                if (source == null) throw new IOException("该目录没有 EBuild 实例清单。");
                var imported = Spec(source);
                imported.At("overrides").Obj().Remove("java");
                return await Job((n,p,t) => Add(imported,n,p,t,stage => Task.Run(() => ImportPlayerData(given,stage,t),t),false));
            case "instances:external":
                var folder = id == "" ? Folder("选择 Minecraft 游戏文件夹") : id;
                return folder == "" ? null : await ImportExternal(folder, a.Arg(1).S());
            case "instances:relocate":
                var old = Instance(id);
                if (old.S("externalPath") == "") throw new IOException("只有外部实例支持重新定位。");
                var replacement = Folder("重新定位 Minecraft 游戏文件夹", old.S("externalSource"));
                return replacement == "" ? null : await ImportExternal(replacement, "", id);
            case "instances:delete": return await DeleteInstance(id, a.Arg(1).S(), a.Arg(2).S());
            case "instances:verify": return await Job(async (n,p,t) => {
                RequireIdleInstance(id);
                var instance = Instance(id);
                var dir = InstancePath(id);
                var effective = J.Merge(settings, instance.At("overrides"));
                p("download", "正在完整校验游戏文件", 0, 0);
                if (instance.S("externalPath") != "") await Task.Run(() => ExternalInstances.EnsureNotRunning(dir), t);
                await Task.Run(() => minecraft.Install(n, instance, dir, effective, p, t, true), t);
                return true;
            });
            case "game:list": return GameSnapshot();
            case "game:diagnose": return await games.Diagnose(id);
            case "game:log": return OpenGameLog(id);
            case "game:copy-diagnostic":
                if (id.Length == 0 || id.Length > 4000) throw new IOException("诊断摘要无效。");
                Clipboard.SetText(id);
                return true;
            case "game:start":
                if (Leaving) return null;
                InstancePath(id);
                RequireIdleInstance(id);
                var roomId = a.Arg(2).S();
                if (roomId != "") multiplayer.Guest(roomId);
                launchQueue.Add(new LaunchRequest { Id = id, ApprovedJava = a.Arg(1) is int ? (int)a.Arg(1) : 0, Room = roomId });
                SendGames();
                ContinueLaunches();
                return true;
            case "game:cancel-queued":
                var removed = launchQueue.RemoveAll(request => SameInstance(request.Id, id)) > 0;
                if (removed) SendGames();
                return removed;
            case "game:stop": return await games.Stop(id, a.Arg(1).S());
            case "game:minimize": window.WindowState = FormWindowState.Minimized; return true;
            case "game:keep-and-exit":
                games.PersistAll();
                Leaving = true;
                if (multiplayer.HasWork) multiplayer.Leave();
                launchQueue.Clear();
                if (task != null) task.Cancel();
                SendGames();
                ContinueLaunches();
                return true;
            case "lan:get": return multiplayer.Snapshot();
            case "lan:node":
                if (multiplayer.HasWork) throw new IOException("请退出当前联机会话后再更改节点。");
                Save(J.Map("lanNode", id));
                return multiplayer.Snapshot();
            case "lan:port":
                var published = games.Find(id);
                if (published == null) return 0;
                var publishedPid = published.Process.Id;
                return await Task.Run(() => LanWorld.PublishedPort(published.Directory, published.Started, publishedPid));
            case "lan:create":
                if (Leaving) return false;
                var game = games.Find(id);
                if (game == null) throw new IOException("请先通过 EBuild 启动房主游戏，再开放局域网。");
                return await multiplayer.Create(Instance(id), game, Convert.ToInt32(a.Arg(1)));
            case "lan:join": if (Leaving) return false; return await multiplayer.Join(id);
            case "lan:rotate": if (Leaving) return false; return await multiplayer.Rotate();
            case "lan:leave": multiplayer.Leave(); return true;
            case "lan:copy":
                var snapshot = multiplayer.Snapshot().At("room");
                var copied = id == "invite" ? snapshot.S("invite") : id == "address" ? snapshot.S("address") : "";
                if (copied == "") throw new IOException("联机信息已失效。");
                Clipboard.SetText(copied);
                return true;
            case "task:cancel": if (task != null) task.Cancel(); return null;
            case "catalog": using (var net = new Network(settings)) return await minecraft.Catalog(net, CancellationToken.None, Object.Equals(a.Arg(0), true));
            case "loaders": using (var net = new Network(settings)) return await minecraft.Loaders(net,id,a.Arg(1).S(),CancellationToken.None);
            case "data:choose": return Folder("选择游戏数据目录",Root);
            case "data:open": return Open(Root);
            case "data:cache": throw new IOException("请在空间管理中选择清理项目并确认。");
            case "data:change": return await Job(async (n,p,t) => {
                if (HasGameWork) throw new IOException("请先结束运行中的游戏和排队任务。");
                var target = Path.GetFullPath(id == "" ? defaultRoot : id);
                var current = Path.GetFullPath(Root);
                J.NoLinks(target);
                if (target.Equals(current,StringComparison.OrdinalIgnoreCase)) return current;
                if (target.Equals(Path.GetPathRoot(target),StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith(current.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase) ||
                    current.StartsWith(target.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith(AppDomain.CurrentDomain.BaseDirectory,StringComparison.OrdinalIgnoreCase)) throw new IOException("请选择独立的游戏数据子目录。");
                if (Object.Equals(a.Arg(1),true)) {
                    if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) {
                        if (id == "") throw new IOException("默认目录已有数据。请取消勾选复制，切换回已有目录。");
                        throw new IOException("迁移目标必须是空目录。");
                    }
                    var stage = target + "-migration-" + Guid.NewGuid().ToString("N");
                    try {
                        await Task.Run(() => CopyChecked(current,stage,t),t);
                        t.ThrowIfCancellationRequested();
                        if (Directory.Exists(target)) Directory.Delete(target);
                        Directory.Move(stage,target);
                    } finally { if (Directory.Exists(stage)) Directory.Delete(stage,true); }
                } else InitRoot(target);
                Save(J.Map("dataRoot",id == "" ? "" : target)); minecraft.Root = target; resources.Reset();
                RestoreGames();
                SendGames();
                return target;
            });
            case "java:select": return ChooseFile("选择 Java","Java|java.exe;javaw.exe");
            case "java:list":
                var found = new List<object>();
                foreach (var exe in minecraft.JavaCandidates(settings.S("java")).Distinct()) {
                    var major = await minecraft.JavaMajor(exe,CancellationToken.None);
                    if (major > 0) found.Add(J.Map("major",major,"executable",exe));
                }
                return found;
            case "java:install":
                int wanted;
                if (!Int32.TryParse(id,out wanted) || !new[] {8,16,17,21,25,26}.Contains(wanted)) throw new IOException("请选择支持的 Java 版本。");
                return await Job(async (n,p,t) => { await minecraft.Java(n,wanted,"",p,t); return true; });
            case "account:summary": return accounts.Summary();
            case "account:refresh": return await Job(async (n,p,t) => {
                p("download","正在刷新正版档案",0,0);
                return await accounts.RefreshProfile(n,t);
            });
            case "account:appearance": return appearance.Summary();
            case "account:appearance-import":
                if (id != "skin" && id != "cape") throw new IOException("请选择皮肤或披风。");
                var png = ChooseFile(id == "skin" ? "导入离线皮肤" : "导入离线披风", "PNG 图片|*.png");
                if (png == "") return null;
                appearance.Import(id, png);
                return appearance.Summary();
            case "account:appearance-remove": appearance.Remove(id); return appearance.Summary();
            case "account:browser": return Web("https://www.microsoft.com/link");
            case "account:login": return await Job(async (n,p,t) => {
                p("download","正在连接微软登录",0,0);
                var account = await accounts.Login(n,notify,t); Save(J.Map("accountMode","microsoft")); return account;
            });
            case "account:logout": accounts.Logout(); Save(J.Map("accountMode","offline")); return true;
            case "resources:open-link": return Web(id);
            case "resources:copy-name": if (id.Length == 0 || id.Length > 300) throw new IOException("名称无效。"); Clipboard.SetText(id); return true;
            case "resources:import":
                var pack = id == "" ? ChooseFile("导入 Modrinth 整合包","Modrinth 整合包|*.mrpack") : id;
                if (pack != "") pack = PackFile(pack);
                return pack == "" ? null : await CreatePackDownload(pack, null, "");
            default:
                if (!channel.StartsWith("resources:",StringComparison.Ordinal)) throw new IOException("未知指令。");
                using (var net = new Network(settings)) return await resources.Call(channel,a,net);
        }
    }
    internal bool CanClose() {
        if (multiplayer.HasWork) multiplayer.Leave();
        if (task == null) return true;
        task.Cancel();
        return false;
    }
}
