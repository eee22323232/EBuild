using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

sealed partial class Backend
{
    string runningDownload;

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
        if (record == null || record.S("id") != DownloadId(id) || !new[] { "game", "pack" }.Contains(record.S("kind")))
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
            "created", record.S("created"));
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
    object NewDownload(string id, string kind, object input, object remote = null, string source = "") {
        var record = J.Map("id", id, "kind", kind, "input", input, "state", "paused", "source", source,
            "remote", remote, "created", DateTime.UtcNow.ToString("o"), "message", "等待继续安装");
        if (Directory.Exists(DownloadDirectory(id)) || Directory.Exists(InstanceRecord(id))) throw new IOException("该下载任务已存在，请从下载任务中继续。");
        SaveDownload(record);
        return record;
    }
    Task<object> CreateDownload(object input, string id) {
        var spec = Spec(input);
        id = id == "" ? Guid.NewGuid().ToString() : DownloadId(id);
        return Job((n,p,t) => RunDownload(NewDownload(id, "game", spec), n,p,t), "game-install");
    }
    internal Task<object> CreatePackDownload(string file, object remote, string title) {
        var input = file == "" ? J.Map("name", title, "loader", "vanilla") : Spec(Resources.PackSpec(Resources.ReadPackIndex(file)));
        return Job((n,p,t) => RunDownload(NewDownload(Guid.NewGuid().ToString(), "pack", input, remote, file), n,p,t), "game-install");
    }
    Task<object> ResumeDownload(string id) {
        return Job((n,p,t) => RunDownload(ReadDownload(id), n,p,t), "game-install");
    }
    async Task<object> RunDownload(object record, Network net, Action<string,string,int,int> progress, CancellationToken ct) {
        var id = record.S("id");
        var existing = FinishedDownload(record);
        if (existing != null) return existing;
        if (record.S("state") == "complete") throw new IOException("此任务已完成，但实例已被移除。请重新新建实例。");
        runningDownload = id;
        record = J.Merge(record, J.Map("state", "active", "message", "正在准备安装…"));
        SaveDownload(record);
        notify(J.Map("downloadsChanged", true));
        try {
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
                record = J.Merge(record, J.Map("input", Spec(Resources.PackSpec(Resources.ReadPackIndex(archive)))));
                SaveDownload(record);
                notify(J.Map("downloadsChanged", true));
                result = await resources.Pack(archive, net, progress, ct, id);
            } else result = await Add(record.At("input"), net, progress, ct, downloadId: id);
            SaveDownload(J.Merge(record, J.Map("state", "complete", "message", "安装完成")));
            // Completed packs no longer need their private source copy. Keep only the task record.
            var packed = J.Inside(DownloadDirectory(id), "pack.mrpack");
            try { J.NoLinks(packed); File.Delete(packed); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return result;
        } catch (Exception error) {
            SaveDownload(J.Merge(record, J.Map("state", ct.IsCancellationRequested ? "paused" : "failed", "message", error.Message)));
            throw;
        } finally {
            runningDownload = null;
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
