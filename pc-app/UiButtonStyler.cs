using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TapRelay
{
    /// <summary>
    /// 全应用统一的按钮样式入口。所有普通文字按钮（主操作 / 次要 / 幽灵 / 危险）
    /// 都必须经过本入口，保证风格、文本与颜色全局一致：
    /// - 文本统一 Microsoft YaHei UI 9.5pt Regular；
    /// - 统一 Flat 样式、圆角 10、悬停/按下反馈与手型光标；
    /// - 主按钮（Primary）用强调色填充 + 白字；次要（Secondary）用表面色 + 描边；
    ///   幽灵（Ghost）与所在表面融为一体，仅在悬停时显示反馈；危险（Danger）
    ///   用危险色填充 + 白字。
    /// 纯图标按钮、自绘图标按钮（BookIconButton / ThemeIconButton / 复制图标钮）
    /// 属于特殊要求，不经过本入口。
    /// </summary>
    public static class UiButtonStyler
    {
        /// <summary>主窗口按钮统一高度（地址行 / 发送测试等主操作）。</summary>
        public const int MainWindowButtonHeight = 42;

        /// <summary>对话框 / 弹窗按钮统一高度。</summary>
        public const int DialogButtonHeight = 32;

        /// <summary>统一按钮语义，与 APP 端 PlainButtonVariant 对应。</summary>
        public enum UiButtonStyle
        {
            Primary,
            Secondary,
            Ghost,
            Danger
        }

        /// <summary>统一按钮配色。各窗体从自己的 Palette 映射而来，保证跨窗体颜色一致。</summary>
        public struct UiButtonColors
        {
            public Color Accent;
            public Color AccentHover;
            public Color Danger;
            public Color Surface;
            public Color Border;
            public Color TextPrimary;
            public Color SurfaceAlt;
        }

        /// <summary>创建并应用统一样式的按钮。</summary>
        public static Button Create(string text, UiButtonStyle style, UiButtonColors colors)
        {
            var button = new Button { Text = text };
            Apply(button, style, colors);
            return button;
        }

        /// <summary>
        /// 应用统一按钮样式。可重复调用（如主题切换时），幂等。
        /// 主按钮（Primary）浅色主题用顶栏同款淡鼠尾草绿填充 + 深 sage 字（与手机端
        /// TopBarSage / TopBarSageContent 同源统一）；深色主题保持深绿 + 白字。
        /// </summary>
        public static void Apply(Button button, UiButtonStyle style, UiButtonColors c)
        {
            var isPrimary = style == UiButtonStyle.Primary;
            var isDanger = style == UiButtonStyle.Danger;
            var isGhost = style == UiButtonStyle.Ghost;
            // 根据传入 Surface 明暗判断深浅色主题：浅色 Surface(白) → 淡 sage 按钮。
            var isDarkSurface = c.Surface.GetBrightness() < 0.5f;
            var sageFill = isDarkSurface ? c.Accent : Color.FromArgb(216, 228, 222);   // #D8E4DE 顶栏淡 sage
            var sageFillHover = isDarkSurface ? c.AccentHover : Color.FromArgb(201, 216, 208); // hover 稍深
            var sageContent = isDarkSurface ? Color.White : Color.FromArgb(62, 82, 73); // #3E5249 深 sage 字

            var fill = isPrimary ? sageFill : isDanger ? c.Danger : c.Surface;
            var foreground = isPrimary ? sageContent : isDanger ? Color.White : c.TextPrimary;
            var border = isPrimary ? sageFill : isDanger ? c.Danger : c.Border;

            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = fill;
            button.ForeColor = foreground;
            button.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Regular);
            button.FlatAppearance.BorderSize = isPrimary || isDanger || isGhost ? 0 : 1;
            button.FlatAppearance.BorderColor = border;
            button.FlatAppearance.MouseOverBackColor = isPrimary
                ? sageFillHover
                : isDanger
                    ? ControlPaint.Light(c.Danger, 0.08f)
                    : c.SurfaceAlt;
            button.FlatAppearance.MouseDownBackColor = isPrimary
                ? ControlPaint.Dark(sageFill, 0.08f)
                : isDanger
                    ? ControlPaint.Dark(c.Danger, 0.08f)
                    : ControlPaint.Dark(c.SurfaceAlt, 0.04f);
            button.Cursor = Cursors.Hand;

            ApplyRoundedRegion(button, 10);
            if (!(button.Tag is string tag && tag == "rounded"))
            {
                button.Tag = "rounded";
                button.Resize += (_, __) => ApplyRoundedRegion(button, 10);
            }
        }

        /// <summary>
        /// 统一圆角区域。控件宽/高为 0 时 d 会变成 0，AddArc(0,0,...) 抛 ArgumentException，
        /// 与 LogScrollBar 崩溃同源，统一用 Math.Max(1, ...) 保底。
        /// </summary>
        public static void ApplyRoundedRegion(Control control, int radius)
        {
            using (var path = new GraphicsPath())
            {
                var rect = new Rectangle(0, 0, Math.Max(1, control.Width), Math.Max(1, control.Height));
                var d = Math.Max(1, Math.Min(radius * 2, Math.Min(rect.Width, rect.Height)));
                path.AddArc(rect.X, rect.Y, d, d, 180, 90);
                path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
                path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
                path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                control.Region = new Region(path);
            }
        }
    }
}
