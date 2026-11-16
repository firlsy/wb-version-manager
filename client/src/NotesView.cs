using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace WbVm
{
    /// <summary>
    /// 自绘的富文本只读视图（标题、分组、条目、自动换行、滚动）。
    /// 不用 RichTextBox 是为了完全掌控绘制，避免原生控件在截图/离屏渲染下空白。
    /// </summary>
    public sealed class NotesView : Control
    {
        private sealed class Line
        {
            public string Text = "";
            public Font Font;
            public Color Color;
            public int Indent;
            public int Height;
        }

        private readonly List<Line> _lines = new List<Line>();
        private readonly VScrollBar _bar = new VScrollBar();
        private int _contentHeight;

        public NotesView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Palette.Card;

            _bar.Dock = DockStyle.Right;
            _bar.Width = 12;
            _bar.Visible = false;
            _bar.Scroll += delegate { Invalidate(); };
            Controls.Add(_bar);
        }

        public int CharCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _lines.Count; i++) n += _lines[i].Text.Length;
                return n;
            }
        }

        public string PlainText
        {
            get
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                for (int i = 0; i < _lines.Count; i++) sb.AppendLine(_lines[i].Text);
                return sb.ToString();
            }
        }

        public void Clear()
        {
            _lines.Clear();
            _contentHeight = 0;
            _bar.Value = 0;
            _bar.Visible = false;
            Invalidate();
        }

        public void Add(string text, Font font, Color color, int indent)
        {
            string raw = (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
            string[] parts = raw.Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                Line l = new Line();
                l.Text = parts[i];
                l.Font = font ?? UiFont.Base;
                l.Color = color;
                l.Indent = indent;
                _lines.Add(l);
            }
            Relayout();
        }

        private int ViewWidth
        {
            get { return Math.Max(60, ClientSize.Width - (_bar.Visible ? _bar.Width : 0)); }
        }

        private void Relayout()
        {
            int w = ViewWidth;
            int y = 0;
            for (int i = 0; i < _lines.Count; i++)
            {
                Line l = _lines[i];
                if (string.IsNullOrEmpty(l.Text))
                {
                    l.Height = 6;
                }
                else
                {
                    Size sz = TextRenderer.MeasureText(l.Text, l.Font,
                        new Size(Math.Max(20, w - 8 - l.Indent), int.MaxValue),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                    l.Height = Math.Max(16, sz.Height + 2);
                }
                y += l.Height;
            }
            _contentHeight = y;

            bool need = _contentHeight > ClientSize.Height;
            if (_bar.Visible != need)
            {
                _bar.Visible = need;
                // 可见性变化会改变可用宽度，再排一次
                Relayout();
                return;
            }

            _bar.LargeChange = Math.Max(1, ClientSize.Height);
            _bar.Maximum = Math.Max(0, _contentHeight - 1);
            int max = Math.Max(0, _contentHeight - ClientSize.Height);
            if (_bar.Value > max) _bar.Value = max;

            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            int y = -_bar.Value;
            int w = ViewWidth;
            for (int i = 0; i < _lines.Count; i++)
            {
                Line l = _lines[i];
                if (y + l.Height > 0 && y < ClientSize.Height && !string.IsNullOrEmpty(l.Text))
                {
                    Rectangle r = new Rectangle(4 + l.Indent, y, Math.Max(20, w - 8 - l.Indent), l.Height);
                    TextRenderer.DrawText(e.Graphics, l.Text, l.Font, r, l.Color,
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                }
                y += l.Height;
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (!Focused) Focus();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (_bar.Visible)
            {
                int max = Math.Max(0, _contentHeight - ClientSize.Height);
                int v = _bar.Value - (e.Delta / 120) * 48;
                if (v < 0) v = 0;
                if (v > max) v = max;
                _bar.Value = v;
                Invalidate();
            }
            base.OnMouseWheel(e);
        }
    }
}
