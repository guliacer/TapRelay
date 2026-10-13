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
    /// - 主按钮（Primary）用实心品牌绿 + 白字；次要（Secondary）用表面色 + 描边；
    ///   幽灵（Ghost）与所在表面融为一体，仅在悬停时显示反馈；
    ///   危险（Danger）用表面色 + 红描边 + 红字，仅悬停时铺淡红底。
    /// 纯图标按钮、自绘图标按钮（BookIconButton / ThemeIconButton / 复制图标钮）
    /// 属于特殊要求，不经过本入口。
    ///
    /// 视觉权重约定（2026-09-24 按钮方案）：
    /// 一个视图内应当只有一个实心按钮，且必须是建设性操作。危险操作改用描边，
    /// 避免"停止服务"这类破坏性按钮成为页面上唯一的高饱和实心色而抢走主操作。
    /// </summary>
    public static class UiButtonStyler
    {
        /// <summary>主窗口按钮统一高度（地址行 / 发送测试等主操作）。</summary>
        public const int MainWindowButtonHeight = UiTokens.MainButtonHeight;

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
        ///
        /// 主按钮（Primary）：实心品牌绿 + 白字。
        /// 旧实现沿用顶栏淡 sage 填充（#D8E4DE）+ 深 sage 字，本意是与手机端
        /// TopBarSage 同源，但该填充与白色卡片底的对比度只有 1.31:1，主按钮在卡片上
        /// 几乎不可见；同时危险按钮是整页唯一高饱和实心色，导致"停止服务比启动服务
        /// 更抢眼"的视觉权重倒挂。现改为实心绿：
        ///   浅色 #2F6E5C — 白字 5.99:1、对白卡片 5.99:1
        ///   深色 #357F69 — 白字 4.78:1、对深色卡片 3.35:1
        /// 两者均达 WCAG AA 小字门槛 4.5:1。
        ///
        /// 危险按钮（Danger）：表面色填充 + 红描边 + 红字，仅悬停/按下时铺淡红底。
        ///   浅色 字 #B03A34（对白 6.00:1）、描边 #DC9E99（对白 2.24:1）
        ///   深色 字 #EB7169（对深色面 5.42:1）、描边 #8A4A44（对深色面 2.40:1）
        /// 描边比填充更能表达"这是破坏性操作，但不该被第一眼看到"。
        /// </summary>
        public static void Apply(Button button, UiButtonStyle style, UiButtonColors c)
        {
            var isPrimary = style == UiButtonStyle.Primary;
            var isDanger = style == UiButtonStyle.Danger;
            var isGhost = style == UiButtonStyle.Ghost;
            // 根据传入 Surface 明暗判断深浅色主题。
            var isDarkSurface = c.Surface.GetBrightness() < 0.5f;

            // 主按钮：实心品牌绿 + 白字。
            var primaryFill = isDarkSurface ? Color.FromArgb(53, 127, 105) : Color.FromArgb(47, 110, 92);
            var primaryFillHover = isDarkSurface ? Color.FromArgb(62, 143, 118) : Color.FromArgb(38, 92, 77);
            var primaryFillDown = isDarkSurface ? Color.FromArgb(45, 108, 89) : Color.FromArgb(30, 74, 62);

            // 危险按钮：描边 + 红字，仅悬停/按下铺淡红底。
            var dangerText = isDarkSurface ? Color.FromArgb(235, 113, 105) : Color.FromArgb(176, 58, 52);
            var dangerBorder = isDarkSurface ? Color.FromArgb(138, 74, 68) : Color.FromArgb(220, 158, 153);
            var dangerHover = isDarkSurface ? Color.FromArgb(58, 34, 32) : Color.FromArgb(253, 236, 235);
            var dangerDown = isDarkSurface ? Color.FromArgb(74, 42, 40) : Color.FromArgb(249, 218, 216);

            Color fill;
            Color foreground;
            if (isPrimary)
            {
                fill = primaryFill;
                foreground = Color.White;
            }
            else if (isDanger)
            {
                fill = c.Surface;
                foreground = dangerText;
            }
            else
            {
                fill = c.Surface;
                foreground = c.TextPrimary;
            }

            // 主按钮实心、幽灵与所在表面同色，都不需要描边；只有次要与危险需要。
            var borderSize = isPrimary || isGhost ? 0 : 1;
            var borderColor = isDanger ? dangerBorder : c.Border;

            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = fill;
            button.ForeColor = foreground;
            button.Font = new Font(UiTokens.UiFontFamily, UiTokens.BodyFontSize, FontStyle.Regular);
            button.FlatAppearance.BorderSize = borderSize;
            button.FlatAppearance.BorderColor = borderColor;
            button.FlatAppearance.MouseOverBackColor = isPrimary
                ? primaryFillHover
                : isDanger
                    ? dangerHover
                    : c.SurfaceAlt;
            button.FlatAppearance.MouseDownBackColor = isPrimary
                ? primaryFillDown
                : isDanger
                    ? dangerDown
                    : ControlPaint.Dark(c.SurfaceAlt, 0.04f);
            button.Cursor = Cursors.Hand;

            ApplyRoundedRegion(button, UiTokens.ButtonRadius);
            if (!(button.Tag is string tag && tag == "rounded"))
            {
                button.Tag = "rounded";
                button.Resize += (_, __) => ApplyRoundedRegion(button, UiTokens.ButtonRadius);
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
