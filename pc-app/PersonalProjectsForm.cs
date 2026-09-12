using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TapRelay
{
    /// <summary>
    /// 个人项目面板：严格参照素言（W:\提示词）PromptCard 的视觉规范——
    /// 圆角 12px 卡片 + 1px 描边 + 顶部主题色带 header + 描述 + 胶囊标签 +
    /// 底栏（分隔线 + 统计/操作）。8 个胶囊色轮换（sage/mist/clay/lavender/
    /// fog/rose/sand/stone），深/浅色各一套，与手机端 PersonalProjectsDialog
    /// 的 ProjectCardTone 色值保持一致。
    /// </summary>
    internal sealed class PersonalProjectsForm : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;
        private const int DWMWA_COLOR_DEFAULT = unchecked((int)0xFFFFFFFF);

        private readonly bool _darkMode;
        private readonly TableLayoutPanel _root = new TableLayoutPanel();
        private readonly Panel _scrollBody = new Panel();

        // 与手机端 PersonalProjectsDialog 保持一致（10 个真实项目）。
        private static readonly PersonalProject[] Projects =
        {
            new PersonalProject("SuYan", "github.com", "https://github.com/guliacer/SuYan", "为 AI 创作者整理、复用和打磨提示词而生。", "个人项目", "提示词"),
            new PersonalProject("ComfyUI-GuliNodes", "github.com", "https://github.com/guliacer/ComfyUI-GuliNodes", "ComfyUI 工具合集，囊括多种有用节点与工具。", "个人项目", "ComfyUI", "节点扩展"),
            new PersonalProject("Prompt Polish 提示词优化器", "github.com", "https://github.com/guliacer/prompt-polish", "轻量级桌面提示词优化器，完整整合提示词润色功能。", "个人项目", "提示词优化", "桌面工具"),
            new PersonalProject("GetPhoto 图像提取", "github.com", "https://github.com/guliacer/GetPhoto", "油猴插件，适配多站点，支持小红书 AI 爬图。", "个人项目", "油猴插件", "图像提取"),
            new PersonalProject("PagePurifier 网页优化", "github.com", "https://github.com/guliacer/PagePurifier", "油猴插件，拉黑 B 站广告 UP，精简网页广告。", "个人项目", "油猴插件", "网页净化"),
            new PersonalProject("Preview 网页图像悬浮工具", "github.com", "https://github.com/guliacer/preview", "油猴插件，鼠标悬停即可查看图像大图。", "个人项目", "油猴插件", "图像预览"),
            new PersonalProject("VeilReader 网页摸鱼小说工具", "github.com", "https://github.com/guliacer/VeilReader", "油猴插件，摸鱼的钱才是你赚到的钱。", "个人项目", "油猴插件", "摸鱼阅读"),
            new PersonalProject("CookClick 网页美化", "github.com", "https://github.com/guliacer/cookclick", "油猴插件，点击网页随机生成符号。", "个人项目", "油猴插件", "网页美化"),
            new PersonalProject("PixGo", "github.com", "https://github.com/guliacer/PixGo", "改善索尼相机无线传图。", "个人项目", "软件", "无线传图"),
            new PersonalProject("新闻 HTML 日报", "github.com", "https://github.com/guliacer/news-html-digest", "Skill 脚本，抓取新闻，消除信息差。", "个人项目", "Skill", "新闻抓取"),
        };

        public PersonalProjectsForm(bool darkMode)
        {
            _darkMode = darkMode;

            Text = "个人项目";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ShowInTaskbar = false;
            MinimumSize = new Size(620, 480);
            ClientSize = new Size(880, 680);
            Font = new Font("Microsoft YaHei UI", 9.5F);
            BackColor = _darkMode
                ? Color.FromArgb(22, 25, 22)
                : Color.FromArgb(246, 245, 241);

            BuildLayout();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (Handle != IntPtr.Zero)
            {
                ApplyTitleBarTheme(Handle, _darkMode);
            }
        }

        private void BuildLayout()
        {
            var colors = Palette.Create(_darkMode);

            _root.Dock = DockStyle.Fill;
            _root.ColumnCount = 1;
            _root.RowCount = 2;
            _root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52F));
            _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            _root.BackColor = colors.Background;
            _root.Padding = new Padding(20, 14, 20, 14);

            var header = new Label
            {
                Text = "个人项目",
                AutoSize = false,
                Dock = DockStyle.Fill,
                Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold),
                ForeColor = colors.TextPrimary,
                BackColor = colors.Background,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _root.Controls.Add(header, 0, 0);

            _scrollBody.Dock = DockStyle.Fill;
            _scrollBody.AutoScroll = true;
            _scrollBody.BackColor = colors.Background;

            // 单列卡片：用 TableLayoutPanel（支持 Margin 间距）替代 Dock=Top 直接堆叠——
            // Dock=Top 布局会忽略 Margin，导致卡片之间没有间距、贴在一起。
            // TableLayoutPanel 的坑：RowCount 初始为 0，直接 Add(col,row) 传绝对行号即可，
            // 面板会自动扩展行数；不要用 RowCount-1 之类先减后加的写法。
            var cardList = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                BackColor = colors.Background,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            cardList.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            for (var i = 0; i < Projects.Length; i++)
            {
                var card = BuildCard(Projects[i], i);
                card.Dock = DockStyle.Fill;
                card.Margin = new Padding(0, 0, 0, 18); // 卡片间距（TableLayout 中生效）
                cardList.Controls.Add(card, 0, i);
            }
            _scrollBody.Controls.Add(cardList);

            _root.Controls.Add(_scrollBody, 0, 1);
            Controls.Add(_root);
        }

        private Control BuildCard(PersonalProject project, int index)
        {
            var colors = Palette.Create(_darkMode);
            var tone = colors.Tones[index % colors.Tones.Length];
            // 统一高度：2/3 标签都留足空间，避免底栏与标签重叠。
            const int cardHeight = 152;

            var card = new Panel
            {
                Height = cardHeight,
                Margin = new Padding(0, 0, 0, 12),
                Cursor = Cursors.Hand,
                BackColor = colors.Panel
            };
            // 悬停反馈：阴影加深（对齐素言 hover:-translate-y-1 的层级感）。
            // 注意不能移动 card.Top——在 Dock/TableLayout 布局中会被打回且破坏定位。
            var hovered = false;
            card.MouseEnter += (_, __) => { hovered = true; card.Invalidate(); };
            card.MouseLeave += (_, __) => { hovered = false; card.Invalidate(); };

            // 圆角描边 + 阴影 + 头部色带 + 内容 + 标签 + 底栏，整体自绘。
            card.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                var rect = new Rectangle(1, 1, card.Width - 3, card.Height - 3);

                // 卡片底（panel）+ 1px 描边（tone 色），对齐 rounded-xl + border。
                using (var path = RoundedRect(rect, 12))
                using (var shadowPath = RoundedRect(new Rectangle(3, 4, card.Width - 6, card.Height - 5), 12))
                using (var bg = new SolidBrush(colors.Panel))
                using (var borderPen = new Pen(hovered ? tone.Foreground : tone.Border, 1f))
                using (var shadowBrush = new SolidBrush(hovered ? colors.ShadowHover : colors.Shadow))
                {
                    g.FillPath(shadowBrush, shadowPath); // 底部投影（shadow-sm）
                    g.FillPath(bg, path);
                    g.DrawPath(borderPen, path);
                }

                // ---- Header：主题色带（solid）----
                var headerRect = new Rectangle(rect.X + 1, rect.Y + 1, rect.Width - 2, 44);
                using (var headerPath = RoundedRectTop(headerRect, 12))
                using (var headerBrush = new SolidBrush(tone.Header))
                {
                    g.FillPath(headerBrush, headerPath);
                }
                // header 底部描边（border-b）
                using (var linePen = new Pen(tone.Border, 1f))
                {
                    g.DrawLine(linePen, rect.X + 12, headerRect.Bottom, rect.Right - 12, headerRect.Bottom);
                }
                // 左侧 4px accent 条（对齐素言卡片头左侧色条）
                using (var accentBrush = new SolidBrush(tone.Border))
                {
                    g.FillRectangle(accentBrush, rect.X + 14, headerRect.Y + 11, 4, 22);
                }

                // 标题
                using (var titleBrush = new SolidBrush(tone.Foreground))
                {
                    g.DrawString(project.Title, new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
                        titleBrush, new RectangleF(rect.X + 26, headerRect.Y + 11, rect.Width - 120, 20));
                }
                // GitHub 图标（右侧，简单绘制）
                using (var iconPen = new Pen(tone.ForegroundDim, 1.4f))
                {
                    var ix = rect.Right - 34;
                    var iy = headerRect.Y + 13;
                    g.DrawEllipse(iconPen, ix, iy, 18, 18);       // 猫头
                    g.DrawArc(iconPen, ix - 3, iy + 12, 10, 10, 180, 180);
                    g.DrawArc(iconPen, ix + 11, iy + 12, 10, 10, 0, 180);
                }

                // ---- 描述（内容区）----
                using (var descBrush = new SolidBrush(colors.Muted))
                {
                    g.DrawString(project.Description, new Font("Microsoft YaHei UI", 9F),
                        descBrush, new RectangleF(rect.X + 18, headerRect.Bottom + 12, rect.Width - 36, 34));
                }

                // ---- 胶囊标签 ----
                var tagFont = new Font("Microsoft YaHei UI", 8F);
                float tx = rect.X + 18;
                var ty = headerRect.Bottom + 44f;
                foreach (var tag in project.Tags)
                {
                    var tagSize = g.MeasureString(tag, tagFont);
                    var tagRect = new RectangleF(tx, ty, tagSize.Width + 16, 20);
                    using (var tagPath = RoundedRect(tagRect, 10))
                    using (var tagBrush = new SolidBrush(tone.Tag))
                    using (var tagBorder = new Pen(tone.Border, 1f))
                    using (var tagText = new SolidBrush(tone.Foreground))
                    {
                        g.FillPath(tagBrush, tagPath);
                        g.DrawPath(tagBorder, tagPath);
                        g.DrawString(tag, tagFont, tagText, new PointF(tx + 8, ty + 3));
                    }
                    tx += tagRect.Width + 8;
                }

                // ---- 底栏（border-t + 复制按钮）----
                var footerY = rect.Bottom - 34;
                using (var linePen = new Pen(tone.Border, 1f))
                {
                    g.DrawLine(linePen, rect.X + 12, footerY, rect.Right - 12, footerY);
                }
                // 复制按钮（底栏右侧，hover 态由外层按钮处理）
                using (var copyBrush = new SolidBrush(tone.Header))
                using (var copyBorder = new Pen(tone.Border, 1f))
                using (var copyText = new SolidBrush(tone.Foreground))
                {
                    var copyRect = new RectangleF(rect.Right - 96, footerY + 4, 76, 24);
                    using (var copyPath = RoundedRect(copyRect, 6))
                    {
                        g.FillPath(copyBrush, copyPath);
                        g.DrawPath(copyBorder, copyPath);
                        g.DrawString("复制地址", new Font("Microsoft YaHei UI", 8F), copyText,
                            new PointF(copyRect.X + 16, copyRect.Y + 4));
                    }
                }
            };

            card.Click += (s, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(project.Url) { UseShellExecute = true });
                }
                catch (Exception)
                {
                }
            };

            return card;
        }

        private static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static GraphicsPath RoundedRectTop(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddLine(rect.Right, rect.Bottom, rect.X, rect.Bottom);
            path.CloseFigure();
            return path;
        }

        private sealed class PersonalProject
        {
            public PersonalProject(string title, string domain, string url, string description, params string[] tags)
            {
                Title = title;
                Domain = domain;
                Url = url;
                Description = description;
                Tags = tags ?? Array.Empty<string>();
            }

            public string Title { get; }
            public string Domain { get; }
            public string Url { get; }
            public string Description { get; }
            public string[] Tags { get; }
        }

        internal sealed class CardTone
        {
            public Color Border { get; set; }
            public Color Header { get; set; }
            public Color Tag { get; set; }
            public Color Foreground { get; set; }
            public Color ForegroundDim { get; set; }
        }

        private sealed class Palette
        {
            public Color Background { get; set; }
            public Color Panel { get; set; }
            public Color Muted { get; set; }
            public Color Shadow { get; set; }
            public Color ShadowHover { get; set; }
            public Color TextPrimary { get; set; }
            public CardTone[] Tones { get; set; }

            public static Palette Create(bool dark) => dark ? Dark : Light;

            // 与手机端 projectCardTone 完全一致（浅色）。
            private static readonly Palette Light = new Palette
            {
                Background = Color.FromArgb(246, 245, 241),
                Panel = Color.FromArgb(251, 250, 247),
                Muted = Color.FromArgb(126, 133, 128),
                Shadow = Color.FromArgb(30, 24, 24, 20),
                ShadowHover = Color.FromArgb(60, 24, 24, 20),
                TextPrimary = Color.FromArgb(32, 37, 33),
                Tones = new[]
                {
                    new CardTone { Border = Color.FromArgb(0xC9, 0xD5, 0xC6), Header = Color.FromArgb(0xE7, 0xED, 0xE4), Tag = Color.FromArgb(0xE7, 0xED, 0xE4), Foreground = Color.FromArgb(0x42, 0x59, 0x4D), ForegroundDim = Color.FromArgb(0x80, 0x42, 0x59, 0x4D) },
                    new CardTone { Border = Color.FromArgb(0xC4, 0xD0, 0xD8), Header = Color.FromArgb(0xE2, 0xE9, 0xEE), Tag = Color.FromArgb(0xE2, 0xE9, 0xEE), Foreground = Color.FromArgb(0x43, 0x56, 0x63), ForegroundDim = Color.FromArgb(0x80, 0x43, 0x56, 0x63) },
                    new CardTone { Border = Color.FromArgb(0xD8, 0xC4, 0xCA), Header = Color.FromArgb(0xEE, 0xE2, 0xE4), Tag = Color.FromArgb(0xEE, 0xE2, 0xE4), Foreground = Color.FromArgb(0x66, 0x4D, 0x55), ForegroundDim = Color.FromArgb(0x80, 0x66, 0x4D, 0x55) },
                    new CardTone { Border = Color.FromArgb(0xD8, 0xCE, 0xB8), Header = Color.FromArgb(0xEE, 0xE8, 0xDC), Tag = Color.FromArgb(0xEE, 0xE8, 0xDC), Foreground = Color.FromArgb(0x62, 0x58, 0x46), ForegroundDim = Color.FromArgb(0x80, 0x62, 0x58, 0x46) },
                    new CardTone { Border = Color.FromArgb(0xCE, 0xC6, 0xD8), Header = Color.FromArgb(0xE8, 0xE4, 0xEE), Tag = Color.FromArgb(0xE8, 0xE4, 0xEE), Foreground = Color.FromArgb(0x57, 0x4F, 0x68), ForegroundDim = Color.FromArgb(0x80, 0x57, 0x4F, 0x68) },
                    new CardTone { Border = Color.FromArgb(0xD8, 0xC6, 0xBB), Header = Color.FromArgb(0xEE, 0xE3, 0xDC), Tag = Color.FromArgb(0xEE, 0xE3, 0xDC), Foreground = Color.FromArgb(0x66, 0x50, 0x48), ForegroundDim = Color.FromArgb(0x80, 0x66, 0x50, 0x48) },
                    new CardTone { Border = Color.FromArgb(0xC9, 0xD5, 0xC6), Header = Color.FromArgb(0xEE, 0xF1, 0xEC), Tag = Color.FromArgb(0xEE, 0xF1, 0xEC), Foreground = Color.FromArgb(0x53, 0x69, 0x62), ForegroundDim = Color.FromArgb(0x80, 0x53, 0x69, 0x62) },
                    new CardTone { Border = Color.FromArgb(0xCF, 0xCB, 0xC3), Header = Color.FromArgb(0xE7, 0xE4, 0xDF), Tag = Color.FromArgb(0xE7, 0xE4, 0xDF), Foreground = Color.FromArgb(0x59, 0x55, 0x4D), ForegroundDim = Color.FromArgb(0x80, 0x59, 0x55, 0x4D) },
                }
            };

            // 与手机端 projectCardTone dark 一致。
            private static readonly Palette Dark = new Palette
            {
                Background = Color.FromArgb(22, 25, 22),
                Panel = Color.FromArgb(28, 32, 28),
                Muted = Color.FromArgb(148, 155, 150),
                Shadow = Color.FromArgb(60, 0, 0, 0),
                ShadowHover = Color.FromArgb(110, 0, 0, 0),
                TextPrimary = Color.FromArgb(232, 240, 234),
                Tones = new[]
                {
                    new CardTone { Border = Color.FromArgb(0x3D, 0x4B, 0x40), Header = Color.FromArgb(0x26, 0x31, 0x29), Tag = Color.FromArgb(0x26, 0x31, 0x29), Foreground = Color.FromArgb(0xC7, 0xDB, 0xC9), ForegroundDim = Color.FromArgb(0xA0, 0xC7, 0xDB, 0xC9) },
                    new CardTone { Border = Color.FromArgb(0x3A, 0x4A, 0x55), Header = Color.FromArgb(0x25, 0x31, 0x3A), Tag = Color.FromArgb(0x25, 0x31, 0x3A), Foreground = Color.FromArgb(0xC2, 0xD7, 0xE3), ForegroundDim = Color.FromArgb(0xA0, 0xC2, 0xD7, 0xE3) },
                    new CardTone { Border = Color.FromArgb(0x51, 0x40, 0x47), Header = Color.FromArgb(0x38, 0x2C, 0x31), Tag = Color.FromArgb(0x38, 0x2C, 0x31), Foreground = Color.FromArgb(0xE5, 0xC8, 0xD0), ForegroundDim = Color.FromArgb(0xA0, 0xE5, 0xC8, 0xD0) },
                    new CardTone { Border = Color.FromArgb(0x4F, 0x47, 0x37), Header = Color.FromArgb(0x37, 0x31, 0x25), Tag = Color.FromArgb(0x37, 0x31, 0x25), Foreground = Color.FromArgb(0xE4, 0xD4, 0xB8), ForegroundDim = Color.FromArgb(0xA0, 0xE4, 0xD4, 0xB8) },
                    new CardTone { Border = Color.FromArgb(0x49, 0x43, 0x56), Header = Color.FromArgb(0x30, 0x2D, 0x38), Tag = Color.FromArgb(0x30, 0x2D, 0x38), Foreground = Color.FromArgb(0xD7, 0xCB, 0xE5), ForegroundDim = Color.FromArgb(0xA0, 0xD7, 0xCB, 0xE5) },
                    new CardTone { Border = Color.FromArgb(0x4E, 0x40, 0x39), Header = Color.FromArgb(0x36, 0x2D, 0x29), Tag = Color.FromArgb(0x36, 0x2D, 0x29), Foreground = Color.FromArgb(0xE3, 0xCB, 0xBF), ForegroundDim = Color.FromArgb(0xA0, 0xE3, 0xCB, 0xBF) },
                    new CardTone { Border = Color.FromArgb(0x3D, 0x4B, 0x40), Header = Color.FromArgb(0x2A, 0x35, 0x30), Tag = Color.FromArgb(0x2A, 0x35, 0x30), Foreground = Color.FromArgb(0xA8, 0xBE, 0xB5), ForegroundDim = Color.FromArgb(0xA0, 0xA8, 0xBE, 0xB5) },
                    new CardTone { Border = Color.FromArgb(0x4A, 0x47, 0x40), Header = Color.FromArgb(0x34, 0x32, 0x2D), Tag = Color.FromArgb(0x34, 0x32, 0x2D), Foreground = Color.FromArgb(0xD8, 0xD1, 0xC5), ForegroundDim = Color.FromArgb(0xA0, 0xD8, 0xD1, 0xC5) },
                }
            };
        }

        private static void ApplyTitleBarTheme(IntPtr hwnd, bool dark)
        {
            try
            {
                var value = dark ? 1 : 0;
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref value, sizeof(int));
                }

                var captionColor = dark
                    ? ToColorRef(Color.FromArgb(22, 25, 22))
                    : DWMWA_COLOR_DEFAULT;
                var textColor = dark
                    ? ToColorRef(Color.FromArgb(232, 240, 234))
                    : DWMWA_COLOR_DEFAULT;
                var borderColor = dark
                    ? ToColorRef(Color.FromArgb(61, 75, 64))
                    : DWMWA_COLOR_DEFAULT;
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textColor, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
            }
            catch (Exception)
            {
            }
        }

        private static int ToColorRef(Color color)
        {
            return color.R | (color.G << 8) | (color.B << 16);
        }
    }
}
