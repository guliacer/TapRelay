using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TapRelay
{
    /// <summary>
    /// 托盘提示卡片：显示 3 秒后在 300ms 内平滑淡出，点击立即关闭并执行打开详情回调。
    /// </summary>
    internal sealed class TrayToastForm : Form
    {
        private const int DisplayMilliseconds = 3000;
        private const int FadeMilliseconds = 300;
        private const int TickMilliseconds = 30;
        private const int MinWidth = 160;
        private const int MaxWidth = 440;
        private const int MaxHeight = 120;
        private const int HorizontalPadding = 16;
        private const int VerticalPadding = 9;
        private const int TitleMessageGap = 4;
        private const int CornerRadius = 12;

        private readonly Timer _timer = new Timer { Interval = TickMilliseconds };
        private readonly Action _onClick;
        private int _elapsed;
        private bool _fading;

        public TrayToastForm(string title, string message, Action onClick)
        {
            _onClick = onClick;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            ShowIcon = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;

            var titleFont = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold);
            var messageFont = new Font("Microsoft YaHei UI", 9F);
            var titleHeight = Math.Max(titleFont.Height, TextRenderer.MeasureText(title ?? string.Empty, titleFont).Height);
            Size = CalculateSize(title, message, titleFont, messageFont);
            BackColor = Color.FromArgb(255, 255, 255);
            ForeColor = Color.FromArgb(25, 48, 42);
            Padding = new Padding(HorizontalPadding, VerticalPadding, HorizontalPadding, VerticalPadding);
            Cursor = Cursors.Hand;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
                true);

            var titleLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = titleHeight + TitleMessageGap,
                Text = title,
                Font = titleFont,
                ForeColor = Color.FromArgb(25, 48, 42),
                BackColor = Color.Transparent,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Cursor = Cursors.Hand
            };
            var messageLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = message,
                Font = messageFont,
                ForeColor = Color.FromArgb(101, 118, 111),
                BackColor = Color.Transparent,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Cursor = Cursors.Hand
            };
            Controls.Add(messageLabel);
            Controls.Add(titleLabel);
            AttachClickHandler(this);

            _timer.Tick += OnTimerTick;
            Shown += (_, __) =>
            {
                Opacity = 1.0;
                _timer.Start();
            };
            Resize += (_, __) => UpdateRegion();
            FormClosed += (_, __) =>
            {
                _timer.Dispose();
                titleFont.Dispose();
                messageFont.Dispose();
            };
            UpdateRegion();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var brush = new SolidBrush(BackColor))
            using (var path = RoundedPath(new Rectangle(Point.Empty, new Size(Width - 1, Height - 1)), CornerRadius))
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.FillPath(brush, path);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Color.FromArgb(216, 228, 222), 1))
            using (var path = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius))
            {
                e.Graphics.DrawPath(pen, path);
            }
        }

        private void AttachClickHandler(Control control)
        {
            control.Click += HandleClick;
            foreach (Control? child in control.Controls)
            {
                if (child != null)
                {
                    AttachClickHandler(child);
                }
            }
        }

        private void HandleClick(object? sender, EventArgs e)
        {
            if (IsDisposed) return;
            _timer.Stop();
            var callback = _onClick;
            Close();
            callback();
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            _elapsed += TickMilliseconds;
            if (!_fading && _elapsed >= DisplayMilliseconds)
            {
                _fading = true;
                _elapsed = 0;
            }

            if (_fading)
            {
                var progress = Math.Min(1.0, _elapsed / (double)FadeMilliseconds);
                Opacity = 1.0 - progress;
                if (progress >= 1.0)
                {
                    _timer.Stop();
                    Close();
                }
            }
        }

        private static Size CalculateSize(string? title, string? message, Font titleFont, Font messageFont)
        {
            var titleWidth = TextRenderer.MeasureText(title ?? string.Empty, titleFont).Width;
            var messageWidth = TextRenderer.MeasureText(message ?? string.Empty, messageFont).Width;
            var titleHeight = Math.Max(titleFont.Height, TextRenderer.MeasureText(title ?? string.Empty, titleFont).Height);
            var messageHeight = Math.Max(messageFont.Height, TextRenderer.MeasureText(message ?? string.Empty, messageFont).Height);
            var width = Math.Min(
                MaxWidth,
                Math.Max(MinWidth, Math.Max(titleWidth, messageWidth) + HorizontalPadding * 2));
            var height = Math.Min(
                MaxHeight,
                VerticalPadding * 2 + titleHeight + TitleMessageGap + messageHeight);
            return new Size(width, height);
        }

        private void UpdateRegion()
        {
            if (Width <= 0 || Height <= 0)
            {
                return;
            }

            using (var path = RoundedPath(new Rectangle(0, 0, Width, Height), CornerRadius))
            {
                Region = new Region(path);
            }
        }

        private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
        {
            // 宽/高为 0 时 AddArc(0,0,...) 抛 ArgumentException，保底为 1。
            var diameter = Math.Max(1, Math.Min(radius * 2, Math.Min(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height))));
            var path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
