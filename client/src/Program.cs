using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace WbVm
{
    internal static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        private static void LogCrash(string where, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Paths.DataRoot);
                File.AppendAllText(Path.Combine(Paths.DataRoot, "client-error.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [" + where + "]  " + ex + "\r\n\r\n");
            }
            catch { }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0 && IsCliCommand(args[0]))
            {
                RunCli(args);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string renderPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--render" && i + 1 < args.Length) renderPath = args[i + 1];
            }
            // 文件名以 .shot 结尾时视为截图副本，直接渲染到同目录 shot.png
            string exeName = Path.GetFileNameWithoutExtension(Application.ExecutablePath);
            if (renderPath == null && exeName.EndsWith(".shot", StringComparison.OrdinalIgnoreCase))
                renderPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "shot.png");

            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                LogCrash("ThreadException", e.Exception);
                MessageBox.Show(e.Exception.Message, "出错了", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                if (ex != null) LogCrash("UnhandledException", ex);
                MessageBox.Show(ex == null ? "未知错误" : ex.Message, "出错了", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            bool created;
            using (Mutex mutex = new Mutex(true, "Global\\WBVersionManager.CSharp.v1", out created))
            {
                if (!created && renderPath == null)
                {
                    MessageBox.Show("版本管理器已经在运行了。", "WB 版本管理器", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                MainForm form;
                try { form = new MainForm(); }
                catch (Exception ex) { LogCrash("MainForm", ex); return; }

                if (renderPath != null)
                {
                    form.TopMost = true;
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(40, 40);
                    // 开发用：渲染一张界面截图后退出
                    System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
                    timer.Interval = 3200;
                    timer.Tick += delegate
                    {
                        timer.Stop();
                        try
                        {
                            string sel = Environment.GetEnvironmentVariable("WBVM_SELECT");
                            if (!string.IsNullOrEmpty(sel))
                            {
                                form.DevSelect(sel);
                                Application.DoEvents();
                            }
                            using (Bitmap bmp = new Bitmap(form.Width, form.Height))
                            {
                                form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                                bmp.Save(renderPath, ImageFormat.Png);
                            }
                            File.WriteAllText(renderPath + ".txt", form.DebugDump(), Encoding.UTF8);
                        }
                        catch (Exception ex)
                        {
                            LogCrash("Render", ex);
                        }
                        form.Close();
                    };
                    timer.Start();
                }
                Application.Run(form);
                GC.KeepAlive(mutex);
            }
        }

        /* ---------------- 命令行模式 ---------------- */

        private static bool IsCliCommand(string a)
        {
            return a == "--status" || a == "--lock" || a == "--unlock" || a == "--help" || a == "-h" || a == "/?";
        }

        private static void RunCli(string[] args)
        {
            if (!AttachConsole(-1)) AllocConsole();
            StreamWriter w = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            w.AutoFlush = true;
            Console.SetOut(w);

            try
            {
                string cmd = args[0];
                if (cmd == "--help" || cmd == "-h" || cmd == "/?")
                {
                    w.WriteLine("WB 版本管理器 · 命令行");
                    w.WriteLine("  --status               查看两款客户端的版本与锁定状态");
                    w.WriteLine("  --lock   [appId]       锁定（appId: workbuddy | workbuddyai，默认 workbuddy）");
                    w.WriteLine("  --unlock [appId]       解除锁定");
                    w.WriteLine("  不带参数               打开图形界面");
                    return;
                }

                string appId = args.Length > 1 ? args[1] : "workbuddy";

                if (cmd == "--status")
                {
                    PrintStatus(w);
                    return;
                }

                if (Apps.ById(appId) == null)
                {
                    w.WriteLine("未知应用：" + appId + "（可选 workbuddy / workbuddyai）");
                    Environment.ExitCode = 2;
                    return;
                }

                if (cmd == "--lock")
                {
                    w.WriteLine("锁定 " + appId + " …");
                    BlockResult r = UpdateBlock.Enable(appId);
                    if (!r.Ok)
                    {
                        w.WriteLine("失败：" + r.Error);
                        Environment.ExitCode = 1;
                        return;
                    }
                    InstallInfo info = Detector.Detect(appId, StateStore.Instance.CustomPath(appId));
                    if (info.Found) StateStore.Instance.SetLock(appId, info.Version, "命令行锁定");
                    foreach (string m in r.Moved) w.WriteLine("  回收安装包 " + m);
                    foreach (string c in r.Cleared) w.WriteLine("  清除记录 " + c);
                    w.WriteLine("完成。启动强制更新已关闭，缓存目录已拒绝写入。");
                    PrintOne(w, appId);
                    return;
                }

                if (cmd == "--unlock")
                {
                    w.WriteLine("解除锁定 " + appId + " …");
                    BlockResult r = UpdateBlock.Disable(appId);
                    if (!r.Ok)
                    {
                        w.WriteLine("失败：" + r.Error);
                        Environment.ExitCode = 1;
                        return;
                    }
                    StateStore.Instance.ClearLock(appId);
                    w.WriteLine("完成。配置已还原，缓存目录恢复可写。");
                    PrintOne(w, appId);
                }
            }
            catch (Exception ex)
            {
                w.WriteLine("出错：" + ex.Message);
                Environment.ExitCode = 1;
            }
        }

        private static void PrintStatus(StreamWriter w)
        {
            w.WriteLine("数据目录：" + Paths.DataRoot);
            w.WriteLine();
            foreach (AppDef a in Apps.All) PrintOne(w, a.Id);
        }

        private static void PrintOne(StreamWriter w, string appId)
        {
            AppDef app = Apps.ById(appId);
            InstallInfo info = Detector.Detect(appId, StateStore.Instance.CustomPath(appId));
            BlockStatus st = UpdateBlock.GetStatus(appId);
            string locked = StateStore.Instance.LockedVersion(appId);

            w.WriteLine("[" + app.Name + " " + app.Edition + "]");
            w.WriteLine("  安装目录 : " + (info.Found ? info.InstallDir : "未检测到"));
            w.WriteLine("  当前版本 : " + (info.Found ? info.Version : "-") + (info.Found ? "  (" + info.VersionSource + ")" : ""));
            w.WriteLine("  锁定状态 : " + (st.Enabled ? "已锁定" : "未锁定") + (string.IsNullOrEmpty(locked) ? "" : "  → " + locked));
            w.WriteLine("  启动开关 : app-config=" + ShowBool(st.Config.ConfigValue) + "  settings=" + ShowBool(st.Config.SettingsValue));
            w.WriteLine("  缓存目录 : " + (st.CacheBlocked ? "已拒绝写入" : "可写") + "  安装包 " + st.Installers.Count + " 个 / " + Fmt.Bytes(st.Total));
            w.WriteLine("  失败记录 : " + (st.Failure.Exists ? (st.Failure.Count + " 次 · " + st.Failure.Code) : "无"));
            w.WriteLine();
        }

        private static string ShowBool(bool? v)
        {
            if (!v.HasValue) return "未设置";
            return v.Value ? "true" : "false";
        }
    }
}
