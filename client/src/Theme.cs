using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace WbVm
{
    public static class Palette
    {
        public static readonly Color Bg = ColorTranslator.FromHtml("#f3f5f9");
        public static readonly Color BgSoft = ColorTranslator.FromHtml("#f8fafc");
        public static readonly Color Card = Color.White;
        public static readonly Color Border = ColorTranslator.FromHtml("#e5e9f0");
        public static readonly Color BorderStrong = ColorTranslator.FromHtml("#d8dee9");
        public static readonly Color Text = ColorTranslator.FromHtml("#101828");
        public static readonly Color Text2 = ColorTranslator.FromHtml("#475467");
        public static readonly Color Muted = ColorTranslator.FromHtml("#8a94a6");
        public static readonly Color Accent = ColorTranslator.FromHtml("#2f6bff");
        public static readonly Color AccentSoft = ColorTranslator.FromHtml("#eef3ff");
        public static readonly Color Accent2 = ColorTranslator.FromHtml("#7c4dff");
        public static readonly Color Green = ColorTranslator.FromHtml("#0f9d58");
        public static readonly Color GreenSoft = ColorTranslator.FromHtml("#e9f8f0");
        public static readonly Color Amber = ColorTranslator.FromHtml("#b7791f");
        public static readonly Color AmberSoft = ColorTranslator.FromHtml("#fdf5e6");
        public static readonly Color Red = ColorTranslator.FromHtml("#d92d20");
        public static readonly Color RedSoft = ColorTranslator.FromHtml("#fef3f2");
    }

    public static class UiFont
    {
        public const string Family = "Microsoft YaHei UI";

        public static Font Make(float size, FontStyle style)
        {
            try { return new Font(Family, size, style); }
            catch { return new Font(FontFamily.GenericSansSerif, size, style); }
        }

        public static readonly Font Base = Make(9f, FontStyle.Regular);
        public static readonly Font Small = Make(8.5f, FontStyle.Regular);
        public static readonly Font Bold = Make(9f, FontStyle.Bold);
        public static readonly Font H1 = Make(15f, FontStyle.Bold);
        public static readonly Font H2 = Make(11.5f, FontStyle.Bold);
        public static readonly Font H3 = Make(10f, FontStyle.Bold);
        public static readonly Font Mono = new Font("Consolas", 8.5f, FontStyle.Regular);
    }

    public static class Draw
    {
        /// <summary>取父容器的实际底色（卡片内部是白底，不是窗体底色）</summary>
        public static Color EffectiveBack(Control c)
        {
            Control p = c == null ? null : c.Parent;
            if (p == null) return Palette.Bg;
            CardPanel card = p as CardPanel;
            if (card != null) return card.FillColor;
            return p.BackColor;
        }

        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = Math.Max(1, radius * 2);
            GraphicsPath p = new GraphicsPath();
            if (radius <= 0)
            {
                p.AddRectangle(r);
                return p;
            }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Rectangle r, int radius, Color fill)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = RoundRect(r, radius))
            using (SolidBrush b = new SolidBrush(fill))
                g.FillPath(b, p);
        }

        public static void StrokeRound(Graphics g, Rectangle r, int radius, Color color)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = RoundRect(r, radius))
            using (Pen pen = new Pen(color, 1f))
                g.DrawPath(pen, p);
        }

        public static void Card(Graphics g, Rectangle bounds, Color fill, Color border, int radius)
        {
            Rectangle r = new Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
            FillRound(g, r, radius, fill);
            StrokeRound(g, r, radius, border);
        }

        public static void Text(Graphics g, string text, Font font, Color color, Rectangle rect, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, text, font, rect, color, flags);
        }
    }

    /// <summary>圆角卡片容器</summary>
    public class CardPanel : Panel
    {
        public int Radius = 14;
        public Color FillColor = Palette.Card;
        public Color BorderColor = Palette.Border;

        public CardPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Palette.Bg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Draw.EffectiveBack(this));
            Draw.Card(e.Graphics, ClientRectangle, FillColor, BorderColor, Radius);
            base.OnPaint(e);
        }
    }

    /// <summary>扁平圆角按钮</summary>
    public class FlatButton : Control
    {
        public int Radius = 9;
        public Color FillColor = Color.White;
        public Color HoverColor = Palette.AccentSoft;
        public Color PressColor = ColorTranslator.FromHtml("#dfe8ff");
        public Color BorderColor = Palette.BorderStrong;
        public Color TextColor = Palette.Text;
        public bool Primary;
        public bool Danger;

        private bool _hover;
        private bool _down;

        public FlatButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.StandardClick | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Font = UiFont.Base;
            Cursor = Cursors.Hand;
            Height = 34;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Draw.EffectiveBack(this));
            Color fill;
            Color border;
            Color fore;

            if (!Enabled)
            {
                fill = Palette.BgSoft;
                border = Palette.Border;
                fore = Palette.Muted;
            }
            else if (Primary)
            {
                fill = _down ? ColorTranslator.FromHtml("#2455d6") : (_hover ? ColorTranslator.FromHtml("#3b74ff") : Palette.Accent);
                border = fill;
                fore = Color.White;
            }
            else if (Danger)
            {
                fill = _down ? Palette.RedSoft : (_hover ? ColorTranslator.FromHtml("#fdeceb") : Color.White);
                border = _hover ? Palette.Red : Palette.BorderStrong;
                fore = Palette.Red;
            }
            else
            {
                fill = _down ? PressColor : (_hover ? HoverColor : FillColor);
                border = _hover ? Palette.Accent : BorderColor;
                fore = TextColor;
            }

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Draw.FillRound(e.Graphics, r, Radius, fill);
            Draw.StrokeRound(e.Graphics, r, Radius, border);

            TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
            Draw.Text(e.Graphics, Text, Font, fore, r, flags);
        }
    }

    /// <summary>圆角状态胶囊</summary>
    public class Badge : Control
    {
        public Color FillColor = Palette.AccentSoft;
        public Color TextColor = Palette.Accent;
        public Color DotColor = Color.Empty;
        public int Radius = 8;

        public Badge()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Font = UiFont.Small;
            Height = 22;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Draw.EffectiveBack(this));
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Draw.FillRound(e.Graphics, r, Radius, FillColor);

            int left = 9;
            if (DotColor != Color.Empty)
            {
                using (SolidBrush b = new SolidBrush(DotColor))
                    e.Graphics.FillEllipse(b, left, Height / 2 - 3, 6, 6);
                left += 11;
            }
            Rectangle textRect = new Rectangle(left, 0, Math.Max(0, Width - left - 8), Height);
            Draw.Text(e.Graphics, Text, Font, TextColor, textRect,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>细进度条</summary>
    public class MiniProgress : Control
    {
        private int _value;
        public int Value
        {
            get { return _value; }
            set
            {
                int v = value;
                if (v < 0) v = 0;
                if (v > 100) v = 100;
                if (v != _value) { _value = v; Invalidate(); }
            }
        }

        public Color BarColor = Palette.Accent;

        public MiniProgress()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Height = 6;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Draw.EffectiveBack(this));
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Draw.FillRound(e.Graphics, r, Height / 2, ColorTranslator.FromHtml("#edf1f7"));
            if (_value > 0)
            {
                int w = (int)Math.Round(r.Width * _value / 100.0);
                if (w < 4) w = 4;
                Rectangle fill = new Rectangle(0, 0, w, r.Height);
                Draw.FillRound(e.Graphics, fill, Height / 2, BarColor);
            }
        }
    }

    /// <summary>左侧客户端卡片</summary>
    public class AppCard : CardPanel
    {
        public AppDef App;
        public InstallInfo Install;
        public BlockStatus Block;
        public string LockedVersion = "";

        public event EventHandler Selected;
        public event EventHandler LockRequested;

        public event EventHandler LocateRequested;

        private bool _active;
        public bool Active
        {
            get { return _active; }
            set { _active = value; BorderColor = value ? Palette.Accent : Palette.Border; Invalidate(); }
        }

        private Label _name;
        private Label _version;
        private Label _path;
        private Badge _badge;
        private FlatButton _lockBtn;
        private FlatButton _locateBtn;

        public AppCard(AppDef app)
        {
            App = app;
            Radius = 14;
            Height = 158;
            Padding = new Padding(16, 14, 16, 14);
            Cursor = Cursors.Hand;

            _name = new Label();
            _name.Text = app.Name;
            _name.Font = UiFont.H2;
            _name.ForeColor = Palette.Text;
            _name.AutoSize = true;
            _name.BackColor = Color.Transparent;
            _name.Location = new Point(16, 14);
            Controls.Add(_name);

            Label edition = new Label();
            edition.Text = app.Edition;
            edition.Font = UiFont.Small;
            edition.ForeColor = Palette.Muted;
            edition.AutoSize = true;
            edition.BackColor = Color.Transparent;
            edition.Location = new Point(_name.Right + 8, 20);
            Controls.Add(edition);

            _badge = new Badge();
            _badge.Location = new Point(16, 46);
            _badge.Size = new Size(120, 22);
            Controls.Add(_badge);

            _version = new Label();
            _version.Font = UiFont.Bold;
            _version.ForeColor = Palette.Text2;
            _version.AutoSize = true;
            _version.BackColor = Color.Transparent;
            _version.Location = new Point(16, 78);
            Controls.Add(_version);

            _path = new Label();
            _path.Font = UiFont.Small;
            _path.ForeColor = Palette.Muted;
            _path.AutoSize = false;
            _path.BackColor = Color.Transparent;
            _path.Location = new Point(16, 102);
            _path.Size = new Size(280, 18);
            _path.AutoEllipsis = true;
            Controls.Add(_path);

            _lockBtn = new FlatButton();
            _lockBtn.Text = "锁定";
            _lockBtn.Size = new Size(74, 30);
            _lockBtn.Location = new Point(16, 122);
            _lockBtn.Primary = true;
            _lockBtn.Click += delegate { if (LockRequested != null) LockRequested(this, EventArgs.Empty); };
            Controls.Add(_lockBtn);

            _locateBtn = new FlatButton();
            _locateBtn.Text = "指定目录";
            _locateBtn.Size = new Size(84, 30);
            _locateBtn.Location = new Point(96, 122);
            _locateBtn.Click += delegate { if (LocateRequested != null) LocateRequested(this, EventArgs.Empty); };
            Controls.Add(_locateBtn);

            HookClick(this);
        }

        private void HookClick(Control c)
        {
            c.Click += delegate { if (Selected != null) Selected(this, EventArgs.Empty); };
            foreach (Control child in c.Controls) HookClick(child);
        }

        public void Refresh1()
        {
            bool found = Install != null && Install.Found;
            string version = found ? Install.Version : "";

            if (!found)
            {
                _version.Text = "未检测到安装";
                _version.ForeColor = Palette.Muted;
                _path.Text = "可点「指定目录」手动指向安装位置";
                _badge.Text = "未安装";
                _badge.FillColor = Palette.BgSoft;
                _badge.TextColor = Palette.Muted;
                _badge.DotColor = Palette.Muted;
                _lockBtn.Text = "锁定";
                _lockBtn.Enabled = false;
                _locateBtn.Visible = true;
                return;
            }

            _version.Text = "当前版本 " + version;
            _version.ForeColor = Palette.Text;
            _path.Text = Paths.Display(Install.InstallDir);

            bool blocked = Block != null && Block.Enabled;
            if (blocked)
            {
                _badge.Text = string.IsNullOrEmpty(LockedVersion) ? "已锁定" : "已锁定 " + LockedVersion;
                _badge.FillColor = Palette.GreenSoft;
                _badge.TextColor = Palette.Green;
                _badge.DotColor = Palette.Green;
                _lockBtn.Text = "解除锁定";
                _lockBtn.Primary = false;
                _lockBtn.Danger = true;
                _lockBtn.Enabled = true;
            }
            else
            {
                _badge.Text = "未锁定";
                _badge.FillColor = Palette.AmberSoft;
                _badge.TextColor = Palette.Amber;
                _badge.DotColor = Palette.Amber;
                _lockBtn.Text = "锁定";
                _lockBtn.Primary = true;
                _lockBtn.Danger = false;
                _lockBtn.Enabled = true;
            }
            _locateBtn.Visible = true;
        }
    }
}
