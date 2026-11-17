using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WbVm
{
    public sealed class BrandMark : Control
    {
        public BrandMark()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(38, 38);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Draw.EffectiveBack(this));
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Draw.RoundRect(r, 10))
            using (LinearGradientBrush b = new LinearGradientBrush(r, Palette.Accent, Palette.Accent2, 45f))
                e.Graphics.FillPath(b, p);
            Draw.Text(e.Graphics, "WB", UiFont.Make(11f, FontStyle.Bold), Color.White, r,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    public sealed class MainForm : Form
    {
        private const int LeftWidth = 322;

        private Catalog _catalog;
        private readonly Dictionary<string, InstallInfo> _installs = new Dictionary<string, InstallInfo>(StringComparer.Ordinal);
        private readonly Dictionary<string, BlockStatus> _blocks = new Dictionary<string, BlockStatus>(StringComparer.Ordinal);
        private string _selectedApp = "workbuddy";
        private CancellationTokenSource _downloadCts;
        private bool _busy;

        private Badge _sourceBadge;
        private FlatButton _refreshBtn;
        private FlatButton _dataDirBtn;

        private readonly Dictionary<string, AppCard> _cards = new Dictionary<string, AppCard>(StringComparer.Ordinal);
        private Label _cacheLabel;
        private FlatButton _openStoreBtn;

        private Label _appTitle;
        private Label _appSub;
        private Badge _lockBadge;
        private Badge _cacheBadge;
        private Badge _runBadge;

        private FlatButton _btnLock;
        private FlatButton _btnUnlock;
        private FlatButton _btnDownload;
        private FlatButton _btnCopy;
        private FlatButton _btnExport;
        private FlatButton _btnRollback;

        private MiniProgress _progress;
        private Label _progressLabel;

        private DataGridView _grid;
        private NotesView _notes;
        private Label _notesTitle;
        private TextBox _log;
        private System.Windows.Forms.Timer _runTimer;

        public MainForm()
        {
            _catalog = Catalog.Current;
            BuildUi();
            Load += delegate { TryLoadDiskCatalog(); };
            Shown += async delegate { await ReloadAll(true); };

            // 运行状态是外部事实，界面上自己刷一下，不然开了客户端还显示「未运行」
            _runTimer = new System.Windows.Forms.Timer();
            _runTimer.Interval = 5000;
            _runTimer.Tick += delegate { RefreshRunBadge(); };
            _runTimer.Start();
            FormClosed += delegate { if (_runTimer != null) _runTimer.Stop(); };
        }

        private void TryLoadDiskCatalog()
        {
            try
            {
                string cache = Path.Combine(Paths.DataRoot, "catalog.json");
                if (!File.Exists(cache)) return;
                Catalog disk = Catalog.LoadFromFile(cache);
                if (disk.Releases.Count > _catalog.Releases.Count) _catalog = disk;
            }
            catch { }
        }

        /* ---------------- UI 构建 ---------------- */

        private void BuildUi()
        {
            Text = "WB 版本管理器";
            BackColor = Palette.Bg;
            ClientSize = new Size(1200, 830);
            MinimumSize = new Size(1040, 720);
            StartPosition = FormStartPosition.CenterScreen;
            Font = UiFont.Base;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            try { Icon = BuildIcon(); } catch { }

            Panel header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = 74;
            header.BackColor = Palette.Card;
            header.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Palette.Border))
                    e.Graphics.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1);
            };
            Controls.Add(header);

            BrandMark mark = new BrandMark();
            mark.Location = new Point(20, 17);
            header.Controls.Add(mark);

            Label t1 = new Label();
            t1.Text = "版本管理器";
            t1.Font = UiFont.H1;
            t1.ForeColor = Palette.Text;
            t1.AutoSize = true;
            t1.BackColor = Color.Transparent;
            t1.Location = new Point(70, 12);
            header.Controls.Add(t1);

            Label t2 = new Label();
            t2.Text = "WorkBuddy · WorkBuddy AI 版本锁定与回退";
            t2.Font = UiFont.Small;
            t2.ForeColor = Palette.Muted;
            t2.AutoSize = true;
            t2.BackColor = Color.Transparent;
            t2.Location = new Point(72, 42);
            header.Controls.Add(t2);

            _sourceBadge = new Badge();
            _sourceBadge.Size = new Size(190, 24);
            _sourceBadge.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _sourceBadge.Location = new Point(ClientSize.Width - 470, 24);
            _sourceBadge.Text = "目录加载中…";
            _sourceBadge.FillColor = Palette.BgSoft;
            _sourceBadge.TextColor = Palette.Text2;
            _sourceBadge.DotColor = Palette.Muted;
            header.Controls.Add(_sourceBadge);

            _refreshBtn = new FlatButton();
            _refreshBtn.Text = "刷新目录";
            _refreshBtn.Size = new Size(94, 32);
            _refreshBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _refreshBtn.Location = new Point(ClientSize.Width - 268, 20);
            _refreshBtn.Click += async delegate { await RefreshCatalog(); };
            header.Controls.Add(_refreshBtn);

            _dataDirBtn = new FlatButton();
            _dataDirBtn.Text = "数据目录";
            _dataDirBtn.Size = new Size(94, 32);
            _dataDirBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _dataDirBtn.Location = new Point(ClientSize.Width - 168, 20);
            _dataDirBtn.Click += delegate
            {
                try { Process.Start("explorer.exe", Paths.DataRoot); }
                catch (Exception ex) { AppendLog("打开数据目录失败：" + ex.Message); }
            };
            header.Controls.Add(_dataDirBtn);

            Panel body = new Panel();
            body.Dock = DockStyle.Fill;
            body.Padding = new Padding(18, 16, 18, 16);
            body.BackColor = Palette.Bg;
            Controls.Add(body);
            body.BringToFront();

            /* ---------- 左侧 ---------- */

            Panel left = new Panel();
            left.Dock = DockStyle.Left;
            left.Width = LeftWidth;
            left.BackColor = Palette.Bg;
            body.Controls.Add(left);

            int y = 0;
            foreach (AppDef app in Apps.All)
            {
                AppCard card = new AppCard(app);
                card.Location = new Point(0, y);
                card.Size = new Size(LeftWidth - 12, 158);
                card.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                card.Selected += delegate { SelectApp(app.Id); };
                card.LockRequested += async delegate { SelectApp(app.Id); await ToggleLock(); };
                card.LocateRequested += delegate { LocateApp(app.Id); };
                left.Controls.Add(card);
                _cards[app.Id] = card;
                y += 170;
            }

            CardPanel cacheCard = new CardPanel();
            cacheCard.Location = new Point(0, y);
            cacheCard.Size = new Size(LeftWidth - 12, 150);
            cacheCard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            left.Controls.Add(cacheCard);

            Label cl = new Label();
            cl.Text = "安装包缓存";
            cl.Font = UiFont.H3;
            cl.ForeColor = Palette.Text;
            cl.AutoSize = true;
            cl.BackColor = Color.Transparent;
            cl.Location = new Point(16, 14);
            cacheCard.Controls.Add(cl);

            _cacheLabel = new Label();
            _cacheLabel.Font = UiFont.Small;
            _cacheLabel.ForeColor = Palette.Text2;
            _cacheLabel.BackColor = Color.Transparent;
            _cacheLabel.Location = new Point(16, 42);
            _cacheLabel.Size = new Size(270, 64);
            cacheCard.Controls.Add(_cacheLabel);

            _openStoreBtn = new FlatButton();
            _openStoreBtn.Text = "打开缓存目录";
            _openStoreBtn.Size = new Size(118, 30);
            _openStoreBtn.Location = new Point(16, 108);
            _openStoreBtn.Click += delegate
            {
                try
                {
                    Directory.CreateDirectory(Paths.StoreRoot);
                    Process.Start("explorer.exe", Paths.StoreRoot);
                }
                catch (Exception ex) { AppendLog("打开缓存目录失败：" + ex.Message); }
            };
            cacheCard.Controls.Add(_openStoreBtn);

            /* ---------- 右侧 ---------- */

            TableLayoutPanel right = new TableLayoutPanel();
            right.Dock = DockStyle.Fill;
            right.BackColor = Palette.Bg;
            right.ColumnCount = 1;
            right.RowCount = 5;
            right.Padding = new Padding(14, 0, 0, 0);
            right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 92f));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 52f));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 366f));
            body.Controls.Add(right);
            right.BringToFront();

            /* 应用头 */
            CardPanel head = new CardPanel();
            head.Dock = DockStyle.Fill;
            head.Margin = new Padding(0, 0, 0, 10);
            right.Controls.Add(head, 0, 0);

            _appTitle = new Label();
            _appTitle.Text = "WorkBuddy";
            _appTitle.Font = UiFont.H2;
            _appTitle.ForeColor = Palette.Text;
            _appTitle.AutoSize = true;
            _appTitle.BackColor = Color.Transparent;
            _appTitle.Location = new Point(18, 14);
            head.Controls.Add(_appTitle);

            _lockBadge = new Badge();
            _lockBadge.Location = new Point(18, 48);
            _lockBadge.Size = new Size(110, 22);
            _lockBadge.AutoWidth = true;
            head.Controls.Add(_lockBadge);

            _cacheBadge = new Badge();
            _cacheBadge.Location = new Point(136, 48);
            _cacheBadge.Size = new Size(150, 22);
            _cacheBadge.AutoWidth = true;
            head.Controls.Add(_cacheBadge);

            _runBadge = new Badge();
            _runBadge.Location = new Point(294, 48);
            _runBadge.Size = new Size(96, 22);
            _runBadge.AutoWidth = true;
            head.Controls.Add(_runBadge);

            _appSub = new Label();
            _appSub.Font = UiFont.Small;
            _appSub.ForeColor = Palette.Muted;
            _appSub.BackColor = Color.Transparent;
            _appSub.AutoSize = false;
            _appSub.AutoEllipsis = true;
            _appSub.Location = new Point(400, 50);
            _appSub.Size = new Size(560, 18);
            _appSub.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            head.Controls.Add(_appSub);

            /* 操作按钮 */
            Panel actions = new Panel();
            actions.Dock = DockStyle.Fill;
            actions.Margin = new Padding(0, 0, 0, 8);
            actions.BackColor = Palette.Bg;
            right.Controls.Add(actions, 0, 1);

            _btnLock = MakeAction("锁定当前版本", 128, Palette.Accent, true);
            _btnLock.Location = new Point(0, 8);
            _btnLock.Click += async delegate { await ToggleLock(); };
            actions.Controls.Add(_btnLock);

            _btnUnlock = MakeAction("解除锁定", 96, Color.Empty, false);
            _btnUnlock.Location = new Point(136, 8);
            _btnUnlock.Click += async delegate { await ToggleLock(); };
            actions.Controls.Add(_btnUnlock);

            _btnDownload = MakeAction("下载选中版本", 116, Color.Empty, false);
            _btnDownload.Location = new Point(240, 8);
            _btnDownload.Click += async delegate { await DownloadSelected(); };
            actions.Controls.Add(_btnDownload);

            _btnRollback = MakeAction("回退安装", 96, Color.Empty, false);
            _btnRollback.Location = new Point(364, 8);
            _btnRollback.Click += async delegate { await RollbackSelected(); };
            actions.Controls.Add(_btnRollback);

            _btnCopy = MakeAction("复制链接", 88, Color.Empty, false);
            _btnCopy.Location = new Point(468, 8);
            _btnCopy.Click += delegate { CopyLinks(false); };
            actions.Controls.Add(_btnCopy);

            _btnExport = MakeAction("导出链接", 88, Color.Empty, false);
            _btnExport.Location = new Point(564, 8);
            _btnExport.Click += delegate { ExportLinks(); };
            actions.Controls.Add(_btnExport);

            /* 进度 */
            Panel progressRow = new Panel();
            progressRow.Dock = DockStyle.Fill;
            progressRow.Margin = new Padding(0);
            progressRow.BackColor = Palette.Bg;
            right.Controls.Add(progressRow, 0, 2);

            _progress = new MiniProgress();
            _progress.Location = new Point(0, 12);
            _progress.Size = new Size(420, 6);
            _progress.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            progressRow.Controls.Add(_progress);

            _progressLabel = new Label();
            _progressLabel.Font = UiFont.Small;
            _progressLabel.ForeColor = Palette.Muted;
            _progressLabel.BackColor = Color.Transparent;
            _progressLabel.Location = new Point(432, 6);
            _progressLabel.Size = new Size(500, 18);
            _progressLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            progressRow.Controls.Add(_progressLabel);

            /* 版本列表 */
            CardPanel listPanel = new CardPanel();
            listPanel.Dock = DockStyle.Fill;
            listPanel.Margin = new Padding(0, 0, 0, 10);
            listPanel.Padding = new Padding(1);
            right.Controls.Add(listPanel, 0, 3);

            _grid = new DataGridView();
            _grid.Dock = DockStyle.Fill;
            _grid.BackgroundColor = Palette.Card;
            _grid.BorderStyle = BorderStyle.None;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.AllowUserToResizeColumns = false;
            _grid.ReadOnly = true;
            _grid.RowHeadersVisible = false;
            _grid.MultiSelect = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _grid.EnableHeadersVisualStyles = false;
            _grid.ColumnHeadersHeight = 34;
            _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            _grid.RowTemplate.Height = 30;
            _grid.GridColor = ColorTranslator.FromHtml("#f0f3f8");
            _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;

            _grid.ColumnHeadersDefaultCellStyle.BackColor = Palette.BgSoft;
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Palette.Text2;
            _grid.ColumnHeadersDefaultCellStyle.Font = UiFont.Bold;
            _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Palette.BgSoft;
            _grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Palette.Text2;
            _grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 0, 0, 0);

            _grid.DefaultCellStyle.BackColor = Palette.Card;
            _grid.DefaultCellStyle.ForeColor = Palette.Text;
            _grid.DefaultCellStyle.SelectionBackColor = Palette.AccentSoft;
            _grid.DefaultCellStyle.SelectionForeColor = Palette.Text;
            _grid.DefaultCellStyle.Padding = new Padding(8, 0, 0, 0);
            _grid.DefaultCellStyle.Font = UiFont.Base;
            _grid.AlternatingRowsDefaultCellStyle.BackColor = ColorTranslator.FromHtml("#fbfcfe");

            _grid.Columns.Add(MakeColumn("版本", 52));
            _grid.Columns.Add(MakeColumn("发布日期", 80));
            _grid.Columns.Add(MakeColumn("更新说明", 66));
            _grid.Columns.Add(MakeColumn("安装包", 78));
            _grid.Columns.Add(MakeColumn("本机", 76));

            _grid.SelectionChanged += delegate { ShowSelectedNotes(); UpdateActionState(); };
            _grid.CellDoubleClick += async delegate(object s, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex >= 0) await DownloadSelected();
            };
            listPanel.Controls.Add(_grid);

            /* 底部：更新内容 + 日志 */
            TableLayoutPanel bottom = new TableLayoutPanel();
            bottom.Dock = DockStyle.Fill;
            bottom.Margin = new Padding(0);
            bottom.ColumnCount = 1;
            bottom.RowCount = 2;
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 150f));
            right.Controls.Add(bottom, 0, 4);

            CardPanel notesPanel = new CardPanel();
            notesPanel.Dock = DockStyle.Fill;
            notesPanel.Margin = new Padding(0, 0, 0, 10);
            bottom.Controls.Add(notesPanel, 0, 0);

            _notesTitle = new Label();
            _notesTitle.Text = "更新内容";
            _notesTitle.Font = UiFont.H3;
            _notesTitle.ForeColor = Palette.Text;
            _notesTitle.AutoSize = true;
            _notesTitle.BackColor = Color.Transparent;
            _notesTitle.Location = new Point(18, 14);
            notesPanel.Controls.Add(_notesTitle);

            _notes = new NotesView();
            _notes.BackColor = Palette.Card;
            _notes.Location = new Point(18, 40);
            _notes.Size = new Size(notesPanel.Width - 36, notesPanel.Height - 56);
            _notes.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            notesPanel.Controls.Add(_notes);

            CardPanel logPanel = new CardPanel();
            logPanel.Dock = DockStyle.Fill;
            logPanel.Margin = new Padding(0);
            bottom.Controls.Add(logPanel, 0, 1);

            Label logTitle = new Label();
            logTitle.Text = "运行日志";
            logTitle.Font = UiFont.H3;
            logTitle.ForeColor = Palette.Text;
            logTitle.AutoSize = true;
            logTitle.BackColor = Color.Transparent;
            logTitle.Location = new Point(18, 10);
            logPanel.Controls.Add(logTitle);

            _log = new TextBox();
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.BorderStyle = BorderStyle.None;
            _log.BackColor = ColorTranslator.FromHtml("#fbfcfe");
            _log.ForeColor = Palette.Text2;
            _log.Font = UiFont.Mono;
            _log.Location = new Point(18, 34);
            _log.Size = new Size(logPanel.Width - 36, logPanel.Height - 48);
            _log.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            logPanel.Controls.Add(_log);

            body.BringToFront();
        }

        private static Icon BuildIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (GraphicsPath p = Draw.RoundRect(new Rectangle(1, 1, 30, 30), 8))
                    using (LinearGradientBrush b = new LinearGradientBrush(new Rectangle(0, 0, 32, 32), Palette.Accent, Palette.Accent2, 45f))
                        g.FillPath(b, p);
                    Draw.Text(g, "WB", UiFont.Make(11f, FontStyle.Bold), Color.White, new Rectangle(0, 0, 32, 32),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                IntPtr h = bmp.GetHicon();
                return Icon.FromHandle(h);
            }
        }

        private FlatButton MakeAction(string text, int width, Color fill, bool primary)
        {
            FlatButton b = new FlatButton();
            b.Text = text;
            b.Size = new Size(width, 34);
            b.Primary = primary;
            return b;
        }

        private static DataGridViewTextBoxColumn MakeColumn(string header, int weight)
        {
            DataGridViewTextBoxColumn c = new DataGridViewTextBoxColumn();
            c.HeaderText = header;
            c.FillWeight = weight;
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
            c.Resizable = DataGridViewTriState.False;
            return c;
        }

        /* ---------------- 数据刷新 ---------------- */

        private async Task ReloadAll(bool firstTime)
        {
            if (_busy) return;
            _busy = true;
            try
            {
                _installs.Clear();
                _blocks.Clear();

                List<string> ids = new List<string>();
                foreach (AppDef a in Apps.All) ids.Add(a.Id);

                await Task.Run(delegate
                {
                    foreach (string id in ids)
                    {
                        string custom = StateStore.Instance.CustomPath(id);
                        InstallInfo info = Detector.Detect(id, custom);
                        _installs[id] = info;
                        _blocks[id] = UpdateBlock.GetStatus(id);
                    }
                });

                // 锁定记录缺失但实际已拦截时，用当前安装版本补一条，保证跨工具显示一致
                foreach (AppDef a in Apps.All)
                {
                    BlockStatus bs;
                    _blocks.TryGetValue(a.Id, out bs);
                    InstallInfo ins;
                    _installs.TryGetValue(a.Id, out ins);
                    if (bs != null && bs.Enabled && ins != null && ins.Found
                        && string.IsNullOrEmpty(StateStore.Instance.LockedVersion(a.Id)))
                    {
                        StateStore.Instance.SetLock(a.Id, ins.Version, "按实际拦截状态推断");
                    }
                }

                foreach (AppDef a in Apps.All)
                {
                    AppCard card = _cards[a.Id];
                    card.Install = _installs[a.Id];
                    card.Block = _blocks[a.Id];
                    card.LockedVersion = StateStore.Instance.LockedVersion(a.Id);
                    card.Refresh1();
                }

                RefreshCacheSummary();
                RefreshHeader();
                FillGrid();

                if (firstTime)
                {
                    AppendLog("数据目录：" + Paths.Display(Paths.DataRoot));
                    foreach (AppDef a in Apps.All)
                    {
                        InstallInfo info = _installs[a.Id];
                        AppendLog(a.Name + " " + a.Edition + "：" + (info.Found ? info.Version + " @ " + Paths.Display(info.InstallDir) : "未检测到安装"));
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog("刷新失败：" + ex.Message);
            }
            finally
            {
                _busy = false;
                UpdateActionState();
            }
        }

        private void UpdateActionState()
        {
            InstallInfo info;
            _installs.TryGetValue(_selectedApp, out info);
            BlockStatus st;
            _blocks.TryGetValue(_selectedApp, out st);

            bool found = info != null && info.Found;
            bool blocked = st != null && st.Enabled;
            bool hasRelease = SelectedRelease() != null;

            _btnLock.Enabled = found && !blocked && !_busy;
            _btnUnlock.Enabled = blocked && !_busy;
            _btnDownload.Enabled = hasRelease && !_busy && _downloadCts == null;
            _btnRollback.Enabled = found && hasRelease && !_busy;
            _btnCopy.Enabled = hasRelease && !_busy;
            _btnExport.Enabled = !_busy;
            _refreshBtn.Enabled = !_busy;
        }

        private void RefreshCacheSummary()
        {
            List<CachedFile> cached = Downloads.ListCached();
            long total = 0;
            for (int i = 0; i < cached.Count; i++) total += cached[i].Size;

            StringBuilder sb = new StringBuilder();
            if (cached.Count == 0) sb.Append("暂无缓存的安装包。下载后会存到本地，回退时直接复用。");
            else
            {
                sb.Append(cached.Count + " 个安装包 · 共 " + Fmt.Bytes(total) + "\n");
                int n = 0;
                foreach (CachedFile f in cached)
                {
                    if (n++ >= 2) break;
                    sb.Append("· " + f.VersionShort + "  " + f.SizeText + "\n");
                }
                if (cached.Count > 2) sb.Append("…");
            }
            _cacheLabel.Text = sb.ToString();
        }

        private void RefreshHeader()
        {
            _sourceBadge.Text = _catalog.Releases.Count + " 个版本 · 最新 " + (_catalog.LatestVersion == "" ? "未知" : _catalog.LatestVersion);
            _sourceBadge.FillColor = Palette.AccentSoft;
            _sourceBadge.TextColor = Palette.Accent;
            _sourceBadge.DotColor = Palette.Accent;
        }

        private void SelectApp(string appId)
        {
            _selectedApp = appId;
            foreach (KeyValuePair<string, AppCard> kv in _cards) kv.Value.Active = kv.Key == appId;
            FillGrid();
        }

        /// <summary>开发用：截图时指定要展示的客户端</summary>
        public void DevSelect(string appId)
        {
            if (Apps.ById(appId) != null) SelectApp(appId);
        }

        /// <summary>只刷「运行中 / 未运行」这一枚徽章，5 秒一次</summary>
        private void RefreshRunBadge()
        {
            if (_runBadge == null || _runBadge.IsDisposed) return;
            bool running = Detector.IsRunning(_selectedApp);
            _runBadge.Text = running ? "运行中" : "未运行";
            _runBadge.FillColor = running ? Palette.GreenSoft : Palette.BgSoft;
            _runBadge.TextColor = running ? Palette.Green : Palette.Muted;
            _runBadge.DotColor = running ? Palette.Green : Palette.Muted;
            _runBadge.FitWidth();
            LayoutBadges();
        }

        /// <summary>三枚徽章宽度随文字变化，按顺序重排，路径标签跟着让位</summary>
        private void LayoutBadges()
        {
            if (_lockBadge == null || _lockBadge.Parent == null) return;
            Badge[] list = new Badge[] { _lockBadge, _cacheBadge, _runBadge };
            int x = 18;
            for (int i = 0; i < list.Length; i++)
            {
                if (list[i] == null) continue;
                list[i].Location = new Point(x, 48);
                x += list[i].Width + 8;
            }
            if (_appSub != null)
            {
                Control head = _lockBadge.Parent;
                int w = head.ClientSize.Width - x - 6 - 12;
                _appSub.Left = x + 6;
                _appSub.Width = Math.Max(120, w);
            }
        }

        private void FillGrid()
        {
            AppDef app = Apps.ById(_selectedApp);
            _appTitle.Text = app.Name + "  " + app.Edition;

            InstallInfo info;
            _installs.TryGetValue(_selectedApp, out info);
            BlockStatus st;
            _blocks.TryGetValue(_selectedApp, out st);

            _grid.Rows.Clear();
            string installed = info != null && info.Found ? info.Version : "";
            string locked = StateStore.Instance.LockedVersion(_selectedApp);

            if (info != null && info.Found)
                _appSub.Text = Paths.Display(info.InstallDir) + "   ·   " + info.VersionSource;
            else
                _appSub.Text = "未检测到安装目录";

            bool blocked = st != null && st.Enabled;
            if (blocked)
            {
                _lockBadge.Text = string.IsNullOrEmpty(locked) ? "已锁定" : "已锁定 " + locked;
                _lockBadge.FillColor = Palette.GreenSoft;
                _lockBadge.TextColor = Palette.Green;
                _lockBadge.DotColor = Palette.Green;
            }
            else
            {
                _lockBadge.Text = "未锁定";
                _lockBadge.FillColor = Palette.AmberSoft;
                _lockBadge.TextColor = Palette.Amber;
                _lockBadge.DotColor = Palette.Amber;
            }

            int cacheCount = st == null ? 0 : st.Installers.Count;
            _cacheBadge.Text = cacheCount == 0 ? "更新缓存 空" : ("更新缓存 " + cacheCount + " 个 · " + Fmt.Bytes(st.Total));
            _cacheBadge.FillColor = st != null && st.CacheBlocked ? Palette.GreenSoft : Palette.BgSoft;
            _cacheBadge.TextColor = st != null && st.CacheBlocked ? Palette.Green : Palette.Text2;
            _cacheBadge.DotColor = st != null && st.CacheBlocked ? Palette.Green : Palette.Muted;

            RefreshRunBadge();
            LayoutBadges();

            _btnLock.Text = "锁定当前版本";

            HashSet<string> cachedVersions = new HashSet<string>(StringComparer.Ordinal);
            foreach (CachedFile f in Downloads.ListCached())
                if (f.AppId == _selectedApp && f.Platform == "win32-x64-user") cachedVersions.Add(f.VersionShort);

            foreach (Release rel in _catalog.Releases)
            {
                string noteText = rel.NoteCount > 0 ? rel.NoteCount + " 条" : "—";
                PackageInfo win = rel.WinPackage;
                string pkg = win == null ? "—" : (string.IsNullOrEmpty(win.SizeText) ? "有" : win.SizeText);
                string local = "";
                if (!string.IsNullOrEmpty(installed) && rel.Version == installed) local = "当前版本";
                else if (cachedVersions.Contains(rel.Version)) local = "已缓存";
                if (rel.LocalOnly && local == "") local = "本机";

                int idx = _grid.Rows.Add(rel.Version, rel.Date, noteText, pkg, local);
                _grid.Rows[idx].Tag = rel;
                if (rel.Version == installed) _grid.Rows[idx].DefaultCellStyle.ForeColor = Palette.Accent;
                if (!string.IsNullOrEmpty(locked) && rel.Version == locked)
                    _grid.Rows[idx].Cells[0].Style.Font = UiFont.Bold;
            }

            if (_grid.Rows.Count > 0)
            {
                int target = 0;
                for (int i = 0; i < _grid.Rows.Count; i++)
                {
                    Release r = _grid.Rows[i].Tag as Release;
                    if (r != null && !string.IsNullOrEmpty(installed) && r.Version == installed) { target = i; break; }
                }
                _grid.CurrentCell = _grid.Rows[target].Cells[0];
                _grid.Rows[target].Selected = true;
            }
            ShowSelectedNotes();
        }

        private Release SelectedRelease()
        {
            if (_grid.CurrentRow == null) return null;
            return _grid.CurrentRow.Tag as Release;
        }

        private void ShowSelectedNotes()
        {
            Release rel = SelectedRelease();
            _notes.Clear();
            if (rel == null) return;

            _notesTitle.Text = "更新内容 · " + rel.Version + (string.IsNullOrEmpty(rel.Date) ? "" : "（" + rel.Date + "）");

            if (!string.IsNullOrEmpty(rel.NotesTitle)) AppendNote(rel.NotesTitle.Trim() + "\n\n", UiFont.H3, Palette.Text);

            if (rel.Notes.Count == 0)
            {
                AppendNote("该版本暂无结构化更新说明。", UiFont.Base, Palette.Muted);
            }
            else
            {
                foreach (ReleaseNote note in rel.Notes)
                {
                    if (note.IsGroup)
                    {
                        AppendNote("\n▍ " + (string.IsNullOrEmpty(note.Group) ? "其他" : note.Group) + "\n", UiFont.Bold, Palette.Text2);
                        foreach (string item in note.Items) AppendNote("    • " + item + "\n", UiFont.Base, Palette.Text2);
                    }
                    else
                    {
                        AppendNote("• " + note.Text + "\n", UiFont.Base, Palette.Text2);
                    }
                }
            }

            PackageInfo win = rel.WinPackage;
            if (win != null)
            {
                AppendNote("\nWindows 安装包：" + (string.IsNullOrEmpty(win.SizeText) ? "" : win.SizeText + "  ") + win.Url + "\n", UiFont.Small, Palette.Muted);
            }
            if (!string.IsNullOrEmpty(rel.NotesUrl))
                AppendNote("官方说明：" + rel.NotesUrl + "\n", UiFont.Small, Palette.Muted);
        }

        private void AppendNote(string text, Font font, Color color)
        {
            _notes.Add(text, font, color, 0);
        }

        /* ---------------- 操作 ---------------- */

        private async Task ToggleLock()
        {
            if (_busy) return;
            BlockStatus st;
            _blocks.TryGetValue(_selectedApp, out st);
            bool blocked = st != null && st.Enabled;
            AppDef app = Apps.ById(_selectedApp);

            if (blocked)
            {
                BlockResult r = await Task.Run(delegate { return UpdateBlock.Disable(_selectedApp); });
                if (!r.Ok) { AppendLog("解除锁定失败：" + r.Error); MessageBox.Show(this, r.Error, "解除锁定失败", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
                StateStore.Instance.ClearLock(_selectedApp);
                AppendLog("已解除 " + app.Name + " 的锁定：启动更新开关已还原，缓存目录恢复可写");
            }
            else
            {
                InstallInfo info;
                _installs.TryGetValue(_selectedApp, out info);
                string version = info != null && info.Found ? info.Version : "";
                if (string.IsNullOrEmpty(version))
                {
                    MessageBox.Show(this, "没检测到已安装的版本，无法锁定。可以先用「指定目录」指向安装位置。", "无法锁定", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                AppendLog("开始锁定 " + app.Name + " 到 " + version + " …");
                BlockResult r = await Task.Run(delegate { return UpdateBlock.Enable(_selectedApp); });
                if (!r.Ok) { AppendLog("锁定失败：" + r.Error); MessageBox.Show(this, r.Error, "锁定失败", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }

                StateStore.Instance.SetLock(_selectedApp, version, "锁定当前安装版本");

                foreach (string m in r.Moved) AppendLog("  回收安装包 " + m);
                foreach (string c in r.Cleared) AppendLog("  清除记录 " + c);
                AppendLog("  已关闭启动强制更新（app-config.json / settings.json）");
                AppendLog("  已拒绝写入缓存目录 " + Paths.ProfileOf(_selectedApp).CacheDirs[0]);
                AppendLog("锁定完成：" + app.Name + " → " + version);
            }

            await ReloadAll(false);
        }

        private async Task DownloadSelected()
        {
            Release rel = SelectedRelease();
            if (rel == null) return;
            if (_downloadCts != null) { AppendLog("已有下载任务在进行"); return; }

            PackageInfo win = rel.WinPackage;
            if (win == null || string.IsNullOrEmpty(win.Url))
            {
                MessageBox.Show(this, "该版本没有 Windows 安装包链接。", "无法下载", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string existing = Downloads.FindCached(_selectedApp, rel.Version) != null ? "（本地已存在，将覆盖）" : "";
            AppendLog("开始下载 " + rel.Version + " " + (string.IsNullOrEmpty(win.SizeText) ? "" : "(" + win.SizeText + ")") + existing);

            _downloadCts = new CancellationTokenSource();
            CancellationToken token = _downloadCts.Token;
            SetBusy(true);

            Progress<DownloadJob> progress = new Progress<DownloadJob>(delegate(DownloadJob j)
            {
                _progress.Value = j.Percent;
                if (j.Status == "downloading")
                    _progressLabel.Text = j.Percent + "%  " + Fmt.Bytes(j.Received) + " / " + Fmt.Bytes(j.Total) + "  " + j.SpeedText;
                else if (j.Status == "done")
                    _progressLabel.Text = "下载完成 · " + Fmt.Bytes(j.Received);
                else
                    _progressLabel.Text = j.Status == "cancelled" ? "已取消" : ("失败：" + j.Error);
            });

            try
            {
                DownloadJob job = await Downloads.DownloadAsync(_selectedApp, rel.Version, win.Url, progress, token);
                if (job.Status == "done")
                {
                    AppendLog("下载完成：" + Paths.Display(job.FilePath));
                    AppendLog("  大小 " + Fmt.Bytes(job.Received));
                }
                else
                {
                    AppendLog("下载失败：" + job.Error);
                }
            }
            catch (Exception ex)
            {
                AppendLog("下载异常：" + ex.Message);
            }
            finally
            {
                _downloadCts.Dispose();
                _downloadCts = null;
                SetBusy(false);
                RefreshCacheSummary();
                FillGrid();
            }
        }

        private async Task RollbackSelected()
        {
            Release rel = SelectedRelease();
            if (rel == null) return;

            InstallInfo info;
            _installs.TryGetValue(_selectedApp, out info);
            if (info == null || !info.Found)
            {
                MessageBox.Show(this, "未找到安装目录。", "无法安装", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (info.Version == rel.Version)
            {
                MessageBox.Show(this, "当前已经是 " + rel.Version + "。", "无需回退", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            CachedFile cached = Downloads.FindCached(_selectedApp, rel.Version);
            if (cached == null)
            {
                DialogResult ask = MessageBox.Show(this,
                    "本地没有 " + rel.Version + " 的安装包。要现在下载吗？\n\n下载完成后会自动继续安装。",
                    "缺少安装包", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (ask == DialogResult.Yes) await DownloadSelected();
                cached = Downloads.FindCached(_selectedApp, rel.Version);
                if (cached == null) return;
            }

            DialogResult confirm = MessageBox.Show(this,
                "将把 " + Apps.ById(_selectedApp).Name + " 从 " + info.Version + " 换成 " + rel.Version + "。\n\n" +
                "· 会先关闭客户端\n" +
                "· 安装过程需要管理员权限（UAC）\n" +
                "· 工作区、任务、技能等数据不会改动\n\n继续？",
                "确认回退", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (confirm != DialogResult.OK) return;

            AppendLog("开始回退到 " + rel.Version + " …");
            SetBusy(true);
            Progress<string> log = new Progress<string>(delegate(string msg) { AppendLog("  " + msg); });

            try
            {
                string sha = "";
                if (_catalog.Latest != null && _catalog.LatestBuild.StartsWith(rel.Version, StringComparison.Ordinal))
                    sha = _catalog.Latest.GetString("sha256", "");

                InstallResult r = await InstallService.InstallAsync(_selectedApp, rel.Version, cached.FullPath, sha, log);
                if (r.Ok)
                {
                    AppendLog("回退完成：" + r.BeforeVersion + " → " + r.AfterVersion + "，用时 " + (r.DurationMs / 1000) + " 秒");
                    if (StateStore.Instance.LockedVersion(_selectedApp) != rel.Version)
                        StateStore.Instance.SetLock(_selectedApp, rel.Version, "回退后锁定");
                }
                else
                {
                    AppendLog("回退失败：" + r.Error);
                    MessageBox.Show(this, r.Error, "回退失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                AppendLog("回退异常：" + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
            await ReloadAll(false);
        }

        private void CopyLinks(bool exportAll)
        {
            Release rel = SelectedRelease();
            if (rel == null) return;
            string text = BuildLinkText(rel, true);
            try
            {
                Clipboard.SetText(text);
                AppendLog("已复制 " + rel.Version + " 的下载链接（3 个平台）");
            }
            catch (Exception ex)
            {
                AppendLog("复制失败：" + ex.Message);
            }
        }

        private void ExportLinks()
        {
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = "导出下载链接";
                dlg.Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
                dlg.FileName = "workbuddy-links-" + DateTime.Now.ToString("yyyyMMdd") + ".txt";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# WorkBuddy / WorkBuddy AI 官方安装包链接");
                sb.AppendLine("# 导出时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("# 来源：https://www.workbuddy.cn/docs/workbuddy/Download-History");
                sb.AppendLine();

                foreach (Release rel in _catalog.Releases)
                {
                    sb.AppendLine("## " + rel.Version + (string.IsNullOrEmpty(rel.Date) ? "" : "  " + rel.Date));
                    foreach (KeyValuePair<string, PackageInfo> kv in rel.Packages)
                    {
                        string label;
                        if (!Catalog.PlatformLabels.TryGetValue(kv.Key, out label)) label = kv.Key;
                        sb.AppendLine(label + "  " + kv.Value.SizeText);
                        sb.AppendLine("  " + kv.Value.Url);
                    }
                    sb.AppendLine();
                }

                try
                {
                    File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(false));
                    AppendLog("已导出 " + _catalog.Releases.Count + " 个版本的链接：" + Paths.Display(dlg.FileName));
                }
                catch (Exception ex)
                {
                    AppendLog("导出失败：" + ex.Message);
                }
            }
        }

        private static string BuildLinkText(Release rel, bool includeAll)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(rel.Version + (string.IsNullOrEmpty(rel.Date) ? "" : "  " + rel.Date));
            foreach (KeyValuePair<string, PackageInfo> kv in rel.Packages)
            {
                string label;
                if (!Catalog.PlatformLabels.TryGetValue(kv.Key, out label)) label = kv.Key;
                if (!includeAll && kv.Key != "win32-x64-user") continue;
                sb.AppendLine(label + "：" + kv.Value.Url);
            }
            return sb.ToString();
        }

        private async Task RefreshCatalog()
        {
            if (_busy) return;
            SetBusy(true);
            AppendLog("开始刷新版本目录…");
            Progress<string> progress = new Progress<string>(delegate(string msg) { _progressLabel.Text = msg; });

            try
            {
                Catalog.RefreshReport report = await _catalog.RefreshAsync(progress);
                _progress.Value = 100;
                _progressLabel.Text = "目录已更新 · " + report.Total + " 个版本";
                foreach (string w in report.Warnings) AppendLog("  警告：" + w);
                if (report.Added.Count > 0) AppendLog("  新增版本：" + string.Join("、", report.Added.ToArray()));
                if (report.UpdatedNotes.Count > 0) AppendLog("  补全说明：" + string.Join("、", report.UpdatedNotes.ToArray()));
                AppendLog("目录刷新完成：共 " + report.Total + " 个版本，最新 " + (string.IsNullOrEmpty(report.LatestVersion) ? "未知" : report.LatestVersion));
            }
            catch (Exception ex)
            {
                AppendLog("刷新失败：" + ex.Message);
            }
            finally
            {
                SetBusy(false);
                RefreshHeader();
                FillGrid();
            }
        }

        private async void LocateApp(string appId)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择 " + Apps.ById(appId).Name + " 的安装目录（包含 " + Apps.ById(appId).Exe + " 的那一层）";
                InstallInfo info;
                if (_installs.TryGetValue(appId, out info) && info.Found) dlg.SelectedPath = info.InstallDir;

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                AppDef app = Apps.ById(appId);
                if (!File.Exists(Path.Combine(dlg.SelectedPath, app.Exe)))
                {
                    MessageBox.Show(this, "这个目录里没有 " + app.Exe + "，请重新选择。", "目录无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                StateStore.Instance.SetCustomPath(appId, dlg.SelectedPath);
                AppendLog("已指定 " + app.Name + " 安装目录：" + Paths.Display(dlg.SelectedPath));
                await ReloadAll(false);
            }
        }

        /// <summary>开发自检：导出界面关键状态</summary>
        public string DebugDump()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("selected=" + _selectedApp);
            foreach (KeyValuePair<string, AppCard> kv in _cards)
            {
                InstallInfo i;
                _installs.TryGetValue(kv.Key, out i);
                BlockStatus b;
                _blocks.TryGetValue(kv.Key, out b);
                sb.AppendLine(kv.Key + " found=" + (i != null && i.Found) + " version=" + (i == null ? "" : i.Version) +
                    " dir=" + (i == null ? "" : i.InstallDir) + " blocked=" + (b != null && b.Enabled) +
                    " cardVisible=" + kv.Value.Visible + " cardBounds=" + kv.Value.Bounds);
            }
            sb.AppendLine("gridRows=" + _grid.Rows.Count);
            sb.AppendLine("notesChars=" + _notes.CharCount);
            sb.AppendLine("---- notes ----");
            sb.Append(_notes.PlainText);
            return sb.ToString();
        }

        private void SetBusy(bool busy)
        {
            _busy = busy;
            UpdateActionState();
        }

        private void AppendLog(string line)
        {
            if (_log == null) return;
            if (_log.Lines.Length > 400)
            {
                string[] keep = _log.Lines;
                string[] next = new string[200];
                Array.Copy(keep, keep.Length - 200, next, 0, 200);
                _log.Lines = next;
            }
            _log.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line + "\r\n");
        }
    }
}
