using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

// A room-scoped listener for room metadata and shared files. Legacy clients see only the leading mod entries.
sealed class LanModShare : IDisposable
{
    internal const long MaxBytes = 4L * 1024 * 1024 * 1024;
    const int MaxManifest = 1024 * 1024;
    const int Manifest = -1;
    const int PackManifest = -2;
    const int RoomInfo = -3;
    const int MaxFiles = 4096;
    readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stop = new CancellationTokenSource();
    readonly string secret;
    readonly string[] files;
    readonly object[] manifest;
    readonly byte[] manifestBytes;
    readonly byte[] legacyBytes;
    readonly byte[] roomBytes;
    int clients;

    internal readonly string DirectoryPath;
    internal readonly object Choice, Original;
    internal int Port { get { return ((IPEndPoint)listener.LocalEndpoint).Port; } }
    internal int Count { get { return manifest.Length; } }

    internal LanModShare(string instance, string secret, CancellationToken ct, object choice, object original, object room) {
        this.secret = secret;
        DirectoryPath = instance;
        Choice = choice;
        Original = LanPackFiles.Original(original);
        roomBytes = room == null ? null : Encoding.UTF8.GetBytes(J.Dump(room));
        var paths = (instance == null ? Enumerable.Empty<string>() : LanPackFiles.List(instance, choice, ct))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxFiles + 1)
            .OrderBy(path => !path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        var mods = paths.Count(path => LanPackFiles.Group(path) == "模组");
        if (paths.Length > MaxFiles || mods > 2048) throw new IOException("当前实例的文件数量超过联机共享上限。");
        files = paths.Select(path => J.Inside(instance, path)).ToArray();
        manifest = new object[files.Length];
        long total = 0;
        for (var i = 0; i < files.Length; i++) {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            J.NoLinks(file);
            using (var input = File.OpenRead(file))
            using (var sha = SHA256.Create()) {
                if (input.Length > MaxBytes - total || (i < mods && input.Length == 0))
                    throw new IOException("空模组或共享文件超出大小上限，请检查共享内容。");
                total += input.Length;
                manifest[i] = J.Map("name", Path.GetFileName(file), "size", input.Length, "sha256", Network.Hex(sha.ComputeHash(input)));
                if (i >= mods) manifest[i].Obj().Add("path", paths[i]);
            }
        }
        Validate(manifest);
        legacyBytes = Encoding.UTF8.GetBytes(J.Dump(manifest.Take(mods).ToArray()));
        manifestBytes = Encoding.UTF8.GetBytes(J.Dump(J.Map("files", manifest, "original", Original)));
        if (manifestBytes.Length > MaxManifest) throw new IOException("联机共享清单过大。");
        listener.Start();
        var token = stop.Token;
        Task.Run(() => Listen(token));
    }

    static void Configure(TcpClient client) {
        client.ReceiveTimeout = 15000;
        client.SendTimeout = 15000;
    }

    async Task Listen(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { return; }
            if (Interlocked.Increment(ref clients) <= 8) StartClient(client, ct);
            else { client.Dispose(); Interlocked.Decrement(ref clients); }
        }
    }

    void StartClient(TcpClient client, CancellationToken ct) { Task.Run(() => Serve(client, ct)); }

    void Serve(TcpClient client, CancellationToken ct) {
        try {
            using (client)
            using (ct.Register(client.Close)) {
                Configure(client);
                using (var stream = client.GetStream())
                using (var read = new BinaryReader(stream, Encoding.UTF8, true))
                using (var write = new BinaryWriter(stream, Encoding.UTF8, true)) {
                    if (Encoding.ASCII.GetString(read.ReadBytes(64)) != secret) return;
                    int index = read.ReadInt32();
                    if (index == Manifest || index == PackManifest || index == RoomInfo) {
                        var payload = index == RoomInfo ? roomBytes : index == Manifest ? legacyBytes : manifestBytes;
                        if (payload == null || (index != RoomInfo && DirectoryPath == null)) return;
                        write.Write((long)payload.Length);
                        write.Write(payload);
                    } else if (index >= 0 && index < files.Length) {
                        var file = files[index];
                        J.NoLinks(file);
                        using (var input = File.OpenRead(file)) {
                            if (input.Length != Size(manifest[index])) return;
                            write.Write(input.Length);
                            input.CopyTo(stream);
                        }
                    }
                }
            }
        } catch (IOException) { }
        catch (SocketException) { }
        catch (UnauthorizedAccessException) { }
        catch (InvalidOperationException) { }
        finally { Interlocked.Decrement(ref clients); }
    }

    internal static long Size(object file) { long size; return long.TryParse(file.S("size"), out size) ? size : -1; }
    internal static string Relative(object file) { return file.S("path") == "" ? "mods/" + file.S("name") : file.S("path"); }
    internal static string ManifestKey(object[] files) {
        using (var sha = SHA256.Create()) return Network.Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(J.Dump(files))));
    }

    internal static object[] Validate(object value) {
        if (!(value is System.Collections.IList)) throw new IOException("房主的共享清单格式无效。");
        var result = value.A();
        if (result.Length > MaxFiles) throw new IOException("房主的共享清单过大。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        for (var i = 0; i < result.Length; i++) {
            var file = result[i];
            var name = file.S("name");
            var path = Relative(file);
            var group = LanPackFiles.Group(path);
            var size = Size(file);
            if (group == "" || name != Path.GetFileName(path) || !names.Add(path) ||
                !Regex.IsMatch(file.S("sha256"), "^[a-f0-9]{64}$") || size < 0 || (size == 0 && group == "模组") || size > MaxBytes - total)
                throw new IOException("房主的共享清单中有无效文件。");
            total += size;
            result[i] = J.Map("name", name, "size", size, "sha256", file.S("sha256"));
            if (group != "模组") result[i].Obj().Add("path", path);
        }
        foreach (var path in names) {
            for (var split = path.LastIndexOf('/'); split > 0; split = path.LastIndexOf('/', split - 1))
                if (names.Contains(path.Substring(0, split))) throw new IOException("房主的共享清单中有冲突路径。");
        }
        return result;
    }

    static T Receive<T>(int port, string secret, int index, CancellationToken ct, Func<BinaryReader, T> receive) {
        try {
            using (var client = new TcpClient(AddressFamily.InterNetwork))
            using (ct.Register(client.Close)) {
                Configure(client);
                client.Connect(IPAddress.Loopback, port);
                using (var stream = client.GetStream())
                using (var write = new BinaryWriter(stream, Encoding.UTF8, true))
                using (var read = new BinaryReader(stream, Encoding.UTF8, true)) {
                    write.Write(Encoding.ASCII.GetBytes(secret));
                    write.Write(index);
                    write.Flush();
                    return receive(read);
                }
            }
        } catch (Exception error) {
            if (!(error is IOException) && !(error is SocketException) && !(error is ObjectDisposedException)) throw;
            ct.ThrowIfCancellationRequested();
            throw new IOException("读取房主文件失败。请确认房间仍开放，再重试。" + (error is SocketException ? "" : " " + error.Message));
        }
    }

    internal static object ReadManifest(int port, string secret, bool expanded, CancellationToken ct) {
        return Receive(port, secret, expanded ? PackManifest : Manifest, ct, read => {
            var payload = ReadPayload(read, MaxManifest);
            return J.Map("files", Validate(expanded ? payload.At("files") : payload),
                "original", expanded ? LanPackFiles.Original(payload.At("original")) : null);
        });
    }

    internal static object ReadRoom(int port, string secret, CancellationToken ct) {
        return Receive(port, secret, RoomInfo, ct, read => ReadPayload(read, 1024));
    }

    static object ReadPayload(BinaryReader read, int limit) {
        long size = read.ReadInt64();
        if (size < 2 || size > limit) throw new IOException("房主发送的联机信息大小无效。");
        var bytes = read.ReadBytes((int)size);
        if (bytes.Length != size) throw new IOException("房主的联机信息传输中断。");
        return J.Parse(Encoding.UTF8.GetString(bytes));
    }

    internal static void Download(int port, string secret, int index, object file, string path, CancellationToken ct) {
        var partial = Path.Combine(Path.GetDirectoryName(path), "." + Guid.NewGuid().ToString("N") + ".part");
        J.NoLinks(partial);
        try {
            Receive(port, secret, index, ct, read => {
                var remaining = read.ReadInt64();
                if (remaining != Size(file)) throw new IOException("房主的文件已变化，请让房主重新创建房间。");
                using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                    var buffer = new byte[64 * 1024];
                    while (remaining > 0) {
                        ct.ThrowIfCancellationRequested();
                        var count = read.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (count == 0) throw new IOException("文件传输中断。");
                        output.Write(buffer, 0, count);
                        remaining -= count;
                    }
                }
                return true;
            });
            ct.ThrowIfCancellationRequested();
            if (!Matches(partial, file)) throw new IOException("文件校验失败：" + Relative(file) + "。请让房主重新创建房间。");
            J.NoLinks(path);
            if (File.Exists(path)) File.Replace(partial, path, null); else File.Move(partial, path);
        } finally { File.Delete(partial); }
    }

    internal static bool Matches(string path, object file) {
        if (Size(file) != 0) return Network.Check(path, file);
        var info = new FileInfo(path);
        return info.Exists && info.Length == 0 && file.S("sha256") ==
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    }

    public void Dispose() {
        stop.Cancel();
        listener.Stop();
        stop.Dispose();
    }
}
