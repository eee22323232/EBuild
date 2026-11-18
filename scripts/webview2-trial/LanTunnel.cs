using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

sealed class LanTunnel : IDisposable
{
    const string Version = "2.6.4";
    const string CoreHash = "da7eb2d24b5416f3d3407636949e964a0750e3f9dc53a828cb6799a57ead445d";
    const string CliHash = "d8783e851e944b44a9b71b39fd02f227ec0a2a82b3165c55ead5dd32dcde53a1";
    const string PacketHash = "c7c03a87eac7243ccbe331554624b18803010b740e311fc8cfddb573096eacac";
    const string DreamLifeTcp = "tcp://dreamlife.indevs.in:11010";
    const string DreamLifeUdp = "udp://dreamlife.indevs.in:11010";
    internal static readonly string[] PublicNodes = {
        // The operator publishes both transports for use as a shared node.
        DreamLifeTcp,
        "tcp://public.easytier.cn:11010",
        "tcp://public.easytier.top:11010"
    };
    internal static bool IsPublicNode(string node) { return node == DreamLifeUdp || PublicNodes.Contains(node); }

    static string PeersFor(string node) {
        return node == DreamLifeTcp || node == DreamLifeUdp ? DreamLifeTcp + " " + DreamLifeUdp : node;
    }
    readonly Process process;
    readonly SafeFileHandle job;
    readonly string folder;
    readonly int rpcPort;
    internal readonly int LocalPort;

    internal static string NormalizeNode(string text) {
        var value = text.Trim();
        Uri uri;
        if (!Regex.IsMatch(value, @"^(?:tcp|udp)://(?:[a-zA-Z0-9.-]{1,253}|\[[a-fA-F0-9:]+\]):\d{1,5}$") ||
            !Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Port < 1 || uri.Port > 65535 || String.IsNullOrEmpty(uri.Host))
            throw new IOException("节点格式应为 tcp://主机:端口 或 udp://主机:端口。");
        return uri.Scheme + "://" + uri.Host + ":" + uri.Port;
    }

    internal static string RuntimeFolder(string profile) { return J.Inside(profile, "multiplayer/easytier-" + Version); }

    static bool Verified(string path, string hash) {
        J.NoLinks(path);
        return Network.Check(path, J.Map("sha256", hash));
    }

    internal static async Task<string> Prepare(string profile, Network network, Action<string> progress, CancellationToken ct) {
        var runtime = RuntimeFolder(profile);
        var core = J.Inside(runtime, "easytier-core.exe");
        var cli = J.Inside(runtime, "easytier-cli.exe");
        var packet = J.Inside(runtime, "Packet.dll");
        if (Verified(core, CoreHash) && Verified(cli, CliHash) && Verified(packet, PacketHash)) return runtime;
        Directory.CreateDirectory(runtime);
        var archiveName = "easytier-windows-x86_64-v" + Version + ".zip";
        var archive = J.Inside(runtime, archiveName);
        progress("正在下载联机组件 · 约 33 MB");
        await network.FileTo(J.Map("url", "https://github.com/EasyTier/EasyTier/releases/download/v" + Version + "/" + archiveName,
            "size", 32657720, "sha256", "27af91e270e554709b048bd32327fefd2dfce5062ae1e8701af7550c6f525f84"), archive, ct);
        ct.ThrowIfCancellationRequested();
        progress("正在校验联机组件");
        using (var zip = ZipFile.OpenRead(archive)) {
            Extract(zip, core, CoreHash);
            Extract(zip, cli, CliHash);
            Extract(zip, packet, PacketHash);
        }
        File.Delete(archive);
        return runtime;
    }

    static void Extract(ZipArchive zip, string target, string hash) {
        var entry = zip.Entries.SingleOrDefault(item => item.Name == Path.GetFileName(target));
        if (entry == null) throw new IOException("联机组件不完整。");
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".part";
        try {
            entry.ExtractToFile(temp);
            if (!Verified(temp, hash)) throw new IOException("联机组件的文件校验失败。");
            J.NoLinks(target);
            if (File.Exists(target)) File.Replace(temp, target, null);
            else File.Move(temp, target);
        } finally { File.Delete(temp); }
    }

    static ProcessStartInfo StartInfo(string folder, string name, string args) {
        var info = new ProcessStartInfo(J.Inside(folder, name), args) {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var key in info.EnvironmentVariables.Keys.Cast<string>().ToArray())
            if (key.StartsWith("ET_", StringComparison.OrdinalIgnoreCase) || key.Equals("RUST_LOG", StringComparison.OrdinalIgnoreCase))
                info.EnvironmentVariables.Remove(key);
        return info;
    }

    internal LanTunnel(string folder, string node, string network, string secret, string hostIp, int worldPort, bool host) {
        this.folder = folder;
        rpcPort = LanWorld.FreeLoopbackPort();
        do { LocalPort = host ? 0 : LanWorld.FreeLoopbackPort(); } while (LocalPort == rpcPort);
        var ownIp = host ? hostIp : hostIp.Substring(0, hostIp.Length - 1) + "2";
        var args = "--no-tun --bind-device false --disable-upnp --listeners udp://0.0.0.0:0" +
            " --relay-network-whitelist --udp-whitelist 0 --rpc-portal 127.0.0.1:" + rpcPort +
            " --ipv4 " + ownIp + "/24 --hostname " + (host ? "EBuild-Host" : "EBuild-Guest") +
            " --tcp-whitelist " + (host ? worldPort : 0) + " --peers " + PeersFor(node);
        if (!host) args += " --dhcp --port-forward tcp://127.0.0.1:" + LocalPort + "/" + hostIp + ":" + worldPort;
        var info = StartInfo(folder, "easytier-core.exe", args);
        info.EnvironmentVariables["ET_NETWORK_NAME"] = network;
        info.EnvironmentVariables["ET_NETWORK_SECRET"] = secret;
        job = NewJob();
        try {
            process = Process.Start(info);
            if (!AssignProcessToJobObject(job, process.Handle)) throw new IOException("无法绑定联机进程，已停止。");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        } catch { Dispose(); throw; }
    }

    internal bool Running {
        get { try { return !process.HasExited; } catch (InvalidOperationException) { return false; } }
    }

    internal async Task<object[]> Peers(CancellationToken ct) {
        if (!Running) return new object[0];
        try {
            using (var cli = Process.Start(StartInfo(folder, "easytier-cli.exe", "-p 127.0.0.1:" + rpcPort + " -o json peer"))) {
                var read = cli.StandardOutput.ReadToEndAsync();
                cli.BeginErrorReadLine();
                try {
                    if (await Task.WhenAny(read, Task.Delay(1800, ct)) != read) return new object[0];
                    var value = await read;
                    return value.Length == 0 || value.Length > 128 * 1024 ? new object[0] : J.Parse(value).A();
                } finally { Kill(cli); }
            }
        } catch (IOException) { return new object[0]; }
        catch (ArgumentException) { return new object[0]; }
        catch (InvalidOperationException) { return new object[0]; }
    }

    static void Kill(Process child) {
        try { if (child != null && !child.HasExited) child.Kill(); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void Dispose() {
        Kill(process);
        job.Dispose();
        if (process != null) process.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JobLimits {
        internal long ProcessTime, JobTime;
        internal uint Flags;
        internal UIntPtr MinimumMemory, MaximumMemory;
        internal uint ProcessLimit;
        internal UIntPtr Affinity;
        internal uint Priority, SchedulingClass;
        internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        internal UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeFileHandle CreateJobObject(IntPtr attributes, IntPtr name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    static SafeFileHandle NewJob() {
        var handle = CreateJobObject(IntPtr.Zero, IntPtr.Zero);
        var limits = new JobLimits { Flags = 0x2000 }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (!handle.IsInvalid && SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf(typeof(JobLimits)))) return handle;
        handle.Dispose();
        throw new IOException("无法为联机组件设置进程清理。");
    }
}
