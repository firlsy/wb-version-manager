using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace WbVm
{
    public sealed class InstallInfo
    {
        public string AppId = "";
        public string InstallDir = "";
        public string ExePath = "";
        public string Version = "";
        public string VersionSource = "";
        public string BuiltAt = "";
        public long AsarSize;
        public bool Found;
    }

    public static class Detector
    {
        private static IEnumerable<string> ScanRoots()
        {
            List<string> roots = new List<string>();
            Action<string> add = delegate(string p)
            {
                if (!string.IsNullOrEmpty(p) && !roots.Contains(p)) roots.Add(p);
            };

            add(Environment.GetEnvironmentVariable("ProgramFiles"));
            add(Environment.GetEnvironmentVariable("ProgramW6432"));
            add(Environment.GetEnvironmentVariable("ProgramFiles(x86)"));

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local)) add(Path.Combine(local, "Programs"));

            string letters = "CDEFGHIJ";
            for (int i = 0; i < letters.Length; i++)
            {
                string drive = letters[i] + ":";
                if (!Directory.Exists(drive + "\\")) continue;
                add(drive + "\\Program Files");
                add(drive + "\\Program Files (x86)");
                add(drive + "\\Programs");
            }
            return roots;
        }

        public static InstallInfo Detect(string appId, string customPath)
        {
            AppDef app = Apps.ById(appId);
            InstallInfo info = new InstallInfo();
            info.AppId = appId;
            if (app == null) return info;

            List<string> candidates = new List<string>();
            if (!string.IsNullOrEmpty(customPath)) candidates.Add(customPath);

            foreach (string root in ScanRoots())
            {
                candidates.Add(Path.Combine(root, app.Name));
                candidates.Add(Path.Combine(root, app.Name.Replace(" ", "")));
                if (appId == "workbuddyai")
                {
                    candidates.Add(Path.Combine(root, "WorkBuddyAI"));
                    candidates.Add(Path.Combine(root, "WorkBuddy AI"));
                }
            }

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string dir in candidates)
            {
                if (!seen.Add(dir)) continue;
                InstallInfo found = ProbeDir(dir, app);
                if (found != null) return found;
            }
            return info;
        }

        private static InstallInfo ProbeDir(string dir, AppDef app)
        {
            try
            {
                string exe = Path.Combine(dir, app.Exe);
                if (!File.Exists(exe)) return null;

                InstallInfo info = new InstallInfo();
                info.AppId = app.Id;
                info.InstallDir = dir;
                info.ExePath = exe;
                info.Found = true;

                string asar = Path.Combine(dir, "resources", "app.asar");
                try
                {
                    FileInfo fi = new FileInfo(asar);
                    if (fi.Exists) info.AsarSize = fi.Length;
                }
                catch { }

                string manifest = Path.Combine(dir, "resources", "install-manifest.json");
                JsonValue m = JsonFile.Read(manifest);
                if (m != null)
                {
                    string v = m.GetString("appVersion", "");
                    if (!string.IsNullOrEmpty(v))
                    {
                        info.Version = v;
                        info.VersionSource = "install-manifest.json";
                        info.BuiltAt = m.GetString("generatedAt", "");
                        return info;
                    }
                }

                string asarVersion = ReadAsarPackageVersion(asar);
                if (!string.IsNullOrEmpty(asarVersion))
                {
                    info.Version = asarVersion;
                    info.VersionSource = "app.asar/package.json";
                }
                return info;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>只解析 asar 头部，取 package.json 的 version</summary>
        public static string ReadAsarPackageVersion(string asarPath)
        {
            try
            {
                if (!File.Exists(asarPath)) return null;
                using (FileStream fs = new FileStream(asarPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] head = new byte[16];
                    if (fs.Read(head, 0, 16) != 16) return null;
                    uint headerSize = BitConverter.ToUInt32(head, 12);
                    if (headerSize == 0 || headerSize > 64 * 1024 * 1024) return null;

                    byte[] hbuf = new byte[headerSize];
                    int read = 0;
                    while (read < hbuf.Length)
                    {
                        int n = fs.Read(hbuf, read, hbuf.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read != hbuf.Length) return null;

                    JsonValue header = JsonValue.Parse(Encoding.UTF8.GetString(hbuf));
                    JsonValue files = header.Get("files");
                    if (files == null) return null;
                    JsonValue entry = files.Get("package.json");
                    if (entry == null) return null;
                    if (entry.Get("files") != null) return null;

                    long size = (long)entry.Get("size").AsDouble(0);
                    long offset = (long)entry.Get("offset").AsDouble(0);
                    if (size <= 0 || size > 4 * 1024 * 1024) return null;

                    fs.Seek(16L + headerSize + offset, SeekOrigin.Begin);
                    byte[] data = new byte[size];
                    read = 0;
                    while (read < data.Length)
                    {
                        int n = fs.Read(data, read, data.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read != data.Length) return null;

                    JsonValue pkg = JsonValue.Parse(Encoding.UTF8.GetString(data));
                    return pkg.GetString("version", null);
                }
            }
            catch
            {
                return null;
            }
        }

        public static bool IsRunning(string appId)
        {
            AppDef app = Apps.ById(appId);
            if (app == null) return false;
            try
            {
                Process[] list = Process.GetProcessesByName(app.Process);
                return list != null && list.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        public static void Kill(string appId)
        {
            AppDef app = Apps.ById(appId);
            if (app == null) return;
            try
            {
                foreach (Process p in Process.GetProcessesByName(app.Process))
                {
                    try { p.Kill(); } catch { }
                }
            }
            catch { }
        }
    }
}
