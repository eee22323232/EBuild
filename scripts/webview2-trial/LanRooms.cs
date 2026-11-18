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
    internal sealed class Invitation {
        const string Prefix = "EB1-";
        internal string Secret, Node, Version, Loader, LoaderVersion;
        internal int Port;
        internal string NetworkName { get { return "ebuild-" + Secret.Substring(0, 24); } }
        internal string HostIp { get { return "10.234." + Convert.ToInt32(Secret.Substring(24, 2), 16) + ".1"; } }
        internal string Encode() {
            var payload = J.Dump(J.Map("s", Secret, "n", Node, "v", Version, "l", Loader, "r", LoaderVersion, "p", Port));
            return Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        internal static Invitation Decode(string text) {
            var input = text.Trim();
            if (input.Length > 1200 || !input.StartsWith(Prefix, StringComparison.Ordinal) ||
                !Regex.IsMatch(input.Substring(Prefix.Length), @"^[A-Za-z0-9_-]+$"))
                throw new IOException("邀请码无效。请复制房主提供的完整 EBuild 邀请码。");
            try {
                var value = input.Substring(Prefix.Length).Replace('-', '+').Replace('_', '/');
                var data = J.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '='))));
                var loader = data.S("l");
                var port = data.I("p");
                if (port < 1 || port > 65535 || !Minecraft.SupportsLoader(loader) || !Regex.IsMatch(data.S("s"), @"^[a-f0-9]{64}$"))
                    throw new FormatException();
                return new Invitation { Secret = data.S("s"), Node = LanTunnel.NormalizeNode(data.S("n")), Version = J.Id(data.S("v")),
                    Loader = loader, LoaderVersion = data.S("r") == "" ? "" : J.Id(data.S("r")), Port = port };
            } catch (ArgumentException) { }
            catch (FormatException) { }
            catch (InvalidOperationException) { }
            catch (IOException) { }
            throw new IOException("邀请码无效。请复制房主提供的完整 EBuild 邀请码。");
        }
        internal bool Matches(object instance) {
            return Version == instance.S("version") && Loader == instance.S("loader") &&
                (LoaderVersion == "" || LoaderVersion == instance.S("loaderVersion"));
        }
        internal static Invitation ForWorld(object instance, int port) {
            var token = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(token);
            return new Invitation { Secret = Network.Hex(token), Version = instance.S("version"), Loader = instance.S("loader"),
                LoaderVersion = instance.S("loaderVersion"), Port = port };
        }
    }

    internal sealed class Session {
        internal readonly string Id = Guid.NewGuid().ToString("N");
        internal Invitation Invite;
        internal LanTunnel Tunnel;
        internal string InstanceId, InstanceName;
        internal int GamePid, Missing;
        internal DateTime GameStarted;
        internal string Connection = "";
        internal bool Host { get { return InstanceId != null; } }
        internal string Address { get { return "127.0.0.1:" + Tunnel.LocalPort; } }
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
                "invite", room.Host ? room.Invite.Encode() : "", "address", room.Host ? "" : room.Address));
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
                if (old != null) old.Tunnel.Dispose();
                timer.Start();
                return true;
            } catch (OperationCanceledException) {
                if (!source.IsCancellationRequested) throw;
                return false;
            }
            finally {
                if (next != null) next.Tunnel.Dispose();
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

    static async Task<LanTunnel> Connect(string runtime, Invitation invite, bool host, int limitSeconds, CancellationToken ct, bool fallback = false) {
        LanTunnel tunnel = null;
        try {
            tunnel = new LanTunnel(runtime, invite.Node, invite.NetworkName, invite.Secret, invite.HostIp, invite.Port, host);
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
            if (!tunnel.Running && !fallback) throw new IOException("联机组件提前退出，请检查节点设置后重试。");
            return null;
        } finally { if (tunnel != null) tunnel.Dispose(); }
    }

    async Task<Session> OpenHost(string runtime, Invitation invite, string id, string name, int pid, DateTime started, string customNode, CancellationToken ct) {
        foreach (var node in customNode == "" ? LanTunnel.PublicNodes : new[] { customNode }) {
            ct.ThrowIfCancellationRequested();
            invite.Node = node;
            Progress("正在连接联机节点 · " + new Uri(node).Host);
            var tunnel = await Connect(runtime, invite, true, 7, ct, customNode == "");
            if (tunnel != null) return new Session { InstanceId = id, InstanceName = name, GamePid = pid, GameStarted = started, Invite = invite, Tunnel = tunnel };
        }
        throw new IOException("无法与联机节点握手。可以在联机设置更换节点，房间尚未开放。");
    }

    internal Task<object> Create(object instance, GameProcesses.Game game, int port) {
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
            var invite = Invitation.ForWorld(instance, world.Item1);
            return await OpenHost(await Runtime(ct), invite, instance.S("id"), instance.S("name"), world.Item2, world.Item3, Node, ct);
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
            return new Session { Invite = invite, Tunnel = tunnel };
        });
    }

    internal Task<object> Rotate() {
        var old = current;
        if (old == null || !old.Host) throw new IOException("只有房主可以更换邀请码。");
        return Run(async ct => {
            var original = old.Invite;
            var next = Invitation.ForWorld(J.Map("version", original.Version, "loader", original.Loader, "loaderVersion", original.LoaderVersion), original.Port);
            return await OpenHost(LanTunnel.RuntimeFolder(profile), next, old.InstanceId, old.InstanceName, old.GamePid, old.GameStarted, original.Node, ct);
        });
    }

    void End(string reason) {
        if (operation != null) operation.Cancel();
        var room = current;
        current = null;
        timer.Stop();
        if (room != null) room.Tunnel.Dispose();
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
