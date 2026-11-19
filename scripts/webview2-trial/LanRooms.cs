using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

sealed class LanRooms
{
    const int HostHandshakeSeconds = 18;

    internal sealed class Invitation {
        const string Prefix = "EB1-";
        internal string Secret, Node, Version, Loader, LoaderVersion;
        internal int Port, ModPort, InfoPort;
        internal byte[] Token;
        internal bool PublicPeers, PackFiles;
        internal string NetworkName { get { return "ebuild-" + Secret.Substring(0, 24); } }
        internal string HostIp { get { return "10.234." + Convert.ToInt32(Secret.Substring(24, 2), 16) + ".1"; } }
        internal string ShareKey {
            get { using (var sha = SHA256.Create()) return Network.Hex(sha.ComputeHash(Encoding.ASCII.GetBytes(Secret))); }
        }
        internal string Encode() {
            return Token == null ? EncodeLegacy() : LanInviteCode.Encode(Token, Port, InfoPort);
        }
        internal string EncodeLegacy() {
            var payload = J.Dump(J.Map("s", Secret, "n", Node, "v", Version, "l", Loader, "r", LoaderVersion, "p", Port, "m", ModPort,
                "f", PublicPeers, "x", PackFiles));
            return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        internal static Invitation Decode(string text) {
            var input = text.Trim();
            if (input.Length > 1200)
                throw new IOException("邀请码无效。请复制房主提供的完整 EBuild 邀请码。");
            try {
                if (input.Length < 80 || !input.StartsWith(Prefix, StringComparison.Ordinal)) return LanInviteCode.Decode(input);
                if (!Regex.IsMatch(input.Substring(Prefix.Length), @"^[A-Za-z0-9_-]+$")) throw new FormatException();
                var value = input.Substring(Prefix.Length).Replace('-', '+').Replace('_', '/');
                var data = J.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '='))));
                var loader = data.S("l");
                var port = data.I("p");
                var modPort = data.I("m");
                if (port < 1 || port > 65535 || modPort < 0 || modPort > 65535 || modPort == port ||
                    !Minecraft.SupportsLoader(loader) || !Regex.IsMatch(data.S("s"), @"^[a-f0-9]{64}$"))
                    throw new FormatException();
                var node = LanTunnel.NormalizeNode(data.S("n"));
                return new Invitation { Secret = data.S("s"), Node = node, PublicPeers = data.B("f") && LanTunnel.IsPublicNode(node), PackFiles = modPort != 0 && data.B("x"),
                    Version = J.Id(data.S("v")),
                    Loader = loader, LoaderVersion = data.S("r") == "" ? "" : J.Id(data.S("r")), Port = port, ModPort = modPort };
            } catch (ArgumentException) { }
            catch (FormatException) { }
            catch (InvalidOperationException) { }
            catch (IOException) { }
            throw new IOException("邀请码无效。请核对字符，或粘贴房主提供的邀请码。");
        }
        internal bool Matches(object instance) {
            return Version == instance.S("version") && Loader == instance.S("loader") &&
                (LoaderVersion == "" || LoaderVersion == instance.S("loaderVersion"));
        }
        internal static Invitation ForWorld(object instance, int port, bool shortCode) {
            var token = new byte[shortCode ? LanInviteCode.TokenSize : 32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(token);
            return new Invitation { Secret = shortCode ? LanInviteCode.Secret(token) : Network.Hex(token), Token = shortCode ? token : null,
                Version = instance.S("version"), Loader = instance.S("loader"),
                LoaderVersion = instance.S("loaderVersion"), Port = port };
        }

        internal void LoadRoom(object room) {
            var loader = room.S("loader");
            var version = room.S("loaderVersion");
            if (!Minecraft.SupportsLoader(loader) || (version == "") != (loader == "vanilla"))
                throw new IOException("房主的游戏或加载器信息无效。");
            Version = J.Id(room.S("version"));
            Loader = loader;
            LoaderVersion = version == "" ? "" : J.Id(version);
            ModPort = room.B("share") ? InfoPort : 0;
            PackFiles = ModPort != 0;
        }
    }

    internal sealed class Session : IDisposable {
        internal readonly string Id = Guid.NewGuid().ToString("N");
        internal Invitation Invite;
        internal LanTunnel Tunnel;
        internal LanModShare Mods;
        internal string InstanceId, InstanceName;
        internal int GamePid, Missing;
        internal DateTime GameStarted;
        internal string Connection = "";
        internal bool Host { get { return InstanceId != null; } }
        internal string Address { get { return "127.0.0.1:" + Tunnel.LocalPort; } }
        public void Dispose() {
            try { Tunnel.Dispose(); }
            finally { if (Mods != null) Mods.Dispose(); }
        }
    }

    readonly string profile;
    readonly Func<object> settings;
    readonly Action<object> notify;
    readonly System.Windows.Forms.Timer timer;
    CancellationTokenSource operation;
    volatile Session current;
    bool monitoring;
    string progress = "", notice = "";

    internal LanRooms(string profile, Func<object> settings, Action<object> notify) {
        this.profile = profile;
        this.settings = settings;
        this.notify = notify;
        timer = new System.Windows.Forms.Timer { Interval = 3000 };
        timer.Tick += Monitor;
    }

    internal bool HasWork { get { return operation != null || current != null; } }
    internal string Node { get { return settings().S("lanNode"); } }

    internal object Snapshot() {
        var room = current;
        return J.Map("working", operation != null, "progress", progress, "notice", notice, "node", Node,
            "room", room == null ? null : J.Map("id", room.Id, "host", room.Host, "name", room.InstanceName,
                "version", room.Invite.Version, "loader", room.Invite.Loader, "loaderVersion", room.Invite.LoaderVersion,
                "connection", room.Connection, "node", room.Invite.Node,
                "shareMods", room.Invite.ModPort != 0, "packFiles", room.Invite.PackFiles, "sharedCount", room.Mods == null ? 0 : room.Mods.Count,
                "invite", room.Host ? room.Invite.Encode() : "", "shortInvite", room.Host && room.Invite.Token != null,
                "address", room.Host ? "" : room.Address));
    }

    internal string LegacyCode() {
        var room = current;
        return room != null && room.Host ? room.Invite.EncodeLegacy() : "";
    }

    void Send() { notify(J.Map("lanState", Snapshot())); }
    void Progress(string message) { progress = message; Send(); }

    async Task<object> Run(Func<CancellationToken,Task<Session>> action) {
        if (operation != null) throw new IOException("联机操作正在进行。");
        using (var source = new CancellationTokenSource()) {
            operation = source;
            notice = "";
            Progress("正在准备联机");
            Session next = null;
            try {
                next = await action(source.Token);
                source.Token.ThrowIfCancellationRequested();
                if (next.Host && !await Task.Run(() => LanWorld.StillListening(next.GamePid, next.GameStarted, next.Invite.Port)))
                    throw new IOException("房主的世界已关闭，请重新开放局域网。");
                source.Token.ThrowIfCancellationRequested();
                var old = current;
                current = next;
                next = null;
                if (old != null) old.Dispose();
                timer.Start();
                return true;
            } catch (OperationCanceledException) {
                if (!source.IsCancellationRequested) throw;
                return false;
            }
            finally {
                if (next != null) next.Dispose();
                operation = null;
                progress = "";
                Send();
            }
        }
    }

    async Task<string> Runtime(CancellationToken ct) {
        var selectedSettings = settings();
        IProgress<string> report = new Progress<string>(message => { if (!ct.IsCancellationRequested && operation != null) Progress(message); });
        using (var net = new Network(selectedSettings, J.Inside(profile, "multiplayer/transfers"))) {
            return await Task.Run(() => LanTunnel.Prepare(profile, net, report.Report, ct), ct);
        }
    }

    static async Task<LanTunnel> Connect(string runtime, Invitation invite, bool host, int limitSeconds, CancellationToken ct) {
        LanTunnel tunnel = null;
        try {
            tunnel = new LanTunnel(runtime, invite.Node, invite.NetworkName, invite.Secret, invite.HostIp, invite.Port, host,
                invite.InfoPort == 0 ? invite.ModPort : invite.InfoPort,
                invite.PublicPeers);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < limitSeconds && tunnel.Running) {
                ct.ThrowIfCancellationRequested();
                var peers = await tunnel.Peers(ct);
                if (host ? peers.Any(peer => peer.S("cost") != "" && peer.S("cost") != "Local") :
                    peers.Any(peer => peer.S("ipv4") == invite.HostIp) && await Task.Run(() => LanWorld.Responds(tunnel.LocalPort), ct)) {
                    var ready = tunnel;
                    tunnel = null;
                    return ready;
                }
                await Task.Delay(400, ct);
            }
            if (!tunnel.Running) throw new IOException("联机组件提前退出，请检查节点设置后重试。");
            return null;
        } finally { if (tunnel != null) tunnel.Dispose(); }
    }

    async Task<Session> OpenHost(string runtime, Invitation invite, string id, string name, int pid, DateTime started,
            string sharedInstance, object choice, object original, CancellationToken ct) {
        LanModShare sharing = null;
        try {
            if (sharedInstance != null || invite.Token != null) {
                if (sharedInstance != null) Progress("正在检查要共享的实例文件");
                var info = invite.Token == null ? null : J.Map("version", invite.Version, "loader", invite.Loader,
                    "loaderVersion", invite.LoaderVersion, "share", sharedInstance != null);
                sharing = await Task.Run(() => new LanModShare(sharedInstance, invite.Secret, ct, choice, original, info), ct);
                if (invite.Token != null) invite.InfoPort = sharing.Port;
                if (sharedInstance != null) {
                    invite.ModPort = sharing.Port;
                    invite.PackFiles = true;
                }
            }
            Progress(invite.PublicPeers ? "正在连接公共联机节点" : "正在连接联机节点 · " + new Uri(invite.Node).Host);
            var tunnel = await Connect(runtime, invite, true, HostHandshakeSeconds, ct);
            if (tunnel == null) throw new IOException("联机节点握手超时。请检查网络或在联机设置中更换节点，再创建房间。");
            var room = new Session { InstanceId = id, InstanceName = name, GamePid = pid, GameStarted = started,
                Invite = invite, Tunnel = tunnel, Mods = sharing };
            sharing = null;
            return room;
        } finally {
            if (sharing != null) sharing.Dispose();
        }
    }

    internal Task<object> Create(object instance, GameProcesses.Game game, int port, bool shareMods, object choice, object original) {
        if (current != null) throw new IOException("请先退出当前联机会话。");
        var pid = game.Process.Id;
        var dir = game.Directory;
        var started = game.Started;
        return Run(async ct => {
            var world = await Task.Run(() => {
                var candidate = port == 0 ? LanWorld.PublishedPort(dir, started, pid) : port;
                var listener = candidate > 0 && candidate <= 65535 ? LanWorld.GameListener(pid, started, candidate) : 0;
                if (listener == 0 || !LanWorld.Responds(candidate))
                    throw new IOException("未找到该实例开放的局域网端口。请在世界里点「对局域网开放」，再试一次或填写游戏提示的端口。");
                using (var process = Process.GetProcessById(listener))
                    return Tuple.Create(candidate, listener, process.StartTime.ToUniversalTime());
            }, ct);
            var configuredNode = Node;
            var invite = Invitation.ForWorld(instance, world.Item1, configuredNode == "");
            invite.PublicPeers = configuredNode == "";
            invite.Node = invite.PublicPeers ? LanTunnel.PublicNodes[0] : configuredNode;
            return await OpenHost(await Runtime(ct), invite, instance.S("id"), instance.S("name"), world.Item2, world.Item3,
                shareMods ? dir : null, choice, original, ct);
        });
    }

    internal Task<object> Join(string code) {
        if (current != null) throw new IOException("请先退出当前联机会话。");
        var invite = Invitation.Decode(code);
        if (!LanTunnel.IsPublicNode(invite.Node) && !String.Equals(invite.Node, Node, StringComparison.OrdinalIgnoreCase))
            throw new IOException("这条邀请码使用自定义节点 " + invite.Node + "。确认信任该节点后，请在联机设置里填入同一地址。");
        return Run(async ct => {
            var runtime = await Runtime(ct);
            Progress("正在连接房主");
            var tunnel = await Connect(runtime, invite, false, 24, ct);
            if (tunnel == null) throw new IOException("未能连到房主的 Minecraft。请检查房间是否仍开放、邀请码是否更换，以及双方能否连接同一个节点。");
            try {
                if (invite.InfoPort != 0) {
                    Progress("正在读取房间信息");
                    var info = await Task.Run(() => LanModShare.ReadRoom(tunnel.ModPort, invite.Secret, ct), ct);
                    invite.LoadRoom(info);
                }
                return new Session { Invite = invite, Tunnel = tunnel };
            } catch { tunnel.Dispose(); throw; }
        });
    }

    internal Task<object> Rotate() {
        var old = current;
        if (old == null || !old.Host) throw new IOException("只有房主可以更换邀请码。");
        return Run(async ct => {
            var original = old.Invite;
            var instance = J.Map("version", original.Version, "loader", original.Loader, "loaderVersion", original.LoaderVersion);
            var next = Invitation.ForWorld(instance, original.Port, original.Token != null);
            next.Node = original.Node;
            next.PublicPeers = original.PublicPeers;
            var sharing = old.Mods;
            return await OpenHost(LanTunnel.RuntimeFolder(profile), next, old.InstanceId, old.InstanceName, old.GamePid,
                old.GameStarted, sharing == null ? null : sharing.DirectoryPath, sharing == null ? null : sharing.Choice,
                sharing == null ? null : sharing.Original, ct);
        });
    }

    void End(string reason) {
        if (operation != null) operation.Cancel();
        var room = current;
        current = null;
        timer.Stop();
        if (room != null) room.Dispose();
        notice = reason;
        Send();
    }

    internal void Leave() {
        End(current != null ? "已退出联机会话" : "");
    }

    internal void GameEnded(string id) {
        var room = current;
        if (room != null && room.InstanceId == id) {
            End("房主的游戏已退出，房间已关闭。");
        }
    }

    internal Session Guest(string id) {
        var room = current;
        if (room == null || room.Host || room.Id != id || !room.Tunnel.Running) throw new IOException("联机地址已失效，请重新加入房间。");
        return room;
    }

    internal async Task<object> CopyPlan(string id, CancellationToken ct) {
        var room = Guest(id);
        var invite = room.Invite;
        var shared = invite.ModPort == 0 ? null :
            await Task.Run(() => LanModShare.ReadManifest(room.Tunnel.ModPort, invite.Secret, invite.PackFiles, ct), ct);
        var files = shared.A("files");
        Guest(id);
        return J.Map("name", "好友房间 " + invite.Version + (invite.Loader == "vanilla" ? "" : " " + invite.Loader),
            "version", invite.Version, "loader", invite.Loader, "loaderVersion", invite.LoaderVersion,
            "files", files, "shareMods", invite.ModPort != 0, "shareKey", invite.ShareKey,
            "manifestKey", LanModShare.ManifestKey(files), "original", shared.At("original"));
    }

    internal Task CopyFiles(string shareKey, object[] files, string instance, string[] sources,
            Action<string,string,int,int> progress, CancellationToken ct) {
        return Task.Run(() => {
            for (var i = 0; i < files.Length; i++) {
                ct.ThrowIfCancellationRequested();
                var file = files[i];
                var name = LanModShare.Relative(file);
                var path = J.Inside(instance, name);
                J.NoLinks(path);
                if (!LanModShare.Matches(path, file)) {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    if (Reuse(sources, path, file, ct)) {
                        progress("download", "已复用本机文件 · " + name, i + 1, files.Length);
                        continue;
                    }
                    var room = current;
                    if (room == null || room.Host || !room.Tunnel.Running || room.Tunnel.ModPort == 0 || room.Invite.ShareKey != shareKey)
                        throw new IOException("请用原邀请码重新加入房间，再继续下载实例文件。");
                    progress("download", "正在从房主下载 · " + name, i, files.Length);
                    LanModShare.Download(room.Tunnel.ModPort, room.Invite.Secret, i, file, path, ct);
                }
                progress("download", "已校验 · " + name, i + 1, files.Length);
            }
        }, ct);
    }

    internal static string MatchingFile(string[] sources, object file, CancellationToken ct) {
        foreach (var root in sources) {
            ct.ThrowIfCancellationRequested();
            var path = J.Inside(root, LanModShare.Relative(file));
            try {
                J.NoLinks(path);
                if (LanModShare.Matches(path, file)) return path;
            } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (InvalidOperationException) { }
        }
        return null;
    }

    static bool Reuse(string[] sources, string target, object file, CancellationToken ct) {
        var source = MatchingFile(sources, file, ct);
        if (source == null) return false;
        var partial = Path.Combine(Path.GetDirectoryName(target), "." + Guid.NewGuid().ToString("N") + ".part");
        try {
            try {
                J.NoLinks(source);
                using (var input = File.OpenRead(source))
                using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    var buffer = new byte[64 * 1024];
                    int size;
                    while ((size = input.Read(buffer, 0, buffer.Length)) != 0) {
                        ct.ThrowIfCancellationRequested();
                        output.Write(buffer, 0, size);
                    }
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (InvalidOperationException) { return false; }
            if (!LanModShare.Matches(partial, file)) return false;
            ct.ThrowIfCancellationRequested();
            if (File.Exists(target)) File.Replace(partial, target, null); else File.Move(partial, target);
            return true;
        } finally { File.Delete(partial); }
    }

    async void Monitor(object sender, EventArgs e) {
        var room = current;
        if (room == null || monitoring) return;
        monitoring = true;
        try {
            if (!room.Tunnel.Running) { End("联机组件已退出。"); return; }
            if (room.Host && !await Task.Run(() => LanWorld.StillListening(room.GamePid, room.GameStarted, room.Invite.Port))) {
                if (current == room) End("局域网世界已关闭，邀请码已失效。");
                return;
            }
            if (current != room) return;
            var peers = (await room.Tunnel.Peers(CancellationToken.None)).Where(peer => peer.S("ipv4") != "" && peer.S("cost") != "Local").ToArray();
            if (current != room) return;
            if (!room.Host) peers = peers.Where(peer => peer.S("ipv4") == room.Invite.HostIp).ToArray();
            if (!room.Host && peers.Length == 0) {
                if (++room.Missing >= 4) { End("房主已断开或更换了邀请码。"); return; }
            } else room.Missing = 0;
            var direct = peers.Any(peer => peer.S("cost") == "p2p");
            var relay = peers.Any(peer => peer.S("cost").StartsWith("relay", StringComparison.OrdinalIgnoreCase));
            var state = direct ? (relay ? "直连和中转" : "直连") : relay ? "中转" : (room.Host ? "等待好友" : "正在确认连接");
            if (state == room.Connection) return;
            room.Connection = state;
            Send();
        } catch (IOException) { }
        catch (InvalidOperationException) { }
        finally { monitoring = false; }
    }
}
