using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace WbVm
{
    /// <summary>工具自身的版本与发布地址</summary>
    public static class AppInfo
    {
        public const string Version = "1.3.3";
        public const string RepoUrl = "https://github.com/firlsy/wb-version-manager";
        public const string ReleasesUrl = RepoUrl + "/releases/latest";
        public const string LatestApi = "https://api.github.com/repos/firlsy/wb-version-manager/releases/latest";
        public const string Title = "WB 版本管理器";
        public static string TitleWithVersion { get { return Title + " v" + Version; } }
    }

    public sealed class AppDef
    {
        public string Id;
        public string Name;
        public string Edition;
        public string Exe;
        public string Process;
        public string Endpoint;
        public string Accent;
        /// <summary>官方更新通道基址（不含 version 参数）</summary>
        public string FeedBase;
        /// <summary>官方是否公开发布历史版本安装包</summary>
        public bool PublishHistory;

        public AppDef(string id, string name, string edition, string exe, string processName, string endpoint, string accent,
            string feedBase, bool publishHistory)
        {
            Id = id;
            Name = name;
            Edition = edition;
            Exe = exe;
            Process = processName;
            Endpoint = endpoint;
            Accent = accent;
            FeedBase = feedBase;
            PublishHistory = publishHistory;
        }
    }

    public static class Apps
    {
        public static readonly AppDef[] All = new AppDef[]
        {
            new AppDef("workbuddy", "WorkBuddy", "国内版", "WorkBuddy.exe", "WorkBuddy", "www.workbuddy.cn", "#2f6bff",
                "https://copilot.tencent.com/v2/update?platform=workbuddy-win32-x64-user", true),
            new AppDef("workbuddyai", "WorkBuddy AI", "国际版", "WorkBuddyAI.exe", "WorkBuddyAI", "www.workbuddy.ai", "#7c4dff",
                "https://www.workbuddy.ai/v2/update?platform=workbuddy-win32-x64-user", false)
        };

        public static AppDef ById(string id)
        {
            for (int i = 0; i < All.Length; i++)
                if (All[i].Id == id) return All[i];
            return null;
        }
    }

    /// <summary>每个客户端的落点画像（与网页版 lib/block.js 保持一致）</summary>
    public sealed class AppProfile
    {
        public string AppId;
        public string Name;
        public string Edition;
        public string Slug;
        public string RuntimeDir;
        public string ConfigFile;
        public string SettingsFile;
        public string FailureFile;
        public string AttemptFile;
        /// <summary>
        /// 锁定前配置值的快照。放在客户端配置目录里，而不是工具自己的 state.json，
        /// 这样 C# / 网页版 / ps1 任何一个前端解除锁定都能读到同一份原始值。
        /// </summary>
        public string LockSnapshotFile;
        public string[] CacheDirs;
        public string StoreDir;
    }

    public static class Paths
    {
        public static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public static readonly string TempRoot = Path.GetTempPath().TrimEnd('\\');

        /// <summary>客户端自身的数据根目录，可用环境变量 WBVM_DATA_DIR 覆盖</summary>
        public static readonly string DataRoot = ResolveDataRoot();

        public static readonly string StoreRoot = Path.Combine(DataRoot, "store");
        public static readonly string VaultDir = Path.Combine(DataRoot, "quarantine");
        public static readonly string BackupDir = Path.Combine(DataRoot, "block-backup");
        public static readonly string StateFile = Path.Combine(DataRoot, "state.json");

        public static readonly string LegacyUpdaterDir = Path.Combine(Local, "@genieworkbuddy-desktop-updater");
        public static readonly string LegacyStaged = Path.Combine(LegacyUpdaterDir, "installer.exe");

        private static string ResolveDataRoot()
        {
            string custom = Environment.GetEnvironmentVariable("WBVM_DATA_DIR");
            if (!string.IsNullOrEmpty(custom)) return custom;
            return Path.Combine(Local, "wb-version-manager");
        }

        /// <summary>
        /// 网页版的缓存目录：客户端和网页版放在同一个目录（发行包就是这种结构）时共用缓存，
        /// 单独一个 exe 放别处时这里返回空。
        /// </summary>
        public static List<string> WebStoreRoots()
        {
            List<string> list = new List<string>();
            try
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                if (string.IsNullOrEmpty(exeDir)) return list;
                string[] candidates = new string[]
                {
                    exeDir,
                    Path.GetDirectoryName(exeDir)
                };
                foreach (string dir in candidates)
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    if (!File.Exists(Path.Combine(dir, "server.js"))) continue;
                    string store = Path.Combine(dir, "store");
                    if (!Directory.Exists(store)) continue;
                    bool dup = false;
                    foreach (string s in list)
                        if (string.Equals(s, store, StringComparison.OrdinalIgnoreCase)) dup = true;
                    if (!dup) list.Add(store);
                }
            }
            catch { }
            return list;
        }

        public static string SlugOf(string name)        {
            StringBuilder sb = new StringBuilder();
            string lower = (name ?? "").ToLowerInvariant();
            for (int i = 0; i < lower.Length; i++)
            {
                char c = lower[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';
                sb.Append(ok ? c : '-');
            }
            string s = sb.ToString();
            while (s.StartsWith("-")) s = s.Substring(1);
            while (s.EndsWith("-")) s = s.Substring(0, s.Length - 1);
            return s;
        }

        public static AppProfile ProfileOf(string appId)
        {
            AppDef app = Apps.ById(appId);
            if (app == null) return null;
            string slug = SlugOf(app.Name);
            string runtimeDir = Path.Combine(Home, slug == "workbuddy" ? ".workbuddy" : "." + slug);
            AppProfile p = new AppProfile();
            p.AppId = appId;
            p.Name = app.Name;
            p.Edition = app.Edition;
            p.Slug = slug;
            p.RuntimeDir = runtimeDir;
            p.ConfigFile = Path.Combine(runtimeDir, "app", "app-config.json");
            p.SettingsFile = Path.Combine(runtimeDir, "settings.json");
            p.FailureFile = Path.Combine(runtimeDir, "app", "win-install-failure.json");
            p.AttemptFile = Path.Combine(runtimeDir, "app", "win-install-attempt.json");
            p.LockSnapshotFile = Path.Combine(runtimeDir, "app", ".wbvm-block.json");
            p.CacheDirs = new string[]
            {
                Path.Combine(TempRoot, "workbuddy-update-x64-" + slug),
                Path.Combine(TempRoot, "workbuddy-update-arm64-" + slug)
            };
            p.StoreDir = Path.Combine(StoreRoot, appId);
            return p;
        }

        public static string AccountName()
        {
            string domain = Environment.GetEnvironmentVariable("USERDOMAIN");
            string user = Environment.GetEnvironmentVariable("USERNAME");
            if (string.IsNullOrEmpty(user)) return "";
            return string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
        }

        /// <summary>把用户目录前缀换成环境变量写法，便于截图/日志脱敏</summary>
        public static string Display(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (!string.IsNullOrEmpty(Home) && path.StartsWith(Home, StringComparison.OrdinalIgnoreCase))
                return "%USERPROFILE%" + path.Substring(Home.Length);
            if (!string.IsNullOrEmpty(Local) && path.StartsWith(Local, StringComparison.OrdinalIgnoreCase))
                return "%LOCALAPPDATA%" + path.Substring(Local.Length);
            return path;
        }

        public static void EnsureDirs()
        {
            Directory.CreateDirectory(DataRoot);
            Directory.CreateDirectory(StoreRoot);
        }
    }

    /// <summary>
    /// 代理解析：优先 HTTP(S)_PROXY 环境变量，其次系统（IE/WinINET）设置。
    /// 公司内网、开发机常见只配环境变量，.NET 默认读不到。
    /// </summary>
    public static class Net
    {
        private static IWebProxy _cached;
        private static bool _resolved;

        static Net()
        {
            // .NET Framework 默认可能只协商到 TLS 1.0，官方站点要求 1.2，
            // 不显式打开就会报「未能创建 SSL/TLS 安全通道」
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)(192 | 768 | 3072); // Tls | Tls11 | Tls12
            }
            catch { }
            try { ServicePointManager.Expect100Continue = false; } catch { }
        }

        public static IWebProxy Proxy()
        {
            if (_resolved) return _cached;
            _resolved = true;

            string[] keys = new string[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy" };
            foreach (string key in keys)
            {
                string value = Environment.GetEnvironmentVariable(key);
                if (string.IsNullOrEmpty(value)) continue;
                try
                {
                    _cached = new WebProxy(value.Trim());
                    return _cached;
                }
                catch { }
            }

            try { _cached = WebRequest.DefaultWebProxy; }
            catch { _cached = null; }
            return _cached;
        }

        public static HttpClientHandler NewHandler()
        {
            HttpClientHandler h = new HttpClientHandler();
            h.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            IWebProxy p = Proxy();
            if (p != null) h.Proxy = p;
            return h;
        }
    }

    public static class Fmt
    {        public static string Bytes(long n)
        {
            if (n <= 0) return "0 B";
            string[] units = new string[] { "B", "KB", "MB", "GB" };
            int i = 0;
            double v = n;
            while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
            return (i == 0 ? ((long)v).ToString(CultureInfo.InvariantCulture) : v.ToString("0.0", CultureInfo.InvariantCulture)) + " " + units[i];
        }

        public static int CompareVersion(string a, string b)
        {
            string[] pa = (a ?? "").Split('.');
            string[] pb = (b ?? "").Split('.');
            int len = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < len; i++)
            {
                int va = 0, vb = 0;
                if (i < pa.Length) int.TryParse(pa[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out va);
                if (i < pb.Length) int.TryParse(pb[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out vb);
                if (va != vb) return va - vb;
            }
            return 0;
        }

        public static string Stamp()
        {
            DateTime d = DateTime.Now;
            return d.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        }

        public static string ShortVersion(string version)
        {
            string[] parts = (version ?? "").Split('.');
            if (parts.Length <= 3) return version ?? "";
            return parts[0] + "." + parts[1] + "." + parts[2];
        }

        /// <summary>
        /// 比较两个版本是否同一版。目录里是 3 段（5.11.3），安装清单里可能带构建号
        /// （5.11.3.43806934），直接 == 会永远不相等，所以统一按前三段比。
        /// </summary>
        public static bool SameVersion(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return CompareVersion(ShortVersion(a), ShortVersion(b)) == 0;
        }
    }

    public static class JsonFile
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static JsonValue Read(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                string text = File.ReadAllText(file, Encoding.UTF8);
                if (string.IsNullOrEmpty(text.Trim())) return null;
                return JsonValue.Parse(text);
            }
            catch
            {
                return null;
            }
        }

        public static void WriteAtomic(string file, JsonValue value)
        {
            string dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = file + ".wbvm.tmp";
            File.WriteAllText(tmp, value.ToJson(true) + "\n", Utf8NoBom);
            if (File.Exists(file))
            {
                try { File.Replace(tmp, file, null); }
                catch
                {
                    File.Copy(tmp, file, true);
                    try { File.Delete(tmp); } catch { }
                }
            }
            else
            {
                File.Move(tmp, file);
            }
        }
    }
}
