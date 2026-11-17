using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WbVm
{
    public sealed class CachedFile
    {
        public string AppId = "";
        public string FileName = "";
        public string FullPath = "";
        public long Size;
        public DateTime MTime;
        public string Version = "";
        public string VersionShort = "";
        public string Build = "";
        public string Platform = "";
        /// <summary>非空表示这个包其实是另一版的（旧版工具把国内版包下进了国际版目录）</summary>
        public string WrongEdition = "";
        public bool Usable { get { return WrongEdition.Length == 0; } }
        public string SizeText { get { return Fmt.Bytes(Size); } }
    }

    public static class Downloads
    {
        // 3 段或 4 段都要认：官方包名带构建号（5.11.3.43806934），
        // 而回收包可能沿用短版本号（5.11.4-recycled），只认 4 段会把后者当垃圾。
        private static readonly Regex VersionRe = new Regex(@"(\d+(?:\.\d+){2,3})");

        /// <summary>界面那边把实时目录塞进来，用于判断缓存包属于哪一版</summary>
        public static Func<Catalog> CatalogProvider = delegate { return Catalog.Current; };

        /// <summary>
        /// 按官方链接里的文件名反查这个安装包属于哪一版。
        /// 旧版本的工具把国内版包下进了 store\workbuddyai，光看文件名分不出来，
        /// 但和官方链接对一下就能认出来。返回 "" 表示对得上，否则返回它真正的归属。
        /// </summary>
        /* 目录里的包名 → 归属。每扫一次缓存都要按文件名反查，103 个版本 × 每个包
           都要重新解析一遍 URL 纯属浪费；目录不变就复用这张表。 */
        private static readonly Dictionary<string, string> _nameOwners =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _nameGate = new object();
        private static Catalog _nameCatalog;

        private static Dictionary<string, string> NameOwners(Catalog cat)
        {
            lock (_nameGate)
            {
                if (ReferenceEquals(_nameCatalog, cat) && _nameOwners.Count > 0) return _nameOwners;
                _nameOwners.Clear();
                CollectNames(cat, "workbuddy");
                CollectNames(cat, "workbuddyai");
                _nameCatalog = cat;
                return _nameOwners;
            }
        }

        private static void CollectNames(Catalog cat, string appId)
        {
            foreach (Release rel in cat.Releases)
            {
                Dictionary<string, PackageInfo> map = appId == "workbuddyai" ? rel.IntlPackages : rel.Packages;
                foreach (KeyValuePair<string, PackageInfo> kv in map)
                {
                    if (kv.Value == null || string.IsNullOrEmpty(kv.Value.Url)) continue;
                    string name = FileNameFromUrl(kv.Value.Url);
                    if (string.IsNullOrEmpty(name)) continue;
                    string known;
                    if (_nameOwners.TryGetValue(name, out known))
                    {
                        // 两条通道出现同名文件：宁可放过，也别把用户可用的包判成错版
                        if (!string.Equals(known, appId, StringComparison.OrdinalIgnoreCase)) _nameOwners[name] = "ambiguous";
                        continue;
                    }
                    _nameOwners[name] = appId;
                }
            }
        }

        public static string DetectWrongEdition(string appId, string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return "";
            Catalog cat;
            try { cat = CatalogProvider(); }
            catch { return ""; }
            if (cat == null) return "";

            string owner;
            if (!NameOwners(cat).TryGetValue(fileName, out owner)) return "";
            if (string.IsNullOrEmpty(owner) || owner == "ambiguous") return "";
            if (string.Equals(owner, appId, StringComparison.OrdinalIgnoreCase)) return "";
            return owner;
        }

        public static string FileNameFromUrl(string url)
        {
            try
            {
                Uri u = new Uri(url);
                // 先解码再取文件名：解码后的 %2e%2e%2f 会变成 ../，必须在写盘前掐掉
                string decoded = Uri.UnescapeDataString(u.AbsolutePath).Replace('/', '\\');
                string name = Path.GetFileName(decoded);
                if (string.IsNullOrEmpty(name)) return FallbackName();

                char[] bad = Path.GetInvalidFileNameChars();
                StringBuilder sb = new StringBuilder(name.Length);
                for (int i = 0; i < name.Length; i++)
                {
                    char c = name[i];
                    bool ok = true;
                    for (int k = 0; k < bad.Length; k++) if (bad[k] == c) { ok = false; break; }
                    sb.Append(ok ? c : '_');
                }
                name = sb.ToString().Trim('.', ' ');
                return string.IsNullOrEmpty(name) ? FallbackName() : name;
            }
            catch
            {
                return FallbackName();
            }
        }

        private static string FallbackName()
        {
            return "package-" + DateTime.Now.Ticks.ToString(CultureInfo.InvariantCulture) + ".exe";
        }

        public static string PlatformFromName(string name)
        {
            if (Regex.IsMatch(name, "darwin-arm64", RegexOptions.IgnoreCase)) return "darwin-arm64";
            if (Regex.IsMatch(name, "darwin-x64", RegexOptions.IgnoreCase)) return "darwin-x64";
            if (Regex.IsMatch(name, "win32-x64-user", RegexOptions.IgnoreCase)) return "win32-x64-user";
            return "";
        }

        /// <summary>客户端自己的缓存目录 + 同目录网页版的缓存目录</summary>
        public static List<string> AllStoreRoots()
        {
            List<string> list = new List<string>();
            list.Add(Paths.StoreRoot);
            foreach (string s in Paths.WebStoreRoots())
            {
                bool dup = false;
                foreach (string x in list)
                    if (string.Equals(x, s, StringComparison.OrdinalIgnoreCase)) dup = true;
                if (!dup) list.Add(s);
            }
            return list;
        }

        public static List<CachedFile> ListCached()
        {
            return ListCached(null);
        }

        /// <summary>列出缓存安装包；appId 为空时返回全部客户端的</summary>
        public static List<CachedFile> ListCached(string appId)
        {
            List<CachedFile> list = new List<CachedFile>();
            foreach (string root in AllStoreRoots())
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (string appDir in Directory.GetDirectories(root))
                    {
                        string dirAppId = Path.GetFileName(appDir);
                        if (!string.IsNullOrEmpty(appId) && !string.Equals(dirAppId, appId, StringComparison.OrdinalIgnoreCase))
                            continue;
                        foreach (string full in Directory.GetFiles(appDir))
                        {
                            string ext = Path.GetExtension(full).ToLowerInvariant();
                            if (ext != ".exe" && ext != ".dmg" && ext != ".zip" && ext != ".appimage") continue;
                            FileInfo fi = new FileInfo(full);
                            CachedFile f = new CachedFile();
                            f.AppId = dirAppId;
                            f.FileName = fi.Name;
                            f.FullPath = full;
                            f.Size = fi.Length;
                            f.MTime = fi.LastWriteTimeUtc;
                            Match m = VersionRe.Match(fi.Name);
                            f.Version = m.Success ? m.Groups[1].Value : "";
                            f.VersionShort = Fmt.ShortVersion(f.Version);
                            f.Build = f.Version;
                            f.Platform = PlatformFromName(fi.Name);
                            f.WrongEdition = DetectWrongEdition(dirAppId, fi.Name);
                            list.Add(f);
                        }
                    }
                }
                catch { }
            }
            list.Sort(delegate(CachedFile a, CachedFile b) { return b.MTime.CompareTo(a.MTime); });
            return list;
        }

        /// <summary>未完成的下载碎片（*.part）</summary>
        public static List<FileInfo> ListPartFiles(string appId)
        {
            return ListPartFiles(appId, null);
        }

        /// <summary>
        /// 未完成的碎片。keep 里的路径属于队列中的任务（可能在续传），不能删——
        /// 以前「清除无用缓存」会把暂停中的半截文件一起清掉，白下几百 MB。
        /// </summary>
        public static List<FileInfo> ListPartFiles(string appId, HashSet<string> keep)
        {
            List<FileInfo> list = new List<FileInfo>();
            foreach (string root in AllStoreRoots())
            {
                try
                {
                    string dir = Path.Combine(root, appId);
                    if (!Directory.Exists(dir)) continue;
                    foreach (string full in Directory.GetFiles(dir, "*.part"))
                    {
                        if (keep != null && keep.Contains(full)) continue;
                        try { list.Add(new FileInfo(full)); } catch { }
                    }
                }
                catch { }
            }
            return list;
        }

        /// <summary>碎片旁边的指纹文件：记 url 和总长，用来判断「这是不是同一个包」</summary>
        public static string PartMetaPath(string partPath)
        {
            return partPath + ".meta";
        }

        public static void DeletePart(string partPath)
        {
            try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
            try
            {
                string meta = PartMetaPath(partPath);
                if (File.Exists(meta)) File.Delete(meta);
            }
            catch { }
        }

        private static void ReadPartMeta(string metaPath, out string url, out long total, out string validator)
        {
            url = "";
            total = 0;
            validator = "";
            try
            {
                if (!File.Exists(metaPath)) return;
                string[] lines = File.ReadAllLines(metaPath);
                if (lines.Length >= 1) url = lines[0];
                if (lines.Length >= 2)
                {
                    long.TryParse(lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out total);
                }
                if (lines.Length >= 3) validator = lines[2];
            }
            catch { }
        }

        private static void WritePartMeta(string metaPath, string url, long total, string validator)
        {
            try
            {
                File.WriteAllText(metaPath,
                    url + "\r\n" + total.ToString(CultureInfo.InvariantCulture) + "\r\n" + (validator ?? "") + "\r\n",
                    new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>优先 ETag，退而求其次用 Last-Modified：续传时当 If-Range 用</summary>
        private static string ValidatorOf(HttpResponseMessage res)
        {
            try
            {
                if (res.Headers.ETag != null)
                {
                    string tag = res.Headers.ETag.Tag;
                    if (!string.IsNullOrEmpty(tag)) return tag;
                }
            }
            catch { }
            try
            {
                if (res.Content.Headers.LastModified.HasValue)
                    return res.Content.Headers.LastModified.Value.ToString("R", CultureInfo.InvariantCulture);
            }
            catch { }
            return "";
        }

        public static bool MatchesVersion(CachedFile f, string version)
        {
            if (string.IsNullOrEmpty(version)) return false;
            if (f.VersionShort == version || f.Version == version) return true;
            return f.Version.Length > 0 && f.Version.StartsWith(version + ".", StringComparison.Ordinal);
        }

        /// <summary>
        /// 可以清理的缓存安装包。规则：
        ///   · 错版包一律清；
        ///   · 当前已安装版本的安装包也清——已经装上了，留着只占空间；
        ///   · 只有「锁定版本且不是当前安装版本」才有回退价值，保留。
        /// </summary>
        public static List<CachedFile> ListUseless(string appId, string installed, string locked)
        {
            List<CachedFile> list = new List<CachedFile>();
            foreach (CachedFile f in ListCached(appId))
            {
                if (!f.Usable) { list.Add(f); continue; }
                if (MatchesVersion(f, installed)) { list.Add(f); continue; }
                if (MatchesVersion(f, locked)) continue;
                list.Add(f);
            }
            return list;
        }

        public static CachedFile FindCached(string appId, string version)
        {
            foreach (CachedFile f in ListCached())
            {
                if (!string.Equals(f.AppId, appId, StringComparison.OrdinalIgnoreCase)) continue;
                if (f.Platform != "win32-x64-user") continue;
                if (!f.Usable) continue;
                if (f.VersionShort == version || f.Version == version) return f;
                if (f.Version != null && f.Version.StartsWith(version + ".", StringComparison.Ordinal)) return f;
            }
            return null;
        }

        /// <summary>
        /// 把 url 下到 partPath，支持断点续传。
        /// offset 是 part 已有长度；服务器不认 Range（回 200）时自动从头写，避免拼出坏文件。
        /// </summary>
        public static async Task<long> TransferAsync(string url, string partPath, long offset,
            Action<long, long, double> onProgress, CancellationToken token)
        {
            if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("下载地址为空");
            string dir = Path.GetDirectoryName(partPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string metaPath = PartMetaPath(partPath);
            string metaUrl;
            long metaTotal;
            string metaValidator;
            ReadPartMeta(metaPath, out metaUrl, out metaTotal, out metaValidator);
            if (offset > 0)
            {
                // 没有指纹（旧版本留下的碎片）或地址变了：来路不明，不能接在后面下
                bool trusted = metaUrl.Length > 0 && string.Equals(metaUrl, url, StringComparison.Ordinal);
                if (!trusted)
                {
                    DeletePart(partPath);
                    offset = 0;
                }
            }

            using (HttpClient http = new HttpClient(Net.NewHandler()))
            {
                http.Timeout = TimeSpan.FromMinutes(30);
                http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) WorkBuddy-Version-Locker/1.2");
                http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");

                using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    if (offset > 0)
                    {
                        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);
                        // If-Range：文件一换，服务器就回 200 整包，下面的逻辑会从头写，
                        // 不会把新包接在旧碎片后面拼出个坏文件
                        if (!string.IsNullOrEmpty(metaValidator))
                            req.Headers.TryAddWithoutValidation("If-Range", metaValidator);
                    }
                    using (HttpResponseMessage res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                    {
                        if (res.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0)
                        {
                            // part 比服务器上的包还长：不是同一个文件，重下
                            DeletePart(partPath);
                            return await TransferAsync(url, partPath, 0, onProgress, token).ConfigureAwait(false);
                        }
                        if (!res.IsSuccessStatusCode)
                            throw new InvalidOperationException("下载失败：HTTP " + (int)res.StatusCode);

                        bool resumed = res.StatusCode == System.Net.HttpStatusCode.PartialContent;
                        long start = resumed ? offset : 0;
                        if (!resumed && offset > 0)
                        {
                            DeletePart(partPath);
                        }

                        long length = res.Content.Headers.ContentLength.HasValue ? res.Content.Headers.ContentLength.Value : 0;
                        long total = length > 0 ? start + length : 0;

                        if (resumed && total > 0 && metaTotal > 0 && total != metaTotal)
                        {
                            // 文件换了（CDN 重新打包）：旧碎片接上去只会拼出坏包
                            DeletePart(partPath);
                            return await TransferAsync(url, partPath, 0, onProgress, token).ConfigureAwait(false);
                        }

                        WritePartMeta(metaPath, url, total, ValidatorOf(res));

                        using (Stream input = await res.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (FileStream output = new FileStream(partPath, start > 0 ? FileMode.Append : FileMode.Create,
                                   FileAccess.Write, FileShare.None, 81920, true))
                        {
                            byte[] buffer = new byte[81920];
                            long received = start;
                            Stopwatch sw = Stopwatch.StartNew();
                            long lastBytes = received;
                            double lastMs = 0;
                            if (onProgress != null) onProgress(received, total, 0);
                            while (true)
                            {
                                token.ThrowIfCancellationRequested();
                                int n = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                                if (n <= 0) break;
                                await output.WriteAsync(buffer, 0, n, token).ConfigureAwait(false);
                                received += n;

                                double ms = sw.Elapsed.TotalMilliseconds;
                                if (ms - lastMs >= 400)
                                {
                                    double speed = (received - lastBytes) * 1000.0 / Math.Max(1.0, ms - lastMs);
                                    lastBytes = received;
                                    lastMs = ms;
                                    if (onProgress != null) onProgress(received, total, speed);
                                }
                            }
                            output.Flush();
                            if (onProgress != null) onProgress(received, total > 0 ? total : received, 0);
                            return received;
                        }
                    }
                }
            }
        }

        public static string Sha256(string file)
        {
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] hash = sha.ComputeHash(fs);
                System.Text.StringBuilder sb = new System.Text.StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }
}
