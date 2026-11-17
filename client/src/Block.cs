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
        /// <summary>active / partial / off，判断口径见 LockState</summary>
        public string State = LockState.Off;
        public string BlockedAt = "";
    }

    /// <summary>
    /// 锁定状态三态。C# 与网页版必须用同一套口径：
    ///   active  = 配置层关闭 + 缓存目录写入阻断，两层都在
    ///   partial = 只生效了其中一层（用户自己在客户端关掉更新、或 ACL 设置失败）
    ///   off     = 两层都没生效
    /// 关键点：不看工具自己的 state.json 记录。记录会过期、会被别的前端清掉，
    /// 而配置和 ACL 是本机事实，谁写的都一样。
    /// </summary>
    public static class LockState
    {
        public const string Off = "off";
        public const string Partial = "partial";
        public const string Active = "active";

        public static string Evaluate(bool configDisabled, bool cacheBlocked)
        {
            if (configDisabled && cacheBlocked) return Active;
            if (configDisabled || cacheBlocked) return Partial;
            return Off;
        }

        public static string Text(string state)
        {
            if (state == Active) return "已锁定";
            if (state == Partial) return "部分生效";
            return "未锁定";
        }
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
            // 三态只看本机事实（配置开关 + ACL），不看自己的记录：
            // 记录可能是别的工具写的，也可能在别的前端解除后过期。
            st.State = LockState.Evaluate(cfg.Disabled, st.CacheBlocked);
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

        /// <summary>
        /// 按键还原：锁定前有这个键就写回原值（true/false 都算），没有就删键回默认。
        /// 不能沿用「只有 true 才写回」的旧规则——那会把用户自己设的 false 一起删掉。
        /// </summary>
        private static void RestoreKey(JsonValue target, JsonValue prevNode)
        {
            if (target == null || prevNode == null) return;
            JsonValue hadNode = prevNode.Get("had");
            bool had = hadNode != null && hadNode.AsBool(false);
            JsonValue value = prevNode.Get("value");
            if (had && value != null && value.Type == JsonValue.Kind.Bool)
                target.Set("startupForceAutoUpdate", JsonValue.From(value.BoolValue));
            else
                target.Remove("startupForceAutoUpdate");
        }

        private static void RestoreStartupFlag(AppProfile profile, JsonValue prev)
        {
            if (prev == null) return;

            JsonValue prevCfg = prev.Get("config");
            if (prevCfg != null)
            {
                JsonValue cfg = JsonFile.Read(profile.ConfigFile);
                bool had = prevCfg.Get("had") != null && prevCfg.Get("had").AsBool(false);
                if (cfg != null || had)
                {
                    if (cfg == null) cfg = JsonValue.NewObject();
                    RestoreKey(cfg, prevCfg);
                    // 文件本来就不存在、也没有可还原的值时，不要凭空造一个空配置
                    if (cfg.Props != null && cfg.Props.Count > 0) JsonFile.WriteAtomic(profile.ConfigFile, cfg);
                }
            }

            JsonValue prevSettings = prev.Get("settings");
            if (prevSettings != null && !(prevSettings.Get("skipped") != null && prevSettings.Get("skipped").AsBool(false)))
            {
                JsonValue settings = JsonFile.Read(profile.SettingsFile);
                bool had = prevSettings.Get("had") != null && prevSettings.Get("had").AsBool(false);
                if (settings != null || had)
                {
                    if (settings == null) settings = JsonValue.NewObject();
                    RestoreKey(settings, prevSettings);
                    if (settings.Props != null && settings.Props.Count > 0) JsonFile.WriteAtomic(profile.SettingsFile, settings);
                }
            }
        }

        /// <summary>锁定前配置值的快照，落在客户端配置目录里，供任意前端解除锁定时还原</summary>
        internal static JsonValue ReadSnapshot(AppProfile profile)
        {
            JsonValue root = JsonFile.Read(profile.LockSnapshotFile);
            if (root == null) return null;
            return root.Get("prev");
        }

        private static void WriteSnapshot(AppProfile profile, JsonValue prev)
        {
            if (prev == null) return;
            JsonValue root = JsonValue.NewObject();
            root.Set("at", DateTime.UtcNow.ToString("o"));
            root.Set("appId", profile.AppId);
            root.Set("prev", prev);
            JsonFile.WriteAtomic(profile.LockSnapshotFile, root);
        }

        private static void DeleteSnapshot(AppProfile profile)
        {
            try
            {
                if (File.Exists(profile.LockSnapshotFile)) File.Delete(profile.LockSnapshotFile);
            }
            catch { }
        }

        /// <summary>
        /// 第一次动这个客户端的配置前，把原件留一份。解析失败时我们只能写回一个
        /// 新建对象，没有原件就真的找不回来了。
        /// </summary>
        private static void BackupConfigFiles(AppProfile profile)
        {
            try
            {
                string appDir = Path.GetDirectoryName(profile.ConfigFile);
                if (!string.IsNullOrEmpty(appDir)) Directory.CreateDirectory(appDir);
                BackupOne(profile.ConfigFile, Path.Combine(appDir, ".wbvm-backup-app-config.json"));
                BackupOne(profile.SettingsFile, Path.Combine(profile.RuntimeDir, ".wbvm-backup-settings.json"));
            }
            catch { }
        }

        private static void BackupOne(string src, string dest)
        {
            try { if (File.Exists(src)) File.Copy(src, dest, true); } catch { }
        }

        /// <summary>解锁时把「上次锁定顺手建出来的空缓存目录」收掉，别在 TEMP 里留垃圾</summary>
        private static void RemoveCreatedDirs(JsonValue record)
        {
            if (record == null) return;
            JsonValue list = record.Get("createdDirs");
            if (list == null) return;
            foreach (JsonValue item in list.Enumerate())
            {
                string dir = item.AsString("");
                if (string.IsNullOrEmpty(dir)) continue;
                try
                {
                    if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                        Directory.Delete(dir);
                }
                catch { }
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

                // 已经锁过一次时，当前配置里的 false 是我们自己写的，不能拿它覆盖原始值
                JsonValue oldRecord = StateStore.Instance.GetBlock(appId);
                JsonValue prev = oldRecord != null && oldRecord.Get("prev") != null
                    ? oldRecord.Get("prev")
                    : ReadSnapshot(profile);
                if (prev == null) BackupConfigFiles(profile);
                JsonValue now = SetStartupFlag(profile, false);
                if (prev == null) prev = now;
                WriteSnapshot(profile, prev);

                // 每个架构的缓存目录都要挡：x64 仿真下更新器写 x64 目录，
                // 原生 ARM64 客户端写 arm64 目录，跳过哪个都会漏。
                List<string> aclErrors = new List<string>();
                JsonValue createdDirs = JsonValue.NewArray();
                foreach (string dir in profile.CacheDirs)
                {
                    bool existed = Directory.Exists(dir);
                    try
                    {
                        DenyWrite(dir);
                        if (!existed) createdDirs.Add(JsonValue.From(dir));
                    }
                    catch (Exception ex) { aclErrors.Add(dir + " → " + ex.Message); }
                }
                ClearProbeCache();

                try { EnableLegacyQuarantine(); } catch { }

                if (aclErrors.Count > 0)
                {
                    // 没有 ACL 就等于没拦住下载。宁可报失败，也不要写一条 enabled 假装锁上了。
                    result.Ok = false;
                    result.Error = "缓存目录写入阻断失败，拦截没有完成：\n" +
                        string.Join("\n", aclErrors.ToArray()) +
                        "\n\n启动开关已关闭、已下载的安装包已回收，但客户端仍可能把新版本下下来。";
                    result.Status = GetStatus(appId);
                    return result;
                }

                JsonValue record = JsonValue.NewObject();
                record.Set("enabled", true);
                record.Set("at", DateTime.UtcNow.ToString("o"));
                record.Set("prev", prev);
                record.Set("createdDirs", createdDirs);
                record.Set("moved", result.Moved.Count);
                JsonValue cleared = JsonValue.NewArray();
                foreach (string c in result.Cleared) cleared.Add(JsonValue.From(c));
                record.Set("cleared", cleared);
                StateStore.Instance.SetBlock(appId, record);

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

                // 撤掉拒绝项之后再探一次：还是写不进去就说明没解开，别报成功
                List<string> stuck = new List<string>();
                foreach (string dir in profile.CacheDirs)
                    if (Directory.Exists(dir) && !ProbeWritable(dir)) stuck.Add(dir);
                if (stuck.Count > 0)
                {
                    result.Ok = false;
                    result.Error = "缓存目录仍不可写，解除没有完成：\n" + string.Join("\n", stuck.ToArray());
                    result.Status = GetStatus(appId);
                    return result;
                }

                JsonValue record = StateStore.Instance.GetBlock(appId);
                RemoveCreatedDirs(record);
                JsonValue prev = record != null && record.Get("prev") != null
                    ? record.Get("prev")
                    : ReadSnapshot(profile);

                // 找不到原始值就不要动客户端配置：那个 false 可能是用户自己在客户端里设的，
                // 删掉它等于替用户把自动更新打开。
                if (prev != null) RestoreStartupFlag(profile, prev);
                DeleteSnapshot(profile);
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

        /// <summary>把客户端更新缓存里的待安装包挪进工具自己的 store，避免装完又被顶回新版本</summary>
        public static List<string> RecycleCachedInstallers(string appId)
        {
            AppProfile profile = Paths.ProfileOf(appId);
            if (profile == null) return new List<string>();
            try { return RecycleInstallers(profile); }
            catch { return new List<string>(); }
        }
    }
}
