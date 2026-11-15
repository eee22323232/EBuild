using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

sealed class Backend
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
    Process game;
    internal Backend(Form owner, string profile, Action<object> status) {
        window = owner; notify = status;
        Directory.CreateDirectory(profile);
        defaultRoot = Path.Combine(profile, "游戏数据");
        settingsFile = Path.Combine(profile, "settings.json");
        settings = J.Merge(J.Map("username", "Player", "accountMode", "offline", "offlineSkinModel", "classic", "selected", "", "dataRoot", "",
            "memory", 4096, "java", "", "width", 0, "height", 0, "afterLaunch", "keep",
            "theme", "system", "concurrency", 12, "downloadSource", "bmclapi", "proxyMode", "system", "proxy", "",
            "gc", "", "jvmArgs", new object[0], "gameArgs", new object[0], "logs", true), J.Read(settingsFile));
        accounts = new Accounts(profile);
        appearance = new LocalAppearance(profile);
        minecraft = new Minecraft(settings.S("dataRoot") == "" ? defaultRoot : settings.S("dataRoot"), settings);
        InitRoot(minecraft.Root);
        resources = new Resources(this);
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
    internal string InstancePath(string id) { return J.Inside(Root, "instances/" + J.Id(id)); }
    internal IEnumerable<object> Instances() {
        var root = Path.Combine(Root, "instances");
        if (!Directory.Exists(root)) return Enumerable.Empty<object>();
        return Directory.GetDirectories(root).Where(x => !Path.GetFileName(x).StartsWith(".") && (File.GetAttributes(x) & FileAttributes.ReparsePoint) == 0)
            .Select(x => new { Path = x, Spec = J.Read(Path.Combine(x, ".ebuild.json")) })
            .Where(x => x.Spec != null).Select(x => (object)J.Merge(x.Spec, J.Map("id", Path.GetFileName(x.Path)))).OrderBy(x => x.S("name"));
    }
    internal object Instance(string id) {
        var file = Path.Combine(InstancePath(id), ".ebuild.json");
        J.NoLinks(file);
        var value = J.Read(file);
        if (value == null) throw new IOException("实例不存在。");
        return J.Merge(value, J.Map("id", id));
    }
    object Save(object delta) {
        var next = J.Merge(settings, delta);
        if (!Regex.IsMatch(next.S("username"), "^[a-zA-Z0-9_]{3,16}$")) throw new IOException("离线档案名须为 3–16 位字母、数字或下划线。");
        if (!new[] { "classic", "slim" }.Contains(next.S("offlineSkinModel"))) throw new IOException("请选择经典或纤细模型。");
        if (!new[] {"system","manual","direct"}.Contains(next.S("proxyMode"))) throw new IOException("代理设置无效。");
        if (next.S("proxyMode") == "manual") {
            Uri proxy;
            if (!Uri.TryCreate(next.S("proxy"), UriKind.Absolute, out proxy) || !new[] {"http","https"}.Contains(proxy.Scheme))
                throw new IOException("请输入有效的 HTTP 或 HTTPS 代理地址。");
        }
        if (!new[] {"offline","microsoft"}.Contains(next.S("accountMode")) || !new[] {"system","light","dark"}.Contains(next.S("theme")) ||
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
        if (!new[] {"vanilla","fabric","neoforge"}.Contains(loader)) throw new IOException("不支持该加载器。");
        return J.Map("name",name,"version",J.Id(input.S("version")),"loader",loader,
            "loaderVersion",loader == "vanilla" ? "" : J.Id(input.S("loaderVersion")),
            "overrides", CheckOverrides(input.At("overrides")));
    }
    internal async Task<object> Add(object input, Network net, Action<string,string,int,int> progress, CancellationToken ct, Func<string,Task> populate = null, bool install = true) {
        var spec = Spec(input);
        var id = Guid.NewGuid().ToString();
        var final = InstancePath(id);
        var stage = Path.Combine(Path.GetDirectoryName(final), "." + id);
        Directory.CreateDirectory(stage);
        try {
            if (populate != null) await populate(stage);
            if (install) {
                var effective = J.Merge(settings,spec.At("overrides"));
                await Task.Run(() => minecraft.Install(net,spec,stage,effective,progress,ct),ct);
            }
            ct.ThrowIfCancellationRequested();
            J.Save(Path.Combine(stage,".ebuild.json"),spec);
            Directory.Move(stage,final);
            Save(J.Map("selected",id));
            return J.Merge(spec,J.Map("id",id));
        } finally { if (Directory.Exists(stage)) Directory.Delete(stage,true); }
    }
    void Status(string phase, string message, string kind = null, int done = 0, int total = 0) {
        notify(J.Map("phase",phase,"message",message,"kind",kind,"done",done,"total",total));
    }
    internal async Task<object> Job(Func<Network,Action<string,string,int,int>,CancellationToken,Task<object>> work, string kind = null) {
        if (task != null || game != null) throw new IOException("请先结束当前游戏或下载任务。");
        using (var source = new CancellationTokenSource())
        using (var net = new Network(settings)) {
            task = source;
            try {
                var result = await work(net,(phase,message,done,total) => Status(phase,message,kind,done,total),source.Token);
                if (game == null) Status("idle","就绪",kind);
                return result;
            } catch (OperationCanceledException) { if (!source.IsCancellationRequested) throw; Status("idle","任务已取消",kind); return null; }
            catch (Exception e) { Status("error",e.Message,kind); throw; }
            finally { task = null; }
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
    async Task<object> Start(string id, int approvedJava, Network net, Action<string,string,int,int> progress, CancellationToken ct) {
        var instance = Instance(id);
        var effective = J.Merge(settings, instance.At("overrides"));
        progress("java", "正在检查游戏所需的 Java", 0, 0);
        var baseSpec = await minecraft.ReadBaseVersion(net, instance, ct);
        var major = Minecraft.RequiredJavaMajor(baseSpec);
        var java = await minecraft.FindJava(major, effective.S("java"), ct);
        if (java == null) {
            if (approvedJava != major) return J.Map("requiredJava", major);
            java = await minecraft.DownloadJava(net, major, progress, ct);
        }
        var account = effective.S("accountMode") == "microsoft" ? await accounts.ForLaunch(net,ct) : null;
        var dir = InstancePath(id);
        var process = await Task.Run(() => minecraft.Launch(net, instance, baseSpec, dir, effective, account, java, progress, ct), ct);
        game = process;
        progress("running","Minecraft 正在运行",0,0);
        if (effective.S("afterLaunch") == "minimize") window.WindowState = FormWindowState.Minimized;
        process.Exited += (s,e) => { if (!window.IsDisposed && window.IsHandleCreated) window.BeginInvoke((MethodInvoker)(() => Finish(process))); };
        if (process.HasExited) Finish(process);
        return true;
    }
    void Finish(Process process) {
        if (game != process) return;
        var code = process.ExitCode;
        game = null; process.Dispose();
        Status(code == 0 ? "idle" : "error",code == 0 ? "游戏已退出" : "游戏退出（代码 " + code + "）。可查看实例日志。");
    }
    internal async Task<object> Call(string channel, object[] a) {
        string id = a.Arg(0).S();
        switch (channel) {
            case "settings:get": return J.Merge(settings,J.Map("currentRoot",Root,"defaultRoot",defaultRoot,"version","dev.0.1.1 · WebView2 + C#"));
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
                var edited = Spec(J.Merge(Instance(id),J.Map("name",a.Arg(1).S("name"),"overrides",a.Arg(1).At("overrides"))));
                J.Save(Path.Combine(InstancePath(id),".ebuild.json"),edited);
                return J.Merge(edited,J.Map("id",id));
            case "instances:create": return await Job((n,p,t) => Add(a.Arg(0),n,p,t), "game-install");
            case "instances:import":
                var given = Folder("选择包含 .ebuild.json 的旧实例文件夹");
                if (given == "") return null;
                J.NoLinks(given);
                var source = J.Read(Path.Combine(given,".ebuild.json"));
                if (source == null) throw new IOException("该目录没有 EBuild 实例清单。");
                var imported = Spec(source);
                imported.At("overrides").Obj().Remove("java");
                return await Job((n,p,t) => Add(imported,n,p,t,stage => Task.Run(() => ImportPlayerData(given,stage,t),t),false));
            case "game:start":
                var approvedJava = a.Arg(1) is int ? (int)a.Arg(1) : 0;
                return await Job((n,p,t) => Start(id, approvedJava, n, p, t));
            case "task:cancel": if (task != null) task.Cancel(); return null;
            case "catalog": using (var net = new Network(settings)) return await minecraft.Catalog(net, CancellationToken.None, Object.Equals(a.Arg(0), true));
            case "loaders": using (var net = new Network(settings)) return await minecraft.Loaders(net,id,a.Arg(1).S(),CancellationToken.None);
            case "data:choose": return Folder("选择游戏数据目录",Root);
            case "data:open": return Open(Root);
            case "data:cache": return await Job((n,p,t) => Task.Run<object>(() => {
                var dir = Path.Combine(Root,"cache"); J.NoLinks(dir);
                if (Directory.Exists(dir)) Directory.Delete(dir,true);
                return true;
            },t));
            case "data:change": return await Job(async (n,p,t) => {
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
                var pack = ChooseFile("导入 Modrinth 整合包","Modrinth 整合包|*.mrpack");
                return pack == "" ? null : await Job((n,p,t) => resources.Pack(pack,n,p,t));
            default:
                if (!channel.StartsWith("resources:",StringComparison.Ordinal)) throw new IOException("未知指令。");
                using (var net = new Network(settings)) return await resources.Call(channel,a,net);
        }
    }
    internal bool CanClose() { if (task == null) return true; task.Cancel(); return false; }
}
