using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TapRelay
{
    /// <summary>
    /// 个人项目面板：双列项目墙，与手机端 PersonalProjectsDialog 共用卡片语汇。
    ///
    /// 卡片结构（自上而下）：
    ///   - 头部条带：左侧主题色 accent 条 + 标题 + 右上 GitHub 图标
    ///   - 描述：单行/双行省略
    ///   - 胶囊形标签（单行排列，超出截断）
    ///   - 底部分隔线
    ///   - 直达（实心主题色） / 复制（描边） 按钮
    ///
    /// PC 端使用双列以提高浏览效率；卡片内部仍用「主题色按钮 + 文字按钮」承担主/次操作，
    /// 并显示最近更新时间，让项目排序依据可被用户直接理解。
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
        private readonly TableLayoutPanel _cardList = new TableLayoutPanel();
        private readonly ToolTip _toolTip = new ToolTip();
        private readonly Label _projectCountLabel = new Label();
        private IReadOnlyList<PersonalProject> _projects = Array.Empty<PersonalProject>();

        private static readonly HttpClient GitHubClient = CreateGitHubClient();
        private const string GitHubOwner = "guliacer";
        private static readonly string[] PinnedProjectNames =
        {
            "SuYan",
            "ComfyUI-GuliNodes",
            "PixGo"
        };

        private static readonly HashSet<string> IgnoredProjectNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ComfyUI-GuliNodes-Dependencies",
                "harvest-match-helper",
                "suyan-components"
            };

        private const int HeaderHeight = 84;
        // PC 页面用双列项目墙承载更多自有仓库：卡片保持舒适的信息密度，
        // 不再让 10 个项目全部纵向堆叠成一条很长的列表。
        private const int CardHeight = 212;
        private const int CardGap = 16;
        private const int CardHeaderHeight = 56;
        private const int CardCornerRadius = 12;   // 手机端 RoundedCornerShape(12.dp)
        private const int ActionButtonHeight = 32;
        private const int ActionButtonPaddingX = 12;

        /// <summary>
        /// 手机端 <c>GITHUB_MARK_PATH</c> 的原文照搬（24×24 viewport，线稿 Octocat）。
        /// 改这个字符串前请先同步 android-app 的 PersonalProjectsDialog.kt，两端必须一致。
        /// </summary>
        private const string GitHubMarkPath =
            "M12,3.5 C7.3,3.5 3.5,7.3 3.5,12 C3.5,15.7 5.9,18.8 9.2,19.9 " +
            "M14.8,19.9 C18.1,18.8 20.5,15.7 20.5,12 C20.5,7.3 16.7,3.5 12,3.5 " +
            "M8.5,18 C8.5,16.5 9.2,15.5 10.2,15 " +
            "C8,14.7 6.6,13.6 6.6,11.2 C6.6,10.1 7,9.2 7.7,8.5 " +
            "C7.5,7.8 7.5,7.1 7.9,6.4 C8.9,6.5 9.7,6.9 10.3,7.3 " +
            "C11.4,7 12.6,7 13.7,7.3 C14.3,6.9 15.1,6.5 16.1,6.4 " +
            "C16.5,7.1 16.5,7.8 16.3,8.5 C17,9.2 17.4,10.1 17.4,11.2 " +
            "C17.4,13.6 16,14.7 13.8,15 C14.8,15.5 15.5,16.5 15.5,18";

        // 操作图标也直接复用手机端的 pathData，避免「卡片头部一样、按钮却不同」。
        private const string ExternalLinkPath =
            "M14,3 L21,3 L21,10 M21,3 L11,13 M18,13 L18,19 " +
            "C18,20.1 17.1,21 16,21 L5,21 C3.9,21 3,20.1 3,19 L3,8 " +
            "C3,6.9 3.9,6 5,6 L11,6";

        private const string GlobePath =
            "M12,3 C7,3 3,7 3,12 C3,17 7,21 12,21 C17,21 21,17 21,12 " +
            "C21,7 17,3 12,3 Z M3,12 L21,12 " +
            "M12,3 C14.2,5.4 15.5,8.5 15.5,12 C15.5,15.5 14.2,18.6 12,21 " +
            "M12,3 C9.8,5.4 8.5,8.5 8.5,12 C8.5,15.5 9.8,18.6 12,21";

        // GitHub API 不可用时的离线兜底清单。运行时仍会按 pushed_at 重新同步，
        // 并在客户端再次过滤 fork，避免网络异常时把旧的 fork 项目带回来。
        private static readonly PersonalProject[] FallbackProjects =
        {
            new PersonalProject("ComfyUI-GuliPoses", "github.com", "https://github.com/guliacer/ComfyUI-GuliPoses", "ComfyUI 白模姿态参考图库节点，提供可直接复用的姿态素材。", "ComfyUI", "节点扩展"),
            new PersonalProject("ComfyUI-GuliNodes", "github.com", "https://github.com/guliacer/ComfyUI-GuliNodes", "面向中文用户习惯的 ComfyUI 实用节点合集。", "ComfyUI", "节点扩展"),
            new PersonalProject("PixGo", "github.com", "https://github.com/guliacer/PixGo", "使用 AI 编写的照片传输应用，改善相机到安卓设备的传图体验。", "桌面工具", "照片传输"),
            new PersonalProject("TapRelay", "github.com", "https://github.com/guliacer/TapRelay", "Windows 与 Android 之间的 AI Agent 任务通知中继。", "Windows", "Android"),
            new PersonalProject("SuYan", "github.com", "https://github.com/guliacer/SuYan", "本地 AI 提示词与图像素材管理工具。", "AI 工具", "提示词"),
            new PersonalProject("link-slimmer", "github.com", "https://github.com/guliacer/link-slimmer", "轻量的链接整理与分享小工具。", "工具"),
            new PersonalProject("GetPhoto", "github.com", "https://github.com/guliacer/GetPhoto", "网页图片提取插件，用于快速筛选、预览并下载当前页面图片。", "浏览器脚本", "图像提取"),
            new PersonalProject("computer-status", "github.com", "https://github.com/guliacer/computer-status", "轻量、常驻桌面的 Windows 系统状态监控悬浮窗。", "Windows", "桌面工具"),
            new PersonalProject("news-html-digest", "github.com", "https://github.com/guliacer/news-html-digest", "抓取多源新闻并生成北京时间 HTML 日报。", "Skill", "新闻抓取"),
            new PersonalProject("PagePurifier", "github.com", "https://github.com/guliacer/PagePurifier", "通用网页去广告用户脚本，保持页面清爽。", "浏览器脚本", "网页净化"),
            new PersonalProject("LShot", "github.com", "https://github.com/guliacer/LShot", "生成当前网页的完整 PNG 长截图。", "浏览器脚本", "截图"),
            new PersonalProject("preview", "github.com", "https://github.com/guliacer/preview", "面向 Tampermonkey 的图片悬浮预览脚本。", "浏览器脚本", "图像预览"),
            new PersonalProject("VeilReader", "github.com", "https://github.com/guliacer/VeilReader", "网页隐藏式 TXT 小说阅读器。", "浏览器脚本", "阅读工具"),
            new PersonalProject("cookclick", "github.com", "https://github.com/guliacer/cookclick", "用于 Tampermonkey / Violentmonkey 的网页互动脚本。", "浏览器脚本", "网页美化"),
        };

        public PersonalProjectsForm(bool darkMode)
        {
            _darkMode = darkMode;

            Text = "个人项目";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ShowInTaskbar = false;
            MinimumSize = new Size(520, 520);
            ClientSize = new Size(760, 640);
            Font = new Font(UiTokens.UiFontFamily, UiTokens.BodyFontSize);
            BackColor = _darkMode
                ? Color.FromArgb(16, 25, 22)
                : Color.FromArgb(244, 247, 245);

            BuildLayout();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (Handle != IntPtr.Zero)
            {
                ApplyTitleBarTheme(Handle, _darkMode);
            }

            _ = RefreshProjectsAsync();
        }

        private void BuildLayout()
        {
            var colors = Palette.Create(_darkMode);
            // 手机端标题计数使用全局 PrimarySageStrong；PC 调色板中对应第 7 个 tone。
            var headerAccent = colors.Tones.Length > 6
                ? colors.Tones[6].Foreground
                : colors.Tones[0].Foreground;

            _root.Dock = DockStyle.Fill;
            _root.ColumnCount = 1;
            _root.RowCount = 2;
            _root.RowStyles.Add(new RowStyle(SizeType.Absolute, HeaderHeight));
            _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            _root.BackColor = colors.Background;
            _root.Padding = new Padding(24, 20, 24, 20);

            // 顶部头部：标题 + 筛选规则 + 同步状态。把“只展示自有仓库”和排序依据
            // 直接放在页面上，避免用户误以为列表是手工维护的固定推荐。
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = colors.Background,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));

            header.Controls.Add(new Label
            {
                Text = "个人项目",
                Dock = DockStyle.Fill,
                Font = new Font(UiTokens.UiFontFamily, 14F, FontStyle.Bold),
                ForeColor = colors.TextPrimary,
                BackColor = colors.Background,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            }, 0, 0);
            header.Controls.Add(new Label
            {
                Text = "只展示自己拥有的仓库，按最近更新排序；派生项目已隐藏。",
                Dock = DockStyle.Fill,
                Font = new Font(UiTokens.UiFontFamily, UiTokens.BodyFontSize, FontStyle.Regular),
                ForeColor = colors.Muted,
                BackColor = colors.Background,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            }, 0, 1);
            _projectCountLabel.Text = "正在读取 GitHub 项目…";
            _projectCountLabel.Dock = DockStyle.Fill;
            _projectCountLabel.Font = new Font(UiTokens.UiFontFamily, UiTokens.BodyFontSize, FontStyle.Bold);
            _projectCountLabel.ForeColor = headerAccent;
            _projectCountLabel.BackColor = colors.Background;
            _projectCountLabel.TextAlign = ContentAlignment.MiddleLeft;
            _projectCountLabel.Margin = new Padding(0);
            header.Controls.Add(_projectCountLabel, 0, 2);
            _root.Controls.Add(header, 0, 0);

            _scrollBody.Dock = DockStyle.Fill;
            _scrollBody.AutoScroll = true;
            _scrollBody.BackColor = colors.Background;

            // PC 端用双列项目墙提升浏览效率；手机端仍保持自己的单列项目页。
            _cardList.Dock = DockStyle.Top;
            _cardList.AutoSize = true;
            _cardList.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _cardList.ColumnCount = 2;
            _cardList.RowCount = 1;
            _cardList.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            _cardList.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            _cardList.BackColor = colors.Background;
            _cardList.Padding = new Padding(0);
            _cardList.Margin = new Padding(0);
            _scrollBody.Resize += (_, __) => ResizeCardList();
            _scrollBody.Controls.Add(_cardList);

            _root.Controls.Add(_scrollBody, 0, 1);
            Controls.Add(_root);
            RenderProjects(FallbackProjects, "正在同步 GitHub…");
        }

        private void ResizeCardList()
        {
            if (_scrollBody.IsDisposed || _cardList.IsDisposed)
            {
                return;
            }

            var width = _scrollBody.ClientSize.Width;
            if (_scrollBody.VerticalScroll.Visible)
            {
                width -= SystemInformation.VerticalScrollBarWidth;
            }

            _cardList.Width = Math.Max(0, width);
        }

        private void RenderProjects(IReadOnlyList<PersonalProject> projects, string status)
        {
            if (IsDisposed || _cardList.IsDisposed)
            {
                return;
            }

            _projects = OrderProjects(projects ?? Array.Empty<PersonalProject>());
            _cardList.SuspendLayout();
            try
            {
                foreach (Control control in _cardList.Controls.Cast<Control>().ToArray())
                {
                    _cardList.Controls.Remove(control);
                    control.Dispose();
                }

                _cardList.RowStyles.Clear();
                _cardList.ColumnStyles.Clear();
                _cardList.ColumnCount = 2;
                _cardList.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
                _cardList.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

                var rowCount = Math.Max(1, (int)Math.Ceiling(_projects.Count / 2d));
                _cardList.RowCount = rowCount;
                for (var row = 0; row < rowCount; row++)
                {
                    var isLastRow = row == rowCount - 1;
                    _cardList.RowStyles.Add(new RowStyle(
                        SizeType.Absolute,
                        CardHeight + (isLastRow ? 0 : CardGap)));
                }

                for (var i = 0; i < _projects.Count; i++)
                {
                    var column = i % 2;
                    var row = i / 2;
                    var card = BuildCard(_projects[i], i);
                    card.Dock = DockStyle.Fill;
                    card.Margin = new Padding(
                        column == 0 ? 0 : CardGap / 2,
                        0,
                        column == 0 ? CardGap / 2 : 0,
                        row == rowCount - 1 ? 0 : CardGap);
                    _cardList.Controls.Add(card, column, row);
                }
            }
            finally
            {
                _cardList.ResumeLayout(true);
            }

            var pinnedCount = _projects.Count(project => PinnedProjectNames.Any(name =>
                string.Equals(name, project.Title, StringComparison.OrdinalIgnoreCase)));
            _projectCountLabel.Text =
                $"{_projects.Count} 个项目 · 置顶 {pinnedCount} 个 · {status}";
            ResizeCardList();
        }

        private async Task RefreshProjectsAsync()
        {
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"https://api.github.com/users/{GitHubOwner}/repos?type=owner&sort=pushed&direction=desc&per_page=100");
                using var response = await GitHubClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                var payload = await response.Content.ReadAsStringAsync();
                var projects = ParseGitHubProjects(payload);
                if (projects.Count == 0)
                {
                    throw new InvalidOperationException("GitHub 没有返回可展示的自有项目。");
                }

                RenderProjects(projects, $"最近同步 {DateTime.Now:HH:mm}");
            }
            catch (Exception)
            {
                // 页面打开不能被网络问题阻塞：保留本地清单，并明确告知当前数据不是实时同步结果。
                RenderProjects(
                    _projects.Count > 0 ? _projects : FallbackProjects,
                    "网络不可用，已显示本地清单");
            }
        }

        private static IReadOnlyList<PersonalProject> ParseGitHubProjects(string payload)
        {
            using var document = JsonDocument.Parse(payload);
            var projects = new List<PersonalProject>();

            foreach (var repo in document.RootElement.EnumerateArray())
            {
                if (repo.ValueKind != JsonValueKind.Object ||
                    (repo.TryGetProperty("fork", out var fork) && fork.ValueKind == JsonValueKind.True))
                {
                    continue;
                }

                if (repo.TryGetProperty("owner", out var owner) &&
                    owner.TryGetProperty("login", out var login) &&
                    !string.Equals(login.GetString(), GitHubOwner, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = ReadString(repo, "name");
                var url = ReadString(repo, "html_url");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var description = ReadString(repo, "description");
                if (string.IsNullOrWhiteSpace(description))
                {
                    description = "个人开源项目，点击查看 GitHub 详情。";
                }

                DateTimeOffset? updatedAt = null;
                var pushedAt = ReadString(repo, "pushed_at");
                if (DateTimeOffset.TryParse(pushedAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var parsedPushedAt))
                {
                    updatedAt = parsedPushedAt;
                }
                else if (DateTimeOffset.TryParse(ReadString(repo, "updated_at"),
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedUpdatedAt))
                {
                    updatedAt = parsedUpdatedAt;
                }

                var tags = new List<string>();
                if (updatedAt.HasValue)
                {
                    tags.Add("更新 " + updatedAt.Value.ToLocalTime().ToString("MM/dd"));
                }

                var language = ReadString(repo, "language");
                if (!string.IsNullOrWhiteSpace(language))
                {
                    tags.Add(language);
                }

                if (repo.TryGetProperty("topics", out var topics) && topics.ValueKind == JsonValueKind.Array)
                {
                    foreach (var topic in topics.EnumerateArray().Take(1))
                    {
                        var topicText = topic.GetString();
                        if (!string.IsNullOrWhiteSpace(topicText))
                        {
                            tags.Add(topicText);
                        }
                    }
                }

                if (tags.Count == 0)
                {
                    tags.Add("GitHub");
                }

                projects.Add(new PersonalProject(
                    name,
                    "github.com",
                    url,
                    description,
                    updatedAt,
                    tags.ToArray()));
            }

            return OrderProjects(projects);
        }

        private static IReadOnlyList<PersonalProject> OrderProjects(IEnumerable<PersonalProject> source)
        {
            var visible = source
                .Where(project => !IgnoredProjectNames.Contains(project.Title))
                .GroupBy(project => project.Title, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            var pinned = PinnedProjectNames
                .Select(name => visible.FirstOrDefault(project =>
                    string.Equals(project.Title, name, StringComparison.OrdinalIgnoreCase)))
                .Where(project => project != null)
                .Cast<PersonalProject>()
                .ToList();

            var pinnedNames = new HashSet<string>(
                pinned.Select(project => project.Title),
                StringComparer.OrdinalIgnoreCase);
            var rest = visible
                .Where(project => !pinnedNames.Contains(project.Title))
                .OrderByDescending(project => project.UpdatedAtUtc ?? DateTimeOffset.MinValue)
                .ThenBy(project => project.Title, StringComparer.OrdinalIgnoreCase);

            return pinned.Concat(rest).ToArray();
        }

        private static string ReadString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var property) &&
                   property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;
        }

        private static HttpClient CreateGitHubClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(8)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TapRelay-PC/0.3");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        private Control BuildCard(PersonalProject project, int index)
        {
            var colors = Palette.Create(_darkMode);
            var tone = colors.Tones[index % colors.Tones.Length];
            var card = new Panel
            {
                Height = CardHeight,
                BackColor = colors.Panel,
                TabStop = false
            };
            var hovered = false;
            card.MouseEnter += (_, __) => { hovered = true; card.Invalidate(); };
            card.MouseLeave += (_, __) => { hovered = false; card.Invalidate(); };

            var openButton = new CardActionButton(CardActionButton.Glyph.Open, CardActionButton.StyleKind.Primary, "直达");
            var copyButton = new CardActionButton(CardActionButton.Glyph.Copy, CardActionButton.StyleKind.Secondary, "复制");
            openButton.ApplyColors(tone.Foreground, tone.Border, tone.Header, colors.Panel);
            copyButton.ApplyColors(tone.Foreground, tone.Border, tone.Header, colors.Panel);
            _toolTip.SetToolTip(openButton, "打开 GitHub 项目");
            _toolTip.SetToolTip(copyButton, "复制项目地址");
            openButton.Click += (_, __) => OpenProject(project.Url, project.Title, openButton);
            copyButton.Click += (_, __) => CopyProjectAddress(project.Url, project.Title, copyButton);
            card.Controls.Add(copyButton);
            card.Controls.Add(openButton);

            void LayoutActions()
            {
                var right = Math.Max(0, card.ClientSize.Width - 16);
                copyButton.Size = copyButton.GetPreferredSize(Size.Empty);
                copyButton.Location = new Point(right - copyButton.Width, CardHeight - ActionButtonHeight - 12);
                openButton.Size = openButton.GetPreferredSize(Size.Empty);
                openButton.Location = new Point(copyButton.Left - 8 - openButton.Width, CardHeight - ActionButtonHeight - 12);
            }
            card.Resize += (_, __) => LayoutActions();
            LayoutActions();

            // 卡内布局的纵向坐标常量（与手机端 PersonalProjectCard 的 spacing 对齐）
            // 手机端正文区 Column(padding start/end 16, top 14, bottom 12; spacedBy 14)：
            //   分隔线 → +14 描述(2 行 36) → +14 标签(18) → +14 分隔线 → +14 按钮(32) → +12 底距
            const float DescriptionTopOffset = CardHeaderHeight + 15;     // 分隔线 1 + 14
            const float DescriptionHeight = 36;
            const float TagTopOffset = CardHeaderHeight + 65;             // 描述下沿 + 14
            const float TagHeight = 18;                                   // 手机端 10.sp 文字 + 上下 1.dp + 1.dp 描边
            const float Divider2Y = CardHeaderHeight + 98;                // 标签下沿 + 14
            // 按钮的纵向位置不在这里：它是真实子控件，位置由上面的 LayoutActions() 决定，
            // 两者必须一致（CardHeight - ActionButtonHeight - 12）。

            card.Paint += (_, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                var rect = new Rectangle(1, 1, Math.Max(1, card.Width - 3), Math.Max(1, card.Height - 3));

                using (var path = RoundedRect(rect, CardCornerRadius))
                using (var shadowPath = RoundedRect(new Rectangle(2, 3, Math.Max(1, card.Width - 5), Math.Max(1, card.Height - 4)), CardCornerRadius))
                using (var bg = new SolidBrush(colors.Panel))
                using (var borderPen = new Pen(hovered ? tone.Foreground : tone.Border, 1f))
                using (var shadowBrush = new SolidBrush(hovered ? colors.ShadowHover : colors.Shadow))
                {
                    g.FillPath(shadowBrush, shadowPath);
                    g.FillPath(bg, path);
                    g.DrawPath(borderPen, path);
                }

                // 头部条带（圆角顶部，tone.Header 填色）
                var headerRect = new Rectangle(rect.X + 1, rect.Y + 1, rect.Width - 2, CardHeaderHeight);
                using (var headerPath = RoundedRectTop(headerRect, CardCornerRadius))
                using (var headerBrush = new SolidBrush(tone.Header))
                {
                    g.FillPath(headerBrush, headerPath);
                }

                // 左侧 accent 条：手机端是 4.dp × 34.dp、圆角 2.dp、位于 header 左右内边距 14.dp 处，
                // 上下各留 11.dp 内边距——所以头部才有 56 那么高。
                using (var accentBrush = new SolidBrush(tone.Border))
                using (var accentPath = RoundedRect(
                    new RectangleF(rect.X + 14, headerRect.Y + 11, 4, 34), 2))
                {
                    g.FillPath(accentBrush, accentPath);
                }

                // 标题（粗体）
                using (var titleBrush = new SolidBrush(tone.Foreground))
                using (var titleFont = new Font(UiTokens.UiFontFamily, 11.5F, FontStyle.Bold))
                using (var titleFormat = new StringFormat
                {
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap
                })
                {
                    // 手机端：accent 条后 10.dp 间距才开始标题，且在 56 高的 header 里垂直居中
                    var titleRect = new RectangleF(rect.X + 28, headerRect.Y + 17,
                        Math.Max(80, headerRect.Width - 68), 22);
                    g.DrawString(project.Title, titleFont, titleBrush, titleRect, titleFormat);
                }

                // 右上 GitHub 图标：手机端是 18.dp、距右 14.dp、tone.foreground alpha 0.8，这里完全对齐
                DrawGitHubMark(g, Color.FromArgb(204, tone.Foreground),
                    headerRect.Right - 32, headerRect.Y + 19, 18);

                // 头部下方分隔线（手机端 HorizontalDivider 是通栏的，不内缩）
                using (var divPen = new Pen(tone.Border, 1f))
                {
                    g.DrawLine(divPen, rect.X, headerRect.Bottom, rect.Right, headerRect.Bottom);
                }

                // 描述（双行省略）
                using (var descBrush = new SolidBrush(colors.Muted))
                using (var descFont = new Font(UiTokens.UiFontFamily, UiTokens.CaptionFontSize + 0.5F))
                using (var descFormat = new StringFormat
                {
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.LineLimit
                })
                {
                    g.DrawString(project.Description, descFont, descBrush,
                        new RectangleF(rect.X + 16, rect.Y + DescriptionTopOffset, rect.Width - 32, DescriptionHeight),
                        descFormat);
                }

                // 胶囊形标签（单行，超出截断）
                using (var tagFont = new Font(UiTokens.UiFontFamily, 8F))
                {
                    float tx = rect.X + 16;
                    var ty = rect.Y + TagTopOffset;
                    foreach (var tag in project.Tags)
                    {
                        var tagSize = g.MeasureString(tag, tagFont);
                        var tagRect = new RectangleF(tx, ty, tagSize.Width + 14, TagHeight);
                        if (tagRect.Right > rect.Right - 16)
                        {
                            break;
                        }
                        using (var tagPath = RoundedRect(tagRect, 9))
                        using (var tagBrush = new SolidBrush(tone.Tag))
                        using (var tagBorder = new Pen(tone.Border, 1f))
                        using (var tagText = new SolidBrush(tone.Foreground))
                        {
                            g.FillPath(tagBrush, tagPath);
                            g.DrawPath(tagBorder, tagPath);
                            g.DrawString(tag, tagFont, tagText, new PointF(tx + 7, ty + 3));
                        }
                        tx += tagRect.Width + 4;   // 手机端标签间距 4.dp
                    }
                }

                // 按钮上方的分隔线（对齐手机端 tags 与 buttons 之间的 border-t）
                using (var divPen = new Pen(tone.Border, 1f))
                {
                    g.DrawLine(divPen, rect.X, rect.Y + Divider2Y, rect.Right, rect.Y + Divider2Y);
                }
            };

            return card;
        }

        private void OpenProject(string url, string title, Control anchor)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception)
            {
                ShowTransientTip(anchor, $"无法打开 {title}，请检查浏览器");
            }
        }

        private void CopyProjectAddress(string url, string title, Control anchor)
        {
            try
            {
                Clipboard.SetText(url);
                anchor.Focus();
                ShowTransientTip(anchor, $"已复制 {title} 地址");
            }
            catch (Exception)
            {
                ShowTransientTip(anchor, "复制失败，请重试");
            }
        }

        /// <summary>
        /// 手机端这两个操作都有明确反馈：打开失败显示「无法打开 X，请检查浏览器。」（Danger 色），
        /// 复制成功弹 Toast「已复制 X 地址」。PC 端原先两个 catch 都是空实现，点了完全没反应，
        /// 用户无法判断成功还是失败。这里用按钮下方的短暂气泡做等价反馈——
        /// 刻意不用 MessageBox：模态弹窗会打断连续浏览多个项目的操作。
        /// </summary>
        private void ShowTransientTip(Control anchor, string message)
        {
            _toolTip.Show(message, anchor, new Point(anchor.Width / 2, anchor.Height + 6), 1800);
        }

        /// <summary>
        /// 卡片右上角的 GitHub 标记：与手机端 <c>GITHUB_MARK_PATH</c> 逐字符一致（24×24 viewport 线稿）。
        /// 早先这里用的是抽象 tree-glyph，与手机端的 Octocat 轮廓不是一回事；改为直接解析
        /// 手机端的 pathData，避免手工翻译造成两端漂移。
        /// </summary>
        private static void DrawGitHubMark(Graphics g, Color color, float x, float y, float size)
        {
            const float viewport = 24f;
            const float strokeInViewport = 1.5f;   // 手机端 strokeLineWidth = 1.5f
            var scale = size / viewport;

            using var path = BuildSvgPath(GitHubMarkPath);
            // 先缩放后平移：GDI+ 行向量约定下 [s 0 0 s dx dy] 即 x' = s*x + dx。
            using var matrix = new Matrix(scale, 0f, 0f, scale, x, y);
            path.Transform(matrix);

            using var pen = new Pen(color, strokeInViewport * scale)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            g.DrawPath(pen, path);
        }

        /// <summary>
        /// 精简 SVG path 解析：支持本项目图标使用的 M / L / C / Z（绝对坐标）。
        /// 存在的意义是让 PC 端图标能与手机端 ImageVector 的 pathData 共用同一份字符串，
        /// 而不是各写一份几何代码——后者必然随时间漂移。
        /// </summary>
        private static GraphicsPath BuildSvgPath(string pathData)
        {
            var path = new GraphicsPath();
            var tokens = Regex.Matches(pathData, @"[MLCZ]|-?\d*\.?\d+");
            var index = 0;

            float Next()
            {
                if (index >= tokens.Count) throw new FormatException("SVG path ended mid-command.");
                return float.Parse(tokens[index++].Value, CultureInfo.InvariantCulture);
            }

            var current = PointF.Empty;
            while (index < tokens.Count)
            {
                switch (tokens[index++].Value)
                {
                    case "M":
                        // SVG 的 M 是新子路径；如果不显式 StartFigure，GDI+ 可能把多个
                        // M/C/L 片段接成一条线，产生手机端没有的横向连线。
                        path.StartFigure();
                        current = new PointF(Next(), Next());
                        break;
                    case "L":
                        current = AddLine(path, current, new PointF(Next(), Next()));
                        break;
                    case "C":
                        // 手机端把连续贝塞尔写成一条 C 后跟多组坐标，这里要一直读到下一个命令。
                        while (index < tokens.Count && !IsSvgCommand(tokens[index].Value))
                        {
                            var c1 = new PointF(Next(), Next());
                            var c2 = new PointF(Next(), Next());
                            var end = new PointF(Next(), Next());
                            path.AddBezier(current, c1, c2, end);
                            current = end;
                        }
                        break;
                    case "Z":
                        path.CloseFigure();
                        break;
                }
            }
            return path;
        }

        private static bool IsSvgCommand(string token)
        {
            return token == "M" || token == "L" || token == "C" || token == "Z";
        }

        private static PointF AddLine(GraphicsPath path, PointF from, PointF to)
        {
            path.AddLine(from, to);
            return to;
        }

        private static void DrawLineIcon(Graphics g, Color color, string pathData, RectangleF rect)
        {
            const float viewport = 24f;
            const float strokeInViewport = 1.5f;   // 手机端 pixGoLineIcon.strokeLineWidth
            var scale = rect.Width / viewport;
            using var path = BuildSvgPath(pathData);
            using var matrix = new Matrix(scale, 0f, 0f, scale, rect.Left, rect.Top);
            path.Transform(matrix);
            using var pen = new Pen(color, strokeInViewport * scale)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            g.DrawPath(pen, path);
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

        /// <summary>
        /// 卡片底部的「直达」/「复制」按钮：圆角矩形 + 图标 + 文字。
        ///
        /// 故意不走 UiButtonStyler 入口：那个入口是为了「全应用统一品牌绿主按钮」，
        /// 这里的按钮必须随卡片主题色着色（tone.Foreground 实心 / tone.Tag 描边），
        /// 与手机端 PersonalProjectsDialog 的 per-card 主题按钮对齐；
        /// 走 UiButtonStyler 会把所有卡片的主按钮都染成同一抹绿，破坏视觉一致性。
        /// </summary>
        private sealed class CardActionButton : Control
        {
            internal enum Glyph
            {
                Open,
                Copy
            }

            internal enum StyleKind
            {
                Primary,
                Secondary
            }

            private const float IconSize = 13F;
            private const float IconTextGap = 5F;
            private const float BorderWidth = 1F;

            private readonly Glyph _glyph;
            private readonly StyleKind _style;
            private readonly string _text;
            private readonly Font _font;

            private Color _foreground = Color.Black;
            private Color _border = Color.Gray;
            private Color _surface = Color.White;      // 主按钮的文字/图标色 = 手机端 tone.header
            private Color _panel = Color.White;        // 次按钮的底色 = 手机端 Surface（卡片底色）
            private Color _hoverSurface = Color.LightGray;
            private bool _hovered;
            private bool _focused;
            private bool _pressed;

            internal CardActionButton(Glyph glyph, StyleKind style, string text)
            {
                _glyph = glyph;
                _style = style;
                _text = text;
                AccessibleRole = AccessibleRole.PushButton;
                AccessibleName = text;
                TabStop = true;
                Cursor = Cursors.Hand;
                _font = new Font(
                    UiTokens.UiFontFamily,
                    10F,
                    style == StyleKind.Primary ? FontStyle.Bold : FontStyle.Regular);
                SetStyle(
                    ControlStyles.UserPaint |
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.ResizeRedraw |
                    ControlStyles.SupportsTransparentBackColor,
                    true);
                MouseEnter += (_, __) => { _hovered = true; Invalidate(); };
                MouseLeave += (_, __) => { _hovered = false; _pressed = false; Invalidate(); };
                MouseDown += (_, __) => { if (ClientRectangle.Contains(PointToClient(MousePosition))) { _pressed = true; Invalidate(); } };
                MouseUp += (_, __) => { _pressed = false; Invalidate(); };
                GotFocus += (_, __) => { _focused = true; Invalidate(); };
                LostFocus += (_, __) => { _focused = false; Invalidate(); };
            }

            /// <summary>
            /// 对齐手机端两套按钮的配色规则：
            ///   直达（Primary）  containerColor = tone.foreground，contentColor = tone.header
            ///   复制（Secondary）containerColor = Surface（卡片底色），contentColor = tone.foreground
            /// 注意主按钮的内容色是 tone.header 而不是白色——深色主题下 tone.foreground 是浅色，
            /// 白字会糊成一片；这正是之前用 Color.White 的问题。
            /// </summary>
            internal void ApplyColors(Color foreground, Color border, Color header, Color panel)
            {
                _foreground = foreground;
                _border = border;
                _surface = header;
                _panel = panel;
                _hoverSurface = ControlPaint.Light(panel, 0.06f);
                Invalidate();
            }

            public override Size GetPreferredSize(Size proposedSize)
            {
                var textSize = TextRenderer.MeasureText(_text, _font);
                var w = ActionButtonPaddingX * 2 + IconSize + IconTextGap + textSize.Width;
                return new Size((int)Math.Ceiling(w), ActionButtonHeight);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                var rect = new RectangleF(
                    BorderWidth / 2f,
                    BorderWidth / 2f,
                    Math.Max(1, Width - BorderWidth),
                    Math.Max(1, Height - BorderWidth));

                // 背景与边框
                Color bg;
                Color borderColor;
                Color textColor;
                bool drawBorder;
                if (_style == StyleKind.Primary)
                {
                    bg = _hovered ? ControlPaint.Dark(_foreground, 0.08f)
                        : _focused ? ControlPaint.Dark(_foreground, 0.05f)
                        : _foreground;
                    borderColor = bg;
                    drawBorder = false;
                    textColor = _surface;      // 手机端 contentColor = tone.header
                }
                else
                {
                    bg = _pressed ? ControlPaint.Dark(_panel, 0.06f)
                        : _hovered ? _hoverSurface
                        : _focused ? _hoverSurface
                        : _panel;              // 手机端 containerColor = Surface
                    borderColor = _border;
                    drawBorder = true;
                    textColor = _foreground;
                }

                using (var path = RoundedRect(rect, UiTokens.ButtonRadius))
                using (var bgBrush = new SolidBrush(bg))
                {
                    g.FillPath(bgBrush, path);
                    if (drawBorder)
                    {
                        using var borderPen = new Pen(borderColor, BorderWidth);
                        g.DrawPath(borderPen, path);
                    }
                }

                // 图标 + 文字（水平居中）
                var textSize = g.MeasureString(_text, _font);
                var totalW = IconSize + IconTextGap + textSize.Width;
                var startX = (Width - totalW) / 2f;
                var iconY = (Height - IconSize) / 2f;
                var textY = (Height - textSize.Height) / 2f;

                using (var textBrush = new SolidBrush(textColor))
                {
                    var iconRect = new RectangleF(startX, iconY, IconSize, IconSize);
                    // 手机端 ExternalLinkIcon / GlobeIcon 都是 24×24、1.5px 圆角线稿；
                    // 这里用同一份 pathData，而不是近似的手绘框线。
                    DrawLineIcon(g, textColor,
                        _glyph == Glyph.Open ? ExternalLinkPath : GlobePath,
                        iconRect);
                    g.DrawString(_text, _font, textBrush, new PointF(startX + IconSize + IconTextGap, textY));
                }
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
                {
                    OnClick(EventArgs.Empty);
                    e.Handled = true;
                    return;
                }
                base.OnKeyDown(e);
            }
        }

        private sealed class PersonalProject
        {
            public PersonalProject(string title, string domain, string url, string description, params string[] tags)
                : this(title, domain, url, description, null, tags)
            {
            }

            public PersonalProject(
                string title,
                string domain,
                string url,
                string description,
                DateTimeOffset? updatedAtUtc,
                params string[] tags)
            {
                Title = title;
                Domain = domain;
                Url = url;
                Description = description;
                UpdatedAtUtc = updatedAtUtc;
                Tags = tags ?? Array.Empty<string>();
            }

            public string Title { get; }
            public string Domain { get; }
            public string Url { get; }
            public string Description { get; }
            public DateTimeOffset? UpdatedAtUtc { get; }
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
            public CardTone[] Tones { get; set; } = Array.Empty<CardTone>();

            public static Palette Create(bool dark) => dark ? Dark : Light;

            // 与手机端 projectCardTone 完全一致（浅色）。
            private static readonly Palette Light = new Palette
            {
                Background = Color.FromArgb(244, 247, 245),
                Panel = Color.FromArgb(255, 255, 255),
                Muted = Color.FromArgb(99, 118, 109),
                Shadow = Color.FromArgb(30, 24, 24, 20),
                ShadowHover = Color.FromArgb(60, 24, 24, 20),
                TextPrimary = Color.FromArgb(24, 45, 38),
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
                Background = Color.FromArgb(16, 25, 22),
                Panel = Color.FromArgb(24, 38, 32),
                Muted = Color.FromArgb(165, 190, 177),
                Shadow = Color.FromArgb(60, 0, 0, 0),
                ShadowHover = Color.FromArgb(110, 0, 0, 0),
                TextPrimary = Color.FromArgb(239, 248, 242),
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
