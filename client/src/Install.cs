using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace WbVm
{
    public sealed class PrepareResult
    {
        public bool Ok;
        public string AppId = "";
        public string Version = "";
        public string BeforeVersion = "";
        public string InstallDir = "";
        public string InstallerPath = "";
        public string InstallerDir = "";
        public string Error = "";
        public bool WasLocked;
        public bool Unlocked;
        public int ClearedRecords;
        public int Recycled;
        public List<string> Steps = new List<string>();
    }

    /// <summary>
    /// 回退只做「环境准备」，不代替用户安装。
    /// 安装器由用户双击运行，由它自己匹配原安装目录，工具不传 /D，也不静默。
    /// </summary>
    public static class InstallService
    {
        public static async Task<PrepareResult> PrepareAsync(string appId, string version, string installerPath,
            string sha256, IProgress<string> log)
        {
            PrepareResult result = new PrepareResult();
            result.AppId = appId;
            result.Version = version;
            result.InstallerPath = installerPath;

            Action<string> say = delegate(string msg)
            {
                result.Steps.Add(msg);
                if (log != null) log.Report(msg);
            };

            AppDef app = Apps.ById(appId);
            if (app == null) { result.Error = "未知应用"; return result; }

            if (string.IsNullOrEmpty(installerPath) || !File.Exists(installerPath))
            {
                result.Error = "安装包不存在，请先下载";
                return result;
            }
            result.InstallerDir = Path.GetDirectoryName(installerPath);

            string custom = StateStore.Instance.CustomPath(appId);
            InstallInfo before = Detector.Detect(appId, custom);
            result.BeforeVersion = before.Found ? before.Version : "";
            result.InstallDir = before.Found ? before.InstallDir : "";

            /* 1. 校验安装包 */
            if (!string.IsNullOrEmpty(sha256))
            {
                say("校验安装包 SHA-256…");
                string actual = await Task.Run(delegate { return Downloads.Sha256(installerPath); }).ConfigureAwait(false);
                if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
                {
                    result.Error = "安装包 SHA-256 校验失败，已中止（文件可能没下完整，清掉重新下载）";
                    return result;
                }
                say("SHA-256 校验通过");
            }
            else
            {
                say("官方未提供 SHA-256，跳过校验");
            }

            /* 2. 结束正在运行的客户端，否则安装器写不进程序目录 */
            if (Detector.IsRunning(appId))
            {
                say("结束 " + app.Name + " 进程…");
                Detector.Kill(appId);
                for (int i = 0; i < 25; i++)
                {
                    if (!Detector.IsRunning(appId)) break;
                    await Task.Delay(300).ConfigureAwait(false);
                }
                if (Detector.IsRunning(appId))
                    say("警告：进程仍未退出，安装时可能提示文件被占用");
                else
                    say("已关闭 " + app.Name);
            }
            else
            {
                say(app.Name + " 未在运行");
            }

            /* 3. 自动解除锁定：还原启动开关、放开缓存目录写入、清掉拦截记录 */
            BlockStatus st = UpdateBlock.GetStatus(appId);
            // partial 也要清：配置层关着、ACL 没加上，同样会让安装器/缓存回收出问题
            result.WasLocked = st != null && st.State != LockState.Off;
            if (result.WasLocked)
            {
                say("自动解除锁定（当前状态：" + LockState.Text(st.State) + "）…");
                BlockResult off = await Task.Run(delegate { return UpdateBlock.Disable(appId); }).ConfigureAwait(false);
                if (off.Ok)
                {
                    result.Unlocked = true;
                    say("已还原启动更新开关，缓存目录恢复可写");
                }
                else
                {
                    say("解除锁定失败：" + off.Error + "（安装可能因目录只读而失败）");
                }
            }
            else
            {
                say("当前未锁定，无需解除");
                result.Unlocked = true;
            }
            // 解除失败时保留锁定记录，否则连「锁的是哪一版」都丢了
            if (result.Unlocked) StateStore.Instance.ClearLock(appId);

            /* 4. 清熔断记录：安装失败计数会让客户端拒绝再次安装低版本 */
            result.ClearedRecords = UpdateBlock.ClearFailureRecord(appId);
            if (result.ClearedRecords > 0)
                say("清除安装失败熔断记录 " + result.ClearedRecords + " 项（原文件已备份到 block-backup）");
            else
                say("没有需要清理的熔断记录");

            /* 5. 回收客户端自己缓存的更新包，避免装完又被它顶回新版本 */
            List<string> moved = UpdateBlock.RecycleCachedInstallers(appId);
            result.Recycled = moved.Count;
            if (moved.Count > 0)
            {
                say("回收客户端更新缓存里的安装包 " + moved.Count + " 个");
                foreach (string m in moved) say("  · " + m);
            }
            else
            {
                say("客户端更新缓存里没有待安装的包");
            }

            /* 6. 打开资源管理器并选中安装包，剩下的交给用户双击 */
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("explorer.exe", "/select,\"" + installerPath + "\"");
                psi.UseShellExecute = true;
                psi.WorkingDirectory = result.InstallerDir;
                Process.Start(psi);
                say("已在资源管理器里选中安装包");
            }
            catch (Exception ex)
            {
                say("打开资源管理器失败：" + ex.Message + "，请手动到 " + Paths.Display(result.InstallerDir) + " 双击");
            }

            result.Ok = true;

            JsonValue rec = JsonValue.NewObject();
            rec.Set("appId", appId);
            rec.Set("kind", "prepare");
            rec.Set("version", version);
            rec.Set("at", DateTime.UtcNow.ToString("o"));
            rec.Set("beforeVersion", result.BeforeVersion);
            rec.Set("installerPath", installerPath);
            rec.Set("unlocked", result.Unlocked);
            rec.Set("clearedRecords", (double)result.ClearedRecords);
            rec.Set("recycled", (double)result.Recycled);
            StateStore.Instance.SetLastInstall(rec);

            return result;
        }
    }
}
