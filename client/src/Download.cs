using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
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

    public sealed class DownloadJob
    {
        public string AppId = "";
        public string Version = "";
        public string Url = "";
        public string FilePath = "";
        public long Received;
        public long Total;
        public double Speed;
        public string Status = "downloading";
        public string Error = "";
        public int Percent
        {
            get { return Total > 0 ? (int)Math.Min(100, Math.Round(Received * 100.0 / Total)) : 0; }
        }
        public string SpeedText { get { return Fmt.Bytes((long)Speed) + "/s"; } }
    }

    public static class Downloads
    {
        private static readonly Regex VersionRe = new Regex(@"(\d+\.\d+\.\d+\.\d+)");

        /// <summary>界面那边把实时目录塞进来，用于判断缓存包属于哪一版</summary>
        public static Func<Catalog> CatalogProvider = delegate { return Catalog.Current; };

        /// <summary>
        /// 按官方链接里的文件名反查这个安装包属于哪一版。
        /// 旧版本的工具把国内版包下进了 store\workbuddyai，光看文件名分不出来，
        /// 但和官方链接对一下就能认出来。返回 "" 表示对得上，否则返回它真正的归属。
        /// </summary>
        public static string DetectWrongEdition(string appId, string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return "";
            Catalog cat;
            try { cat = CatalogProvider(); }
            catch { return ""; }
            if (cat == null) return "";

            if (NameInCatalog(cat, appId, fileName)) return "";
            string other = appId == "workbuddyai" ? "workbuddy" : "workbuddyai";
            if (NameInCatalog(cat, other, fileName)) return other;
            return "";
        }

        private static bool NameInCatalog(Catalog cat, string appId, string fileName)
        {
            foreach (Release rel in cat.Releases)
            {
                Dictionary<string, PackageInfo> map = appId == "workbuddyai" ? rel.IntlPackages : rel.Packages;
                foreach (KeyValuePair<string, PackageInfo> kv in map)
                {
                    if (kv.Value == null || string.IsNullOrEmpty(kv.Value.Url)) continue;
                    if (string.Equals(FileNameFromUrl(kv.Value.Url), fileName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        public static string FileNameFromUrl(string url)
        {
            try
            {
                Uri u = new Uri(url);
                return Uri.UnescapeDataString(Path.GetFileName(u.AbsolutePath));
            }
            catch
            {
                return "package-" + DateTime.Now.Ticks.ToString(CultureInfo.InvariantCulture) + ".exe";
            }
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
            List<FileInfo> list = new List<FileInfo>();
            foreach (string root in AllStoreRoots())
            {
                try
                {
                    string dir = Path.Combine(root, appId);
                    if (!Directory.Exists(dir)) continue;
                    foreach (string full in Directory.GetFiles(dir, "*.part"))
                    {
                        try { list.Add(new FileInfo(full)); } catch { }
                    }
                }
                catch { }
            }
            return list;
        }

        public static bool MatchesVersion(CachedFile f, string version)
        {
            if (string.IsNullOrEmpty(version)) return false;
            if (f.VersionShort == version || f.Version == version) return true;
            return f.Version.Length > 0 && f.Version.StartsWith(version + ".", StringComparison.Ordinal);
        }

        /// <summary>既不是当前安装版本、也不是锁定版本的缓存安装包</summary>
        public static List<CachedFile> ListUseless(string appId, string installed, string locked)
        {
            List<CachedFile> list = new List<CachedFile>();
            foreach (CachedFile f in ListCached(appId))
            {
                // 错版的包一律算无用，不管版本号对不对
                if (!f.Usable) { list.Add(f); continue; }
                if (MatchesVersion(f, installed)) continue;
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

        public static async Task<DownloadJob> DownloadAsync(string appId, string version, string url,
            IProgress<DownloadJob> progress, CancellationToken token)
        {
            DownloadJob job = new DownloadJob();
            job.AppId = appId;
            job.Version = version;
            job.Url = url;

            if (string.IsNullOrEmpty(url))
            {
                job.Status = "error";
                job.Error = "该版本没有可用的 Windows 安装包链接";
                return job;
            }

            string dir = Path.Combine(Paths.StoreRoot, appId);
            Directory.CreateDirectory(dir);
            string name = FileNameFromUrl(url);
            string file = Path.Combine(dir, name);
            job.FilePath = file;

            string part = file + ".part";
            try
            {
                using (HttpClient http = new HttpClient(Net.NewHandler()))
                {
                    http.Timeout = TimeSpan.FromMinutes(30);
                    http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) WorkBuddy-Version-Locker/1.1");
                    http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");

                    using (HttpResponseMessage res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                    {
                        if (!res.IsSuccessStatusCode)
                        {
                            job.Status = "error";
                            job.Error = "下载失败：HTTP " + (int)res.StatusCode;
                            return job;
                        }
                        job.Total = res.Content.Headers.ContentLength.HasValue ? res.Content.Headers.ContentLength.Value : 0;
                        if (progress != null) progress.Report(job);

                        using (Stream input = await res.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (FileStream output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                        {
                            byte[] buffer = new byte[81920];
                            Stopwatch sw = Stopwatch.StartNew();
                            long lastBytes = 0;
                            double lastMs = 0;
                            while (true)
                            {
                                token.ThrowIfCancellationRequested();
                                int n = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                                if (n <= 0) break;
                                await output.WriteAsync(buffer, 0, n, token).ConfigureAwait(false);
                                job.Received += n;

                                double ms = sw.Elapsed.TotalMilliseconds;
                                if (ms - lastMs >= 400)
                                {
                                    job.Speed = (job.Received - lastBytes) * 1000.0 / Math.Max(1.0, ms - lastMs);
                                    lastBytes = job.Received;
                                    lastMs = ms;
                                    if (progress != null) progress.Report(job);
                                }
                            }
                            output.Flush();
                        }
                    }
                }

                if (File.Exists(file)) { try { File.Delete(file); } catch { } }
                File.Move(part, file);
                job.Status = "done";
                job.Speed = 0;
                if (job.Total <= 0) job.Total = job.Received;
                if (progress != null) progress.Report(job);
                return job;
            }
            catch (OperationCanceledException)
            {
                job.Status = "cancelled";
                job.Error = "已取消";
                try { File.Delete(part); } catch { }
                return job;
            }
            catch (Exception ex)
            {
                job.Status = "error";
                job.Error = ex.Message;
                job.Speed = 0;
                try { File.Delete(part); } catch { }
                if (progress != null) progress.Report(job);
                return job;
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
