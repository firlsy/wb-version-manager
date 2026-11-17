using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WbVm
{
    /// <summary>队列里的一条下载任务</summary>
    public sealed class QueueItem
    {
        public string Id = "";
        public string AppId = "";
        public string Edition = "";
        public string Version = "";
        public string Url = "";
        public string FileName = "";
        public string FilePath = "";
        public string PartPath = "";
        public long Received;
        public long Total;
        public double Speed;
        /// <summary>queued / downloading / paused / done / error / stopped</summary>
        public string Status = "queued";
        public string Error = "";
        public DateTime Added = DateTime.Now;

        internal CancellationTokenSource Cts;
        internal bool Removed;

        public int Percent
        {
            get { return Total > 0 ? (int)Math.Min(100, Math.Round(Received * 100.0 / Total)) : 0; }
        }

        public string StatusText
        {
            get
            {
                if (Status == "queued") return "排队中";
                if (Status == "downloading") return "下载中";
                if (Status == "paused") return "已暂停";
                if (Status == "done") return "已完成";
                if (Status == "stopped") return "已停止";
                return "失败";
            }
        }

        public string SpeedText
        {
            get { return Status == "downloading" && Speed > 0 ? Fmt.Bytes((long)Speed) + "/s" : ""; }
        }

        public string SizeText
        {
            get
            {
                if (Total > 0) return Fmt.Bytes(Received) + " / " + Fmt.Bytes(Total);
                return Received > 0 ? Fmt.Bytes(Received) : "";
            }
        }

        public bool IsFinished { get { return Status == "done" || Status == "error" || Status == "stopped"; } }
    }

    /// <summary>
    /// 单工作线程的下载队列：一次只跑一个任务，其余排队。
    /// 暂停保留 .part 并支持 Range 续传；停止/删除会连碎片一起清掉。
    /// </summary>
    public sealed class DownloadQueue
    {
        public static readonly DownloadQueue Instance = new DownloadQueue();

        private readonly List<QueueItem> _items = new List<QueueItem>();
        private readonly object _gate = new object();
        private bool _running;

        /// <summary>界面定时轮询用，返回的是一份快照，别拿去改</summary>
        public QueueItem[] Snapshot()
        {
            lock (_gate)
            {
                QueueItem[] arr = new QueueItem[_items.Count];
                for (int i = 0; i < _items.Count; i++)
                {
                    QueueItem t = _items[i];
                    QueueItem c = new QueueItem();
                    c.Id = t.Id;
                    c.AppId = t.AppId;
                    c.Edition = t.Edition;
                    c.Version = t.Version;
                    c.Url = t.Url;
                    c.FileName = t.FileName;
                    c.FilePath = t.FilePath;
                    c.PartPath = t.PartPath;
                    c.Received = t.Received;
                    c.Total = t.Total;
                    c.Speed = t.Speed;
                    c.Status = t.Status;
                    c.Error = t.Error;
                    c.Added = t.Added;
                    arr[i] = c;
                }
                return arr;
            }
        }

        public int Count
        {
            get { lock (_gate) return _items.Count; }
        }

        public bool Busy
        {
            get
            {
                lock (_gate)
                {
                    for (int i = 0; i < _items.Count; i++)
                        if (_items[i].Status == "downloading") return true;
                }
                return false;
            }
        }

        /// <summary>同版本已在队列里就复用：暂停/失败/停止的会自动继续</summary>
        public QueueItem Enqueue(string appId, string version, string url, string edition)
        {
            if (string.IsNullOrEmpty(url)) throw new ArgumentException("该版本没有可用的 Windows 安装包链接");
            string id = appId + "|" + version;
            lock (_gate)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    QueueItem t = _items[i];
                    if (t.Id != id) continue;
                    if (t.Status == "paused" || t.Status == "error" || t.Status == "stopped")
                    {
                        t.Status = "queued";
                        t.Error = "";
                        EnsureWorkerLocked();
                    }
                    QueueItem copy = Clone(t);
                    return copy;
                }

                QueueItem item = new QueueItem();
                item.Id = id;
                item.AppId = appId;
                item.Edition = edition;
                item.Version = version;
                item.Url = url;
                item.FileName = Downloads.FileNameFromUrl(url);
                string dir = Path.Combine(Paths.StoreRoot, appId);
                item.FilePath = Path.Combine(dir, item.FileName);
                item.PartPath = item.FilePath + ".part";
                item.Received = PartLength(item.PartPath);
                item.Status = "queued";
                _items.Add(item);
                EnsureWorkerLocked();
                return Clone(item);
            }
        }

        public void Pause(string id)
        {
            lock (_gate)
            {
                QueueItem t = Find(id);
                if (t == null) return;
                if (t.Status == "queued")
                {
                    t.Status = "paused";
                    return;
                }
                if (t.Status != "downloading") return;
                t.Status = "paused";
                if (t.Cts != null) { try { t.Cts.Cancel(); } catch { } }
            }
        }

        public void Resume(string id)
        {
            lock (_gate)
            {
                QueueItem t = Find(id);
                if (t == null) return;
                if (t.Status != "paused" && t.Status != "stopped" && t.Status != "error") return;
                t.Status = "queued";
                t.Error = "";
                EnsureWorkerLocked();
            }
        }

        /// <summary>停止：中断并把 .part 删掉，任务留在列表里可以重下</summary>
        public void Stop(string id)
        {
            bool deleteNow = false;
            string part = "";
            lock (_gate)
            {
                QueueItem t = Find(id);
                if (t == null) return;
                part = t.PartPath;
                if (t.Status == "downloading")
                {
                    t.Status = "stopped";
                    if (t.Cts != null) { try { t.Cts.Cancel(); } catch { } }
                }
                else
                {
                    t.Status = "stopped";
                    t.Received = 0;
                    t.Total = 0;
                    t.Speed = 0;
                    deleteNow = true;
                }
            }
            // 正在下载的那条由工作线程在退出时清理，避免两边同时删文件
            if (deleteNow) TryDelete(part);
        }

        public void ResumeAll()
        {
            lock (_gate)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    QueueItem t = _items[i];
                    if (t.Status == "paused" || t.Status == "stopped" || t.Status == "error") t.Status = "queued";
                }
                EnsureWorkerLocked();
            }
        }

        public void PauseAll()
        {
            lock (_gate)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    QueueItem t = _items[i];
                    if (t.Status == "downloading" || t.Status == "queued")
                    {
                        t.Status = "paused";
                        if (t.Cts != null) { try { t.Cts.Cancel(); } catch { } }
                    }
                }
            }
        }

        /// <summary>删除：从列表里移除；未完成的连 .part 一起删，已完成的只删记录不动安装包</summary>
        public void Remove(string id)
        {
            string part = "";
            bool deleteNow = false;
            lock (_gate)
            {
                QueueItem t = Find(id);
                if (t == null) return;
                t.Removed = true;
                part = t.PartPath;
                _items.Remove(t);

                if (t.Status == "downloading")
                {
                    // 写入中的文件删不掉（FileShare.None），交给工作线程在关流后清；
                    // 状态必须改成 removed，否则 catch 会把它当「暂停」把碎片留下。
                    t.Status = "removed";
                    if (t.Cts != null) { try { t.Cts.Cancel(); } catch { } }
                }
                else
                {
                    deleteNow = t.Status != "done";
                }
            }
            if (deleteNow) TryDelete(part);
        }

        public int ClearFinished()
        {
            List<string> parts = new List<string>();
            int n = 0;
            lock (_gate)
            {
                for (int i = _items.Count - 1; i >= 0; i--)
                {
                    QueueItem t = _items[i];
                    if (!t.IsFinished) continue;
                    t.Removed = true;
                    if (t.Status != "done") parts.Add(t.PartPath);
                    _items.RemoveAt(i);
                    n++;
                }
            }
            foreach (string p in parts) TryDelete(p);
            return n;
        }

        private QueueItem Find(string id)
        {
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].Id == id) return _items[i];
            return null;
        }

        private static QueueItem Clone(QueueItem t)
        {
            QueueItem c = new QueueItem();
            c.Id = t.Id;
            c.AppId = t.AppId;
            c.Edition = t.Edition;
            c.Version = t.Version;
            c.Url = t.Url;
            c.FileName = t.FileName;
            c.FilePath = t.FilePath;
            c.PartPath = t.PartPath;
            c.Received = t.Received;
            c.Total = t.Total;
            c.Speed = t.Speed;
            c.Status = t.Status;
            c.Error = t.Error;
            c.Added = t.Added;
            return c;
        }

        private void EnsureWorkerLocked()
        {
            if (_running) return;
            _running = true;
            Task.Run(delegate
            {
                try { WorkerLoop(); }
                finally
                {
                    bool again;
                    lock (_gate)
                    {
                        _running = false;
                        again = HasQueuedLocked();
                    }
                    // 收尾瞬间进来的新任务：再拉一次，别让它一直躺着
                    if (again)
                    {
                        lock (_gate) EnsureWorkerLocked();
                    }
                }
            });
        }

        private bool HasQueuedLocked()
        {
            for (int i = 0; i < _items.Count; i++)
                if (_items[i].Status == "queued" && !_items[i].Removed) return true;
            return false;
        }

        private void WorkerLoop()
        {
            while (true)
            {
                QueueItem next = null;
                lock (_gate)
                {
                    for (int i = 0; i < _items.Count; i++)
                    {
                        QueueItem t = _items[i];
                        if (t.Status == "queued" && !t.Removed) { next = t; break; }
                    }
                    if (next == null) return;
                    next.Status = "downloading";
                    next.Error = "";
                    next.Cts = new CancellationTokenSource();
                }
                Run(next);
            }
        }

        private void Run(QueueItem t)
        {
            CancellationTokenSource cts;
            long offset;
            lock (_gate)
            {
                cts = t.Cts;
                offset = PartLength(t.PartPath);
                t.Received = offset;
            }

            try
            {
                long received = Downloads.TransferAsync(t.Url, t.PartPath, offset,
                    delegate(long got, long total, double speed)
                    {
                        lock (_gate)
                        {
                            t.Received = got;
                            t.Total = total;
                            t.Speed = speed;
                        }
                    }, cts.Token).GetAwaiter().GetResult();

                // 刚好在收尾瞬间被删掉：别把文件又冒出来
                bool removed;
                lock (_gate) removed = t.Removed;
                if (removed)
                {
                    TryDelete(t.PartPath);
                    return;
                }

                if (File.Exists(t.FilePath)) { try { File.Delete(t.FilePath); } catch { } }
                File.Move(t.PartPath, t.FilePath);
                try
                {
                    string meta = Downloads.PartMetaPath(t.PartPath);
                    if (File.Exists(meta)) File.Delete(meta);
                }
                catch { }

                lock (_gate)
                {
                    t.Received = received;
                    if (t.Total <= 0) t.Total = received;
                    t.Speed = 0;
                    t.Status = "done";
                }
            }
            catch (OperationCanceledException)
            {
                // HttpClient 超时也抛 TaskCanceledException，但此时没人点过暂停：
                // 不区分就会把「连接卡死」显示成「已暂停」，用户以为是自己按的。
                bool byUser = cts != null && cts.IsCancellationRequested;
                string status;
                lock (_gate)
                {
                    t.Speed = 0;
                    if (t.Status == "downloading")
                    {
                        if (byUser) t.Status = "paused";
                        else { t.Status = "error"; t.Error = "连接中断或超时，点「继续」可续传"; }
                    }
                    status = t.Status;
                }
                if (t.Removed || status == "stopped" || status == "removed")
                {
                    lock (_gate) { t.Received = 0; t.Total = 0; }
                    TryDelete(t.PartPath);
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    t.Speed = 0;
                    t.Error = ex.Message;
                    // 半截文件留着，点「继续」能续传
                    t.Status = "error";
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (t.Cts != null) { try { t.Cts.Dispose(); } catch { } t.Cts = null; }
                }
            }
        }

        private static long PartLength(string part)
        {
            try
            {
                FileInfo fi = new FileInfo(part);
                return fi.Exists ? fi.Length : 0;
            }
            catch { return 0; }
        }

        private static void TryDelete(string path)
        {
            // 连 .meta 一起删，否则下次续传会被指纹拦下来重下
            Downloads.DeletePart(path);
        }

        /// <summary>队列里仍引用的碎片路径：清缓存时不能动它们</summary>
        public HashSet<string> KeepPartPaths()
        {
            lock (_gate)
            {
                HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < _items.Count; i++)
                {
                    QueueItem t = _items[i];
                    if (t.Removed) continue;
                    if (t.Status == "done") continue;
                    if (!string.IsNullOrEmpty(t.PartPath)) set.Add(t.PartPath);
                }
                return set;
            }
        }

        /// <summary>仅供 --render 截图演示用：塞几条假任务，不联网</summary>
        public void SeedPreview()
        {
            lock (_gate)
            {
                _items.Clear();
                QueueItem a = new QueueItem();
                a.Id = "workbuddy|5.11.2"; a.AppId = "workbuddy"; a.Edition = "国内版";
                a.Version = "5.11.2"; a.FileName = "WorkBuddy-win32-x64-user-5.11.2.exe";
                a.FilePath = Path.Combine(Paths.StoreRoot, "workbuddy", a.FileName);
                a.PartPath = a.FilePath + ".part";
                a.Status = "downloading"; a.Received = 271800000; a.Total = 459800000; a.Speed = 2400000;
                _items.Add(a);

                QueueItem b = new QueueItem();
                b.Id = "workbuddyai|5.11.4"; b.AppId = "workbuddyai"; b.Edition = "国际版";
                b.Version = "5.11.4"; b.FileName = "WorkBuddy-win32-x64-user-5.11.4.44121853-0026dff0.exe";
                b.FilePath = Path.Combine(Paths.StoreRoot, "workbuddyai", b.FileName);
                b.PartPath = b.FilePath + ".part";
                b.Status = "queued";
                _items.Add(b);

                QueueItem c = new QueueItem();
                c.Id = "workbuddy|5.9.3"; c.AppId = "workbuddy"; c.Edition = "国内版";
                c.Version = "5.9.3"; c.FileName = "WorkBuddy-win32-x64-user-5.9.3.exe";
                c.FilePath = Path.Combine(Paths.StoreRoot, "workbuddy", c.FileName);
                c.PartPath = c.FilePath + ".part";
                c.Status = "paused"; c.Received = 132000000; c.Total = 430000000;
                _items.Add(c);
            }
        }
    }
}
