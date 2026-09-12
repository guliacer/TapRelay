using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TapRelay
{
    /// <summary>
    /// 自绘滚动条共享渲染器。桌面端保留克制的视觉，但不能把 WebKit 的 4px
    /// 内容滑块原样搬过来：它在 WinForms 卡片中既难看清，也难以稳定命中。
    /// 因此使用 12px 命中槽、低对比连续轨道与会随交互状态扩宽的胶囊滑块。
    /// </summary>
    internal static class ScrollBarRenderer
    {
        public enum Feedback
        {
            Normal,
            Hovered,
            Dragging,
        }

        /// <summary>
        /// 预留给滚动条的稳定宽度。视觉滑块会在槽内变化，布局无需因悬停而跳动。
        /// </summary>
        public const int PreferredWidth = 12;

        /// <summary>轨道上下的留白，使滑块到达两端时仍有明确边界。</summary>
        public const int TrackVerticalInset = 4;

        /// <summary>
        /// 滑块最小高度：对应素言 scrollbar-thumb min-height: 40px，
        /// 保证长短内容都可轻松命中拖拽。
        /// </summary>
        public const int MinThumbHeight = 40;

        // 轨道始终低调，滑块在 hover/drag 时由 6px 扩到 8px 并提高对比度。
        // 透明度以语义色为基准，因此明暗主题不用各维护一套硬编码色值。
        private static readonly FeedbackPalette NormalPalette = new FeedbackPalette(24, 100, 2, 6);
        private static readonly FeedbackPalette HoverPalette = new FeedbackPalette(42, 154, 4, 8);
        private static readonly FeedbackPalette DragPalette = new FeedbackPalette(54, 188, 4, 8);

        public static int TrackHeightFor(int controlHeight)
        {
            return Math.Max(1, controlHeight - TrackVerticalInset * 2);
        }

        public static void Render(
            Graphics g,
            int width,
            int height,
            int thumbTop,
            int thumbHeight,
            Color thumbColor,
            Color trackColor,
            Feedback feedback = Feedback.Normal)
        {
            if (width <= 0 || height <= 0 || thumbHeight <= 0)
            {
                return;
            }

            var state = g.Save();
            try
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.CompositingQuality = CompositingQuality.HighQuality;

                var palette = PaletteFor(feedback);
                if (trackColor.A > 0 && palette.TrackAlpha > 0)
                {
                    var track = CenteredBounds(
                        width,
                        TrackVerticalInset,
                        TrackHeightFor(height),
                        palette.TrackWidth);
                    FillRoundedRect(g, track, Adjust(trackColor, palette.TrackAlpha));
                }

                var thumb = CenteredBounds(width, thumbTop, thumbHeight, palette.ThumbWidth);
                FillRoundedRect(g, thumb, Adjust(thumbColor, palette.ThumbAlpha));
            }
            finally
            {
                g.Restore(state);
            }
        }

        private static Rectangle CenteredBounds(int controlWidth, int top, int height, int desiredWidth)
        {
            var width = Math.Max(1, Math.Min(Math.Max(1, controlWidth), desiredWidth));
            var left = (controlWidth - width) / 2;
            return new Rectangle(left, top, width, Math.Max(1, height));
        }

        private static FeedbackPalette PaletteFor(Feedback feedback)
        {
            switch (feedback)
            {
                case Feedback.Hovered:
                    return HoverPalette;
                case Feedback.Dragging:
                    return DragPalette;
                default:
                    return NormalPalette;
            }
        }

        private static void FillRoundedRect(Graphics g, Rectangle bounds, Color color)
        {
            using (var brush = new SolidBrush(color))
            using (var path = RoundedRectPath(bounds, bounds.Width / 2f))
            {
                g.FillPath(brush, path);
            }
        }

        private static Color Adjust(Color source, int alpha)
        {
            return Color.FromArgb(alpha, source);
        }

        private static GraphicsPath RoundedRectPath(Rectangle rect, float radius)
        {
            var path = new GraphicsPath();
            var d = Math.Max(1f, radius * 2f);
            // 全圆角胶囊，d 上限取短边，防止短滑块变成椭圆。
            var cap = Math.Min(d, Math.Max(1f, Math.Min(rect.Width, rect.Height)));
            path.AddArc(rect.X, rect.Y, cap, cap, 180, 90);
            path.AddArc(rect.Right - cap, rect.Y, cap, cap, 270, 90);
            path.AddArc(rect.Right - cap, rect.Bottom - cap, cap, cap, 0, 90);
            path.AddArc(rect.X, rect.Bottom - cap, cap, cap, 90, 90);
            path.CloseFigure();
            return path;
        }

        private sealed class FeedbackPalette
        {
            public readonly int TrackAlpha;
            public readonly int ThumbAlpha;
            public readonly int TrackWidth;
            public readonly int ThumbWidth;

            public FeedbackPalette(int trackAlpha, int thumbAlpha, int trackWidth, int thumbWidth)
            {
                TrackAlpha = trackAlpha;
                ThumbAlpha = thumbAlpha;
                TrackWidth = trackWidth;
                ThumbWidth = thumbWidth;
            }
        }
    }
}
