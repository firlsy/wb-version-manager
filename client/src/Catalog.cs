using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WbVm
{
    public sealed class PackageInfo
    {
        public string Url = "";
        public string SizeText = "";
        /// <summary>来源：cn=国内版官方站，intl=国际版更新通道，local=本机缓存</summary>
        public string Source = "";
    }

    public sealed class ReleaseNote
    {
        public string Text = "";              // 普通条目
        public string Group = "";             // 分组名（有分组时）
        public List<string> Items;            // 分组条目
        public bool IsGroup { get { return Items != null && Items.Count > 0; } }
    }

    public sealed class Release
    {
        public string Version = "";
        public string Build = "";
        public string Date = "";
        public string NotesUrl = "";
        public string NotesTitle = "";
        public bool LocalOnly;
        /// <summary>国内版安装包（workbuddy.cn / cos.ap-guangzhou.myqcloud.com）</summary>
        public Dictionary<string, PackageInfo> Packages = new Dictionary<string, PackageInfo>(StringComparer.Ordinal);
        /// <summary>国际版安装包（workbuddy.ai / download.codebuddy.ai），与国内版完全独立</summary>
        public Dictionary<string, PackageInfo> IntlPackages = new Dictionary<string, PackageInfo>(StringComparer.Ordinal);
        public List<ReleaseNote> Notes = new List<ReleaseNote>();

        public PackageInfo WinPackage
        {
            get
            {
                PackageInfo p;
                if (Packages.TryGetValue("win32-x64-user", out p)) return p;
                return null;
            }
        }

        /// <summary>按客户端取对应的 Windows 安装包，取不到返回 null（绝不跨版回退）</summary>
        public PackageInfo PackageFor(string appId)
        {
            Dictionary<string, PackageInfo> map = appId == "workbuddyai" ? IntlPackages : Packages;
            PackageInfo p;
            if (map.TryGetValue("win32-x64-user", out p) && p != null && !string.IsNullOrEmpty(p.Url)) return p;
            return null;
        }

        public bool HasPackageFor(string appId)
        {
            return PackageFor(appId) != null;
        }

        public int NoteCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Notes.Count; i++) n += Notes[i].IsGroup ? Notes[i].Items.Count : 1;
                return n;
            }
        }
    }

    public sealed class Catalog
    {
        public const string DocsBase = "https://www.workbuddy.cn/docs/workbuddy";
        /// <summary>国内版更新通道（历史保留常量，实际取 Apps.ById().FeedBase）</summary>
        public const string UpdateFeed = "https://copilot.tencent.com/v2/update?platform=workbuddy-win32-x64-user";
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36";

        private static readonly string[] PlatformKeys = new string[] { "darwin-arm64", "darwin-x64", "win32-x64-user" };
        public static readonly Dictionary<string, string> PlatformLabels = new Dictionary<string, string>
        {
            { "darwin-arm64", "macOS · Apple 芯片" },
            { "darwin-x64", "macOS · Intel" },
            { "win32-x64-user", "Windows · 64 位" }
        };

        public string GeneratedAt = "";
        public JsonValue Latest;          // 国内版最新
        public JsonValue LatestIntl;      // 国际版最新
        /// <summary>国际版 版本短号 → 安装包。国际版官方不发布历史包，这里累积每次联网见到的当前版本。</summary>
        public Dictionary<string, PackageInfo> IntlHistory = new Dictionary<string, PackageInfo>(StringComparer.Ordinal);
        public List<Release> Releases = new List<Release>();

        private static Catalog _current;
        public static Catalog Current
        {
            get
            {
                if (_current == null) _current = LoadEmbedded();
                return _current;
            }
        }

        /* ---------------- 内置目录 ---------------- */

        public static Catalog LoadEmbedded()
        {
            Catalog c = new Catalog();
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string name = null;
                foreach (string n in asm.GetManifestResourceNames())
                {
                    if (n.EndsWith("catalog.json", StringComparison.OrdinalIgnoreCase)) { name = n; break; }
                }
                if (name == null) return c;
                using (Stream s = asm.GetManifestResourceStream(name))
                using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                {
                    c.FromJson(JsonValue.Parse(r.ReadToEnd()));
                }
            }
            catch { }
            return c;
        }

        public static Catalog LoadFromFile(string path)
        {
            Catalog c = new Catalog();
            JsonValue root = JsonFile.Read(path);
            if (root != null) c.FromJson(root);
            return c;
        }

        private void FromJson(JsonValue root)
        {
            if (root == null) return;
            GeneratedAt = root.GetString("generatedAt", "");
            Latest = root.Get("latest");
            LatestIntl = root.Get("latestIntl");

            JsonValue history = root.Get("intlHistory");
            if (history != null && history.Props != null)
            {
                foreach (KeyValuePair<string, JsonValue> kv in history.Props)
                {
                    PackageInfo p = new PackageInfo();
                    p.Url = kv.Value.GetString("url", "");
                    p.SizeText = kv.Value.GetString("sizeText", "");
                    p.Source = "intl";
                    if (!string.IsNullOrEmpty(p.Url)) IntlHistory[kv.Key] = p;
                }
            }

            JsonValue releases = root.Get("releases");
            if (releases == null) return;
            foreach (JsonValue r in releases.Enumerate())
            {
                Release rel = new Release();
                rel.Version = r.GetString("version", "");
                rel.Build = r.GetString("build", "");
                rel.Date = r.GetString("date", "");
                rel.NotesUrl = r.GetString("notesUrl", "");
                rel.NotesTitle = r.GetString("notesTitle", "");
                JsonValue localOnly = r.Get("localOnly");
                rel.LocalOnly = localOnly != null && localOnly.AsBool(false);

                JsonValue pkgs = r.Get("packages");
                if (pkgs != null && pkgs.Props != null)
                {
                    foreach (KeyValuePair<string, JsonValue> kv in pkgs.Props)
                    {
                        PackageInfo p = new PackageInfo();
                        p.Url = kv.Value.GetString("url", "");
                        p.SizeText = kv.Value.GetString("sizeText", "");
                        p.Source = "cn";
                        rel.Packages[kv.Key] = p;
                    }
                }

                JsonValue intl = r.Get("intlPackages");
                if (intl != null && intl.Props != null)
                {
                    foreach (KeyValuePair<string, JsonValue> kv in intl.Props)
                    {
                        PackageInfo p = new PackageInfo();
                        p.Url = kv.Value.GetString("url", "");
                        p.SizeText = kv.Value.GetString("sizeText", "");
                        p.Source = "intl";
                        rel.IntlPackages[kv.Key] = p;
                    }
                }

                JsonValue notes = r.Get("notes");
                if (notes != null)
                {
                    foreach (JsonValue n in notes.Enumerate())
                    {
                        ReleaseNote note = new ReleaseNote();
                        if (n.Type == JsonValue.Kind.String)
                        {
                            note.Text = n.StringValue;
                        }
                        else
                        {
                            note.Group = n.GetString("group", "");
                            JsonValue items = n.Get("items");
                            note.Items = new List<string>();
                            if (items != null)
                                foreach (JsonValue it in items.Enumerate())
                                    if (!string.IsNullOrEmpty(it.AsString(""))) note.Items.Add(it.AsString(""));
                        }
                        if (!string.IsNullOrEmpty(note.Text) || note.IsGroup) rel.Notes.Add(note);
                    }
                }
                if (!string.IsNullOrEmpty(rel.Version)) Releases.Add(rel);
            }
            Sort();
            ApplyIntlHistory();
        }

        /// <summary>把累积到的国际版安装包贴回版本清单（只补不覆盖）</summary>
        public void ApplyIntlHistory()
        {
            if (IntlHistory.Count == 0) return;
            foreach (Release rel in Releases)
            {
                PackageInfo p;
                if (!IntlHistory.TryGetValue(rel.Version, out p)) continue;
                if (rel.IntlPackages.ContainsKey("win32-x64-user")) continue;
                rel.IntlPackages["win32-x64-user"] = p;
            }
        }

        /// <summary>记一条国际版安装包，返回是否新增</summary>
        public bool RememberIntl(string version, string url)
        {
            if (string.IsNullOrEmpty(version) || string.IsNullOrEmpty(url)) return false;
            if (IntlHistory.ContainsKey(version)) return false;
            PackageInfo p = new PackageInfo();
            p.Url = url;
            p.Source = "intl";
            IntlHistory[version] = p;
            ApplyIntlHistory();
            return true;
        }

        /// <summary>取某个版本的条目，没有就按版本号建一条</summary>
        public Release EnsureRelease(string version)
        {
            Release rel = Find(version);
            if (rel != null) return rel;
            rel = new Release();
            rel.Version = version;
            rel.LocalOnly = true;
            Releases.Add(rel);
            Sort();
            return rel;
        }

        private void Sort()
        {
            Releases.Sort(delegate(Release a, Release b) { return Fmt.CompareVersion(b.Version, a.Version); });
        }

        public Release Find(string version)
        {
            for (int i = 0; i < Releases.Count; i++)
                if (Releases[i].Version == version) return Releases[i];
            return null;
        }

        public string LatestVersion
        {
            get { return Latest == null ? "" : Latest.GetString("version", ""); }
        }

        public string LatestBuild
        {
            get { return Latest == null ? "" : Latest.GetString("build", ""); }
        }

        public string IntlLatestVersion
        {
            get { return LatestIntl == null ? "" : LatestIntl.GetString("version", ""); }
        }

        public string IntlLatestBuild
        {
            get { return LatestIntl == null ? "" : LatestIntl.GetString("build", ""); }
        }

        public string LatestVersionFor(string appId)
        {
            return appId == "workbuddyai" ? IntlLatestVersion : LatestVersion;
        }

        public string LatestBuildFor(string appId)
        {
            return appId == "workbuddyai" ? IntlLatestBuild : LatestBuild;
        }

        /// <summary>只有官方给了 sha256 才返回，国际版通道该字段为空</summary>
        public string ShaFor(string appId, string version)
        {
            JsonValue latest = appId == "workbuddyai" ? LatestIntl : Latest;
            if (latest == null) return "";
            string build = latest.GetString("build", "");
            if (!build.StartsWith(version, StringComparison.Ordinal)) return "";
            return latest.GetString("sha256", "");
        }

        public int TotalNotes
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Releases.Count; i++) if (Releases[i].NoteCount > 0) n++;
                return n;
            }
        }

        /* ---------------- 联网刷新 ---------------- */

        public sealed class RefreshReport
        {
            public List<string> Added = new List<string>();
            public List<string> UpdatedNotes = new List<string>();
            public List<string> Warnings = new List<string>();
            public string LatestVersion = "";
            public string IntlVersion = "";
            public string IntlUrl = "";
            public bool IntlNew;
            public int Total;
        }

        /// <summary>把内层异常也带出来，不然只有一句「发送请求时出错」没法排查</summary>
        public static string Describe(Exception ex)
        {
            if (ex == null) return "";
            StringBuilder sb = new StringBuilder(ex.Message);
            Exception inner = ex.InnerException;
            int depth = 0;
            while (inner != null && depth++ < 3)
            {
                sb.Append(" → ").Append(inner.Message);
                inner = inner.InnerException;
            }
            return sb.ToString();
        }

        private static HttpClient NewClient()
        {
            HttpClient c = new HttpClient(Net.NewHandler());
            c.Timeout = TimeSpan.FromSeconds(25);
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
            c.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
            return c;
        }

        /// <summary>
        /// 问一次官方更新通道：给定 version 返回「比它新的那个版本」。
        /// 传 0.0.0 即拿到当前最新；服务端已是最新时返回 204 空体。
        /// </summary>
        private static async Task<JsonValue> FetchLatestAsync(HttpClient http, AppDef app)
        {
            string feed = app.FeedBase + "&version=0.0.0&_t=" +
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            string json = await http.GetStringAsync(feed).ConfigureAwait(false);
            if (string.IsNullOrEmpty(json) || json.Trim().Length == 0) return null;

            JsonValue data = JsonValue.Parse(json);
            string raw = data.GetString("version", "");
            if (string.IsNullOrEmpty(raw)) return null;

            string[] parts = raw.Split('.');
            string shortVersion = parts.Length >= 3 ? parts[0] + "." + parts[1] + "." + parts[2] : raw;

            JsonValue latest = JsonValue.NewObject();
            latest.Set("version", shortVersion);
            latest.Set("build", raw);
            latest.Set("url", data.GetString("url", ""));
            latest.Set("sha256", data.GetString("sha256hash", ""));
            return latest;
        }

        public async Task<RefreshReport> RefreshAsync(IProgress<string> progress)
        {
            RefreshReport report = new RefreshReport();
            Action<string> report2 = delegate(string msg) { if (progress != null) progress.Report(msg); };

            Dictionary<string, Release> merged = new Dictionary<string, Release>(StringComparer.Ordinal);

            using (HttpClient http = NewClient())
            {
                report2("读取国内版更新通道…");
                try
                {
                    JsonValue dom = await FetchLatestAsync(http, Apps.ById("workbuddy")).ConfigureAwait(false);
                    if (dom != null)
                    {
                        Latest = dom;
                        report.LatestVersion = dom.GetString("version", "");
                        Release rel = EnsureRelease(report.LatestVersion);
                        PackageInfo p = new PackageInfo();
                        p.Url = dom.GetString("url", "");
                        p.Source = "cn";
                        if (!string.IsNullOrEmpty(p.Url)) rel.Packages["win32-x64-user"] = p;
                        if (string.IsNullOrEmpty(rel.Build)) rel.Build = dom.GetString("build", "");
                    }
                }
                catch (Exception ex)
                {
                    report.Warnings.Add("国内版更新通道：" + Describe(ex));
                }

                report2("读取国际版更新通道…");
                try
                {
                    JsonValue intl = await FetchLatestAsync(http, Apps.ById("workbuddyai")).ConfigureAwait(false);
                    if (intl != null)
                    {
                        LatestIntl = intl;
                        report.IntlVersion = intl.GetString("version", "");
                        report.IntlUrl = intl.GetString("url", "");
                        if (!string.IsNullOrEmpty(report.IntlUrl))
                        {
                            Release rel = EnsureRelease(report.IntlVersion);
                            PackageInfo p = new PackageInfo();
                            p.Url = report.IntlUrl;
                            p.Source = "intl";
                            rel.IntlPackages["win32-x64-user"] = p;
                            report.IntlNew = RememberIntl(report.IntlVersion, report.IntlUrl);
                        }
                    }
                }
                catch (Exception ex)
                {
                    report.Warnings.Add("国际版更新通道：" + Describe(ex));
                }

                for (int i = 0; i < Releases.Count; i++) merged[Releases[i].Version] = Releases[i];

                report2("读取官网历史版本…");
                try
                {
                    string url = DocsBase + "/Download-History?_t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
                    string html = await http.GetStringAsync(url).ConfigureAwait(false);
                    Dictionary<string, Release> parsed = ParseDownloadHistory(html);
                    foreach (KeyValuePair<string, Release> kv in parsed)
                    {
                        Release old;
                        if (merged.TryGetValue(kv.Key, out old))
                        {
                            Release mergedRel = kv.Value;
                            if (old.Notes.Count > 0)
                            {
                                mergedRel.Notes = old.Notes;
                                mergedRel.NotesUrl = old.NotesUrl;
                                mergedRel.NotesTitle = old.NotesTitle;
                            }
                            merged[kv.Key] = mergedRel;
                        }
                        else
                        {
                            merged[kv.Key] = kv.Value;
                            report.Added.Add(kv.Key);
                        }
                    }
                }
                catch (Exception ex)
                {
                    report.Warnings.Add("历史版本页：" + Describe(ex));
                }

                report2("读取更新日志索引…");
                List<KeyValuePair<string, string>> versions = new List<KeyValuePair<string, string>>();
                try
                {
                    string sm = await http.GetStringAsync(DocsBase + "/sitemap-workbuddy-workbuddy.xml").ConfigureAwait(false);
                    MatchCollection ms = Regex.Matches(sm, "<loc>([^<]*Changelog-(\\d+\\.\\d+\\.\\d+)[^<]*)</loc>");
                    HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (Match m in ms)
                    {
                        string ver = m.Groups[2].Value;
                        if (seen.Add(ver)) versions.Add(new KeyValuePair<string, string>(ver, m.Groups[1].Value));
                    }
                }
                catch (Exception ex)
                {
                    report.Warnings.Add("更新日志索引：" + Describe(ex));
                }

                List<KeyValuePair<string, string>> missing = new List<KeyValuePair<string, string>>();
                for (int i = 0; i < versions.Count; i++)
                {
                    Release rel;
                    if (merged.TryGetValue(versions[i].Key, out rel) && rel.Notes.Count == 0) missing.Add(versions[i]);
                }

                if (missing.Count > 0)
                {
                    int done = 0;
                    SemaphoreSlim gate = new SemaphoreSlim(5);
                    List<Task> tasks = new List<Task>();
                    foreach (KeyValuePair<string, string> item in missing)
                    {
                        KeyValuePair<string, string> captured = item;
                        tasks.Add(Task.Run(async delegate
                        {
                            await gate.WaitAsync().ConfigureAwait(false);
                            try
                            {
                                string html = await http.GetStringAsync(captured.Value).ConfigureAwait(false);
                                Release rel;
                                if (merged.TryGetValue(captured.Key, out rel))
                                {
                                    List<ReleaseNote> notes;
                                    string title;
                                    string notesUrl;
                                    if (ParseChangelogPage(html, captured.Key, out notes, out title, out notesUrl) && notes.Count > 0)
                                    {
                                        rel.Notes = notes;
                                        rel.NotesTitle = title;
                                        rel.NotesUrl = notesUrl;
                                        lock (report.UpdatedNotes) report.UpdatedNotes.Add(captured.Key);
                                    }
                                }
                            }
                            catch { }
                            finally
                            {
                                gate.Release();
                                int n = Interlocked.Increment(ref done);
                                report2("补全更新说明 " + n + "/" + missing.Count + "…");
                            }
                        }));
                    }
                    try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
                }
            }

            List<Release> all = new List<Release>(merged.Values);
            all.Sort(delegate(Release a, Release b) { return Fmt.CompareVersion(b.Version, a.Version); });
            Releases = all;
            ApplyIntlHistory();
            GeneratedAt = DateTime.UtcNow.ToString("o");
            report.Total = Releases.Count;

            try { SaveCache(); } catch { }
            return report;
        }

        /// <summary>把刷新结果落到 %LOCALAPPDATA%\wb-version-manager\catalog.json</summary>
        public void SaveCache()
        {
            JsonValue root = ToJson();
            JsonFile.WriteAtomic(Path.Combine(Paths.DataRoot, "catalog.json"), root);
        }

        public JsonValue ToJson()
        {
            JsonValue root = JsonValue.NewObject();
            root.Set("generatedAt", GeneratedAt);
            root.Set("generator", "wb-version-manager-csharp");
            if (Latest != null) root.Set("latest", Latest);
            if (LatestIntl != null) root.Set("latestIntl", LatestIntl);

            if (IntlHistory.Count > 0)
            {
                JsonValue history = JsonValue.NewObject();
                foreach (KeyValuePair<string, PackageInfo> kv in IntlHistory)
                {
                    JsonValue p = JsonValue.NewObject();
                    p.Set("url", kv.Value.Url);
                    p.Set("sizeText", kv.Value.SizeText);
                    history.Set(kv.Key, p);
                }
                root.Set("intlHistory", history);
            }

            JsonValue arr = JsonValue.NewArray();
            for (int i = 0; i < Releases.Count; i++)
            {
                Release rel = Releases[i];
                JsonValue r = JsonValue.NewObject();
                r.Set("version", rel.Version);
                r.Set("build", rel.Build);
                r.Set("date", rel.Date);
                r.Set("notesUrl", rel.NotesUrl);
                r.Set("notesTitle", rel.NotesTitle);
                if (rel.LocalOnly) r.Set("localOnly", true);

                JsonValue pkgs = JsonValue.NewObject();
                foreach (KeyValuePair<string, PackageInfo> kv in rel.Packages)
                {
                    JsonValue p = JsonValue.NewObject();
                    p.Set("url", kv.Value.Url);
                    p.Set("sizeText", kv.Value.SizeText);
                    pkgs.Set(kv.Key, p);
                }
                r.Set("packages", pkgs);

                JsonValue intlPkgs = JsonValue.NewObject();
                foreach (KeyValuePair<string, PackageInfo> kv in rel.IntlPackages)
                {
                    JsonValue p = JsonValue.NewObject();
                    p.Set("url", kv.Value.Url);
                    p.Set("sizeText", kv.Value.SizeText);
                    intlPkgs.Set(kv.Key, p);
                }
                r.Set("intlPackages", intlPkgs);

                JsonValue notes = JsonValue.NewArray();
                for (int k = 0; k < rel.Notes.Count; k++)
                {
                    ReleaseNote note = rel.Notes[k];
                    if (note.IsGroup)
                    {
                        JsonValue g = JsonValue.NewObject();
                        g.Set("group", note.Group);
                        JsonValue items = JsonValue.NewArray();
                        for (int j = 0; j < note.Items.Count; j++) items.Add(JsonValue.From(note.Items[j]));
                        g.Set("items", items);
                        notes.Add(g);
                    }
                    else
                    {
                        notes.Add(JsonValue.From(note.Text));
                    }
                }
                r.Set("notes", notes);
                arr.Add(r);
            }
            root.Set("releases", arr);
            return root;
        }

        /* ---------------- 解析 ---------------- */

        public static string StripTags(string html)
        {
            string s = html ?? "";
            s = Regex.Replace(s, "<script[\\s\\S]*?</script>", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "<style[\\s\\S]*?</style>", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "</(p|li|h\\d|tr|div)>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "<[^>]+>", "");
            s = WebUtility.HtmlDecode(s);
            s = Regex.Replace(s, "[ \\t]+", " ");
            s = Regex.Replace(s, "\\n{2,}", "\n");
            return s.Trim();
        }

        public static Dictionary<string, Release> ParseDownloadHistory(string html)
        {
            Dictionary<string, Release> map = new Dictionary<string, Release>(StringComparer.Ordinal);
            MatchCollection rows = Regex.Matches(html ?? "", "<tr>[\\s\\S]*?</tr>", RegexOptions.IgnoreCase);
            foreach (Match row in rows)
            {
                MatchCollection cellMatches = Regex.Matches(row.Value, "<td[^>]*>([\\s\\S]*?)</td>", RegexOptions.IgnoreCase);
                if (cellMatches.Count < 3) continue;

                List<string> cells = new List<string>();
                foreach (Match cm in cellMatches) cells.Add(cm.Groups[1].Value);

                Match vm = Regex.Match(cells[0], "v?(\\d+\\.\\d+\\.\\d+)");
                if (!vm.Success) continue;

                Release rel = new Release();
                rel.Version = vm.Groups[1].Value;
                rel.Date = StripTags(cells[1]).Trim();

                for (int i = 2; i < cells.Count; i++)
                {
                    int keyIndex = i - 2;
                    if (keyIndex >= PlatformKeys.Length) break;
                    Match link = Regex.Match(cells[i], "href=\"([^\"]+)\"", RegexOptions.IgnoreCase);
                    if (!link.Success) continue;
                    Match size = Regex.Match(StripTags(cells[i]), "([\\d.]+)\\s*(MB|GB|KB)", RegexOptions.IgnoreCase);
                    PackageInfo p = new PackageInfo();
                    p.Url = link.Groups[1].Value;
                    p.SizeText = size.Success ? size.Groups[1].Value + " " + size.Groups[2].Value.ToUpperInvariant() : "";
                    rel.Packages[PlatformKeys[keyIndex]] = p;
                }

                if (rel.Packages.Count == 0) continue;

                foreach (KeyValuePair<string, PackageInfo> kv in rel.Packages)
                {
                    Match bm = Regex.Match(kv.Value.Url, "WorkBuddy-[a-z0-9-]+-(\\d+\\.\\d+\\.\\d+\\.\\d+)-", RegexOptions.IgnoreCase);
                    if (bm.Success) { rel.Build = bm.Groups[1].Value; break; }
                }
                map[rel.Version] = rel;
            }
            return map;
        }

        public static bool ParseChangelogPage(string html, string version, out List<ReleaseNote> notes, out string title, out string url)
        {
            notes = new List<ReleaseNote>();
            title = "";
            url = DocsBase + "/Changelog-" + version;

            if (string.IsNullOrEmpty(html)) return false;
            Match h1 = Regex.Match(html, "<h1[\\s>]", RegexOptions.IgnoreCase);
            if (!h1.Success) return false;
            int start = h1.Index;

            int end = html.IndexOf("</main>", StringComparison.OrdinalIgnoreCase);
            string body = end > start ? html.Substring(start, end - start) : html.Substring(start);

            Match h2 = Regex.Match(body, "<h2[^>]*>([\\s\\S]*?)</h2>", RegexOptions.IgnoreCase);
            if (h2.Success) title = StripTags(h2.Groups[1].Value);

            string after = h2.Success ? body.Substring(body.IndexOf(h2.Value, StringComparison.Ordinal) + h2.Value.Length) : body;

            string[] chunks = Regex.Split(after, "<h3[^>]*>", RegexOptions.IgnoreCase);
            string first = chunks.Length > 0 ? chunks[0] : "";
            foreach (Match li in Regex.Matches(first, "<li[^>]*>([\\s\\S]*?)</li>", RegexOptions.IgnoreCase))
            {
                string text = StripTags(li.Groups[1].Value);
                if (!string.IsNullOrEmpty(text))
                {
                    ReleaseNote n = new ReleaseNote();
                    n.Text = text;
                    notes.Add(n);
                }
            }

            for (int i = 1; i < chunks.Length; i++)
            {
                string chunk = chunks[i];
                int close = chunk.IndexOf("</h3>", StringComparison.OrdinalIgnoreCase);
                string groupName = close > 0 ? StripTags(chunk.Substring(0, close)) : "";
                ReleaseNote group = new ReleaseNote();
                group.Group = groupName;
                group.Items = new List<string>();
                foreach (Match li in Regex.Matches(chunk, "<li[^>]*>([\\s\\S]*?)</li>", RegexOptions.IgnoreCase))
                {
                    string text = StripTags(li.Groups[1].Value);
                    if (!string.IsNullOrEmpty(text)) group.Items.Add(text);
                }
                if (group.Items.Count > 0) notes.Add(group);
            }
            return true;
        }
    }
}
