using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

sealed partial class Backend
{
    string runningDownload;
    Network downloadNetwork;

    internal object DownloadTransferStatus(string id) {
        var network = downloadNetwork;
        return runningDownload == id && network != null ? network.TransferStatus() : null;
    }

    static string DownloadId(string id) {
        Guid value;
        if (!Guid.TryParseExact(id, "D", out value)) throw new IOException("下载任务标识无效。");
        return value.ToString();
    }
    string DownloadDirectory(string id) { return J.Inside(Root, "downloads/" + DownloadId(id)); }
    object ReadDownload(string id) {
        var file = J.Inside(DownloadDirectory(id), "task.json");
        J.NoLinks(file);
        var record = J.Read(file);
        if (record == null || record.S("id") != DownloadId(id) || !new[] { "game", "pack", "lan" }.Contains(record.S("kind")))
            throw new IOException("下载任务不存在或记录已损坏。");
        return record;
    }
    void SaveDownload(object record) {
        var file = J.Inside(DownloadDirectory(record.S("id")), "task.json");
        J.NoLinks(file);
        J.Save(file, record);
    }
    object FinishedDownload(object record) {
        var path = J.Inside(InstanceRecord(record.S("id")), ".ebuild.json");
        J.NoLinks(path);
        var instance = J.Read(path);
        return instance != null && (instance.S("installationId") == record.S("id") || record.S("state") == "complete")
            ? J.Merge(instance, J.Map("id", record.S("id"))) : null;
    }
    object DownloadView(object record) {
        var finished = FinishedDownload(record);
        var state = finished != null ? "complete" : record.S("state");
        if (state == "active" && runningDownload != record.S("id")) state = "paused";
        return J.Map("id", record.S("id"), "kind", record.S("kind"), "input", record.At("input"),
            "state", state, "instance", finished, "message", state == "paused" ? "下载已保留，点击继续安装。" : record.S("message"),
            "created", record.S("created"), "transfer", record.At("transfer"));
    }
    object[] DownloadList() {
        var dir = J.Inside(Root, "downloads");
        J.NoLinks(dir);
        if (!Directory.Exists(dir)) return new object[0];
        var list = new List<object>();
        foreach (var folder in Directory.EnumerateDirectories(dir)) {
            Guid id;
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0 || !Guid.TryParseExact(Path.GetFileName(folder), "D", out id)) continue;
            list.Add(DownloadView(ReadDownload(id.ToString())));
        }
        return list.OrderByDescending(x => x.S("created")).ToArray();
    }
    object NewDownload(string id, string kind, object input, object remote = null, string source = "", object expected = null) {
        var record = J.Map("id", id, "kind", kind, "input", input, "state", "paused", "source", source,
            "remote", remote, "created", DateTime.UtcNow.ToString("o"), "message", "等待继续安装");
        if (expected != null) record.Add("expected", expected);
        if (Directory.Exists(DownloadDirectory(id)) || Directory.Exists(InstanceRecord(id))) throw new IOException("该下载任务已存在，请从下载任务中继续。");
        SaveDownload(record);
        return record;
    }
    Task<object> CreateDownload(object input, string id) {
        var spec = Spec(input);
        id = id == "" ? Guid.NewGuid().ToString() : DownloadId(id);
        return Job((n,p,t) => RunDownload(NewDownload(id, "game", spec), n,p,t), "game-install");
    }
    internal Task<object> CreatePackDownload(string file, object remote, string title, object expected = null) {
        var input = file == "" ? J.Map("name", title, "loader", "vanilla") : Spec(Resources.PackSpec(Resources.ReadPackIndex(file)));
        return Job((n,p,t) => RunDownload(NewDownload(Guid.NewGuid().ToString(), "pack", input, remote, file, expected), n,p,t), "game-install");
    }

    object OriginalPack(object instance) {
        var id = instance.S("installationId");
        if (id == "" || id != instance.S("id")) return null;
        try {
            var record = ReadDownload(id);
            return record.S("kind") == "pack" ? LanPackFiles.Original(record.At("remote")) : null;
        } catch (IOException) { return null; }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    string[] LanFileSources(object spec) {
        return Instances().Where(item => !item.B("externalMissing") && item.S("version") == spec.S("version") &&
            item.S("loader") == spec.S("loader") && item.S("loaderVersion") == spec.S("loaderVersion"))
            .Select(item => InstancePath(item.S("id"))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    async Task<object> DescribeLanDownload(string room) {
        var plan = await multiplayer.CopyPlan(room, CancellationToken.None);
        var files = plan.A("files");
        if (files.Length == 0) return plan;
        var sources = LanFileSources(plan);
        var preview = await Task.Run(() => files.Select(file => J.Merge(file,
            J.Map("reuse", LanRooms.MatchingFile(sources, file, CancellationToken.None) != null))).ToArray());
        multiplayer.Guest(room);
        return J.Merge(plan, J.Map("files", preview));
    }

    Task<object> CreateLanDownload(string room, string manifestKey) {
        return Job(async (net, progress, ct) => {
            var plan = await multiplayer.CopyPlan(room, ct);
            if (plan.S("manifestKey") != manifestKey) throw new IOException("房间的共享清单有变化，请重新查看后下载。");
            var input = Spec(plan);
            var files = plan.A("files");
            var record = NewDownload(Guid.NewGuid().ToString(), "lan", input, files, plan.S("shareKey"));
            return await RunDownload(record, net, progress, ct);
        }, "game-install");
    }

    async Task<object> CreateLanOriginal(string room, string confirmedHash) {
        var plan = await multiplayer.CopyPlan(room, CancellationToken.None);
        var original = plan.At("original");
        if (original == null || original.S("sha512") != confirmedHash) throw new IOException("房主的原版整合包信息已变化，请重新查看。");
        return await CreatePackDownload("", original, "好友房间 · 原版整合包", Spec(plan));
    }
    Task<object> ResumeDownload(string id) {
        return Job((n,p,t) => RunDownload(ReadDownload(id), n,p,t), "game-install");
    }
    async Task<object> RunDownload(object record, Network net, Action<string,string,int,int> progress, CancellationToken ct) {
        var id = record.S("id");
        var existing = FinishedDownload(record);
        if (existing != null) return existing;
        if (record.S("state") == "complete") throw new IOException("此任务已完成，但实例已被移除。请重新新建实例。");
        var elapsed = Stopwatch.StartNew();
        var previousTransfer = record.At("transfer");
        Func<object> report = () => {
            var current = net.TransferStatus();
            return J.Map("switch", current.S("switch") == "" ? previousTransfer.S("switch") : current.S("switch"),
                "networkMs", Convert.ToInt64(current.At("networkMs")) + Convert.ToInt64(previousTransfer.At("networkMs")),
                "activeMs", elapsed.ElapsedMilliseconds + Convert.ToInt64(previousTransfer.At("activeMs")),
                "bytes", Convert.ToInt64(current.At("bytes")) + Convert.ToInt64(previousTransfer.At("bytes")));
        };
        record = J.Merge(record, J.Map("state", "active", "message", "正在准备安装…"));
        SaveDownload(record);
        runningDownload = id;
        downloadNetwork = net;
        try {
            notify(J.Map("downloadsChanged", true));
            object result;
            if (record.S("kind") == "pack") {
                var archive = J.Inside(DownloadDirectory(id), "pack.mrpack");
                J.NoLinks(archive);
                if (record.At("remote") != null) {
                    progress("download", "正在下载整合包", 0, 0);
                    await net.FileTo(record.At("remote"), archive, ct);
                } else if (!File.Exists(archive)) {
                    var source = PackFile(record.S("source"));
                    var temporary = archive + ".copy";
                    J.NoLinks(temporary);
                    try {
                        await Task.Run(() => CopyChecked(source, temporary, ct), ct);
                        File.Move(temporary, archive);
                    } finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                var packSpec = Spec(Resources.PackSpec(Resources.ReadPackIndex(archive)));
                var expected = record.At("expected");
                if (expected != null && new[] { "version", "loader", "loaderVersion" }.Any(key => packSpec.S(key) != expected.S(key)))
                    throw new IOException("原版整合包的游戏或加载器版本与房间不一致。请下载房主分享的实例文件。");
                record = J.Merge(record, J.Map("input", packSpec));
                SaveDownload(record);
                notify(J.Map("downloadsChanged", true));
                result = await resources.Pack(archive, net, progress, ct, id);
            } else if (record.S("kind") == "lan") {
                var files = LanModShare.Validate(record.At("remote"));
                result = await Add(record.At("input"), net, progress, ct,
                    files.Length == 0 ? (Func<string,Task>)null : dir => multiplayer.CopyFiles(record.S("source"), files, dir,
                        LanFileSources(record.At("input")), progress, ct),
                    downloadId: id);
            } else result = await Add(record.At("input"), net, progress, ct, downloadId: id);
            SaveDownload(J.Merge(record, J.Map("state", "complete", "message", "安装完成", "transfer", report())));
            // Completed packs no longer need their private source copy. Keep only the task record.
            var packed = J.Inside(DownloadDirectory(id), "pack.mrpack");
            try { J.NoLinks(packed); File.Delete(packed); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return result;
        } catch (Exception error) {
            SaveDownload(J.Merge(record, J.Map("state", ct.IsCancellationRequested ? "paused" : "failed",
                "message", error.Message, "transfer", report())));
            throw;
        } finally {
            runningDownload = null;
            downloadNetwork = null;
            notify(J.Map("downloadsChanged", true));
        }
    }
    Task<object> RemoveDownload(string id) {
        return Job((n,p,t) => Task.Run<object>(() => {
            ReadDownload(id);
            var dir = DownloadDirectory(id);
            ExternalInstances.EnsureDeleteTree(dir, t);
            t.ThrowIfCancellationRequested();
            Directory.Delete(dir, true);
            return true;
        }, t));
    }
}
