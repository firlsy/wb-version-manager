using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net.Http;
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
        /// <summary>当前客户端已缓存的版本短号，FillGrid 时算一次，别处复用</summary>
        private readonly HashSet<string> _cachedVersions = new HashSet<string>(StringComparer.Ordinal);
        private string _selectedApp = "workbuddy";
        private CancellationTokenSource _downloadCts;
        private bool _busy;

        private Badge _sourceBadge;
        private FlatButton _refreshBtn;
        private FlatButton _dataDirBtn;
        private FlatButton _checkBtn;

        private readonly Dictionary<string, AppCard> _cards = new Dictionary<string, AppCard>(StringComparer.Ordinal);
        private Label _cacheTitle;
        private Label _cacheLabel;
        private FlatButton _openStoreBtn;
        private FlatButton _clearCacheBtn;
        private CardPanel _guideCard;
        private FlatButton _guideMore;
        private Panel _guideLeft;

        private Label _appTitle;        private Label _appSub;
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
            // 缓存包归属哪一版要靠实时目录来判断，这里把引用挂过去
            Downloads.CatalogProvider = delegate { return _catalog; };
            BuildUi();
            Load += delegate { TryLoadDiskCatalog(); };
            Shown += async delegate { FitGuideCard(); await ReloadAll(true); };

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
                // 条数一样时也优先用磁盘版：里面带着用户自己刷新出来的国际版记录和更新说明
                if (disk.Releases.Count >= _catalog.Releases.Count) _catalog = disk;
            }
            catch { }
        }

        /* ---------------- UI 构建 ---------------- */

        private void BuildUi()
        {
            Text = AppInfo.TitleWithVersion;
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
            mark.Cursor = Cursors.Hand;
            mark.Click += delegate { OpenRepo(); };
            header.Controls.Add(mark);

            Label t1 = new Label();
            t1.Text = "版本管理器";
            t1.Font = UiFont.H1;
            t1.ForeColor = Palette.Text;
            t1.AutoSize = true;
            t1.BackColor = Color.Transparent;
            t1.Cursor = Cursors.Hand;
            t1.Location = new Point(70, 12);
            t1.Click += delegate { OpenRepo(); };
            header.Controls.Add(t1);

            Label t2 = new Label();
            t2.Text = "WorkBuddy · WorkBuddy AI 版本锁定与回退";
            t2.Font = UiFont.Small;
            t2.ForeColor = Palette.Muted;
            t2.AutoSize = true;
            t2.BackColor = Color.Transparent;
            t2.Cursor = Cursors.Hand;
            t2.Location = new Point(72, 42);
            t2.Click += delegate { OpenRepo(); };
            header.Controls.Add(t2);

            // 版本号 + 检查更新，紧跟在标题后面
            _checkBtn = new FlatButton();
            _checkBtn.Text = "检查更新";
            _checkBtn.Font = UiFont.Small;
            _checkBtn.Radius = 7;
            _checkBtn.Size = new Size(80, 26);
            _checkBtn.TextColor = Palette.Accent;
            _checkBtn.Location = new Point(t1.Right + 12, 15);
            _checkBtn.Click += async delegate { await CheckUpdate(); };
            header.Controls.Add(_checkBtn);

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
            _cacheTitle = cl;

            _cacheLabel = new Label();
            _cacheLabel.Font = UiFont.Small;
            _cacheLabel.ForeColor = Palette.Text2;
            _cacheLabel.BackColor = Color.Transparent;
            _cacheLabel.Location = new Point(16, 42);
            _cacheLabel.Size = new Size(278, 58);
            cacheCard.Controls.Add(_cacheLabel);

            _openStoreBtn = new FlatButton();
            _openStoreBtn.Text = "打开缓存目录";
            _openStoreBtn.Size = new Size(118, 30);
            _openStoreBtn.Location = new Point(16, 108);
            _openStoreBtn.Click += delegate
            {
                try
                {
                    List<CachedFile> cached = Downloads.ListCached(_selectedApp);
                    string dir = cached.Count > 0 ? Path.GetDirectoryName(cached[0].FullPath)
                                                  : Path.Combine(Paths.StoreRoot, _selectedApp);
                    Directory.CreateDirectory(dir);
                    Process.Start("explorer.exe", dir);
                }
                catch (Exception ex) { AppendLog("打开缓存目录失败：" + ex.Message); }
            };
            cacheCard.Controls.Add(_openStoreBtn);

            _clearCacheBtn = new FlatButton();
            _clearCacheBtn.Text = "清除无用缓存";
            _clearCacheBtn.Size = new Size(128, 30);
            _clearCacheBtn.Location = new Point(142, 108);
            _clearCacheBtn.Danger = true;
            _clearCacheBtn.Click += delegate { ClearUselessCache(); };
            cacheCard.Controls.Add(_clearCacheBtn);

            /* 左下：回退步骤 */
            CardPanel guideCard = new CardPanel();
            guideCard.Location = new Point(0, y + 162);
            guideCard.Size = new Size(LeftWidth - 12, 218);
            guideCard.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            left.Controls.Add(guideCard);
            _guideCard = guideCard;

            Label gTitle = new Label();
            gTitle.Text = "从新版本回退到旧版本";
            gTitle.Font = UiFont.H3;
            gTitle.ForeColor = Palette.Text;
            gTitle.AutoSize = true;
            gTitle.BackColor = Color.Transparent;
            gTitle.Location = new Point(16, 12);
            guideCard.Controls.Add(gTitle);

            Label gBody = new Label();
            gBody.Font = UiFont.Small;
            gBody.ForeColor = Palette.Text2;
            gBody.BackColor = Color.Transparent;
            gBody.Location = new Point(16, 38);
            gBody.Size = new Size(278, 100);
            gBody.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            gBody.Text = BuildGuideText();
            guideCard.Controls.Add(gBody);

            Label gNote = new Label();
            gNote.Font = UiFont.Small;
            gNote.ForeColor = Palette.Muted;
            gNote.BackColor = Color.Transparent;
            gNote.Location = new Point(16, 140);
            gNote.Size = new Size(278, 18);
            gNote.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            gNote.Text = "· 数据在工作区目录，回退不会动。";
            guideCard.Controls.Add(gNote);

            FlatButton gMore = new FlatButton();
            gMore.Text = "完整说明";
            gMore.Size = new Size(88, 26);
            gMore.Location = new Point(16, 184);
            gMore.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            gMore.Click += delegate { ShowGuideDialog(); };
            guideCard.Controls.Add(gMore);
            _guideMore = gMore;
            _guideLeft = left;
            left.Resize += delegate { FitGuideCard(); };

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
                SetBusy(false);
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
            Release rel = SelectedRelease();
            bool hasRelease = rel != null;
            // 官方有包、或本机缓存里有包，才算「能装」
            bool hasSource = rel != null && (rel.HasPackageFor(_selectedApp) || _cachedVersions.Contains(rel.Version));
            bool hasRemote = rel != null && rel.HasPackageFor(_selectedApp);

            _btnLock.Enabled = found && !blocked && !_busy;
            _btnUnlock.Enabled = blocked && !_busy;
            _btnDownload.Enabled = hasRemote && !_busy && _downloadCts == null;
            _btnRollback.Enabled = found && hasRelease && hasSource && !_busy;
            _btnCopy.Enabled = hasRelease && !_busy;
            _btnExport.Enabled = !_busy;
            _refreshBtn.Enabled = !_busy;
        }

        /// <summary>回退步骤卡片撑满左栏剩余高度，按钮贴底</summary>
        private void FitGuideCard()
        {
            if (_guideCard == null || _guideLeft == null) return;
            int h = _guideLeft.ClientSize.Height - _guideCard.Top - 12;
            if (h < 150) h = 150;
            _guideCard.Height = h;
            if (_guideMore != null) _guideMore.Top = h - 34;
        }

        private static string BuildGuideText()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("1  左侧点选要回退的客户端\n");
            sb.Append("2  版本清单里选中目标旧版本\n");
            sb.Append("3  点「下载选中版本」等下载完成\n");
            sb.Append("4  点「回退安装」：自动解锁 + 打开资源管理器\n");
            sb.Append("5  双击选中的安装包，按向导装完\n");
            sb.Append("6  回工具点「锁定当前版本」\n");
            return sb.ToString();
        }

        private static string BuildGuideDetail()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("回退前先确认两件事");
            sb.AppendLine();
            sb.AppendLine("· 数据不会丢。工作区、任务、历史会话、技能、资料库都放在");
            sb.AppendLine("  %USERPROFILE%\\.workbuddy 和 %LOCALAPPDATA% 下面，跟客户端版本无关，");
            sb.AppendLine("  回退只是把程序目录换回旧版本，不会碰这些数据。");
            sb.AppendLine("· 回退要靠官方安装包。工具只负责把环境和安装包准备好，真正的安装");
            sb.AppendLine("  由你双击运行安装包完成，安装器会自己识别原安装目录。");
            sb.AppendLine();
            sb.AppendLine("步骤");
            sb.AppendLine();
            sb.AppendLine("1. 在左侧点选要回退的客户端：WorkBuddy 国内版 或 WorkBuddy AI 国际版。");
            sb.AppendLine("   两款互不影响，一次只处理一个。");
            sb.AppendLine();
            sb.AppendLine("2. 在中间的版本清单里找到目标旧版本。看「安装包」这一列：");
            sb.AppendLine("   显示大小或「官方包」= 能下载；显示「本地包」= 官方没提供，");
            sb.AppendLine("   但本机缓存里已经有了；显示「—」= 既没官方地址也没缓存，装不了。");
            sb.AppendLine();
            sb.AppendLine("3. 点「下载选中版本」。已经缓存过的会直接复用，不会重复下载。");
            sb.AppendLine("   下载带进度和速度，中断了会留 .part 文件，下次重新下即可。");
            sb.AppendLine();
            sb.AppendLine("4. 点「回退安装」。工具在这一步做四件事，不需要你手动操作：");
            sb.AppendLine("   · 结束正在运行的客户端");
            sb.AppendLine("   · 自动解除锁定：还原启动更新开关、放开缓存目录写入");
            sb.AppendLine("   · 清除安装失败熔断记录（原文件备份到 block-backup）");
            sb.AppendLine("   · 回收客户端更新缓存里待装的包，免得装完又被顶回新版本");
            sb.AppendLine("   做完之后打开资源管理器，把安装包选中给你。");
            sb.AppendLine();
            sb.AppendLine("5. 弹出「可以开始安装了」对话框时，点「是」表示装完就回来。");
            sb.AppendLine("   安装包在资源管理器里已经选中，双击它开始安装。");
            sb.AppendLine();
            sb.AppendLine("6. 安装向导里按默认走。安装器自己识别原安装目录，不用你指定路径。");
            sb.AppendLine("   弹出 UAC 就点「是」；提示目录被占用就先确认客户端已经退出。");
            sb.AppendLine();
            sb.AppendLine("7. 装完回到工具，看左侧卡片版本号是不是已经变成目标版本。");
            sb.AppendLine("   第 5 步点了「是」的话，工具会自动重新检测并重新锁定，这步可跳过。");
            sb.AppendLine();
            sb.AppendLine("8. 点「锁定当前版本」。这一步别省：不锁定的话，客户端下次启动");
            sb.AppendLine("   还会自己检查更新，过几天又被顶到新版本。");
            sb.AppendLine();
            sb.AppendLine("没成功怎么办");
            sb.AppendLine();
            sb.AppendLine("· 「安装包」列显示「—」：这个版本官方没有发布安装包。国内版可以换");
            sb.AppendLine("  一个更早的版本试；国际版官方只发布当前版本，历史版本只能靠本机缓存。");
            sb.AppendLine("· 安装器报错退出：多半是目标目录被占用。先手动退出客户端，再双击");
            sb.AppendLine("  安装包重试；也可以重启电脑后再装。");
            sb.AppendLine("· 装完版本号没变：安装向导可能被取消了，重新双击安装包走一遍。");
            sb.AppendLine("· 回退成功但还是提示更新：说明没锁定，重复第 8 步。");
            sb.AppendLine("· 想省事也可以用命令行：");
            string exeName = Path.GetFileName(Application.ExecutablePath);
            sb.AppendLine("    " + exeName + " --unlock workbuddy");
            sb.AppendLine("    " + exeName + " --lock workbuddy");
            return sb.ToString();
        }

        private void ShowGuideDialog()
        {
            Form f = new Form();
            f.Text = "从新版本回退到旧版本 · 完整说明";
            f.StartPosition = FormStartPosition.CenterParent;
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MaximizeBox = false;
            f.MinimizeBox = false;
            f.ShowInTaskbar = false;
            f.ClientSize = new Size(620, 540);
            f.BackColor = Palette.Bg;
            f.Font = UiFont.Base;

            Panel bar = new Panel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = 48;
            bar.BackColor = Palette.Bg;
            f.Controls.Add(bar);

            Panel host = new Panel();
            host.Dock = DockStyle.Fill;
            host.Padding = new Padding(14);
            host.BackColor = Color.White;
            f.Controls.Add(host);
            host.BringToFront();

            TextBox tb = new TextBox();
            tb.Multiline = true;
            tb.ReadOnly = true;
            tb.ScrollBars = ScrollBars.Vertical;
            tb.BorderStyle = BorderStyle.None;
            tb.BackColor = Color.White;
            tb.ForeColor = Palette.Text;
            tb.Font = UiFont.Small;
            tb.Dock = DockStyle.Fill;
            tb.Text = BuildGuideDetail();
            tb.Select(0, 0);
            host.Controls.Add(tb);

            FlatButton ok = new FlatButton();
            ok.Text = "知道了";
            ok.Size = new Size(96, 32);
            ok.Primary = true;
            ok.Location = new Point(f.ClientSize.Width - 96 - 16, 8);
            ok.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            ok.Click += delegate { f.Close(); };
            bar.Controls.Add(ok);

            // 开发用：把说明窗口渲染成图片后退出
            string shot = Environment.GetEnvironmentVariable("WBVM_GUIDE_SHOT");
            if (!string.IsNullOrEmpty(shot))
            {
                f.Show();
                Application.DoEvents();
                using (Bitmap bmp = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(shot, System.Drawing.Imaging.ImageFormat.Png);
                }
                f.Close();
                return;
            }

            f.ShowDialog(this);
        }

        private void RefreshCacheSummary()
        {
            AppDef app = Apps.ById(_selectedApp);
            if (_cacheTitle != null) _cacheTitle.Text = "安装包缓存 · " + app.Edition;

            InstallInfo info;
            _installs.TryGetValue(_selectedApp, out info);
            string installed = info != null && info.Found ? info.Version : "";
            string locked = StateStore.Instance.LockedVersion(_selectedApp);

            List<CachedFile> cached = Downloads.ListCached(_selectedApp);
            long total = 0;
            for (int i = 0; i < cached.Count; i++) total += cached[i].Size;
            List<FileInfo> parts = Downloads.ListPartFiles(_selectedApp);
            long partBytes = 0;
            for (int i = 0; i < parts.Count; i++) partBytes += parts[i].Length;

            StringBuilder sb = new StringBuilder();
            if (cached.Count == 0 && parts.Count == 0)
            {
                sb.Append("暂无缓存的安装包。下载后会存到本地，回退时直接复用。");
            }
            else
            {
                int wrong = 0;
                foreach (CachedFile f in cached) if (!f.Usable) wrong++;

                sb.Append(cached.Count + " 个安装包 · 共 " + Fmt.Bytes(total));
                if (wrong > 0) sb.Append(" · " + wrong + " 个错版");
                if (parts.Count > 0) sb.Append("，另有 " + parts.Count + " 个未完成碎片 " + Fmt.Bytes(partBytes));
                sb.Append("\n");
                int n = 0;
                foreach (CachedFile f in cached)
                {
                    if (n++ >= 2) break;
                    bool keep = f.Usable && (Downloads.MatchesVersion(f, installed) || Downloads.MatchesVersion(f, locked));
                    sb.Append("· " + (string.IsNullOrEmpty(f.VersionShort) ? f.FileName : f.VersionShort) + "  " + f.SizeText);
                    if (!f.Usable) sb.Append("（错版）");
                    else if (keep) sb.Append("（在用）");
                    sb.Append("\n");
                }
                if (cached.Count > 2) sb.Append("…");
            }
            _cacheLabel.Text = sb.ToString();

            List<CachedFile> useless = Downloads.ListUseless(_selectedApp, installed, locked);
            int pending = useless.Count + parts.Count;
            if (_clearCacheBtn != null)
            {
                _clearCacheBtn.Text = pending > 0 ? "清除无用缓存 " + pending : "清除无用缓存";
                _clearCacheBtn.Enabled = pending > 0 && !_busy;
            }
        }

        /// <summary>只清当前选中客户端的缓存，另一个客户端不受影响</summary>
        private void ClearUselessCache()
        {
            if (_busy) return;
            AppDef app = Apps.ById(_selectedApp);
            AppDef other = Apps.ById(_selectedApp == "workbuddy" ? "workbuddyai" : "workbuddy");

            InstallInfo info;
            _installs.TryGetValue(_selectedApp, out info);
            string installed = info != null && info.Found ? info.Version : "";
            string locked = StateStore.Instance.LockedVersion(_selectedApp);

            List<CachedFile> useless = Downloads.ListUseless(_selectedApp, installed, locked);
            List<FileInfo> parts = Downloads.ListPartFiles(_selectedApp);
            long bytes = 0;
            for (int i = 0; i < useless.Count; i++) bytes += useless[i].Size;
            for (int i = 0; i < parts.Count; i++) bytes += parts[i].Length;

            if (useless.Count == 0 && parts.Count == 0)
            {
                MessageBox.Show(this,
                    app.Name + " " + app.Edition + " 没有需要清理的缓存。\n\n" +
                    "当前版本：" + (installed == "" ? "未检测到" : installed) + "\n" +
                    "锁定版本：" + (string.IsNullOrEmpty(locked) ? "未锁定" : locked) + "\n\n" +
                    "缓存里只剩下这两个版本要用的安装包。",
                    "清除无用缓存", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("将清理 " + app.Name + " " + app.Edition + " 的 " + (useless.Count + parts.Count) +
                          " 个文件，释放 " + Fmt.Bytes(bytes) + "：");
            sb.AppendLine();
            for (int i = 0; i < useless.Count && i < 12; i++)
                sb.AppendLine("· " + useless[i].FileName + "   " + useless[i].SizeText +
                    (useless[i].Usable ? "" : "（错版，不是" + app.Edition + "的包）"));
            if (useless.Count > 12) sb.AppendLine("· …… 另外 " + (useless.Count - 12) + " 个");
            for (int i = 0; i < parts.Count && i < 6; i++)
                sb.AppendLine("· " + parts[i].Name + "   " + Fmt.Bytes(parts[i].Length) + "（未完成的下载）");
            sb.AppendLine();
            sb.AppendLine("保留：" + (installed == "" ? "（无当前版本）" : "当前版本 " + installed) +
                          "、" + (string.IsNullOrEmpty(locked) ? "（未锁定）" : "锁定版本 " + locked));
            sb.AppendLine();
            sb.Append("只动 " + app.Edition + " 的目录，" + other.Edition + " 的缓存不受影响。确定删除？");

            if (MessageBox.Show(this, sb.ToString(), "清除无用缓存",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return;

            Cursor prev = Cursor;
            Cursor = Cursors.WaitCursor;
            int removed = 0;
            long freed = 0;
            List<string> failed = new List<string>();
            try
            {
                foreach (CachedFile f in useless)
                {
                    try { File.Delete(f.FullPath); removed++; freed += f.Size; }
                    catch (Exception ex) { failed.Add(f.FileName + "（" + ex.Message + "）"); }
                }
                foreach (FileInfo p in parts)
                {
                    try { long len = p.Length; p.Delete(); removed++; freed += len; }
                    catch (Exception ex) { failed.Add(p.Name + "（" + ex.Message + "）"); }
                }
            }
            finally
            {
                Cursor = prev;
            }

            AppendLog("清理 " + app.Name + " 缓存：删除 " + removed + " 个文件，释放 " + Fmt.Bytes(freed));
            if (failed.Count > 0)
            {
                AppendLog("  有 " + failed.Count + " 个文件没删掉：" + string.Join("；", failed.ToArray()));
                MessageBox.Show(this,
                    "已删除 " + removed + " 个文件，释放 " + Fmt.Bytes(freed) + "。\n\n" +
                    "还有 " + failed.Count + " 个没删掉，通常是文件正被占用：\n" +
                    string.Join("\n", failed.ToArray()),
                    "清除无用缓存", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            RefreshCacheSummary();
            FillGrid();
        }

        private void RefreshHeader()
        {
            string latest = _catalog.LatestVersionFor(_selectedApp);
            _sourceBadge.Text = _catalog.Releases.Count + " 个版本 · 最新 " + (latest == "" ? "未知" : latest);
            _sourceBadge.FillColor = Palette.AccentSoft;
            _sourceBadge.TextColor = Palette.Accent;
            _sourceBadge.DotColor = Palette.Accent;
        }

        private void SelectApp(string appId)
        {
            _selectedApp = appId;
            foreach (KeyValuePair<string, AppCard> kv in _cards) kv.Value.Active = kv.Key == appId;
            FillGrid();
            RefreshCacheSummary();
            RefreshHeader();
            UpdateActionState();
        }

        /// <summary>开发用：截图时指定要展示的客户端</summary>
        public void DevSelect(string appId)
        {
            if (Apps.ById(appId) != null) SelectApp(appId);
        }

        /// <summary>开发用：截图时打开回退说明窗口</summary>
        public void DevShowGuide()
        {
            ShowGuideDialog();
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

            _cachedVersions.Clear();
            HashSet<string> wrongVersions = new HashSet<string>(StringComparer.Ordinal);
            foreach (CachedFile f in Downloads.ListCached())
            {
                if (f.AppId != _selectedApp || f.Platform != "win32-x64-user") continue;
                if (f.Usable) _cachedVersions.Add(f.VersionShort);
                else wrongVersions.Add(f.VersionShort);
            }

            foreach (Release rel in _catalog.Releases)
            {
                string noteText = rel.NoteCount > 0 ? rel.NoteCount + " 条" : "—";
                PackageInfo win = rel.PackageFor(_selectedApp);
                bool localPkg = _cachedVersions.Contains(rel.Version);
                string pkg;
                if (win != null) pkg = string.IsNullOrEmpty(win.SizeText) ? "官方包" : win.SizeText;
                else if (localPkg) pkg = "本地包";
                else if (wrongVersions.Contains(rel.Version)) pkg = "错版包";
                else pkg = "—";
                string local = "";
                if (!string.IsNullOrEmpty(installed) && rel.Version == installed) local = "当前版本";
                else if (localPkg) local = "已缓存";
                if (rel.LocalOnly && local == "") local = "本机";

                int idx = _grid.Rows.Add(rel.Version, rel.Date, noteText, pkg, local);
                _grid.Rows[idx].Tag = rel;
                if (rel.Version == installed) _grid.Rows[idx].DefaultCellStyle.ForeColor = Palette.Accent;
                if (!string.IsNullOrEmpty(locked) && rel.Version == locked)
                    _grid.Rows[idx].Cells[0].Style.Font = UiFont.Bold;
                // 当前客户端既没有官方包、本地也没有能用的包，整行压暗，别让人白点
                if (win == null && !localPkg && rel.Version != installed)
                    _grid.Rows[idx].DefaultCellStyle.ForeColor = Palette.Muted;
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

            AppDef noteApp = Apps.ById(_selectedApp);
            PackageInfo win = rel.PackageFor(_selectedApp);
            if (win != null)
            {
                AppendNote("\n" + noteApp.Edition + " Windows 安装包：" +
                    (string.IsNullOrEmpty(win.SizeText) ? "" : win.SizeText + "  ") + win.Url + "\n", UiFont.Small, Palette.Muted);
            }
            else if (_cachedVersions.Contains(rel.Version))
            {
                AppendNote("\n" + noteApp.Edition + " 这个版本官方没有下载地址，但本机缓存里已经有安装包，可以直接回退。\n", UiFont.Small, Palette.Amber);
            }
            else if (noteApp.Id == "workbuddyai")
            {
                AppendNote("\n国际版官方只发布当前版本（" + (_catalog.IntlLatestVersion == "" ? "未知" : _catalog.IntlLatestVersion) +
                    "）的安装包，这个版本没有官方下载地址。\n", UiFont.Small, Palette.Muted);
            }
            else
            {
                AppendNote("\n这个版本没有 Windows 安装包链接。\n", UiFont.Small, Palette.Muted);
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

            AppDef dlApp = Apps.ById(_selectedApp);
            PackageInfo win = rel.PackageFor(_selectedApp);
            if (win == null || string.IsNullOrEmpty(win.Url))
            {
                string msg = dlApp.Id == "workbuddyai"
                    ? "国际版官方只发布当前版本（" + (_catalog.IntlLatestVersion == "" ? "未知" : _catalog.IntlLatestVersion) +
                      "）的安装包，" + rel.Version + " 没有官方下载地址。\n\n" +
                      "如果之前在国际版客户端里下载过这个版本，可以点「打开缓存目录」找找看。"
                    : "这个版本没有 Windows 安装包链接。";
                MessageBox.Show(this, msg, "无法下载", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string existing = Downloads.FindCached(_selectedApp, rel.Version) != null ? "（本地已存在，将覆盖）" : "";
            AppendLog("开始下载 " + dlApp.Edition + " " + rel.Version + " " +
                (string.IsNullOrEmpty(win.SizeText) ? "" : "(" + win.SizeText + ")") + existing);
            AppendLog("  来源 " + win.Url);

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
            AppDef app = Apps.ById(_selectedApp);

            InstallInfo info;
            _installs.TryGetValue(_selectedApp, out info);
            string current = info != null && info.Found ? info.Version : "";
            if (info == null || !info.Found)
            {
                MessageBox.Show(this,
                    "没检测到 " + app.Name + " 的安装目录。\n\n" +
                    "回退要先把安装包下下来，装的时候安装器会自己找原目录。\n" +
                    "如果确实装过但没识别到，先在左侧卡片点「指定目录」。",
                    "无法回退", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (current == rel.Version)
            {
                MessageBox.Show(this, "当前已经是 " + rel.Version + "。", "无需回退", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            CachedFile cached = Downloads.FindCached(_selectedApp, rel.Version);
            PackageInfo pkg = rel.PackageFor(_selectedApp);
            if (cached == null && pkg == null)
            {
                string wrongName = "";
                foreach (CachedFile f in Downloads.ListCached(_selectedApp))
                    if (!f.Usable && Downloads.MatchesVersion(f, rel.Version)) { wrongName = f.FileName; break; }

                string msg;
                if (wrongName != "")
                {
                    string other = app.Id == "workbuddyai" ? "国内版" : "国际版";
                    msg = "本地这个 " + rel.Version + " 的安装包是" + other + "的包，不能用来装" + app.Edition + "：\n" +
                          wrongName + "\n\n" +
                          "旧版本的工具把国内版链接错当成了国际版，已经下过的包都是错的。\n" +
                          "点缓存卡片的「清除无用缓存」清掉，再重新下载。";
                }
                else if (app.Id == "workbuddyai")
                {
                    msg = "国际版官方只发布当前版本（" + (_catalog.IntlLatestVersion == "" ? "未知" : _catalog.IntlLatestVersion) +
                          "）的安装包，" + rel.Version + " 既没有官方下载地址，本机也没有缓存。";
                }
                else
                {
                    msg = "本地没有 " + rel.Version + " 的安装包，官方也没有提供这个版本的下载地址。";
                }
                MessageBox.Show(this, msg, "没有可用安装包", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (cached == null)
            {
                DialogResult ask = MessageBox.Show(this,
                    "本地没有 " + rel.Version + " 的安装包。要现在下载吗？\n\n下载完成后会接着准备回退环境。",
                    "缺少安装包", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (ask == DialogResult.Yes) await DownloadSelected();
                cached = Downloads.FindCached(_selectedApp, rel.Version);
                if (cached == null) return;
            }

            BlockStatus st;
            _blocks.TryGetValue(_selectedApp, out st);
            bool blocked = st != null && st.Enabled;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("将把 " + app.Name + " " + app.Edition + " 从 " + current + " 换回 " + rel.Version + "。");
            sb.AppendLine();
            sb.AppendLine("点「确定」后工具自动做这几件事：");
            sb.AppendLine("① 结束正在运行的客户端");
            if (blocked) sb.AppendLine("② 自动解除锁定（还原启动更新开关、放开缓存写入）");
            else sb.AppendLine("② 确认当前未锁定，无需解除");
            sb.AppendLine("③ 清除安装失败熔断记录（备份到 block-backup）");
            sb.AppendLine("④ 回收客户端更新缓存里待装的包");
            sb.AppendLine("⑤ 打开资源管理器，把安装包选中");
            sb.AppendLine();
            sb.AppendLine("然后由你双击那个 exe 完成安装，安装器会自己匹配原安装目录。");
            sb.AppendLine("工作区、任务、技能等数据不会改动。");
            if (MessageBox.Show(this, sb.ToString(), "确认回退", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                return;

            AppendLog("准备回退 " + app.Edition + " " + current + " → " + rel.Version + " …");
            SetBusy(true);
            Progress<string> log = new Progress<string>(delegate(string msg) { AppendLog("  " + msg); });

            PrepareResult r = null;
            try
            {
                string sha = _catalog.ShaFor(_selectedApp, rel.Version);
                r = await InstallService.PrepareAsync(_selectedApp, rel.Version, cached.FullPath, sha, log);
                if (!r.Ok)
                {
                    AppendLog("准备失败：" + r.Error);
                    MessageBox.Show(this, r.Error, "准备失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                AppendLog("准备异常：" + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }

            if (r == null || !r.Ok) return;

            await ReloadAll(false);
            AppendLog("回退环境已就绪，等你在资源管理器里双击安装包完成安装。");
            await AskAfterPrepare(app, rel);
        }

        /// <summary>环境准备好之后引导用户双击安装；装完点「是」就重新检测并重新锁定</summary>
        private async Task AskAfterPrepare(AppDef app, Release rel)
        {
            DialogResult yes = MessageBox.Show(this,
                "回退环境已准备好，资源管理器已经打开并选中了安装包。\n\n" +
                "现在请：\n" +
                "1. 切到资源管理器窗口，双击那个 exe\n" +
                "2. 安装向导按默认走完（安装器自己找原安装目录，不用指定路径）\n" +
                "3. 装完回到这个工具\n\n" +
                "锁定已经在第 2 步自动解除，装完记得锁回来：\n" +
                "点「是」→ 工具重新检测版本并自动锁定；\n" +
                "点「否」→ 稍后自己点「刷新目录」再点「锁定当前版本」。",
                "可以开始安装了", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (yes != DialogResult.Yes) return;

            await ReloadAll(false);
            InstallInfo now;
            _installs.TryGetValue(_selectedApp, out now);
            string ver = now != null && now.Found ? now.Version : "";

            if (ver == rel.Version)
            {
                AppendLog(app.Name + " 已回退到 " + ver + "，重新锁定…");
                await ToggleLock();
            }
            else
            {
                MessageBox.Show(this,
                    "还没检测到 " + rel.Version + "，当前是 " + (ver == "" ? "未检测到" : ver) + "。\n\n" +
                    "可能是安装向导还没走完。装完后回到这里点「刷新目录」再点「锁定当前版本」即可。",
                    "版本还没变", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void CopyLinks(bool exportAll)
        {
            Release rel = SelectedRelease();
            if (rel == null) return;
            string text = BuildLinkText(rel, true);
            try
            {
                Clipboard.SetText(text);
                AppendLog("已复制 " + rel.Version + " 的下载链接（国内版 + 国际版）");
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
                sb.AppendLine("# WorkBuddy（国内版）/ WorkBuddy AI（国际版）官方安装包链接");
                sb.AppendLine("# 导出时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("# 国内版来源：https://www.workbuddy.cn/docs/workbuddy/Download-History");
                sb.AppendLine("# 国际版来源：https://www.workbuddy.ai/v2/update?platform=workbuddy-win32-x64-user");
                sb.AppendLine("# 两个版本是两套独立安装包，不要混用。");
                sb.AppendLine();

                foreach (Release rel in _catalog.Releases)
                {
                    sb.AppendLine("## " + rel.Version + (string.IsNullOrEmpty(rel.Date) ? "" : "  " + rel.Date));
                    sb.Append(BuildLinkText(rel, true, false));
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
            return BuildLinkText(rel, includeAll, true);
        }

        private static string BuildLinkText(Release rel, bool includeAll, bool withVersionLine)
        {
            StringBuilder sb = new StringBuilder();
            if (withVersionLine)
                sb.AppendLine(rel.Version + (string.IsNullOrEmpty(rel.Date) ? "" : "  " + rel.Date));

            sb.AppendLine("国内版 WorkBuddy");
            bool cnAny = false;
            foreach (KeyValuePair<string, PackageInfo> kv in rel.Packages)
            {
                if (!includeAll && kv.Key != "win32-x64-user") continue;
                if (string.IsNullOrEmpty(kv.Value.Url)) continue;
                string label;
                if (!Catalog.PlatformLabels.TryGetValue(kv.Key, out label)) label = kv.Key;
                sb.AppendLine("  " + label + "：" + kv.Value.Url);
                cnAny = true;
            }
            if (!cnAny) sb.AppendLine("  （无）");

            sb.AppendLine("国际版 WorkBuddy AI");
            bool intlAny = false;
            foreach (KeyValuePair<string, PackageInfo> kv in rel.IntlPackages)
            {
                if (!includeAll && kv.Key != "win32-x64-user") continue;
                if (string.IsNullOrEmpty(kv.Value.Url)) continue;
                string label;
                if (!Catalog.PlatformLabels.TryGetValue(kv.Key, out label)) label = kv.Key;
                sb.AppendLine("  " + label + "：" + kv.Value.Url);
                intlAny = true;
            }
            if (!intlAny) sb.AppendLine("  （官方未提供该版本）");
            return sb.ToString();
        }

        /// <summary>
        /// 刷新目录 = 版本清单 + 本机安装检测 + 拦截状态，三样一起重扫。
        /// 只刷版本清单的话，装完客户端回来点这个按钮会一直显示「未检测到安装」。
        /// </summary>
        private async Task RefreshCatalog()
        {
            if (_busy) return;
            SetBusy(true);
            AppendLog("开始刷新（版本目录 + 本机安装）…");
            Progress<string> progress = new Progress<string>(delegate(string msg) { _progressLabel.Text = msg; });

            try
            {
                Catalog.RefreshReport report = await _catalog.RefreshAsync(progress);
                _progress.Value = 100;
                _progressLabel.Text = "目录已更新 · " + report.Total + " 个版本";
                foreach (string w in report.Warnings) AppendLog("  警告：" + w);
                if (report.Added.Count > 0) AppendLog("  新增版本：" + string.Join("、", report.Added.ToArray()));
                if (report.UpdatedNotes.Count > 0) AppendLog("  补全说明：" + string.Join("、", report.UpdatedNotes.ToArray()));
                AppendLog("  国内版最新 " + (string.IsNullOrEmpty(report.LatestVersion) ? "未知" : report.LatestVersion));
                if (!string.IsNullOrEmpty(report.IntlVersion))
                {
                    AppendLog("  国际版最新 " + report.IntlVersion + (report.IntlNew ? "（已记录安装包地址）" : ""));
                }
                AppendLog("目录刷新完成：共 " + report.Total + " 个版本");
            }
            catch (Exception ex)
            {
                AppendLog("刷新失败：" + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }

            // 关键：重扫本机安装，别让「装好了却显示未检测到」再出现
            await ReloadAll(false);
            foreach (AppDef a in Apps.All)
            {
                InstallInfo ins;
                _installs.TryGetValue(a.Id, out ins);
                AppendLog("  检测 " + a.Name + " " + a.Edition + "：" +
                    (ins != null && ins.Found ? ins.Version + " @ " + Paths.Display(ins.InstallDir) : "未检测到安装"));
            }
        }

        /* ---------------- 关于工具自身 ---------------- */

        private void OpenUrl(string url)
        {
            try
            {
                Process.Start(url);
                AppendLog("已打开：" + url);
            }
            catch (Exception ex)
            {
                AppendLog("打开链接失败：" + ex.Message);
                MessageBox.Show(this, "打不开浏览器，可以手动复制这个地址：\n\n" + url, "打开链接失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        /// <summary>点左上角 logo / 标题进 GitHub 仓库</summary>
        private void OpenRepo()
        {
            OpenUrl(AppInfo.RepoUrl);
        }

        /// <summary>检查工具自身有没有新版本（走 GitHub Releases API）</summary>
        private async Task CheckUpdate()
        {
            if (_busy) return;
            _checkBtn.Enabled = false;
            string old = _checkBtn.Text;
            _checkBtn.Text = "检查中…";
            AppendLog("检查工具更新（当前 v" + AppInfo.Version + "）…");

            try
            {
                string tag = "";
                string name = "";
                string url = AppInfo.ReleasesUrl;

                using (HttpClient http = new HttpClient(Net.NewHandler()))
                {
                    http.Timeout = TimeSpan.FromSeconds(20);
                    http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "wb-version-manager");
                    http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                    string json = await http.GetStringAsync(AppInfo.LatestApi);
                    JsonValue d = JsonValue.Parse(json);
                    tag = d.GetString("tag_name", "");
                    name = d.GetString("name", "");
                    url = d.GetString("html_url", AppInfo.ReleasesUrl);
                }

                string latest = tag == null ? "" : tag.TrimStart('v', 'V').Trim();
                if (string.IsNullOrEmpty(latest) && !string.IsNullOrEmpty(name))
                {
                    // tag 缺失时退回用 release 标题里的版本号（形如 "v1.3.2 · 标题"）
                    System.Text.RegularExpressions.Match m =
                        System.Text.RegularExpressions.Regex.Match(name, @"\d+\.\d+\.\d+");
                    if (m.Success) latest = m.Value;
                }
                if (string.IsNullOrEmpty(latest))
                {
                    AppendLog("  没能解析出线上版本号");
                    MessageBox.Show(this, "没能从发布页解析出版本号，可以手动打开下载页查看。\n\n" + AppInfo.ReleasesUrl,
                        "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                if (Fmt.CompareVersion(latest, AppInfo.Version) > 0)
                {
                    AppendLog("  发现新版本 v" + latest + "（当前 v" + AppInfo.Version + "）");
                    DialogResult r = MessageBox.Show(this,
                        "发现新版本 v" + latest + "（当前 v" + AppInfo.Version + "）\n\n" +
                        (string.IsNullOrEmpty(name) ? "" : name + "\n\n") +
                        "要打开下载页吗？\n" + url,
                        "有新版本", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                    if (r == DialogResult.Yes) OpenUrl(url);
                }
                else
                {
                    AppendLog("  已是最新版本");
                    MessageBox.Show(this, "当前已经是最新版本 v" + AppInfo.Version + "。", "检查更新",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                string detail = Catalog.Describe(ex);
                AppendLog("  检查失败：" + detail);
                DialogResult r = MessageBox.Show(this,
                    "检查更新失败：" + detail + "\n\n" +
                    "网络不通或被拦截时，可以手动打开下载页查看。\n" + AppInfo.ReleasesUrl + "\n\n要现在打开吗？",
                    "检查更新", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r == DialogResult.Yes) OpenUrl(AppInfo.ReleasesUrl);
            }
            finally
            {
                _checkBtn.Text = old;
                _checkBtn.Enabled = true;
            }
        }

        private async void LocateApp(string appId)        {
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
            if (_clearCacheBtn != null) RefreshCacheSummary();
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
