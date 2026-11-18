using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

static class LanWorld
{
    static readonly Regex Opened = new Regex(@"\b(?:Started serving on|Started on port|Local game hosted on port)\s+(\d{1,5})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static int PublishedPort(string directory, DateTime started, int pid) {
        foreach (var name in new[] { "latest.log", "ebuild-launch.log" }) {
            try {
                var file = new FileInfo(LaunchDiagnostics.LogPath(directory, name));
                if (!file.Exists || file.LastWriteTimeUtc < started) continue;
                using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                    var size = (int)Math.Min(192 * 1024, stream.Length);
                    stream.Seek(-size, SeekOrigin.End);
                    var bytes = new byte[size];
                    var read = 0;
                    while (read < size) {
                        var count = stream.Read(bytes, read, size - read);
                        if (count == 0) break;
                        read += count;
                    }
                    var matches = Opened.Matches(Encoding.UTF8.GetString(bytes, 0, read));
                    if (matches.Count == 0) continue;
                    var port = Int32.Parse(matches[matches.Count - 1].Groups[1].Value);
                    if (port > 0 && port <= 65535 && GameListener(pid, started, port) != 0) return port;
                }
            } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (InvalidOperationException) { }
        }
        return 0;
    }

    [DllImport("iphlpapi.dll")]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool ordered, int family, int tableClass, uint reserved);

    static bool HasListener(int family, int pid, int port, int rowSize, int portOffset, int pidOffset) {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
        for (int attempt = 0; attempt < 3 && size >= 4 && size <= 8 * 1024 * 1024; attempt++) {
            var capacity = size;
            var table = Marshal.AllocHGlobal(capacity);
            try {
                var status = GetExtendedTcpTable(table, ref size, false, family, 3, 0);
                if (status == 122) continue;
                if (status != 0) return false;
                var count = Math.Min(Marshal.ReadInt32(table), (capacity - 4) / rowSize);
                for (int index = 0; index < count; index++) {
                    var offset = 4 + index * rowSize;
                    if (Marshal.ReadInt32(table, offset + pidOffset) == pid &&
                        (ushort)IPAddress.NetworkToHostOrder(Marshal.ReadInt16(table, offset + portOffset)) == port) return true;
                }
                return false;
            } finally { Marshal.FreeHGlobal(table); }
        }
        return false;
    }

    internal static bool OwnsPort(int pid, int port) {
        return HasListener(2, pid, port, 24, 8, 20) || HasListener(23, pid, port, 56, 20, 52);
    }

    internal static bool StillListening(int pid, DateTime started, int port) {
        try {
            using (var process = Process.GetProcessById(pid))
                return !process.HasExited && process.StartTime.ToUniversalTime() == started && OwnsPort(pid, port);
        } catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    internal static int GameListener(int pid, DateTime started, int port) {
        if (OwnsPort(pid, port)) return pid;
        // The Oracle Java launcher can wait for a child JVM that owns the actual game sockets.
        try {
            using (var search = new ManagementObjectSearcher("SELECT ProcessId FROM Win32_Process WHERE ParentProcessId = " + pid +
                " AND (Name = 'java.exe' OR Name = 'javaw.exe')"))
            using (var children = search.Get()) {
                foreach (ManagementObject child in children) {
                    using (child) {
                        var id = Convert.ToInt32(child["ProcessId"]);
                        if (!OwnsPort(id, port)) continue;
                        using (var process = Process.GetProcessById(id)) {
                            if (!process.HasExited && process.StartTime.ToUniversalTime() >= started) return id;
                        }
                    }
                }
            }
        } catch (ManagementException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (InvalidOperationException) { }
        catch (ArgumentException) { }
        return 0;
    }

    internal static int FreeLoopbackPort() {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    static int ReadVarInt(Stream input) {
        var result = 0;
        for (var i = 0; i < 5; i++) {
            var value = input.ReadByte();
            if (value < 0) throw new EndOfStreamException();
            result |= (value & 127) << (7 * i);
            if ((value & 128) == 0) return result;
        }
        throw new IOException("无效的游戏状态响应。");
    }

    static void WriteVarInt(Stream output, int value) {
        var remaining = (uint)value;
        while (remaining >= 128) {
            output.WriteByte((byte)(remaining | 128));
            remaining >>= 7;
        }
        output.WriteByte((byte)remaining);
    }

    internal static bool Responds(int port) {
        try {
            using (var client = new TcpClient(AddressFamily.InterNetwork)) {
                client.ReceiveTimeout = 900;
                client.SendTimeout = 900;
                client.Connect(IPAddress.Loopback, port);
                var time = Stopwatch.StartNew();
                using (var stream = client.GetStream())
                using (var handshake = new MemoryStream()) {
                    handshake.WriteByte(0);
                    WriteVarInt(handshake, -1);
                    var hostname = Encoding.ASCII.GetBytes("127.0.0.1");
                    WriteVarInt(handshake, hostname.Length);
                    handshake.Write(hostname, 0, hostname.Length);
                    handshake.WriteByte((byte)(port >> 8));
                    handshake.WriteByte((byte)port);
                    handshake.WriteByte(1);
                    WriteVarInt(stream, (int)handshake.Length);
                    handshake.WriteTo(stream);
                    stream.WriteByte(1);
                    stream.WriteByte(0);
                    var length = ReadVarInt(stream);
                    if (length < 3 || length > 128 * 1024) return false;
                    var bytes = new byte[length];
                    var read = 0;
                    while (read < length && time.ElapsedMilliseconds < 1800) {
                        var count = stream.Read(bytes, read, length - read);
                        if (count == 0) return false;
                        read += count;
                    }
                    if (read != length) return false;
                    using (var packet = new MemoryStream(bytes)) {
                        if (ReadVarInt(packet) != 0) return false;
                        var textBytes = ReadVarInt(packet);
                        if (textBytes <= 0 || textBytes != packet.Length - packet.Position) return false;
                        return J.Parse(Encoding.UTF8.GetString(bytes, (int)packet.Position, textBytes)).At("version") != null;
                    }
                }
            }
        } catch (IOException) { return false; }
        catch (SocketException) { return false; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
