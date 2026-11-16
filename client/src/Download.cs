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

        public static List<CachedFile> ListCached()
        {
            List<CachedFile> list = new List<CachedFile>();
            try
            {
                if (!Directory.Exists(Paths.StoreRoot)) return list;
                foreach (string appDir in Directory.GetDirectories(Paths.StoreRoot))
                {
                    string appId = Path.GetFileName(appDir);
                    foreach (string full in Directory.GetFiles(appDir))
                    {
                        string ext = Path.GetExtension(full).ToLowerInvariant();
                        if (ext != ".exe" && ext != ".dmg" && ext != ".zip" && ext != ".appimage") continue;
                        FileInfo fi = new FileInfo(full);
                        CachedFile f = new CachedFile();
                        f.AppId = appId;
                        f.FileName = fi.Name;
                        f.FullPath = full;
                        f.Size = fi.Length;
                        f.MTime = fi.LastWriteTimeUtc;
                        Match m = VersionRe.Match(fi.Name);
                        f.Version = m.Success ? m.Groups[1].Value : "";
                        f.VersionShort = Fmt.ShortVersion(f.Version);
                        f.Build = f.Version;
                        f.Platform = PlatformFromName(fi.Name);
                        list.Add(f);
                    }
                }
            }
            catch { }
            list.Sort(delegate(CachedFile a, CachedFile b) { return b.MTime.CompareTo(a.MTime); });
            return list;
        }

        public static CachedFile FindCached(string appId, string version)
        {
            foreach (CachedFile f in ListCached())
            {
                if (!string.Equals(f.AppId, appId, StringComparison.OrdinalIgnoreCase)) continue;
                if (f.Platform != "win32-x64-user") continue;
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
                HttpClientHandler handler = new HttpClientHandler();
                handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                using (HttpClient http = new HttpClient(handler))
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
