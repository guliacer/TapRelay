using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TapRelay
{
    /// <summary>
    /// "个人项目"书本图标按钮（自绘，与 ThemeIconButton 同风格）：
    /// 绘制一本翻开的书，点击打开内嵌网页的个人项目面板。
    /// </summary>
    internal sealed class BookIconButton : Control
    {
        private Color _backgroundColor = Color.FromArgb(248, 250, 252);
        private Color _iconColor = Color.FromArgb(111, 134, 126);

        public BookIconButton()
        {
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
                true);
            BackColor = _backgroundColor;
            ForeColor = _iconColor;
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = "个人项目";
            TabStop = false;
        }

        public void ApplyColors(Color backgroundColor, Color iconColor)
        {
            _backgroundColor = backgroundColor;
            _iconColor = iconColor;
            BackColor = backgroundColor;
            ForeColor = iconColor;
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 与 ThemeIconButton 一致：先清背景再自绘，避免系统按钮底色。
            e.Graphics.Clear(BackColor);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(_backgroundColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;

            var viewportSize = Math.Min(Width, Height) * (20f / 28f);
            var viewport = new RectangleF(
                (Width - viewportSize) / 2f,
                (Height - viewportSize) / 2f,
                viewportSize,
                viewportSize);

            PointF P(float x, float y) => new PointF(
                viewport.Left + x * viewport.Width / 24f,
                viewport.Top + y * viewport.Height / 24f);

            using (var pen = new Pen(_iconColor, 1.5f * viewportSize / 24f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;

                using (var path = new GraphicsPath())
                {
                    // 翻开的书：左页脊线 → 上沿 → 右页脊线 → 下沿 → 合拢。
                    path.StartFigure();
                    path.AddLine(P(6f, 4f), P(12f, 7f));
                    path.AddLine(P(12f, 7f), P(18f, 4f));
                    path.AddLine(P(18f, 4f), P(18f, 20f));
                    path.AddLine(P(18f, 20f), P(12f, 17f));
                    path.AddLine(P(12f, 17f), P(6f, 20f));
                    path.CloseFigure();
                    g.DrawPath(pen, path);

                    // 书脊：从书页中部向下延伸一条短线，示意翻页结构。
                    g.DrawLine(pen, P(12f, 7f), P(12f, 17f));
                }
            }
        }
    }
}
