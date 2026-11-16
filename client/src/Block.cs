using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace WbVm
{
    public sealed class InstallerFile
    {
        public string Name = "";
        public string Version = "";
        public long Size;
        public DateTime MTime;
        public string SizeText { get { return Fmt.Bytes(Size); } }
    }

    public sealed class CacheDirInfo
    {
        public string Path = "";
        public bool Exists;
        public bool Writable = true;
        public List<InstallerFile> Installers = new List<InstallerFile>();
        public long Total;
    }

    public sealed class ConfigInfo
    {
        public string ConfigFile = "";
        public bool ConfigExists;
        public bool? ConfigValue;
        public string SettingsFile = "";
        public bool SettingsExists;
        public bool? SettingsValue;
        public bool Disabled;
    }

    public sealed class FailureInfo
    {
        public bool Exists;
        public string Version = "";
        public int Count;
        public string Code = "";
        public string LastAt = "";
    }

    public sealed class LegacyInfo
    {
        public string Dir = "";
        public string StagedKind = "none"; // none | file | placeholder
        public bool Blocked;
        public List<string> Vault = new List<string>();
    }

    public sealed class BlockStatus
    {
        public string AppId = "";
        public string Name = "";
        public string Edition = "";
        public string RuntimeDir = "";
        public string StoreDir = "";
        public CacheDirInfo[] Dirs;
        public List<InstallerFile> Installers = new List<InstallerFile>();
        public long Total;
        public bool CacheBlocked;
        public ConfigInfo Config;
        public FailureInfo Failure;
        public LegacyInfo Legacy;
        public bool Enabled;
        public string BlockedAt = "";
    }

    public sealed class BlockResult
    {
        public bool Ok;
        public string AppId = "";
        public List<string> Moved = new List<string>();
        public List<string> Cleared = new List<string>();
        public string Error = "";
        public BlockStatus Status;
    }

    public static class UpdateBlock
    {
        private static readonly Regex InstallerRe = new Regex(@"^WorkBuddy-Setup-(\d+(?:\.\d+)+)\.exe$", RegexOptions.IgnoreCase);
        private static readonly Regex TmpRe = new Regex(@"^WorkBuddy-Setup-.*\.exe\.tmp$", RegexOptions.IgnoreCase);

        private static readonly Dictionary<string, KeyValuePair<DateTime, bool>> WritableCache =
            new Dictionary<string, KeyValuePair<DateTime, bool>>(StringComparer.OrdinalIgnoreCase);

        /* ---------------- 进程调用 ---------------- */

        public sealed class ProcResult
        {
            public int Code;
            public string Output = "";
            public bool Ok { get { return Code == 0; } }
        }

        public static ProcResult Run(string exe, string args, int timeoutMs)
        {
            ProcResult r = new ProcResult();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                using (Process p = new Process())
                {
                    p.StartInfo = psi;
                    StringBuilder sb = new StringBuilder();
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) sb.AppendLine(e.Data); };
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) sb.AppendLine(e.Data); };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        r.Code = -1;
                        r.Output = "timeout";
                        return r;
                    }
                    r.Code = p.ExitCode;
                    r.Output = sb.ToString();
                    return r;
                }
            }
            catch (Exception ex)
            {
                r.Code = -1;
                r.Output = ex.Message;
                return r;
            }
        }

        /* ---------------- ACL ---------------- */

        private static bool DenyWrite(string dir)
        {
            string who = Paths.AccountName();
            if (string.IsNullOrEmpty(who)) throw new InvalidOperationException("无法确定当前 Windows 账户");
            Directory.CreateDirectory(dir);
            // 只拒「写数据/追加/写属性」，保留目录枚举能力（否则连自己都读不到缓存）
            ProcResult r = Run("icacls", "\"" + dir + "\" /deny \"" + who + ":(WD,AD,WEA,WA)\"", 8000);
            if (!r.Ok) throw new InvalidOperationException("设置写入拒绝失败：" + r.Output.Trim());
            return true;
        }

        private static void AllowWrite(string dir)
        {
            string who = Paths.AccountName();
            if (string.IsNullOrEmpty(who)) return;
            if (!Directory.Exists(dir)) return;
            Run("icacls", "\"" + dir + "\" /remove:d \"" + who + "\"", 8000);
        }

        public static bool ProbeWritable(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return true;
            DateTime now = DateTime.UtcNow;
            KeyValuePair<DateTime, bool> hit;
            if (WritableCache.TryGetValue(dir, out hit) && (now - hit.Key).TotalSeconds < 10) return hit.Value;

            bool value = true;
            try
            {
                string probe = Path.Combine(dir, ".wbvm-probe-" + Process.GetCurrentProcess().Id + "-" + now.Ticks.ToString(CultureInfo.InvariantCulture));
                File.WriteAllText(probe, "x");
                try { File.Delete(probe); } catch { }
            }
            catch
            {
                value = false;
            }
            WritableCache[dir] = new KeyValuePair<DateTime, bool>(now, value);
            return value;
        }

        public static void ClearProbeCache() { WritableCache.Clear(); }

        /* ---------------- 状态 ---------------- */

        private static bool? BoolOrNull(JsonValue obj, string key)
        {
            if (obj == null) return null;
            JsonValue v = obj.Get(key);
            if (v == null || v.Type != JsonValue.Kind.Bool) return null;
            return v.BoolValue;
        }

        public static BlockStatus GetStatus(string appId)
        {
            AppProfile profile = Paths.ProfileOf(appId);
            BlockStatus st = new BlockStatus();
            st.AppId = appId;
            st.Name = profile.Name;
            st.Edition = profile.Edition;
            st.RuntimeDir = profile.RuntimeDir;
            st.StoreDir = profile.StoreDir;

            List<CacheDirInfo> dirs = new List<CacheDirInfo>();
            foreach (string dir in profile.CacheDirs)
            {
                CacheDirInfo info = new CacheDirInfo();
                info.Path = dir;
                info.Exists = Directory.Exists(dir);
                if (info.Exists)
                {
                    try
                    {
                        foreach (string full in Directory.GetFiles(dir))
                        {
                            string name = Path.GetFileName(full);
                            Match m = InstallerRe.Match(name);
                            if (!m.Success) continue;
                            FileInfo fi = new FileInfo(full);
                            InstallerFile f = new InstallerFile();
                            f.Name = name;
                            f.Version = m.Groups[1].Value;
                            f.Size = fi.Length;
                            f.MTime = fi.LastWriteTimeUtc;
                            info.Installers.Add(f);
                            info.Total += f.Size;
                        }
                    }
                    catch { }
                    info.Installers.Sort(delegate(InstallerFile a, InstallerFile b) { return b.MTime.CompareTo(a.MTime); });
                    info.Writable = ProbeWritable(dir);
                }
                dirs.Add(info);
            }
            st.Dirs = dirs.ToArray();
            foreach (CacheDirInfo d in dirs)
            {
                st.Total += d.Total;
                st.Installers.AddRange(d.Installers);
                if (d.Exists && !d.Writable) st.CacheBlocked = true;
            }

            ConfigInfo cfg = new ConfigInfo();
            cfg.ConfigFile = profile.ConfigFile;
            JsonValue cfgJson = JsonFile.Read(profile.ConfigFile);
            cfg.ConfigExists = cfgJson != null;
            cfg.ConfigValue = BoolOrNull(cfgJson, "startupForceAutoUpdate");
            cfg.SettingsFile = profile.SettingsFile;
            JsonValue settingsJson = JsonFile.Read(profile.SettingsFile);
            cfg.SettingsExists = settingsJson != null;
            cfg.SettingsValue = BoolOrNull(settingsJson, "startupForceAutoUpdate");
            cfg.Disabled = (cfg.ConfigValue.HasValue && cfg.ConfigValue.Value == false)
                        || (cfg.SettingsValue.HasValue && cfg.SettingsValue.Value == false);
            st.Config = cfg;

            FailureInfo fi2 = new FailureInfo();
            JsonValue failure = JsonFile.Read(profile.FailureFile);
            if (failure != null)
            {
                fi2.Exists = true;
                fi2.Version = failure.GetString("version", "");
                JsonValue fc = failure.Get("failureCount");
                fi2.Count = fc == null ? 0 : fc.AsInt(0);
                fi2.Code = failure.GetString("lastCode", "");
                JsonValue tsNode = failure.Get("lastFailureAt");
                double ts = tsNode == null ? 0 : tsNode.AsDouble(0);
                if (ts > 0)
                {
                    try { fi2.LastAt = DateTimeOffset.FromUnixTimeMilliseconds((long)ts).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"); }
                    catch { }
                }
            }
            st.Failure = fi2;

            LegacyInfo legacy = new LegacyInfo();
            legacy.Dir = Paths.LegacyUpdaterDir;
            if (Directory.Exists(Paths.LegacyStaged)) { legacy.StagedKind = "placeholder"; legacy.Blocked = true; }
            else if (File.Exists(Paths.LegacyStaged)) legacy.StagedKind = "file";
            try
            {
                if (Directory.Exists(Paths.VaultDir))
                {
                    foreach (string f in Directory.GetFiles(Paths.VaultDir, "installer_*.exe"))
                        legacy.Vault.Add(Path.GetFileName(f));
                }
            }
            catch { }
            st.Legacy = legacy;

            JsonValue record = StateStore.Instance.GetBlock(appId);
            bool recorded = record != null && record.Get("enabled") != null && record.Get("enabled").AsBool(false);
            st.Enabled = recorded || cfg.Disabled || st.CacheBlocked;
            st.BlockedAt = record == null ? "" : record.GetString("at", "");
            return st;
        }

        /* ---------------- 开关 ---------------- */

        private static void MoveFile(string src, string dest)
        {
            try
            {
                File.Move(src, dest);
            }
            catch (IOException)
            {
                File.Copy(src, dest, true);
                File.Delete(src);
            }
        }

        private static List<string> RecycleInstallers(AppProfile profile)
        {
            List<string> moved = new List<string>();
            Directory.CreateDirectory(profile.StoreDir);
            foreach (string dir in profile.CacheDirs)
            {
                if (!Directory.Exists(dir)) continue;
                string[] files;
                try { files = Directory.GetFiles(dir); }
                catch { continue; }

                foreach (string full in files)
                {
                    string name = Path.GetFileName(full);
                    try
                    {
                        if (TmpRe.IsMatch(name))
                        {
                            try { File.Delete(full); } catch { }
                            continue;
                        }
                        Match m = InstallerRe.Match(name);
                        if (!m.Success) continue;
                        string version = m.Groups[1].Value;
                        string target = Path.Combine(profile.StoreDir, "WorkBuddy-win32-x64-user-" + version + "-recycled.exe");
                        if (File.Exists(target))
                        {
                            try { File.Delete(full); } catch { }
                            moved.Add(version + "（已存在，去重）");
                            continue;
                        }
                        MoveFile(full, target);
                        moved.Add(version);
                    }
                    catch (Exception ex)
                    {
                        moved.Add(name + " 回收失败：" + ex.Message);
                    }
                }
            }
            return moved;
        }

        private static List<string> BackupFailureRecord(AppProfile profile)
        {
            List<string> cleared = new List<string>();
            string[][] items = new string[][]
            {
                new string[] { profile.FailureFile, "win-install-failure" },
                new string[] { profile.AttemptFile, "win-install-attempt" }
            };
            foreach (string[] item in items)
            {
                if (!File.Exists(item[0])) continue;
                try
                {
                    Directory.CreateDirectory(Paths.BackupDir);
                    string dest = Path.Combine(Paths.BackupDir, profile.AppId + "-" + item[1] + "-" + Fmt.Stamp() + ".json");
                    try { File.Copy(item[0], dest, true); } catch { }
                    File.Delete(item[0]);
                    cleared.Add(item[1]);
                }
                catch { }
            }
            return cleared;
        }

        private static JsonValue SetStartupFlag(AppProfile profile, bool value)
        {
            JsonValue prev = JsonValue.NewObject();

            JsonValue cfg = JsonFile.Read(profile.ConfigFile);
            if (cfg == null) cfg = JsonValue.NewObject();
            JsonValue prevCfg = JsonValue.NewObject();
            prevCfg.Set("had", cfg.Has("startupForceAutoUpdate"));
            JsonValue cfgVal = cfg.Get("startupForceAutoUpdate");
            prevCfg.Set("value", cfgVal != null && cfgVal.Type == JsonValue.Kind.Bool ? JsonValue.From(cfgVal.BoolValue) : JsonValue.Null());
            prev.Set("config", prevCfg);
            cfg.Set("startupForceAutoUpdate", JsonValue.From(value));
            JsonFile.WriteAtomic(profile.ConfigFile, cfg);

            JsonValue prevSettings = JsonValue.NewObject();
            JsonValue settings = JsonFile.Read(profile.SettingsFile);
            if (settings != null)
            {
                prevSettings.Set("had", settings.Has("startupForceAutoUpdate"));
                JsonValue sVal = settings.Get("startupForceAutoUpdate");
                prevSettings.Set("value", sVal != null && sVal.Type == JsonValue.Kind.Bool ? JsonValue.From(sVal.BoolValue) : JsonValue.Null());
                settings.Set("startupForceAutoUpdate", JsonValue.From(value));
                JsonFile.WriteAtomic(profile.SettingsFile, settings);
            }
            else
            {
                prevSettings.Set("had", false);
                prevSettings.Set("skipped", true);
            }
            prev.Set("settings", prevSettings);
            return prev;
        }

        /// <summary>没有历史值可还原时，直接删键让客户端回到自己的默认行为</summary>
        private static void ClearStartupFlag(AppProfile profile)
        {
            JsonValue cfg = JsonFile.Read(profile.ConfigFile);
            if (cfg != null && cfg.Has("startupForceAutoUpdate"))
            {
                cfg.Remove("startupForceAutoUpdate");
                JsonFile.WriteAtomic(profile.ConfigFile, cfg);
            }
            JsonValue settings = JsonFile.Read(profile.SettingsFile);
            if (settings != null && settings.Has("startupForceAutoUpdate"))
            {
                settings.Remove("startupForceAutoUpdate");
                JsonFile.WriteAtomic(profile.SettingsFile, settings);
            }
        }

        private static void RestoreStartupFlag(AppProfile profile, JsonValue prev)
        {
            if (prev == null)
            {
                ClearStartupFlag(profile);
                return;
            }

            JsonValue prevCfg = prev.Get("config");
            if (prevCfg != null)
            {
                JsonValue cfg = JsonFile.Read(profile.ConfigFile);
                if (cfg == null) cfg = JsonValue.NewObject();
                // 只有当历史值是 true（用户显式开过自动更新）才还原；
                // 否则那个 false 多半是锁具自己写的，删键让它回到客户端默认。
                JsonValue value = prevCfg.Get("value");
                bool explicitTrue = value != null && value.Type == JsonValue.Kind.Bool && value.BoolValue;
                if (explicitTrue) cfg.Set("startupForceAutoUpdate", JsonValue.From(true));
                else cfg.Remove("startupForceAutoUpdate");
                JsonFile.WriteAtomic(profile.ConfigFile, cfg);
            }

            JsonValue prevSettings = prev.Get("settings");
            if (prevSettings != null && !(prevSettings.Get("skipped") != null && prevSettings.Get("skipped").AsBool(false)))
            {
                JsonValue settings = JsonFile.Read(profile.SettingsFile);
                if (settings != null)
                {
                    JsonValue value = prevSettings.Get("value");
                    bool explicitTrue = value != null && value.Type == JsonValue.Kind.Bool && value.BoolValue;
                    if (explicitTrue) settings.Set("startupForceAutoUpdate", JsonValue.From(true));
                    else settings.Remove("startupForceAutoUpdate");
                    JsonFile.WriteAtomic(profile.SettingsFile, settings);
                }
            }
        }

        private static void EnableLegacyQuarantine()
        {
            if (Directory.Exists(Paths.LegacyStaged)) return;
            Directory.CreateDirectory(Paths.LegacyUpdaterDir);
            if (File.Exists(Paths.LegacyStaged))
            {
                Directory.CreateDirectory(Paths.VaultDir);
                MoveFile(Paths.LegacyStaged, Path.Combine(Paths.VaultDir, "installer_" + Fmt.Stamp() + ".exe"));
            }
            Directory.CreateDirectory(Paths.LegacyStaged);
        }

        private static void DisableLegacyQuarantine()
        {
            if (!Directory.Exists(Paths.LegacyStaged)) return;
            try
            {
                if (Directory.GetFileSystemEntries(Paths.LegacyStaged).Length > 0) return;
                Directory.Delete(Paths.LegacyStaged);
            }
            catch { }
        }

        public static BlockResult Enable(string appId)
        {
            BlockResult result = new BlockResult();
            result.AppId = appId;
            AppProfile profile = Paths.ProfileOf(appId);
            if (profile == null)
            {
                result.Error = "未知应用";
                return result;
            }

            try
            {
                result.Moved = RecycleInstallers(profile);
                result.Cleared = BackupFailureRecord(profile);
                JsonValue prev = SetStartupFlag(profile, false);

                // 只对 x64 缓存目录加锁（arm64 目录通常不存在）
                foreach (string dir in profile.CacheDirs)
                {
                    if (dir.IndexOf("arm64", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    try { DenyWrite(dir); }
                    catch
                    {
                        try { Directory.CreateDirectory(dir); } catch { }
                    }
                }
                ClearProbeCache();

                JsonValue record = JsonValue.NewObject();
                record.Set("enabled", true);
                record.Set("at", DateTime.UtcNow.ToString("o"));
                record.Set("prev", prev);
                record.Set("moved", result.Moved.Count);
                JsonValue cleared = JsonValue.NewArray();
                foreach (string c in result.Cleared) cleared.Add(JsonValue.From(c));
                record.Set("cleared", cleared);
                StateStore.Instance.SetBlock(appId, record);

                try { EnableLegacyQuarantine(); } catch { }

                result.Ok = true;
                result.Status = GetStatus(appId);
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            return result;
        }

        public static BlockResult Disable(string appId)
        {
            BlockResult result = new BlockResult();
            result.AppId = appId;
            AppProfile profile = Paths.ProfileOf(appId);
            if (profile == null)
            {
                result.Error = "未知应用";
                return result;
            }

            try
            {
                foreach (string dir in profile.CacheDirs) AllowWrite(dir);
                ClearProbeCache();

                JsonValue record = StateStore.Instance.GetBlock(appId);
                RestoreStartupFlag(profile, record == null ? null : record.Get("prev"));
                StateStore.Instance.ClearBlock(appId);

                if (!StateStore.Instance.AnyBlockEnabled()) DisableLegacyQuarantine();

                result.Ok = true;
                result.Status = GetStatus(appId);
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            return result;
        }

        /// <summary>清掉熔断记录但保留拦截（用于「只修弹窗」场景）</summary>
        public static int ClearFailureRecord(string appId)
        {
            AppProfile profile = Paths.ProfileOf(appId);
            if (profile == null) return 0;
            return BackupFailureRecord(profile).Count;
        }
    }
}
