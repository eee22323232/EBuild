using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

sealed class Resources
{
    const string Base = "https://api.modrinth.com/v2";
    internal static readonly Dictionary<string,string> Folders = new Dictionary<string,string> {
        {"mod","mods"},{"shader","shaderpacks"},{"resourcepack","resourcepacks"}
    };
    static readonly string[] Types = { "mod","modpack","shader","resourcepack" };
    readonly Backend backend;
    readonly Dictionary<string,Plan> plans = new Dictionary<string,Plan>();
    readonly Dictionary<string,Destination> destinations = new Dictionary<string,Destination>();
    sealed class Plan {
        internal string Title, Type, Version, Root, Folder, Instance;
        internal object Pack;
        internal List<Part> Files = new List<Part>();
    }
    sealed class Part { internal string Project, Version, Name, Folder, Title; internal object Spec; }
    sealed class Destination { internal string Root, Folder, Game, Loader; internal object Project, Release; }
    internal Resources(Backend backend) { this.backend = backend; }
    internal void Reset() { plans.Clear(); destinations.Clear(); }
    static string Id(string value) {
        if (!Regex.IsMatch(value, "^[a-zA-Z0-9_-]{1,120}$")) throw new IOException("无效的 Modrinth 标识。");
        return value;
    }
    static string Remember<T>(Dictionary<string,T> map,T value) {
        if (map.Count >= 30) map.Remove(map.Keys.First());
        var key = Guid.NewGuid().ToString("N"); map[key] = value; return key;
    }
    static Task<object> Api(Network net,string route,CancellationToken ct) { return net.Json(Base + route,ct); }
    static string Encode(string value) { return Uri.EscapeDataString(value); }
    static string Short(string value,int length) { return value.Length <= length ? value : value.Substring(0,length); }
    static Task<object> Search(Network net,object input,CancellationToken ct) {
        var type = input.S("type");
        var facets = new List<string[]> {
            (Types.Contains(type) ? new[] {type} : Types).Select(x => "project_type:" + x).ToArray()
        };
        if (input.S("version") != "") facets.Add(new[] {"versions:" + Short(input.S("version"),64)});
        if (new[] {"fabric","neoforge","forge","quilt"}.Contains(input.S("loader"))) facets.Add(new[] {"categories:" + input.S("loader")});
        var sort = input.S("sort");
        if (!new[] {"relevance","downloads","updated","newest"}.Contains(sort)) sort = "relevance";
        return Api(net,"/search?limit=20&query=" + Encode(Short(input.S("query"),150)) + "&index=" + sort + "&facets=" + Encode(J.Dump(facets)) +
            "&offset=" + Math.Max(0,Math.Min(10000,input.I("offset"))),ct);
    }
    static async Task<Tuple<object,object[]>> Versions(Network net,string id,CancellationToken ct) {
        id = Id(id);
        var project = Api(net,"/project/" + id,ct);
        var releases = Api(net,"/project/" + id + "/version",ct);
        await Task.WhenAll(project,releases);
        return Tuple.Create(await project,(await releases).A());
    }
    static bool Compatible(object release,string type,object instance) {
        if (type == "modpack") return release.A("loaders").Any(x => new[] {"minecraft","fabric","neoforge"}.Contains(x.S()));
        return instance != null && release.A("game_versions").Any(x => x.S() == instance.S("version")) &&
            (type != "mod" || release.A("loaders").Any(x => x.S() == instance.S("loader")));
    }
    static object Pick(object project,object[] releases,string id,object instance,bool standalone) {
        var type = project.S("project_type");
        var matches = releases.Where(x => standalone || Compatible(x,type,instance)).ToArray();
        var chosen = id != "" ? matches.FirstOrDefault(x => x.S("id") == id) : matches.FirstOrDefault(x => x.S("version_type") == "release") ?? matches.FirstOrDefault();
        if (chosen == null) throw new IOException("找不到兼容的资源版本。");
        return chosen;
    }
    static object FileSpec(object file) {
        var hash = file.At("hashes").S("sha512");
        if (!Regex.IsMatch(hash, "^[a-fA-F0-9]{128}$") || file.At("size") == null || Convert.ToInt64(file.At("size")) <= 0)
            throw new IOException("Modrinth 文件缺少校验信息。");
        Network.Https(file.S("url"));
        return J.Map("url",file.S("url"),"sha512",hash,"size",file.At("size"));
    }
    static object Primary(object version) { var files = version.A("files"); return files.FirstOrDefault(x => x.B("primary")) ?? files.FirstOrDefault(); }
    static async Task Visit(Network net,Plan plan,object project,object release,object instance,bool dependencies,CancellationToken ct) {
        var pid = project.S("id");
        var existing = plan.Files.FirstOrDefault(x => x.Project == pid);
        if (existing != null) {
            if (existing.Version != release.S("id")) throw new IOException("存在不兼容的依赖版本。");
            return;
        }
        if (plan.Files.Count >= 80) throw new IOException("必需依赖数量超出限制。");
        string folder;
        if (!Folders.TryGetValue(project.S("project_type"),out folder)) throw new IOException("资源类型无效。");
        var file = Primary(release);
        var name = file.S("filename");
        if (name == "" || Path.GetFileName(name) != name || !Regex.IsMatch(name,@"\.(zip|jar)$",RegexOptions.IgnoreCase))
            throw new IOException("资源文件名无效。");
        plan.Files.Add(new Part { Project = pid, Version = release.S("id"), Name = name, Folder = folder, Title = project.S("title"), Spec = FileSpec(file) });
        if (!dependencies) return;
        foreach (var dependency in release.A("dependencies").Where(x => x.S("dependency_type") == "required")) {
            var pinned = dependency.S("version_id") == "" ? null : await Api(net,"/version/" + Id(dependency.S("version_id")),ct);
            var key = pinned == null ? dependency.S("project_id") : pinned.S("project_id");
            if (key == "") throw new IOException("有必需依赖需要手动安装。");
            var next = await Versions(net,key,ct);
            var chosen = Pick(next.Item1,next.Item2,pinned.S("id"),instance,false);
            await Visit(net,plan,next.Item1,chosen,instance,true,ct);
        }
    }
    async Task<object> RememberPlan(Network net,object project,object release,object instance,string folder,CancellationToken ct) {
        var plan = new Plan { Root = backend.Root, Title = project.S("title"), Type = project.S("project_type"), Version = release.S("version_number"),
            Folder = folder, Instance = instance.S("id") };
        if (plan.Type == "modpack") {
            var file = Primary(release);
            if (!file.S("filename").EndsWith(".mrpack",StringComparison.OrdinalIgnoreCase)) throw new IOException("此整合包没有 .mrpack 文件。");
            plan.Pack = FileSpec(file);
        } else await Visit(net,plan,project,release,instance,true,ct);
        return J.Map("key",Remember(plans,plan),"title",plan.Title,"type",plan.Type,"release",plan.Version,"files",plan.Files.Select(x => x.Title).ToArray());
    }
    static async Task<object> LoadDetail(Network net,string key,CancellationToken ct) {
        var result = await Versions(net,key,ct);
        var releases = result.Item2;
        var game = new HashSet<string>(releases.SelectMany(x => x.A("game_versions")).Select(x => x.S()));
        object tags;
        try { tags = (await Api(net,"/tag/game_version",ct)).A().Where(x => game.Contains(x.S("version")))
            .Select(x => J.Map("version",x.S("version"),"type",x.S("version_type"),"date",x.S("date"))).ToArray(); }
        catch (System.Net.Http.HttpRequestException) { tags = new object[0]; }
        var project = result.Item1;
        var wikiId = project.S("id") == "AANobbMI" ? 2785 : project.S("id") == "YL57xq9U" ? 3697 : 0;
        // Remote descriptions are rendered as inert text so they cannot invoke the native bridge.
        var description = String.Join("", Regex.Split(Short(project.S("body"),150000), @"(?:\r?\n){2,}")
            .Select(p => "<p>" + WebUtility.HtmlEncode(p).Replace("\n","<br>") + "</p>"));
        return J.Map("project",Project(project),"description",description,"wikiUrl",wikiId > 0 ? "https://www.mcmod.cn/class/" + wikiId + ".html" : "",
            "gameVersionTags",tags,"releases",releases.Select(x => {
                var spec = Select(x,"id","name","version_number","game_versions","loaders","version_type","date_published","downloads");
                spec["dependencies"] = x.A("dependencies").Where(d => d.S("dependency_type") == "required").ToArray(); return spec;
            }).ToArray());
    }
    static Dictionary<string,object> Select(object value,params string[] keys) {
        return value.Obj().Where(x => keys.Contains(x.Key)).ToDictionary(x=>x.Key,x=>x.Value);
    }
    static object Project(object value) { return Select(value,"id","slug","title","description","icon_url","project_type","downloads","updated","categories"); }
    internal async Task<object> Call(string channel,object[] args,Network net) {
        var ct = CancellationToken.None;
        var id = args.Arg(0).S();
        switch (channel) {
            case "resources:search": return await Search(net,args.Arg(0),ct);
            case "resources:detail": return await LoadDetail(net,id,ct);
            case "resources:dependencies":
                var deps = await Api(net,"/project/" + Id(id) + "/dependencies",ct);
                return J.Map("projects",deps.A("projects").Select(Project).ToArray(),
                    "releases",deps.A("versions").Select(x => Select(x,"id","project_id","version_number")).ToArray());
            case "resources:destination":
            case "resources:plan":
                var result = await Versions(net,id,ct);
                var project = result.Item1;
                var type = project.S("project_type");
                var preferred = args.Arg(2).S();
                var all = backend.Instances().ToArray();
                var selected = all.FirstOrDefault(x => x.S("id") == preferred);
                var choice = Pick(project,result.Item2,args.Arg(1).S(),selected,channel == "resources:destination");
                if (channel == "resources:plan") return await RememberPlan(net,project,choice,selected,"",ct);
                if (type == "modpack") {
                    if (!Compatible(choice,type,null)) throw new IOException("整合包的加载器暂未支持。");
                    return J.Map("mode","pack","plan",await RememberPlan(net,project,choice,null,"",ct));
                }
                string part;
                if (!Folders.TryGetValue(type,out part)) throw new IOException("不支持该资源类型。");
                selected = Compatible(choice,type,selected) ? selected : all.FirstOrDefault(x => Compatible(choice,type,x));
                var starting = selected == null ? "" : Path.Combine(backend.InstancePath(selected.S("id")),part);
                if (starting != "") { J.NoLinks(starting); Directory.CreateDirectory(starting); }
                var folder = backend.Folder("选择保存位置",starting);
                if (folder == "") return null;
                folder = Path.GetFullPath(folder); J.NoLinks(folder);
                var matching = all.FirstOrDefault(x => Path.Combine(backend.InstancePath(x.S("id")),part).Equals(folder,StringComparison.OrdinalIgnoreCase));
                if (matching != null) {
                    if (!Compatible(choice,type,matching)) throw new IOException("所选实例与此资源版本不兼容。");
                    return J.Map("mode","instance","instanceId",matching.S("id"),"directory",folder,
                        "plan",await RememberPlan(net,project,choice,matching,folder,ct));
                }
                var chosenGame = args.Arg(3).S();
                var chosenLoader = args.Arg(4).S();
                var key = Remember(destinations,new Destination { Root = backend.Root, Folder = folder, Game =
                    choice.A("game_versions").Any(x=>x.S() == chosenGame) ? chosenGame : choice.A("game_versions").FirstOrDefault().S(),
                    Loader = choice.A("loaders").Any(x=>x.S() == chosenLoader) ? chosenLoader : selected.S("loader"),
                    Project=project,Release=choice });
                return J.Map("mode","folder","key",key,"directory",folder,"title",project.S("title"),"version",choice.S("version_number"),
                    "dependencyLoader",destinations[key].Loader,"hasDependencies",choice.A("dependencies").Any(x=>x.S("dependency_type")=="required"));
            case "resources:install":
                Plan plan;
                if (!plans.TryGetValue(id,out plan) || plan.Root != backend.Root) throw new IOException("安装计划已失效。");
                return await backend.Job(async (n,p,t) => {
                    object output;
                    if (plan.Pack != null) {
                        var zip = J.Inside(backend.Root,"cache/" + plan.Pack.S("sha512") + ".mrpack");
                        p("download","正在下载整合包",0,0); await n.FileTo(plan.Pack,zip,t);
                        output = await Pack(zip,n,p,t);
                    } else {
                        backend.Instance(plan.Instance);
                        await InstallParts(plan,backend.InstancePath(plan.Instance),false,n,p,t);
                        output = true;
                    }
                    plans.Remove(id); return output;
                });
            case "resources:export":
                Destination dest;
                if (!destinations.TryGetValue(id,out dest) || dest.Root != backend.Root) throw new IOException("所选文件夹已失效。");
                return await backend.Job(async (n,p,t) => {
                    var context = J.Map("version",dest.Game,"loader",dest.Loader == "" ? args.Arg(2).S() : dest.Loader);
                    var export = new Plan();
                    await Visit(n,export,dest.Project,dest.Release,context,Object.Equals(args.Arg(1),true),t);
                    await InstallParts(export,dest.Folder,true,n,p,t);
                    destinations.Remove(id); return true;
                });
            default: throw new IOException("未知资源操作。");
        }
    }
    static async Task InstallParts(Plan plan,string dir,bool standalone,Network net,Action<string,string,int,int> progress,CancellationToken ct) {
        var stage = Path.Combine(dir,".resource-" + Guid.NewGuid().ToString("N"));
        var moved = new List<string>();
        var jobs = plan.Files.Select((x,i) => new { Part=x, Temp=Path.Combine(stage,i.ToString()),
            Dest=J.Inside(dir,(standalone ? "" : x.Folder + "/") + x.Name) }).ToArray();
        try {
            foreach (var job in jobs) {
                J.NoLinks(job.Dest);
                if (File.Exists(job.Dest) && !Network.Check(job.Dest,job.Part.Spec))
                    throw new IOException("同名文件已存在：" + job.Part.Name);
            }
            var done = 0;
            await Network.Pool(jobs,8,async x => {
                if (!Network.Check(x.Dest,x.Part.Spec)) await net.FileTo(x.Part.Spec,x.Temp,ct);
                progress("download","正在下载资源",Interlocked.Increment(ref done),jobs.Length);
            },ct);
            foreach (var job in jobs.Where(x=>File.Exists(x.Temp))) {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(job.Dest));
                File.Move(job.Temp,job.Dest); moved.Add(job.Dest);
            }
        } catch { foreach (var file in moved) File.Delete(file); throw; }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage,true); }
    }
    static string PackPath(string name) {
        if (name.IndexOf('\\') >= 0 || name.Split('/').Any(x => x == ".." || x == "." ||
             Regex.IsMatch(x,@"^\.(ebuild|installed|launch|natives|resource)",RegexOptions.IgnoreCase))) throw new IOException("整合包包含无效路径。");
        return name;
    }
    internal async Task<object> Pack(string file,Network net,Action<string,string,int,int> progress,CancellationToken ct) {
        object index;
        using (var zip = ZipFile.OpenRead(file)) {
            var entry = zip.GetEntry("modrinth.index.json");
            if (entry == null || entry.Length > 16 * 1024 * 1024) throw new IOException("整合包缺少有效清单。");
            using (var reader = new StreamReader(entry.Open())) index = J.Parse(reader.ReadToEnd());
        }
        if (index.I("formatVersion") != 1 || index.S("game") != "minecraft") throw new IOException("不支持该 .mrpack 格式。");
        var deps = index.At("dependencies");
        var keys = deps.Obj().Keys.Where(x => x != "minecraft").ToArray();
        if (keys.Length > 1 || keys.Any(x=>x!="fabric-loader" && x!="neoforge")) throw new IOException("该整合包使用了暂未支持的加载器。");
        var loader = keys.FirstOrDefault();
        return await backend.Add(J.Map("name",index.S("name"),"version",deps.S("minecraft"),
            "loader",loader == null ? "vanilla" : loader == "fabric-loader" ? "fabric" : "neoforge","loaderVersion",loader == null ? "" : deps.S(loader)),
            net,progress,ct,async dir => {
                var entries = index.A("files").Where(x => x.At("env").S("client") != "unsupported").ToArray();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in entries) if (!seen.Add(J.Inside(dir,PackPath(entry.S("path"))))) throw new IOException("整合包里文件路径重复。");
                var done = 0;
                await Network.Pool(entries,12,async entry => {
                    var target=J.Inside(dir,PackPath(entry.S("path")));
                    var hash=entry.At("hashes").S("sha512");
                    if (!Regex.IsMatch(hash,"^[a-fA-F0-9]{128}$") || entry.At("fileSize") == null) throw new IOException("整合包文件缺少校验信息。");
                    foreach (var value in entry.A("downloads")) {
                        Uri uri;
                        if (!Uri.TryCreate(value.S(),UriKind.Absolute,out uri) || uri.Scheme != "https" ||
                            !new[] {"cdn.modrinth.com","github.com","raw.githubusercontent.com","gitlab.com"}.Contains(uri.Host)) continue;
                        try {
                            await net.FileTo(J.Map("url",uri.AbsoluteUri,"sha512",hash,"size",entry.At("fileSize")),target,ct);
                            progress("download","正在下载整合包文件",Interlocked.Increment(ref done),entries.Length); return;
                        } catch (Exception e) { if (!(e is IOException) && !(e is System.Net.Http.HttpRequestException)) throw; }
                    }
                    throw new IOException("整合包文件下载失败：" + entry.S("path"));
                },ct);
                foreach (var prefix in new[] {"overrides/","client-overrides/"})
                    Network.Unzip(file,dir,ct,path=>path.StartsWith(prefix,StringComparison.Ordinal) && path.Length>prefix.Length ? PackPath(path.Substring(prefix.Length)) : null);
            });
    }
}
