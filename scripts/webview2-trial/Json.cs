using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

static class J
{
    internal static Dictionary<string,object> Map(params object[] values) {
        var result = new Dictionary<string,object>(StringComparer.Ordinal);
        for (int i = 0; i < values.Length; i += 2) result.Add((string)values[i], values[i+1]);
        return result;
    }
    internal static Dictionary<string,object> Obj(this object value) { return value as Dictionary<string,object> ?? Map(); }
    internal static object At(this object value, string key) { object item; return value.Obj().TryGetValue(key, out item) ? item : null; }
    internal static string S(this object value, string key = null) { return Convert.ToString(key == null ? value : value.At(key), CultureInfo.InvariantCulture) ?? ""; }
    internal static int I(this object value, string key, int fallback = 0) { int n; return Int32.TryParse(value.S(key), out n) ? n : fallback; }
    internal static bool B(this object value, string key) { return Object.Equals(value.At(key), true); }
    internal static object[] A(this object value, string key = null) {
        value = key == null ? value : value.At(key);
        var items = value as IList;
        return items == null ? new object[0] : items.Cast<object>().ToArray();
    }
    internal static object Arg(this object[] args, int i) { return i < args.Length ? args[i] : null; }
    internal static Dictionary<string,object> Merge(params object[] layers) {
        var result = Map();
        foreach (var layer in layers) foreach (var pair in layer.Obj()) result[pair.Key] = pair.Value;
        return result;
    }
    static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024, RecursionLimit = 96 }; }
    internal static object Parse(string text) { return Serializer().DeserializeObject(text); }
    internal static string Dump(object value) { return Serializer().Serialize(value); }
    internal static object Read(string file) { return File.Exists(file) ? Parse(File.ReadAllText(file)) : null; }
    internal static void Save(string file, object value) {
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            File.WriteAllText(temp, Dump(value), new UTF8Encoding(false));
            if (File.Exists(file)) File.Replace(temp, file, null);
            else File.Move(temp, file);
        } finally { File.Delete(temp); }
    }
    internal static string Id(string id) {
        if (!Regex.IsMatch(id, @"^[a-zA-Z0-9][a-zA-Z0-9_.+\-]{0,79}$") || id.EndsWith(".") ||
            Regex.IsMatch(id, @"^(con|prn|aux|nul|com[1-9]|lpt[1-9])(\.|$)", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("版本或实例标识无效。");
        return id;
    }
    internal static string Inside(string root, string relative) {
        if (String.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0)
            throw new InvalidOperationException("文件路径不合法。");
        var prefix = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(prefix, relative));
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("文件路径超出目标目录。");
        return target;
    }
    internal static void NoLinks(string path) {
        for (var item = Path.GetFullPath(path); !String.IsNullOrEmpty(item); item = Path.GetDirectoryName(item)) {
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("不支持包含符号链接或联接的目录。");
        }
    }
}
