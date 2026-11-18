using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

static class LauncherProfile
{
    internal static string DefaultPath {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EBuild-WebView2-CSharp"); }
    }

    internal static string Resolve() {
        var configured = Environment.GetEnvironmentVariable("EBUILD_CSHARP_PROFILE");
        if (!String.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
#if DEBUG
        return DefaultPath + "-Debug";
#else
        return DefaultPath;
#endif
    }

    internal static string MutexName(string profile) {
        const string name = "Local\\EBuild-WebView2-CSharp-Trial-v1";
        var normalized = profile.TrimEnd('\\', '/');
        if (String.Equals(normalized, DefaultPath, StringComparison.OrdinalIgnoreCase)) return name;
        using (var sha = SHA256.Create())
            return name + "-" + Network.Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(normalized.ToUpperInvariant())));
    }
}
