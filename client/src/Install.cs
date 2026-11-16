using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace WbVm
{
    public sealed class InstallResult
    {
        public bool Ok;
        public bool Cancelled;
        public string AppId = "";
        public string Version = "";
        public string BeforeVersion = "";
        public string AfterVersion = "";
        public string Error = "";
        public string InstallerPath = "";
        public int ExitCode;
        public long DurationMs;
    }

    public static class InstallService
    {
        /// <summary>
        /// 用官方 NSIS 静默参数安装指定版本：
        /// /S --updated /UPDATE=1 /D=&lt;安装目录&gt;（/D= 必须放最后且不加引号）
        /// </summary>
        public static async Task<InstallResult> InstallAsync(string appId, string version, string installerPath,
            string sha256, IProgress<string> log)
        {
            InstallResult result = new InstallResult();
            result.AppId = appId;
            result.Version = version;
            result.InstallerPath = installerPath;
            Stopwatch sw = Stopwatch.StartNew();

            Action<string> say = delegate(string msg) { if (log != null) log.Report(msg); };

            AppDef app = Apps.ById(appId);
            if (app == null) { result.Error = "未知应用"; return result; }

            string custom = StateStore.Instance.CustomPath(appId);
            InstallInfo before = Detector.Detect(appId, custom);
            if (!before.Found)
            {
                result.Error = "未找到安装目录，请先在左侧卡片里手动指定";
                return result;
            }
            result.BeforeVersion = before.Version;

            if (string.IsNullOrEmpty(installerPath) || !File.Exists(installerPath))
            {
                result.Error = "安装包不存在，请先下载";
                return result;
            }

            if (!string.IsNullOrEmpty(sha256))
            {
                say("校验安装包 SHA256…");
                string actual = await Task.Run(delegate { return Downloads.Sha256(installerPath); }).ConfigureAwait(false);
                if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
                {
                    result.Error = "安装包 SHA256 校验失败，已中止安装";
                    return result;
                }
                say("校验通过");
            }

            if (Detector.IsRunning(appId))
            {
                say("关闭 " + app.Name + "…");
                Detector.Kill(appId);
                for (int i = 0; i < 20; i++)
                {
                    if (!Detector.IsRunning(appId)) break;
                    await Task.Delay(300).ConfigureAwait(false);
                }
            }

            string args = "/S --updated /UPDATE=1 /D=" + before.InstallDir;
            say("调用安装程序（会弹出 UAC 授权）…");
            say("参数：" + args);

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(installerPath);
                psi.Arguments = args;
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WorkingDirectory = Path.GetDirectoryName(installerPath);
                using (Process p = Process.Start(psi))
                {
                    if (p != null)
                    {
                        bool exited = await Task.Run(delegate { return p.WaitForExit(10 * 60 * 1000); }).ConfigureAwait(false);
                        if (!exited)
                        {
                            try { p.Kill(); } catch { }
                            result.Error = "安装超时（10 分钟）";
                        }
                        else
                        {
                            result.ExitCode = p.ExitCode;
                        }
                    }
                }
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223)
                {
                    result.Cancelled = true;
                    result.Error = "已取消 UAC 授权";
                }
                else
                {
                    result.Error = "启动安装程序失败：" + ex.Message;
                }
                return result;
            }
            catch (Exception ex)
            {
                result.Error = "启动安装程序失败：" + ex.Message;
                return result;
            }

            say("等待版本落盘…");
            InstallInfo after = null;
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(2000).ConfigureAwait(false);
                after = Detector.Detect(appId, custom);
                if (after.Found && !string.IsNullOrEmpty(after.Version) && after.Version != before.Version) break;
                if (i > 5 && after.Found && after.Version == version) break;
            }

            result.AfterVersion = after != null ? after.Version : "";
            result.Ok = after != null && after.Version == version;
            result.DurationMs = sw.ElapsedMilliseconds;
            if (!result.Ok && string.IsNullOrEmpty(result.Error))
                result.Error = "安装完成但版本未变成 " + version + "（当前 " + (after == null ? "未知" : after.Version) + "）";

            JsonValue rec = JsonValue.NewObject();
            rec.Set("appId", appId);
            rec.Set("version", version);
            rec.Set("ok", result.Ok);
            rec.Set("at", DateTime.UtcNow.ToString("o"));
            rec.Set("durationMs", (double)result.DurationMs);
            rec.Set("beforeVersion", result.BeforeVersion);
            rec.Set("afterVersion", result.AfterVersion);
            rec.Set("exitCode", (double)result.ExitCode);
            rec.Set("installerPath", installerPath);
            rec.Set("error", result.Error);
            StateStore.Instance.SetLastInstall(rec);

            return result;
        }
    }
}
