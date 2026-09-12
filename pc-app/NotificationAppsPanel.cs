using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TapRelay
{
    /// <summary>
    /// 閫氱煡搴旂敤绠＄悊闈㈡澘锛氬睍绀哄凡閫夐€氱煡搴旂敤鍥炬爣锛屾彁渚?鑷姩妫€娴?鍜?搴旂敤閫夋嫨"涓や釜鎿嶄綔鎸夐挳銆?
    /// 甯冨眬锛?
    /// - 绗竴鎺掞細宸插惎鐢ㄧ殑搴旂敤鍥炬爣 + 鍚嶇О锛堢偣鍑荤鐢紝鍥炬爣鍙樼伆锛?
    /// - 绗簩鎺掞細[鑷姩妫€娴媇 [搴旂敤閫夋嫨] 涓や釜鎸夐挳
    /// </summary>
    internal sealed class NotificationAppsPanel : Panel
    {
        /// <summary>
        /// 宸插惎鐢ㄧ殑閫氱煡搴旂敤銆俈alue = 鏄剧ず鍚嶇О銆傝绉婚櫎鍚庡浘鏍囧彉鐏颁笖涓嶅弬涓庨€氱煡鐩戝惉銆?
        /// </summary>
        private readonly Dictionary<string, AppEntry> _enabledApps =
            new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        // 检测/注册到（含 RootPath）的应用，用于在完整目录渲染时保留检测信息。
        private readonly Dictionary<string, AppEntry> _detectedApps =
            new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        // 用户手动禁用的应用键（图标变灰、不再推送通知）。持久化到磁盘，
        // 重启后保持禁用状态，避免自动检测把已取消的应用重新打开。
        private readonly HashSet<string> _disabledKeys;

        private static readonly string PreferencesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LanNotify",
            "notification-apps.json");

        // MSIX / Store 应用不一定注册在传统的卸载项里（ChatGPT 即是如此），
        // 读取 AppModel 包目录可在应用未运行时仍识别为已安装。目录扫描有缓存，
        // 避免目录重绘时反复枚举大量已注册包。
        private static readonly object AppxPackagesGate = new object();
        private static DateTime _appxPackagesCheckedAtUtc = DateTime.MinValue;
        private static string[] _appxPackageNames = Array.Empty<string>();

        // AgentCompletionMonitor 瀹為檯鏀寔鐨勫閮ㄩ€氱煡鏉ユ簮銆俆apRelay 浠呮槸 PC 绔嚜韬殑娴嬭瘯鏉ユ簮銆?
        private static readonly HashSet<string> NotifiableAppKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "claude",
                "claude-haha",
                "codex",
                "trae",
                "traework",
                "zcode",
                "doubao",
                "suyan",
                "baidunetdisk",
                "quarknetdisk",
                "adrive",
                "workbuddy",
                "comfyui",
                "opencode",
                "deepseekharness",
                "freebuff",
                "leigod"
            };

        // 两个分区：已安装（彩色、可点击切换启用/禁用）与已适配但未安装（灰色、可折叠）。
        // 每个分区 = 标题行 + 芯片网格容器 + 独立垂直滚动条，超过可视行数时滚动查看。
        private readonly AppSectionPanel _installedGroup = new AppSectionPanel();
        private readonly Label _installedHeader = new Label();
        private readonly Panel _installedChips = new Panel();
        private readonly ThemedScrollBar _installedScroll = new ThemedScrollBar();

        private readonly AppSectionPanel _uninstalledGroup = new AppSectionPanel();
        private readonly Label _uninstalledHeader = new Label();
        private readonly Panel _uninstalledChips = new Panel();
        private readonly ThemedScrollBar _uninstalledScroll = new ThemedScrollBar();
        private bool _uninstalledCollapsed = true;

        // EnsureGridHeight 内部会赋值 Height，进而触发 OnResize -> EnsureGridHeight。
        // 用该标志把重排折叠为一次，避免递归和布局抖动。
        private bool _layingOut;

        private readonly ToolTip _chipToolTip = new ToolTip();
        private readonly Label _hintLabel = new Label();

        private Color _sectionSurfaceColor = Color.FromArgb(247, 250, 249);
        private Color _chipSurfaceColor = Color.White;
        private Color _textPrimary = Color.FromArgb(25, 48, 42);
        private Color _textSecondary = Color.FromArgb(101, 118, 111);
        private Color _borderColor = Color.FromArgb(216, 228, 222);
        private Color _accentColor = Color.FromArgb(111, 134, 126);

        // 图标芯片布局常量：紧凑尺寸，保留 56px 高度的稳定点击目标，
        // 同时在默认窗口宽度下完整容纳八列，避免呈现为启动器式的大图标墙。
        private const int ChipWidth = 88;
        private const int ChipHeight = 56;
        private const int IconNameGap = 3;
        private const int ChipGapX = 0;
        private const int ChipGapY = 8;
        private const float AppNameFontSize = 8.2F;
        private const int AppsRowLeft = 12;
        private const int ContentTopInset = 4;
        private const int MinPerRow = 4;
        private const int MaxPerRow = 8;
        // 每个分区最多同时显示的行数；超出部分通过分区内垂直滚动查看。
        private const int MaxVisibleRows = 2;
        // 分区标题行使用固定高度，让文字可在标题带中垂直居中，并与上方分隔线留出安全距离。
        private const int GroupHeaderHeight = 30;
        private const int GroupGap = 12;
        private const int HintTopGap = 10;
        private const int ContentBottomInset = 10;
        // 高度预算不足时，单个分区允许压到的最小可视高度。取半行多一点：
        // 能看到图标上半部分，用户可判断"这里还有内容、需要滚动"。
        // 不允许再降到 0，否则分区会变成一条只有滚动条的空行。
        private const int MinRowVisible = 32;

        // 外层允许本面板占用的最大高度。MainForm 在重排时按"运行日志区最小高度"
        // 反推该值，面板据此收缩每个分区的可见行数并改用分区内滚动，
        // 保证展开未安装区或窗口变矮时都不会把运行日志顶出可视范围。
        private int _maxContentHeight = int.MaxValue;

        /// <summary>
        /// 除芯片行以外的固定开销：上边距 + 两个分区标题 + 分区间距 + 末行提示。
        /// 必须读取控件实测高度而不是写死数字：字体行高随系统 DPI 与字体版本变化，
        /// 写死会让本面板与 <see cref="MinContentHeight"/> 对不上，进而算错外层预留空间。
        /// </summary>
        private int ChromeHeight =>
            ContentTopInset + GroupHeaderHeight * 2 + GroupGap +
            HintTopGap + _hintLabel.Height + ContentBottomInset;

        /// <summary>
        /// 已安装分区在不裁剪前提下希望占用的芯片高度：按当前图标数推出的行数
        /// （至多 <see cref="MaxVisibleRows"/> 行）折算，空分区仍保留一段最小可视区。
        /// 图标数变化后本值随之变化，MainForm 会据此重算窗口最小高度。
        /// </summary>
        private int InstalledPreferredHeight
        {
            get
            {
                // 计算可用宽度（扣除左右边距）
                var availableWidth = Math.Max(1, Width - 2 * AppsRowLeft);
                return Math.Max(
                    MinRowVisible,
                    Math.Min(ComputeRows(_installedChips.Controls.Count, availableWidth), MaxVisibleRows) * (ChipHeight + ChipGapY));
            }
        }

        /// <summary>
        /// 本面板在最压缩状态下仍需要的高度：固定开销 + 已安装分区完整可见所需高度
        /// + 展开时未安装分区的一段最小可视区。已安装分区改为按完整行数预留（而不是一段
        /// 最小可视区），因此窗口最小高度足以让已安装应用的整行图标完整展示；未安装分区
        /// 在折叠时只保留标题行，展开后才预留 <see cref="MinRowVisible"/> 并在分区内滚动。
        /// </summary>
        public int MinContentHeight => ChromeHeight + InstalledPreferredHeight +
            (_uninstalledCollapsed ? 0 : MinRowVisible);

        /// <summary>
        /// 外层给定的高度上限（像素）。设置后立即重排；值未变化时不触发重排，
        /// 避免与 MainForm 的 <c>PreferredHeightChanged</c> 回调形成无限递归。
        /// </summary>
        public int MaxContentHeight
        {
            get => _maxContentHeight;
            set
            {
                // 下限取 MinContentHeight：外层给的上限再小也不能低于面板的压缩底线，
                // 否则分区会被裁掉而不是转为滚动。
                var clamped = Math.Max(MinContentHeight, value);
                if (_maxContentHeight == clamped)
                {
                    return;
                }

                _maxContentHeight = clamped;
                EnsureGridHeight();
            }
        }

        /// <summary>
        /// 宸查€夐€氱煡搴旂敤闆嗗悎鍙樺寲鏃惰Е鍙戯紝鍙傛暟涓哄綋鍓嶅惎鐢ㄧ殑搴旂敤鏍囪瘑鍒楄〃銆?
        /// AgentCompletionMonitor 鎹鍔ㄦ€佽皟鏁寸洃鍚洰褰曘€?
        /// </summary>
        public event Action<IReadOnlyList<string>>? EnabledAppsChanged;

        /// <summary>
        /// 面板因图标换行导致自身高度变化时触发，参数为新的建议高度（像素）。
        /// MainForm 据此调整通知卡片与其下方运行日志区域的位置。
        /// </summary>
        public event Action<int>? PreferredHeightChanged;

        public NotificationAppsPanel()
        {
            _disabledKeys = LoadDisabledKeys();
            SetStyle(
                ControlStyles.UserPaint |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
                true);
            DoubleBuffered = true;
            BuildLayout();
            _chipToolTip.UseAnimation = false;
            _chipToolTip.UseFading = false;
            _chipToolTip.AutomaticDelay = 300;
            _chipToolTip.InitialDelay = 400;
            _chipToolTip.ReshowDelay = 100;
        }

        private void BuildLayout()
        {
            _hintLabel.AutoSize = true;
            _hintLabel.Font = new Font("Microsoft YaHei UI", 8.5F);
            _hintLabel.ForeColor = _textSecondary;
            _hintLabel.Text = "点击图标启停通知";

            BuildGroup(_installedGroup, _installedHeader, _installedChips, _installedScroll,
                "已安装应用", false);
            BuildGroup(_uninstalledGroup, _uninstalledHeader, _uninstalledChips, _uninstalledScroll,
                "▸ 已适配应用（未安装）", true);

            Controls.Add(_hintLabel);
            Controls.Add(_installedGroup);
            Controls.Add(_uninstalledGroup);
            _installedScroll.BringToFront();
            _uninstalledScroll.BringToFront();
            _hintLabel.BringToFront();

            EnsureGridHeight();
        }

        /// <summary>
        /// 构建一个分区：标题行 + 芯片网格容器 + 独立垂直滚动条。
        /// 可折叠分区（uninstalled）标题行点击切换展开/折叠。
        /// </summary>
        private void BuildGroup(AppSectionPanel group, Label header, Panel chips, ThemedScrollBar scroll,
            string title, bool collapsible)
        {
            group.AutoSize = false;
            group.BackColor = _sectionSurfaceColor;
            group.BorderColor = _borderColor;
            group.AccentColor = collapsible ? _textSecondary : _accentColor;
            group.Padding = new Padding(0);

            header.AutoSize = false;
            header.Text = title;
            header.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
            header.ForeColor = _textPrimary;
            header.BackColor = Color.Transparent;
            header.TextAlign = ContentAlignment.MiddleLeft;
            header.Cursor = collapsible ? Cursors.Hand : Cursors.Default;
            if (collapsible)
            {
                header.Click += (_, __) => ToggleUninstalledCollapse();
            }
            group.Controls.Add(header);

            chips.AutoSize = false;
            chips.BackColor = Color.Transparent;
            group.Controls.Add(chips);

            scroll.Width = ScrollBarRenderer.PreferredWidth;
            scroll.SmallChange = ChipGapY + ChipHeight;
            scroll.LargeChange = ChipGapY + ChipHeight;
            scroll.Maximum = 0;
            scroll.Visible = false;
            scroll.Scroll += (_, __) => ApplyChipsClip(chips, scroll.Value, chips.Tag is int h ? h : chips.Height);
            group.Controls.Add(scroll);
        }

        /// <summary>
        /// 按当前滚动位置放置芯片容器并裁剪可视区域。裁剪矩形必须随滚动量下移
        /// （容器坐标系随控件一起上移），否则滚动后内容会越过分区标题行。
        /// </summary>
        private static void ApplyChipsClip(Panel chips, int scrollValue, int visibleHeight)
        {
            chips.Location = new Point(AppsRowLeft, GroupHeaderHeight - scrollValue);
            chips.Region = new Region(new Rectangle(
                0,
                scrollValue,
                Math.Max(1, chips.Width),
                Math.Max(1, visibleHeight)));
        }

        private void ToggleUninstalledCollapse()
        {
            // 标题（含折叠箭头）统一由 EnsureGridHeight 生成，这里只翻转状态，
            // 避免两处都拼接箭头导致重复前缀。
            _uninstalledCollapsed = !_uninstalledCollapsed;
            EnsureGridHeight();
        }

        /// <summary>
        /// 重排两个分区。可用于放芯片的高度是一个预算：
        /// <see cref="MaxContentHeight"/> 扣除标题行和间距后的余量，
        /// 优先分给已安装区（用户主要操作对象），剩余给展开的未安装区；
        /// 任一分区拿到的高度低于其内容高度时，该分区内部出现垂直滚动条。
        /// 通过 <see cref="PreferredHeightChanged"/> 把最终高度通知外层。
        /// </summary>
        private void EnsureGridHeight()
        {
            if (_layingOut)
            {
                return;
            }

            _layingOut = true;
            try
            {
                EnsureGridHeightCore();
            }
            finally
            {
                _layingOut = false;
            }
        }

        private void EnsureGridHeightCore()
        {
            var rowHeight = ChipHeight + ChipGapY;
            // 计算可用宽度（扣除左右边距）
            var availableWidth = Math.Max(1, Width - 2 * AppsRowLeft);
            var installedRows = ComputeRows(_installedChips.Controls.Count, availableWidth);
            var uninstalledRows = _uninstalledCollapsed
                ? 0
                : ComputeRows(_uninstalledChips.Controls.Count, availableWidth);

            // 可分给芯片行的高度预算 = 上限扣掉固定开销。下限是"每个可见分区一段
            // 最小可视区"，与 MinContentHeight 同一套算法，保证面板永不超出上限。
            var visibleGroups = uninstalledRows > 0 ? 2 : 1;
            var budget = Math.Max(MinRowVisible * visibleGroups, _maxContentHeight - ChromeHeight);

            var installedWanted = Math.Min(installedRows, MaxVisibleRows) * rowHeight;
            var uninstalledWanted = Math.Min(uninstalledRows, MaxVisibleRows) * rowHeight;

            // 预算不足时先压缩未安装区，再压缩已安装区，各自压到 MinRowVisible 为止。
            // 压缩后分区内容高度大于可视高度，LayoutGrid 会自动显示分区滚动条。
            var installedVisibleHeight = installedWanted;
            var uninstalledVisibleHeight = uninstalledWanted;
            var overflow = installedWanted + uninstalledWanted - budget;
            if (overflow > 0 && uninstalledVisibleHeight > 0)
            {
                var reduce = Math.Min(overflow, Math.Max(0, uninstalledVisibleHeight - MinRowVisible));
                uninstalledVisibleHeight -= reduce;
                overflow -= reduce;
            }
            if (overflow > 0 && installedVisibleHeight > MinRowVisible)
            {
                installedVisibleHeight -= Math.Min(overflow, installedVisibleHeight - MinRowVisible);
            }

            var installedTop = ContentTopInset;
            _installedGroup.Location = new Point(0, installedTop);
            _installedGroup.Size = new Size(Width, GroupHeaderHeight + installedVisibleHeight);
            _installedHeader.Size = new Size(Math.Max(1, Width - AppsRowLeft), GroupHeaderHeight);
            _installedHeader.Location = new Point(AppsRowLeft, 0);
            _installedHeader.Text = BuildGroupTitle("\u5df2\u5b89\u88c5\u5e94\u7528", _installedChips.Controls.Count);
            _installedChips.Size = new Size(Width - 2 * AppsRowLeft, installedVisibleHeight);
            LayoutGrid(_installedChips, _installedScroll, installedRows, installedVisibleHeight, false);

            var uninstalledTop = _installedGroup.Bottom + GroupGap;
            _uninstalledGroup.Location = new Point(0, uninstalledTop);
            _uninstalledGroup.Size = new Size(Width, GroupHeaderHeight + uninstalledVisibleHeight);
            _uninstalledHeader.Size = new Size(Math.Max(1, Width - AppsRowLeft), GroupHeaderHeight);
            _uninstalledHeader.Location = new Point(AppsRowLeft, 0);
            _uninstalledHeader.Text = BuildGroupTitle(
                (_uninstalledCollapsed ? "\u25b8 " : "\u25be ") + "\u5df2\u9002\u914d\u5e94\u7528\uff08\u672a\u5b89\u88c5\uff09",
                _uninstalledChips.Controls.Count);
            _uninstalledChips.Size = new Size(Width - 2 * AppsRowLeft, Math.Max(1, uninstalledVisibleHeight));
            _uninstalledChips.Visible = !_uninstalledCollapsed;
            _uninstalledScroll.Visible = !_uninstalledCollapsed;
            if (!_uninstalledCollapsed)
            {
                LayoutGrid(_uninstalledChips, _uninstalledScroll, uninstalledRows, uninstalledVisibleHeight, true);
            }

            _hintLabel.Location = new Point(AppsRowLeft, _uninstalledGroup.Bottom + HintTopGap);
            _hintLabel.MaximumSize = new Size(
                Math.Max(80, Width - 2 * AppsRowLeft),
                0);

            var height = _hintLabel.Bottom + ContentBottomInset;
            if (Height != height)
            {
                Height = height;
            }
            PreferredHeightChanged?.Invoke(height);
            Invalidate();
        }

        private static string BuildGroupTitle(string baseTitle, int count)
        {
            return baseTitle + " " + count;
        }

        private static int ComputeRows(int count, int availableWidth = int.MaxValue)
        {
            if (count <= 0) return 0;
            var columns = BalancedColumns(count, MaxPerRow, availableWidth);
            return (int)Math.Ceiling(count / (double)columns);
        }

        /// <summary>
        /// 分区网格布局：先定行数再回推列数（均衡列数）。<paramref name="leftAlign"/>
        /// 为 false 时每行图标在整行宽度内平均分布（已安装区撑满卡片）；为 true 时
        /// 从左到右紧凑排布（未安装区，避免一两个图标被稀疏拉开）。
        /// <paramref name="allottedHeight"/> 是外层分配给本分区的可视高度；内容超出时
        /// 显示垂直滚动条，并通过 Region 裁剪可视区域。
        /// </summary>
        private void LayoutGrid(Panel chips, ThemedScrollBar scroll, int rows, int allottedHeight, bool leftAlign)
        {
            var count = chips.Controls.Count;
            // 预留细滚动条宽度，避免芯片被滑块挡住；对应素言 scrollbar-gutter:
            // stable —— 无论内容是否溢出都恒定预留，内容区宽度不因滚动条出现/
            // 消失而左右跳动。
            var scrollWidth = ScrollBarRenderer.PreferredWidth;
            const int scrollGap = 4;
            var scrollReserve = scrollWidth + scrollGap;
            var usableWidth = Math.Max(ChipWidth, chips.Width - scrollReserve);
            // 列数按预留后的内容区宽度计算（而非全宽），避免滚动条槽位存在时
            // 芯片在临界宽度下相互重叠。
            var columns = BalancedColumns(count, MaxPerRow, usableWidth);
            // 根据实际列数重新计算行数
            var actualRows = count > 0 ? (int)Math.Ceiling(count / (double)columns) : 0;
            var innerWidth = leftAlign
                ? Math.Max(columns * (ChipWidth + ChipGapX), usableWidth)
                : usableWidth;
            var contentHeight = Math.Max(1, actualRows) * (ChipHeight + ChipGapY);
            var visibleHeight = Math.Max(1, Math.Min(contentHeight, allottedHeight));

            // 裁剪高度随滚动位置变化，供 scroll 事件复用，避免重复计算。
            chips.Tag = visibleHeight;
            chips.Size = new Size(Math.Max(innerWidth, chips.Width), contentHeight);

            for (var r = 0; r < actualRows; r++)
            {
                var start = r * columns;
                var inRow = Math.Min(columns, count - start);
                if (inRow <= 0) break;
                var cellWidth = (double)usableWidth / inRow;
                for (var j = 0; j < inRow; j++)
                {
                    var chip = chips.Controls[start + j];
                    var x = leftAlign
                        ? j * (ChipWidth + ChipGapX)
                        : (int)Math.Round(j * cellWidth + (cellWidth - ChipWidth) / 2.0);
                    chip.Location = new Point(Math.Max(0, x), r * (ChipHeight + ChipGapY));
                }
            }

            // 是否需要滚动完全由"内容高度是否超过分配到的可视高度"决定，
            // 不再用固定行数阈值判断，否则高度被压缩后会出现内容被裁掉且无法滚动。
            if (contentHeight > visibleHeight)
            {
                scroll.Visible = true;
                scroll.Location = new Point(Math.Max(0, groupContentWidth(chips) - scrollWidth), GroupHeaderHeight);
                scroll.Size = new Size(scrollWidth, visibleHeight);
                scroll.LargeChange = Math.Max(1, visibleHeight);
                scroll.SmallChange = ChipGapY + ChipHeight;
                scroll.Maximum = Math.Max(1, contentHeight - visibleHeight);
                scroll.Value = Math.Min(scroll.Value, scroll.Maximum);
            }
            else
            {
                scroll.Visible = false;
                scroll.Value = 0;
            }
            scroll.BringToFront();
            ApplyChipsClip(chips, scroll.Value, visibleHeight);
        }

        private int groupContentWidth(Panel chips)
        {
            // chips 挂在分区 group 上；滚动条与 chips 同父，按父宽贴右。
            return chips.Parent?.ClientSize.Width > 0
                ? chips.Parent.ClientSize.Width
                : chips.Width;
        }

        /// <summary>
        /// 均衡列数：n 个图标、每行最多 max 个，先定行数再回推列数，避免"第一行 8 个、第二行 1 个"。
        /// 9 -> 2 行 x 5 列 (5+4)；17 -> 3 行 x 6 列 (6+6+5)；8 -> 1 行 x 8 列。
        /// 如果提供了 availableWidth，还会根据可用宽度限制最大列数，确保图标不会溢出。
        /// </summary>
        private static int BalancedColumns(int n, int max = MaxPerRow, int availableWidth = int.MaxValue)
        {
            if (n <= 0) return 1;
            var rows = (int)Math.Ceiling(n / (double)max);
            var columns = (int)Math.Ceiling(n / (double)rows);
            // 如果指定了可用宽度，限制列数不超过可容纳的列数
            if (availableWidth < int.MaxValue && ChipWidth > 0)
            {
                var maxFitColumns = Math.Max(1, availableWidth / ChipWidth);
                columns = Math.Min(columns, maxFitColumns);
            }
            return columns;
        }

        // 外层卡片负责承载整个功能区；分区只用细线和短色线提示层级，
        // 不再嵌套第二层圆角卡片。

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // 宽度变化时重新按列均分图标；高度变化由 EnsureGridHeight 自己驱动，
            // 用 _layingOut 阻断 Height 赋值引发的二次进入。
            if (_layingOut)
            {
                return;
            }

            EnsureGridHeight();
        }

        /// <summary>
        /// 返回内置应用的 PNG 图标字节，供 HTTP /apps/icon/&lt;appKey&gt; 端点使用。
        /// 当应用未通过 API 注册（即 <c>NotificationAppRegistry.TryGetIcon</c> 返回 false）
        /// 时作为回退。仅支持 <see cref="AppCatalogMetadata.BuiltInKeys"/> 中的内置键。
        /// </summary>
        internal static bool TryGetBuiltInIconBytes(string appKey, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            var fileName = appKey.ToLowerInvariant() switch
            {
                "claude" => "ic_claude_logo.png",
                "claude-haha" => "ic_claude_haha_logo.png",
                "codex" => "ic_chatgpt_logo.png",
                "chatgpt" => "ic_chatgpt_logo.png",
                "trae" => "ic_trae_logo.png",
                "traework" => "ic_trae_solo_logo.png",
                "zcode" => "ic_zcode_logo.png",
                "doubao" => "ic_doubao_logo.png",
                "suyan" => "ic_suyan_logo.png",
                "baidunetdisk" => "ic_baidunetdisk_logo.png",
                "quarknetdisk" => "ic_quarknetdisk_logo.png",
                "adrive" => "ic_adrive_logo.png",
                "workbuddy" => "ic_workbuddy_logo.png",
                "comfyui" => "ic_comfyui_logo.png",
                "opencode" => "ic_opencode_logo.png",
                "deepseekharness" => "ic_deepseekharness_logo.png",
                "freebuff" => "ic_freebuff_logo.png",
                "leigod" => "ic_leigod_logo.png",
                _ => string.Empty
            };
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return false;
            }

            try
            {
                var resourceName = Assembly.GetExecutingAssembly()
                    .GetManifestResourceNames()
                    .FirstOrDefault(name => name.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(resourceName))
                {
                    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
                    if (stream != null)
                    {
                        using var source = Image.FromStream(stream);
                        using var ms = new MemoryStream();
                        source.Save(ms, ImageFormat.Png);
                        bytes = ms.ToArray();
                        return bytes.Length > 0;
                    }
                }

                var path = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
                if (!File.Exists(path)) return false;
                using var fileSource = Image.FromFile(path);
                using var fileMs = new MemoryStream();
                fileSource.Save(fileMs, ImageFormat.Png);
                bytes = fileMs.ToArray();
                return bytes.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Returns public metadata for every built-in application. The mobile
        /// registry must not depend on the optional user-supplied registry: the
        /// built-in catalog is the source of truth for all supported agents.
        /// </summary>
        internal static IReadOnlyList<RegisteredAppDefinition> GetBuiltInAppDefinitions()
        {
            return AppCatalogMetadata.BuiltInKeys
                .Select(key => new RegisteredAppDefinition
                {
                    AppKey = key,
                    DisplayName = GetDisplayName(key),
                    BrandColor = ToHexColor(GetBuiltInBrandColor(key))
                })
                .ToArray();
        }

        private static string ToHexColor(Color color) =>
            $"#{color.R:X2}{color.G:X2}{color.B:X2}";

        private static Color GetBuiltInBrandColor(string key)
        {
            return (key ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "claude" => Color.FromArgb(217, 119, 87),
                "claude-haha" => Color.FromArgb(217, 119, 87),
                "codex" => Color.FromArgb(16, 185, 129),
                "trae" => Color.FromArgb(59, 130, 246),
                "traework" => Color.FromArgb(245, 158, 11),
                "zcode" => Color.FromArgb(236, 72, 153),
                "doubao" => Color.FromArgb(245, 101, 72),
                "suyan" => Color.FromArgb(148, 163, 184),
                "baidunetdisk" => Color.FromArgb(54, 119, 246),
                "quarknetdisk" => Color.FromArgb(78, 110, 242),
                "adrive" => Color.FromArgb(255, 143, 45),
                "workbuddy" => Color.FromArgb(99, 81, 241),
                "comfyui" => Color.FromArgb(242, 255, 89),
                "opencode" => Color.FromArgb(244, 244, 245),
                "deepseekharness" => Color.FromArgb(77, 107, 254),
                "freebuff" => Color.Black,
                "leigod" => Color.FromArgb(255, 122, 0),
                _ => Color.FromArgb(148, 163, 184)
            };
        }

        private sealed class AppSectionPanel : Panel
        {
            public Color BorderColor { get; set; } = Color.FromArgb(198, 214, 206);
            public Color AccentColor { get; set; } = Color.FromArgb(111, 134, 126);

            public AppSectionPanel()
            {
                SetStyle(
                    ControlStyles.UserPaint |
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.ResizeRedraw,
                    true);
                TabStop = false;
            }

            public void ApplyTheme(Color surface, Color border, Color accent)
            {
                BackColor = surface;
                BorderColor = border;
                AccentColor = accent;
                Invalidate();
            }

            protected override void OnResize(EventArgs eventargs)
            {
                base.OnResize(eventargs);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

                using (var dividerPen = new Pen(BorderColor, 1f))
                {
                    e.Graphics.DrawLine(dividerPen, 0, 0, Math.Max(0, Width - 1), 0);
                }

                using (var accentBrush = new SolidBrush(AccentColor))
                {
                    var accentTop = Math.Max(4, (GroupHeaderHeight - 14) / 2);
                    e.Graphics.FillRectangle(
                        accentBrush,
                        new Rectangle(0, accentTop, 3, Math.Min(14, Math.Max(1, Height - accentTop))));
                }
            }
        }

        /// <summary>
        /// 应用主题颜色。
        /// </summary>
        /// <param name="surface">面板自身底色，与外层卡片标题带和内容区保持统一。</param>
        /// <param name="chipSurface">图标块底色，需与 <paramref name="surface"/> 有轻微色差以形成层次。</param>
        public void ApplyTheme(
            Color surface,
            Color chipSurface,
            Color textPrimary,
            Color textSecondary,
            Color border,
            Color accent,
            Color accentHover)
        {
            _textPrimary = textPrimary;
            _textSecondary = textSecondary;
            _borderColor = border;
            _accentColor = accent;
            _sectionSurfaceColor = surface;
            BackColor = surface;
            _chipSurfaceColor = chipSurface;
            Invalidate();

            _hintLabel.ForeColor = textSecondary;
            _installedHeader.ForeColor = textPrimary;
            _uninstalledHeader.ForeColor = textPrimary;
            _installedGroup.ApplyTheme(_sectionSurfaceColor, border, accent);
            _uninstalledGroup.ApplyTheme(_sectionSurfaceColor, border, textSecondary);

            // 轨道与滑块共用 muted 语义色，由渲染器按交互状态控制透明度与宽度。
            // 不用强调色，避免像一根彩色进度条。
            var thumb = textSecondary;
            var track = textSecondary;
            _installedScroll.BackColor = _sectionSurfaceColor;
            _uninstalledScroll.BackColor = _sectionSurfaceColor;
            _installedScroll.ApplyTheme(thumb, track);
            _uninstalledScroll.ApplyTheme(thumb, track);

            ApplyThemeChips(_installedChips);
            ApplyThemeChips(_uninstalledChips);

        }

        private static Color Blend(Color first, Color second, float secondWeight)
        {
            var weight = Math.Max(0f, Math.Min(1f, secondWeight));
            return Color.FromArgb(
                (int)Math.Round(first.R * (1f - weight) + second.R * weight),
                (int)Math.Round(first.G * (1f - weight) + second.G * weight),
                (int)Math.Round(first.B * (1f - weight) + second.B * weight));
        }

        private void ApplyThemeChips(Panel chips)
        {
            foreach (Control? c in chips.Controls)
            {
                if (c is AppChip chip)
                {
                    chip.ApplyTheme(_chipSurfaceColor, _textPrimary, _textSecondary, _borderColor);
                }
            }
        }
        /// <summary>
        /// 鎵归噺娣诲姞宸插惎鐢ㄥ簲鐢ㄣ€傚垵濮嬬姸鎬佸叏閮ㄥ惎鐢紙褰╄壊鍥炬爣锛夈€?
        /// </summary>
        public void SetEnabledApps(IEnumerable<AppEntry> apps)
        {
            _detectedApps.Clear();
            foreach (var app in apps ?? Array.Empty<AppEntry>())
            {
                if (app == null || !IsNotifiableAppKey(app.Key))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(app.RootPath))
                {
                    AppInstallRoots.Set(app.Key, app.RootPath);
                }

                // 检测到的应用记为已安装；是否启用由 RenderFullCatalog 依据 _disabledKeys 决定。
                _detectedApps[app.Key] = app;
            }
            RenderFullCatalog();
            EnabledAppsChanged?.Invoke(new List<string>(_enabledApps.Keys));
        }

        /// <summary>
        /// Adds or refreshes applications registered through /apps/register without
        /// dropping the applications already shown by automatic detection.
        /// </summary>
        public void AddRegisteredApps(IEnumerable<RegisteredAppDefinition> apps)
        {
            // 动态注册只提供显示名称、图标和品牌色，不等于本机已经安装。
            // 以前把它们塞进 _detectedApps，会让卸载后仍保留在注册表的应用被
            // 直接归入“已安装”分区。保留当前自动检测快照，只重新按真实安装
            // 证据渲染目录。
            RenderFullCatalog();
            EnabledAppsChanged?.Invoke(new List<string>(_enabledApps.Keys));
        }

        /// <summary>
        /// 构建并渲染完整已适配应用目录：内置 13 个键 + 远程目录注册的额外键。
        /// 已安装的应用渲染到已安装分区（彩色、可点击切换启用/禁用），
        /// 未安装的应用渲染到未安装分区（灰色、点击提示"未检测到安装"，默认折叠）。
        /// </summary>
        public void RenderFullCatalog()
        {
            var keys = new List<string>();
            foreach (var key in AppCatalogMetadata.BuiltInKeys)
            {
                if (!keys.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    keys.Add(key);
                }
            }
            foreach (var app in NotificationAppRegistry.GetAll())
            {
                var key = (app.AppKey ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(key) && !keys.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    keys.Add(key);
                }
            }

            _installedChips.Controls.Clear();
            _uninstalledChips.Controls.Clear();
            _enabledApps.Clear();

            // 已安装分区在前，未安装分区在后。
            var installed = new List<string>();
            var uninstalled = new List<string>();
            foreach (var key in keys)
            {
                if (!IsInstalled(key))
                {
                    uninstalled.Add(key);
                }
                else
                {
                    installed.Add(key);
                }
            }

            // 已安装分区在前，未安装分区在后。
            var ordered = installed.Concat(uninstalled);
            foreach (var key in ordered)
            {
                if (!IsNotifiableAppKey(key))
                {
                    continue;
                }

                // _detectedApps 只是本次扫描的候选快照，不能作为“已安装”本身；
                // 日志目录、动态注册记录都可能在卸载后残留，最终必须经过
                // IsInstalled 的当前进程/程序文件/卸载注册表校验。
                var installedHere = IsInstalled(key);
                var detectedRoot = _detectedApps.TryGetValue(key, out var detected) && detected != null
                    ? detected.RootPath
                    : string.Empty;
                var app = new AppEntry(key, GetDisplayName(key), detectedRoot);
                var enabled = installedHere && !_disabledKeys.Contains(key);
                if (enabled)
                {
                    _enabledApps[key] = app;
                }

                var chip = new AppChip(app)
                {
                    Margin = new Padding(0, 0, ChipGapX, ChipGapY)
                };
                chip.ApplyTheme(_chipSurfaceColor, _textPrimary, _textSecondary, _borderColor);
                var tooltip = AppCatalogMetadata.GetTooltipText(key);
                if (!string.IsNullOrWhiteSpace(tooltip))
                {
                    _chipToolTip.SetToolTip(chip, tooltip);
                }

                if (installedHere)
                {
                    chip.SetMode(AppChipMode.Installed, enabled);
                    chip.Click += (_, __) => ToggleApp(app.Key);
                    _installedChips.Controls.Add(chip);
                }
                else
                {
                    chip.SetMode(AppChipMode.NotInstalled, false);
                    chip.Click += (_, __) =>
                        _hintLabel.Text = "\u672a\u68c0\u6d4b\u5230\u5b89\u88c5\uff0c\u65e0\u6cd5\u542f\u7528";
                    _uninstalledChips.Controls.Add(chip);
                }
            }

            EnsureGridHeight();
        }

        /// <summary>
        /// 判定某 appKey 在本机是否处于"已安装且位于正确位置"。仅做定点检查，
        /// 不做全盘扫描（符合 R11），路径信息复用各数据定位器与远程目录安装根。
        /// </summary>
        private bool IsInstalled(string appKey)
        {
            return HasInstallEvidence(appKey);
        }

        /// <summary>
        /// 按同一套"当前安装证据"标准（进程/程序文件/CLI shim/APPX/卸载注册表项）
        /// 判定 appKey 是否已安装。静态无 UI 依赖，供通知门控在运行期复核：
        /// 启用集只是启动/上次自动检测的快照，期间新安装的应用不该被陈旧快照拦截。
        /// </summary>
        internal static bool HasInstallEvidence(string appKey)
        {
            var key = (appKey ?? string.Empty).Trim().ToLowerInvariant();
            if (key.Length == 0 || !IsNotifiableAppKey(key))
            {
                return false;
            }

            var localAppData = AgentLogPaths.GetLocalApplicationDataRoot();
            var appData = AgentLogPaths.GetApplicationDataRoot();
            var userProfile = AgentLogPaths.GetUserProfileRoot();
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            // 重要：日志目录和 notification-app-registry.json 都是可长期保留的
            // 用户数据，不能单独证明软件仍在电脑上。这里仅接受当前进程、程序文件、
            // 有效卸载注册表项或 CLI 命令 shim 这几类“当前安装证据”。
            switch (key)
            {
                case "claude":
                    return HasDesktopInstallSignal(key, userProfile, appData, localAppData, programFiles,
                        "claude", "Claude", "Claude Code");
                case "claude-haha":
                    return HasDesktopInstallSignal(key, userProfile, appData, localAppData, programFiles,
                        "claude-haha", "Claude Code Haha", "Claude Code Haha");
                case "codex":
                    return HasDesktopInstallSignal(key, userProfile, appData, localAppData, programFiles,
                        "ChatGPT", "Codex", "OpenAI.Codex");
                case "trae":
                    return IsTraeInstalled(false);
                case "traework":
                    return IsTraeInstalled(true);
                case "zcode":
                    return HasDesktopInstallSignal(key, userProfile, appData, localAppData, programFiles,
                        "zcode", "ZCode");
                case "doubao":
                    return HasDesktopInstallSignal(key, userProfile, appData, localAppData, programFiles,
                        "Doubao", "豆包") ||
                           HasExecutableInRoot(Path.Combine(localAppData, "Doubao", "Application", "app"),
                               "Doubao.exe");
                case "suyan":
                    return SuyanAppLocator.IsInstalled();
                case "baidunetdisk":
                    return HasDesktopInstallSignal(key, userProfile, appData, localAppData,
                        programFiles, "BaiduNetdisk", "百度网盘");
                case "quarknetdisk":
                    return HasDesktopInstallSignal(key, userProfile, appData, localAppData,
                        programFiles, "QuarkCloudDrive", "夸克网盘");
                case "adrive":
                    return HasDesktopInstallSignal(key, userProfile, appData, localAppData,
                        programFiles, "aDrive", "阿里云盘");
                case "workbuddy":
                    return HasDesktopInstallSignal(key, userProfile, appData, localAppData,
                        programFiles, "WorkBuddy");
                case "opencode":
                    return HasDesktopInstallSignal(key, userProfile, appData, localAppData,
                        programFiles, "OpenCode") ||
                           HasAnyExecutableInRoot(Path.Combine(localAppData, "Programs", "@opencode"));
                case "deepseekharness":
                    return HasDesktopInstallSignal(key, userProfile, appData, localAppData,
                        programFiles, "DeepSeek Harness Desktop", "DeepSeek Harness",
                        "DeepSeek Harness Desktop.exe");
                case "freebuff":
                    return HasDesktopInstallSignal(key, userProfile, appData, localAppData,
                        programFiles, "Freebuff") || FreebuffDataLocator.IsInstalled();
                case "comfyui":
                    return DetectComfyUI(new DetectionResult(), userProfile, programFiles) ||
                           AdaptedProcessGate.IsAnyProcessRunning(new[] { "ComfyNexus", "ComfyUI" });
                case "leigod":
                    return AdaptedProcessGate.IsAnyProcessRunning(new[] { "LeiGod", "leigod" }) ||
                           HasExecutableInRoot(
                               Environment.ExpandEnvironmentVariables(@"%PROGRAMFILES(X86)%\LeiGod_Acc"),
                               "leigod.exe", "LeiGod.exe") ||
                           HasExecutableInRoot(
                               AppInstallRoots.Get(key),
                               "leigod.exe", "LeiGod.exe");
                default:
                    // 自定义应用也必须有当前可执行文件；仅剩一个卸载后的空目录
                    // 或注册表元数据不能继续显示为“已安装”。
                    return HasAnyExecutableInRoot(AppInstallRoots.Get(key));
            }
        }

        private static bool HasDesktopInstallSignal(
            string appKey,
            string userProfile,
            string appData,
            string localAppData,
            string programFiles,
            params string[] displayAndExecutableNames)
        {
            var roots = BuildTrustedInstallRoots(appKey, localAppData, programFiles);

            // 进程证据必须附带路径校验：临时目录里改名的同名 exe（例如 E2E 的
            // 进程 stub）只满足通知门控，不能冒充"已安装"证据。
            if (AdaptedProcessGate.IsRunningFromTrustedRoot(appKey, roots))
            {
                return true;
            }

            var executableNames = displayAndExecutableNames
                .Where(name => !string.IsNullOrWhiteSpace(name) &&
                               name.IndexOf('\\') < 0 && name.IndexOf('/') < 0)
                .Select(name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (HasExecutableInRoots(roots, executableNames))
            {
                return true;
            }

            // CLI 适配器常以 npm shim 存在，不一定有卸载注册表项。
            foreach (var command in displayAndExecutableNames)
            {
                var commandName = command.Replace(" ", string.Empty);
                if (HasCommandShim(commandName, userProfile, appData, localAppData))
                {
                    return true;
                }
            }

            var terms = displayAndExecutableNames
                .Where(name => name.IndexOfAny(new[] { '\\', '/' }) < 0)
                .ToArray();
            if (HasAppxPackageForTerms(terms))
            {
                return true;
            }
            return terms.Length > 0 && HasUninstallEntry(displayName =>
                terms.Any(term => displayName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        /// <summary>
        /// 汇总判定“已安装”时信任的安装根：Program Files（含 x86）、
        /// %LOCALAPPDATA%\Programs（Electron 按用户安装的默认位置），以及用户
        /// 通过「应用选择」手动指定的安装根。日志目录、注册表元数据等可残留
        /// 数据一律不进此列表。
        /// </summary>
        private static List<string?> BuildTrustedInstallRoots(
            string appKey,
            string localAppData,
            string programFiles)
        {
            return new List<string?>
            {
                programFiles,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(localAppData, "Programs"),
                AppInstallRoots.Get(appKey)
            };
        }

        private static bool HasAppxPackageForTerms(IEnumerable<string> terms)
        {
            var normalizedTerms = (terms ?? Array.Empty<string>())
                .Select(term => (term ?? string.Empty).Trim())
                .Where(term => term.Length >= 3)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (normalizedTerms.Length == 0)
            {
                return false;
            }

            var names = GetAppxPackageNames();
            return names.Any(packageName => normalizedTerms.Any(term =>
                packageName.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        private static string[] GetAppxPackageNames()
        {
            lock (AppxPackagesGate)
            {
                if (DateTime.UtcNow - _appxPackagesCheckedAtUtc < TimeSpan.FromSeconds(15))
                {
                    return _appxPackageNames;
                }

                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
                {
                    try
                    {
                        using var root = hive.OpenSubKey(
                            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
                        if (root == null) continue;
                        foreach (var name in root.GetSubKeyNames())
                        {
                            if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                        }
                    }
                    catch
                    {
                        // 某些系统会限制 AppModel 注册表读取；传统安装证据仍会继续检查。
                    }
                }

                _appxPackageNames = names.ToArray();
                _appxPackagesCheckedAtUtc = DateTime.UtcNow;
                return _appxPackageNames;
            }
        }

        private static bool HasCommandShim(string command, string userProfile, string appData, string localAppData)
        {
            var normalized = (command ?? string.Empty).Trim();
            if (normalized.Length == 0) return false;
            var roots = new[]
            {
                Path.Combine(appData, "npm"),
                Path.Combine(localAppData, "Programs"),
                Path.Combine(userProfile, ".local", "bin")
            };
            foreach (var root in roots)
            {
                if (File.Exists(Path.Combine(root, normalized)) ||
                    File.Exists(Path.Combine(root, normalized + ".cmd")) ||
                    File.Exists(Path.Combine(root, normalized + ".exe")) ||
                    File.Exists(Path.Combine(root, normalized + ".ps1")))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasExecutableInRoots(IEnumerable<string?> roots, params string[] executableNames)
        {
            foreach (var root in roots ?? Array.Empty<string?>())
            {
                if (HasExecutableInRoot(root, executableNames))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasExecutableInRoot(string? root, params string[] executableNames)
        {
            if (string.IsNullOrWhiteSpace(root) || executableNames == null || executableNames.Length == 0)
            {
                return false;
            }

            try
            {
                if (!Directory.Exists(root)) return false;
                foreach (var name in executableNames)
                {
                    if (!string.IsNullOrWhiteSpace(name) && File.Exists(Path.Combine(root, name)))
                    {
                        return true;
                    }
                }

                // Program Files / LocalAppData\Programs 下通常还有一层产品目录；
                // 只查一层，避免自动检测变成全盘扫描。
                foreach (var child in Directory.EnumerateDirectories(root))
                {
                    foreach (var name in executableNames)
                    {
                        if (!string.IsNullOrWhiteSpace(name) && File.Exists(Path.Combine(child, name)))
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // 访问受限时按无安装证据处理。
            }
            return false;
        }

        private static bool HasAnyExecutableInRoot(string? root)
        {
            if (string.IsNullOrWhiteSpace(root)) return false;
            try
            {
                if (!Directory.Exists(root)) return false;
                if (Directory.EnumerateFiles(root, "*.exe", SearchOption.TopDirectoryOnly).Any())
                {
                    return true;
                }
                return Directory.EnumerateDirectories(root)
                    .Any(child => Directory.EnumerateFiles(child, "*.exe", SearchOption.TopDirectoryOnly).Any());
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 切换应用启用状态：已启用 → 禁用（图标变灰），已禁用 → 重新启用。
        /// </summary>
        private void ToggleApp(string key)
        {
            foreach (Control? c in _installedChips.Controls)
            {
                if (c is AppChip chip && chip.App.Key == key)
                {
                    chip.SetEnabled(!chip.Enabled);
                    if (chip.Enabled)
                    {
                        _enabledApps[key] = chip.App;
                        _disabledKeys.Remove(key);
                    }
                    else
                    {
                        _enabledApps.Remove(key);
                        _disabledKeys.Add(key);
                    }
                    SaveDisabledKeys();
                    EnabledAppsChanged?.Invoke(new List<string>(_enabledApps.Keys));
                    break;
                }
            }
        }

        /// <summary>
        /// 鑷姩妫€娴嬪父瑙佸畨瑁呯洰褰曚笅鐨勭洰鏍囧簲鐢ㄣ€?
        /// 妫€娴嬭寖鍥达細Claude/ChatGPT/TraeCode CN/TraeWork CN/ZCode/璞嗗寘銆?
        /// </summary>
        public async Task RunAutoDetectAsync()
        {
            _hintLabel.Text = "正在检测…";

            var detected = await Task.Run(() => DetectInstalledApps());

            if (detected.Apps.Count == 0)
            {
                // 空结果也必须清掉上一轮快照；否则卸载软件后再次检测，
                // 旧的已安装芯片会一直留在界面和启用来源集合里。
                SetEnabledApps(Array.Empty<AppEntry>());
                _hintLabel.Text = detected.UnsupportedMessage ?? "\u672a\u68c0\u6d4b\u5230\u4efb\u4f55\u652f\u6301\u7684\u5e94\u7528\uff0c\u53ef\u4f7f\u7528\u300c\u5e94\u7528\u9009\u62e9\u300d\u624b\u52a8\u6307\u5b9a\u3002";
            }
            else
            {
                SetEnabledApps(detected.Apps);
                // \u7528\u6237\u8981\u6c42\u4e0d\u5c55\u793a"\u68c0\u6d4b\u5230 N \u4e2a\u5e94\u7528"\u7684\u7edf\u8ba1\u6587\u672c\uff0c\u68c0\u6d4b\u5b8c\u6210\u540e\u4ec5\u4fdd\u7559\u4f7f\u7528\u63d0\u793a\u3002
                _hintLabel.Text = detected.UnsupportedMessage ?? "\u70b9\u51fb\u5e94\u7528\u56fe\u6807\u53ef\u542f\u7528/\u505c\u7528\u5176\u901a\u77e5";
            }

        }

        /// <summary>
        /// 鎵弿 C 鐩樺父瑙佸畨瑁呰矾寰勶紝杩斿洖妫€娴嬪埌鐨勫簲鐢ㄥ垪琛ㄣ€?
        /// 璺緞涓?AgentCompletionMonitor.EnsureWatchersLocked 瀵归綈锛岀‘淇濊嚜鍔ㄦ娴嬪嚭鏉ョ殑搴旂敤
        /// 涓庡疄闄呯洃鍚矾寰勪竴鑷淬€?
        /// </summary>
        private static DetectionResult DetectInstalledApps()
        {
            var result = new DetectionResult();
            var localAppData = AgentLogPaths.GetLocalApplicationDataRoot();
            var userProfile = AgentLogPaths.GetUserProfileRoot();
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            bool Exists(string path) => !string.IsNullOrEmpty(path) && Directory.Exists(path);

            // Only list apps whose directories are actively watched by AgentCompletionMonitor.
            // Claude: .claude\projects or .claude\transcripts
            if (Exists(Path.Combine(userProfile, ".claude", "projects")) ||
                Exists(Path.Combine(userProfile, ".claude", "transcripts")))
            {
                result.Apps.Add(new AppEntry("claude", "Claude Code"));
            }
            // Claude Code Haha 改版：与官方 Claude Code 是独立软件，独立 appKey。
            // 上面 .claude\projects/transcripts 同时被 Haha 写事件（cc-haha-task-notification），
            // 这里检测 Haha 是否安装：默认装在 Program Files 下；不论是否安装都监听 .claude 目录。
            {
                var hahaRoot = Path.Combine(programFiles, "Claude Code Haha");
                var hahaExe = Path.Combine(hahaRoot, "Claude Code Haha.exe");
                if (File.Exists(hahaExe) || Exists(Path.Combine(userProfile, ".claude", "cc-haha")))
                {
                    result.Apps.Add(new AppEntry("claude-haha", "Claude Code Haha", hahaRoot));
                }
            }
            // ChatGPT: .codex\sessions
            if (Exists(Path.Combine(userProfile, ".codex", "sessions")))
            {
                result.Apps.Add(new AppEntry("codex", "ChatGPT"));
            }
            // Trae CN / TraeWork CN: 卸载后 %APPDATA%\...\logs 仍残留，不能作为安装证据。
            // 改用"卸载注册表项存在（卸载时会移除）+ 进程在运行"作为权威信号。
            if (IsTraeInstalled(false))
            {
                result.Apps.Add(new AppEntry("trae", "TraeCode CN"));
            }
            if (IsTraeInstalled(true))
            {
                result.Apps.Add(new AppEntry("traework", "TraeWork CN"));
            }
            // ZCode: .zcode\cli\log or .zcode\v2\logs
            if (Exists(Path.Combine(userProfile, ".zcode", "cli", "log")) ||
                Exists(Path.Combine(userProfile, ".zcode", "v2", "logs")))
            {
                result.Apps.Add(new AppEntry("zcode", "ZCode"));
            }
            // Doubao: LocalAppData\Doubao\User Data\sdk_storage\log
            if (Exists(Path.Combine(localAppData, "Doubao", "User Data", "sdk_storage", "log")))
            {
                result.Apps.Add(new AppEntry("doubao", "\u8c46\u5305"));
            }
            // WorkBuddy: installed Electron app or its per-user ACP log directory.
            var workBuddyRoot = Path.Combine(programFiles, "WorkBuddy");
            if (File.Exists(Path.Combine(workBuddyRoot, "WorkBuddy.exe")) ||
                Exists(Path.Combine(userProfile, ".workbuddy", "logs")))
            {
                result.Apps.Add(new AppEntry("workbuddy", "WorkBuddy", workBuddyRoot));
            }
            // OpenCode: shared SQLite at ~/.local/share/opencode/opencode.db (desktop + CLI).
            if (OpenCodeDataLocator.FindDatabase(userProfile).Any())
            {
                result.Apps.Add(new AppEntry("opencode", "OpenCode"));
            }
            // DeepSeek Harness: session logs under %APPDATA%\deepseek-harness-desktop\harness-home\sessions.
            if (DeepSeekHarnessDataLocator.FindRoots().Any())
            {
                result.Apps.Add(new AppEntry("deepseekharness", "DeepSeek Harness"));
            }
            // Freebuff: packaged Electron app under %LOCALAPPDATA%\Programs\@codebufffreebuff-desktop
            // (Codebuff 桌面版)，会话库位于 ~/.config/freebuff-desktop/projects/*/desktop-v2.db。
            if (FreebuffDataLocator.IsInstalled())
            {
                result.Apps.Add(new AppEntry("freebuff", "Freebuff"));
            }
            // Blacklisted applications are reported to the user but never added to the notification catalog.
            if (Exists(Path.Combine(programFiles, "QianwenApp")) ||
                Exists(Path.Combine(localAppData, "Qianwen", "User Data", "EfsReports")))
            {
                result.UnsupportedMessage = UnsupportedAppRegistry.GetMessage();
            }
            // SuYan: packaged executable, running process, or TAPRELAY_SUYAN_ROOT override.
            if (SuyanAppLocator.IsInstalled())
            {
                result.Apps.Add(new AppEntry("suyan", "\u7d20\u8a00"));
            }
            // Baidu Netdisk: standard roaming data directory or portable/install-local users directory.
            if (BaiduNetdiskDataLocator.FindDatabases().Any())
            {
                result.Apps.Add(new AppEntry("baidunetdisk", "\u767e\u5ea6\u7f51\u76d8"));
            }
            // Quark Cloud Drive: %LOCALAPPDATA%\QuarkCloudDrive\User Data\persistence\<userId>\{download,upload}.db
            if (QuarkNetdiskDataLocator.FindDatabases().Any())
            {
                result.Apps.Add(new AppEntry("quarknetdisk", "\u5938\u514b\u7f51\u76d8"));
            }
            // Aliyun Drive: AppData\aDrive\Partitions\adrive\IndexedDB\file__0.indexeddb.leveldb
            if (AdriveTransferDataLocator.FindDatabases().Any())
            {
                result.Apps.Add(new AppEntry("adrive", "\u963f\u91cc\u4e91\u76d8"));
            }

            // ComfyUI: 通用检测——扫描常见安装位置（官方便携版、ComfyNexus 改版、
            // 用户目录等），命中任一 ComfyUI 目录结构即注册到官方 "comfyui" 键。
            if (DetectComfyUI(result, userProfile, programFiles) ||
                AdaptedProcessGate.IsAnyProcessRunning(new[] { "ComfyNexus", "ComfyUI" }))
            {
                result.Apps.Add(new AppEntry("comfyui", "ComfyUI"));
            }

            // LeiGod has no task log monitor of its own, but it is a built-in
            // lifecycle source. Include it in the same auto-detect result so a
            // refresh cannot drop an installed LeiGod chip from the enabled set.
            if (HasInstallEvidence("leigod"))
            {
                result.Apps.Add(new AppEntry("leigod", "\u96f7\u795e\u52a0\u901f\u5668"));
            }

            // 动态注册的 app 只负责补充目录元数据，不作为自动检测结果；
            // RenderFullCatalog 会逐项调用 IsInstalled 再决定它进入哪一栏。
            return result;
        }

        /// <summary>
        /// 鎵撳紑鏂囦欢澶归€夋嫨瀵硅瘽妗嗭紝鐢ㄦ埛閫夋嫨搴旂敤瀹夎鐩綍鍚庡姞鍏ュ凡鍚敤鍒楄〃銆?
        /// </summary>
        /// <summary>
        /// Trae CN / TraeWork CN 是否已安装。%APPDATA%\...\logs 在卸载后仍会残留，
        /// 不能作为安装证据；这里用两个卸载时必然消失的信号：进程在运行，或
        /// Windows 卸载注册表存在对应 DisplayName 且其安装目录仍存在。
        /// </summary>
        private static bool IsTraeInstalled(bool traework)
        {
            var appKey = traework ? "traework" : "trae";
            var localAppData = AgentLogPaths.GetLocalApplicationDataRoot();
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var roots = BuildTrustedInstallRoots(appKey, localAppData, programFiles);

            // 进程证据必须附带路径校验：临时目录里改名的同名 exe（例如 E2E 的
            // 进程 stub）只满足通知门控，不能冒充"已安装"证据。
            if (AdaptedProcessGate.IsRunningFromTrustedRoot(appKey, roots))
            {
                return true;
            }

            // Electron 按用户安装默认落在 %LOCALAPPDATA%\Programs\<产品名>。
            if (traework)
            {
                if (HasExecutableInRoots(roots, "TRAE SOLO CN.exe", "TraeWork CN.exe"))
                {
                    return true;
                }
            }
            else if (HasExecutableInRoots(roots, "Trae CN.exe", "TraeCode CN.exe"))
            {
                return true;
            }

            // TraeCode CN（原 Trae CN）的 DisplayName 含 "trae" 且不含 "solo"/"work"；
            // TraeWork CN（原 Trae SOLO）含 "trae" 且含 "solo" 或 "work"。
            if (traework)
            {
                return HasUninstallEntry(dn =>
                    dn.IndexOf("trae", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (dn.IndexOf("solo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     dn.IndexOf("work", StringComparison.OrdinalIgnoreCase) >= 0));
            }

            return HasUninstallEntry(dn =>
                dn.IndexOf("trae", StringComparison.OrdinalIgnoreCase) >= 0 &&
                dn.IndexOf("solo", StringComparison.OrdinalIgnoreCase) < 0 &&
                dn.IndexOf("work", StringComparison.OrdinalIgnoreCase) < 0);
        }

        /// <summary>
        /// 扫描 HKCU/HKLM 的卸载注册表（含 WOW6432Node），判断是否存在 DisplayName
        /// 匹配且仍有实际文件证据的已安装项。不同安装器可能只写 InstallLocation、
        /// DisplayIcon 或 UninstallString，因此三者都需要校验；仅目录/字符串残留
        /// 不能单独视为已安装。
        /// </summary>
        private static bool HasUninstallEntry(Func<string, bool> displayNameMatches)
        {
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            foreach (var keyPath in new[]
            {
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
                @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            })
            {
                try
                {
                    using var root = hive.OpenSubKey(keyPath);
                    if (root == null) continue;
                    foreach (var name in root.GetSubKeyNames())
                    {
                        using var app = root.OpenSubKey(name);
                        if (app == null) continue;
                        var displayName = Convert.ToString(app.GetValue("DisplayName")) ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(displayName) || !displayNameMatches(displayName))
                        {
                            continue;
                        }

                        var install = Convert.ToString(app.GetValue("InstallLocation")) ?? string.Empty;
                        var displayIcon = Convert.ToString(app.GetValue("DisplayIcon")) ?? string.Empty;
                        var uninstall = Convert.ToString(app.GetValue("UninstallString")) ?? string.Empty;
                        if (HasLiveInstallLocation(install) ||
                            HasExistingExecutableReference(displayIcon) ||
                            HasExistingExecutableReference(uninstall))
                        {
                            return true;
                        }
                    }
                }
                catch
                {
                    // 无法访问的注册表配置单元/子键：视为无证据，不判为已安装。
                }
            }

            return false;
        }

        private static bool HasLiveInstallLocation(string? location)
        {
            if (string.IsNullOrWhiteSpace(location)) return false;
            var value = location.Trim().Trim('"');
            if (HasExistingExecutableReference(value)) return true;

            try
            {
                return Directory.Exists(value) && HasAnyExecutableInRoot(value);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasExistingExecutableReference(string? commandOrIcon)
        {
            foreach (var candidate in ExtractExecutableCandidates(commandOrIcon))
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    var name = Path.GetFileName(candidate);
                    if (string.Equals(name, "msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "rundll32.exe", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "cmd.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    return true;
                }
                catch
                {
                    // 下一种注册表格式继续尝试。
                }
            }
            return false;
        }

        private static IEnumerable<string> ExtractExecutableCandidates(string? commandOrIcon)
        {
            var value = (commandOrIcon ?? string.Empty).Trim();
            if (value.Length == 0) yield break;

            if (value[0] == '"')
            {
                var closingQuote = value.IndexOf('"', 1);
                if (closingQuote > 1)
                {
                    yield return value.Substring(1, closingQuote - 1);
                    yield break;
                }
            }

            var executableIndex = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (executableIndex >= 0)
            {
                yield return value.Substring(0, executableIndex + 4).Trim();
            }
        }

        /// <summary>
        /// 通用 ComfyUI 检测：在常见安装位置下查找具备 ComfyUI 目录结构
        /// （含 main.py / ComfyUI.exe / ComfyUI_ng.exe 且含 custom_nodes）的目录。
        /// 兼容官方便携版、ComfyNexus fork 及用户自定义安装路径。
        /// </summary>
        private static bool DetectComfyUI(DetectionResult result, string userProfile, string programFiles)
        {
            // 在给定父目录下探测常见 ComfyUI 形态：父目录自身，或其下的
            // ComfyUI / ComfyNexus / ComfyUI_windows_portable\ComfyUI 子目录。
            bool ProbeUnder(string parent)
            {
                if (string.IsNullOrWhiteSpace(parent)) return false;
                return IsComfyUIInstall(parent) ||
                       IsComfyUIInstall(Path.Combine(parent, "ComfyUI")) ||
                       IsComfyUIInstall(Path.Combine(parent, "ComfyNexus")) ||
                       IsComfyUIInstall(Path.Combine(parent, "ComfyUI_windows_portable", "ComfyUI"));
            }

            // Program Files 与用户目录下的常见位置。
            if (ProbeUnder(programFiles)) return true;
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                if (ProbeUnder(userProfile) ||
                    ProbeUnder(Path.Combine(userProfile, "Documents")) ||
                    ProbeUnder(Path.Combine(userProfile, "Downloads")) ||
                    ProbeUnder(Path.Combine(userProfile, "Desktop")))
                {
                    return true;
                }
            }

            // 各固定盘符：根目录，以及根目录下的第一层子目录（仅 depth-1 枚举，
            // 不做全盘递归，符合 R11）。用户常把便携版解压到形如
            // W:\AIphotograph\ComfyUI_windows_portable\ComfyUI 的一层子目录下。
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                var root = drive.RootDirectory.FullName;
                if (ProbeUnder(root)) return true;

                string[] topLevel;
                try
                {
                    topLevel = Directory.GetDirectories(root);
                }
                catch
                {
                    continue;
                }
                foreach (var dir in topLevel)
                {
                    if (ProbeUnder(dir)) return true;
                }
            }

            return false;
        }

        private static bool IsComfyUIInstall(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
            bool HasFile(params string[] names) => names.Any(name => File.Exists(Path.Combine(path, name)));
            return HasFile("main.py", "ComfyUI.exe", "ComfyUI_ng.exe") && Directory.Exists(Path.Combine(path, "custom_nodes"));
        }

        /// <summary>打开已适配应用的安装目录选择器，供主窗体标题行操作按钮调用。</summary>
        public void OpenApplicationSelection()
        {
            OpenFolderDialog();
        }

        private void OpenFolderDialog()
        {
            using (var dialog = new FolderBrowserDialog
            {
                Description = "閫夋嫨搴旂敤瀹夎鐩綍锛堝 Claude銆丆odex銆乀rae 绛夛級",
                ShowNewFolderButton = false
            })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    var path = dialog.SelectedPath;
                    var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
                    var key = InferAppKey(path);
                    if (UnsupportedAppRegistry.TryGetMessage(key, out var unsupportedMessage) ||
                        UnsupportedAppRegistry.LooksLikeUnsupportedPath(path))
                    {
                        _hintLabel.Text = unsupportedMessage ?? UnsupportedAppRegistry.GetMessage();
                        return;
                    }

                    if (!IsNotifiableAppKey(key))
                    {
                        _hintLabel.Text = "\u8be5\u76ee\u5f55\u4e0d\u662f\u5df2\u9002\u914d\u5e94\u7528\uff0c\u8bf7\u91cd\u9009\u3002";
                        return;
                    }

                    AppInstallRoots.Set(key, path);
                    var entry = new AppEntry(key, GetDisplayName(key), path);
                    var list = new List<AppEntry>(_enabledApps.Values) { entry };
                    SetEnabledApps(list);
                    _hintLabel.Text = $"\u5df2\u6dfb\u52a0\uff1a{name}\uff08{path}\uff09";
                }
            }
        }

        private static string InferAppKey(string path)
        {
            var lower = path.ToLowerInvariant();
            bool HasFile(params string[] relativePaths) => relativePaths.Any(relative => File.Exists(Path.Combine(path, relative)));
            bool HasDirectory(params string[] relativePaths) => relativePaths.Any(relative => Directory.Exists(Path.Combine(path, relative)));

            if (HasFile("素言.exe", "suyan.exe") || HasFile("release\\win-unpacked\\素言.exe", "release\\win-unpacked\\suyan.exe") || HasFile("src\\assets\\app-logo.png")) return "suyan";
            if (HasFile("BaiduNetdisk.exe", "BaiduNetdiskHost.exe", "BaiduYunGuanjia.db") || HasDirectory("users")) return "baidunetdisk";
            if (HasFile("quark_cloud_drive.exe", "QuarkCloudDrive.exe", "download.db", "upload.db") || HasDirectory("User Data\\persistence")) return "quarknetdisk";
            if (HasFile("aDrive.exe", "AliyunDrive.exe") || HasDirectory("Partitions\\adrive")) return "adrive";
            if (HasFile("ChatGPT.exe", "codex.exe") || HasDirectory(".codex\\sessions")) return "codex";
            // Claude Code Haha shares Claude's session store, so its dedicated
            // executable/root must be checked before the generic Claude match.
            if (HasFile("Claude Code Haha.exe") || HasDirectory(".claude\\cc-haha")) return "claude-haha";
            if (HasFile("claude.exe") || HasDirectory(".claude\\projects", ".claude\\transcripts")) return "claude";
            if (HasFile("Trae CN.exe") || HasDirectory("Trae CN\\logs")) return "trae";
            if (HasFile("TRAE SOLO CN.exe", "TraeWork CN.exe") || HasDirectory("TRAE SOLO CN\\logs", "TraeWork CN\\logs")) return "traework";
            if (HasFile("zcode.exe") || HasDirectory(".zcode\\cli\\log", ".zcode\\v2\\logs")) return "zcode";
            if (HasFile("Doubao.exe") || HasDirectory("User Data\\sdk_storage\\log")) return "doubao";
            if (HasFile("WorkBuddy.exe") || HasDirectory("resources\\app.asar.unpacked\\cli", ".workbuddy\\logs")) return "workbuddy";
            if (HasFile("OpenCode.exe") || HasDirectory("resources\\app.asar", ".local\\share\\opencode")) return "opencode";
            if (HasFile("DeepSeek Harness Desktop.exe", "DeepSeek Harness.exe") ||
                HasDirectory("resources\\harness", "harness-home\\sessions")) return "deepseekharness";
            if (HasFile("Freebuff.exe") || HasFile("resources\\orchestrator\\orchestrator.js")) return "freebuff";
            if (HasFile("leigod.exe", "LeiGod.exe")) return "leigod";
            // ComfyUI: 目录结构特征（官方便携版/ComfyNexus 改版共用），
            // 兼容用户手动选择到 ComfyUI 安装根目录或其中的 ComfyUI 子目录。
            if (HasFile("main.py", "ComfyUI.exe", "ComfyUI_ng.exe") && HasDirectory("custom_nodes")) return "comfyui";
            var dirName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            lower = dirName.ToLowerInvariant();
            if (lower.Contains("comfyui")) return "comfyui";
            if (lower.Contains("comfynexus")) return "comfyui";
            if (lower.Contains("claude") && (lower.Contains("haha") || lower.Contains("cc-haha"))) return "claude-haha";
            if (lower.Contains("claude")) return "claude";
            if (lower.Contains("codex")) return "codex";
            if (lower.Contains("solo") || lower.Contains("traework")) return "traework";
            if (lower.Contains("trae")) return "trae";
            if (lower.Contains("zcode")) return "zcode";
            if (lower.Contains("doubao") || lower.Contains("\u8c46\u5305")) return "doubao";
            if (lower.Contains("suyan") || lower.Contains("\u7d20\u8a00") || lower.Contains("\u63d0\u793a\u8bcd")) return "suyan";
            if (lower.Contains("baidunetdisk") || lower.Contains("\u767e\u5ea6\u7f51\u76d8")) return "baidunetdisk";
            if (lower.Contains("quark") || lower.Contains("\u5938\u514b")) return "quarknetdisk";
            if (lower.Contains("adrive") || lower.Contains("aliyun") || lower.Contains("\u963f\u91cc\u4e91\u76d8")) return "adrive";
            if (lower.Contains("workbuddy")) return "workbuddy";
            if (lower.Contains("opencode")) return "opencode";
            if (lower.Contains("deepseek") || lower.Contains("harness")) return "deepseekharness";
            if (lower.Contains("freebuff")) return "freebuff";
            if (lower.Contains("leigod") || lower.Contains("leigod_acc")) return "leigod";
            return lower.Replace(" ", "-");
        }

        private static string GetDisplayName(string key)
        {
            key = (key ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            if (NotificationAppRegistry.TryGet(key, out var registered))
            {
                return registered.DisplayName;
            }

            switch (key.ToLowerInvariant())
            {
                case "claude": return "Claude Code";
                case "claude-haha": return "Claude Code Haha";
                case "codex": return "ChatGPT";
                case "trae": return "TraeCode CN";
                case "traework": return "TraeWork CN";
                case "doubao": return "\u8c46\u5305";
                case "suyan": return "\u7d20\u8a00";
                case "baidunetdisk": return "\u767e\u5ea6\u7f51\u76d8";
                case "quarknetdisk": return "\u5938\u514b\u7f51\u76d8";
                case "adrive": return "\u963f\u91cc\u4e91\u76d8";
                case "workbuddy": return "WorkBuddy";
                case "opencode": return "OpenCode";
                case "deepseekharness": return "DeepSeek Harness";
                case "freebuff": return "Freebuff";
                case "leigod": return "\u96f7\u795e\u52a0\u901f\u5668";
                default: return key;
            }
        }

        private static bool IsNotifiableAppKey(string key)
        {
            return !string.IsNullOrWhiteSpace(key) &&
                !UnsupportedAppRegistry.IsUnsupported(key) &&
                (NotifiableAppKeys.Contains(key.Trim()) || NotificationAppRegistry.Contains(key));
        }

        private sealed class DetectionResult
        {
            internal List<AppEntry> Apps { get; } = new List<AppEntry>();
            internal string? UnsupportedMessage { get; set; }
        }

        /// <summary>
        /// 查询用户是否在面板中手动禁用了该来源。通知门控运行期自愈时必须
        /// 尊重用户选择：手动禁用的来源即使有安装证据也不得自动重新启用。
        /// </summary>
        internal static bool IsUserDisabled(string appKey)
        {
            var key = (appKey ?? string.Empty).Trim();
            return key.Length > 0 && LoadDisabledKeys().Contains(key);
        }

        private static HashSet<string> LoadDisabledKeys()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(PreferencesPath))
                {
                    return set;
                }

                using var doc = JsonDocument.Parse(File.ReadAllText(PreferencesPath));
                if (doc.RootElement.TryGetProperty("disabled", out var disabled) &&
                    disabled.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in disabled.EnumerateArray())
                    {
                        var key = item.GetString();
                        if (!string.IsNullOrWhiteSpace(key))
                        {
                            set.Add(key.Trim());
                        }
                    }
                }
            }
            catch
            {
                // 偏好文件损坏时按"全部启用"处理，不影响启动。
            }
            return set;
        }

        private void SaveDisabledKeys()
        {
            try
            {
                var dir = Path.GetDirectoryName(PreferencesPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var payload = new { disabled = _disabledKeys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray() };
                File.WriteAllText(PreferencesPath, JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8);
            }
            catch
            {
                // 持久化失败不应阻断 UI 交互。
            }
        }

        /// <summary>
        /// 搴旂敤鏉＄洰鏁版嵁銆?
        /// </summary>
        public sealed class AppEntry
        {
            public AppEntry(string key, string displayName, string? rootPath = null)
            {
                Key = key;
                DisplayName = displayName;
                RootPath = rootPath ?? string.Empty;
            }
            public string Key { get; }
            public string DisplayName { get; }
            public string RootPath { get; }
        }

        /// <summary>
        /// 鍗曚釜搴旂敤鍥炬爣 + 鍚嶇О鐨勭粍鍚堟帶浠躲€傜偣鍑诲垏鎹㈠惎鐢?绂佺敤鐘舵€併€?
        /// 绂佺敤鏃跺浘鏍囧彉鐏帮紝鍚敤鏃跺僵鑹层€?
        /// </summary>
        private enum AppChipMode
        {
            Installed,
            NotInstalled
        }

        private sealed class AppChip : Control
        {
            public AppEntry App { get; }
            public new bool Enabled { get; private set; } = true;
            private bool _notInstalled;

            private Color _surface;
            private Color _textPrimary;
            private Color _textSecondary;
            private Color _border;
            private readonly Size _iconSize = new Size(26, 26);
            private readonly Image? _icon;

            public AppChip(AppEntry app)
            {
                App = app;
                Size = new Size(ChipWidth, ChipHeight);
                SetStyle(
                    ControlStyles.UserPaint |
                    ControlStyles.AllPaintingInWmPaint |
                    ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.SupportsTransparentBackColor,
                    true);
                BackColor = Color.Transparent;
                DoubleBuffered = true;
                Cursor = Cursors.Hand;
                AccessibleRole = AccessibleRole.PushButton;
                AccessibleName = app.DisplayName;
                _icon = LoadIcon(app.Key);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _icon?.Dispose();
                }
                base.Dispose(disposing);
            }

            public void ApplyTheme(Color surface, Color textPrimary, Color textSecondary, Color border)
            {
                _surface = surface;
                _textPrimary = textPrimary;
                _textSecondary = textSecondary;
                _border = border;
                Invalidate();
            }

            public void SetEnabled(bool enabled)
            {
                Enabled = enabled;
                Invalidate();
            }

            public void SetMode(AppChipMode mode, bool enabled)
            {
                _notInstalled = mode == AppChipMode.NotInstalled;
                Enabled = enabled;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var isOn = !_notInstalled && Enabled;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                // 图标在紧凑芯片中居中，名称保留独立一行。
                var iconRect = new Rectangle((Width - _iconSize.Width) / 2, 0, _iconSize.Width, _iconSize.Height);

                // 鍦嗚鑳屾櫙
                var radius = 10;
                var path = new GraphicsPath();
                var d = Math.Max(1, Math.Min(radius * 2, Math.Min(Math.Max(1, iconRect.Width), Math.Max(1, iconRect.Height))));
                path.AddArc(iconRect.X, iconRect.Y, d, d, 180, 90);
                path.AddArc(iconRect.Right - d, iconRect.Y, d, d, 270, 90);
                path.AddArc(iconRect.Right - d, iconRect.Bottom - d, d, d, 0, 90);
                path.AddArc(iconRect.X, iconRect.Bottom - d, d, d, 90, 90);
                path.CloseFigure();

                var disabledSurface = _surface.R + _surface.G + _surface.B > 380
                    ? Color.FromArgb(242, 245, 244)
                    : Color.FromArgb(37, 54, 49);
                var disabledBorder = _surface.R + _surface.G + _surface.B > 380
                    ? Color.FromArgb(211, 220, 216)
                    : Color.FromArgb(63, 82, 74);
                using (var bgBrush = new SolidBrush(isOn ? _surface : disabledSurface))
                using (var borderPen = new Pen(isOn ? _border : disabledBorder, 1))
                {
                    g.FillPath(bgBrush, path);
                    g.DrawPath(borderPen, path);
                }

                // 搴旂敤棣栧瓧姣嶄綔涓哄浘鏍囷紙鏃犵綉缁滃浘鏍囨椂鐨勫彲闈犲厹搴曪紝閬垮厤渚濊禆澶栭儴璧勬簮锛?
                if (_icon != null)
                {
                    using var imageAttributes = new System.Drawing.Imaging.ImageAttributes();
                    if (!isOn)
                    {
                        imageAttributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix
                        {
                            Matrix00 = 0.45f,
                            Matrix11 = 0.45f,
                            Matrix22 = 0.45f,
                            Matrix33 = 1f
                        });
                    }
                    g.DrawImage(_icon, iconRect, 0, 0, _icon.Width, _icon.Height, GraphicsUnit.Pixel, imageAttributes);
                }
                else
                {
                    var letter = string.IsNullOrEmpty(App.DisplayName) ? "?" : App.DisplayName.Substring(0, 1).ToUpperInvariant();
                    var letterColor = isOn ? GetAppColor(App.Key) : Color.FromArgb(160, 160, 160);
                    using (var font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold))
                    using (var letterBrush = new SolidBrush(letterColor))
                    {
                        var sf = new StringFormat
                        {
                            Alignment = StringAlignment.Center,
                            LineAlignment = StringAlignment.Center
                        };
                        g.DrawString(letter, font, letterBrush, iconRect, sf);
                    }
                }

                // 鍚嶇О
                using (var nameFont = new Font("Microsoft YaHei UI", AppNameFontSize))
                using (var nameBrush = new SolidBrush(isOn ? _textPrimary : _textSecondary))
                {
                    var nameRect = new Rectangle(
                        0,
                        _iconSize.Height + IconNameGap,
                        Width,
                        Math.Max(1, Height - _iconSize.Height - IconNameGap));
                    var sf = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Near,
                        // 芯片保留了两行名称高度，允许较长的应用名占用该空间，
                        // 而不是在首行截成“DeepSeek Ha...”。
                        Trimming = StringTrimming.EllipsisWord
                    };
                    g.DrawString(App.DisplayName, nameFont, nameBrush, nameRect, sf);
                }
            }

            /// <summary>
            /// 鍚勫簲鐢ㄥ搧鐗岃壊锛屼笌 APP 绔?Channel 鏋氫妇棰滆壊瀵归綈銆?
            /// </summary>
            private static Image? LoadIcon(string appKey)
            {
                if (NotificationAppRegistry.TryGet(appKey, out var registered))
                {
                    try
                    {
                        var bytes = Convert.FromBase64String(registered.IconBase64);
                        using var stream = new MemoryStream(bytes, writable: false);
                        using var source = Image.FromStream(stream);
                        return new Bitmap(source);
                    }
                    catch
                    {
                        return null;
                    }
                }

                var fileName = appKey.ToLowerInvariant() switch
                {
                    "claude" => "ic_claude_logo.png",
                    "claude-haha" => "ic_claude_haha_logo.png",
                    "codex" => "ic_chatgpt_logo.png",
                    "chatgpt" => "ic_chatgpt_logo.png",
                    "trae" => "ic_trae_logo.png",
                    "traework" => "ic_trae_solo_logo.png",
                    "zcode" => "ic_zcode_logo.png",
                    "doubao" => "ic_doubao_logo.png",
                    "suyan" => "ic_suyan_logo.png",
                    "baidunetdisk" => "ic_baidunetdisk_logo.png",
                    "quarknetdisk" => "ic_quarknetdisk_logo.png",
                    "adrive" => "ic_adrive_logo.png",
                    "workbuddy" => "ic_workbuddy_logo.png",
                    "comfyui" => "ic_comfyui_logo.png",
                    "opencode" => "ic_opencode_logo.png",
                    "deepseekharness" => "ic_deepseekharness_logo.png",
                    "freebuff" => "ic_freebuff_logo.png",
                    "leigod" => "ic_leigod_logo.png",
                    _ => string.Empty
                };
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    return null;
                }

                try
                {
                    var resourceName = Assembly.GetExecutingAssembly()
                        .GetManifestResourceNames()
                        .FirstOrDefault(name => name.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(resourceName))
                    {
                        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
                        if (stream != null)
                        {
                            using var source = Image.FromStream(stream);
                            return new Bitmap(source);
                        }
                    }

                    var path = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
                    if (!File.Exists(path)) return null;
                    using var fileSource = Image.FromFile(path);
                    return new Bitmap(fileSource);
                }
                catch
                {
                    return null;
                }
            }

            private static Color GetAppColor(string key)
            {
                if (NotificationAppRegistry.TryGet(key, out var registered))
                {
                    try
                    {
                        return ColorTranslator.FromHtml(registered.BrandColor);
                    }
                    catch
                    {
                        // Fall back to the neutral color below when a registry entry is stale.
                    }
                }

                switch (key.ToLowerInvariant())
                {
                    case "claude": return Color.FromArgb(217, 119, 87);    // 姗?
                    case "claude-haha": return Color.FromArgb(217, 119, 87);  // Claude Code Haha 改版，与官方 Claude Code 同源品牌色
                    case "codex": return Color.FromArgb(16, 185, 129);     // 缁?
                    case "trae": return Color.FromArgb(59, 130, 246);      // 钃?
                    case "traework": return Color.FromArgb(245, 158, 11); // 榛?
                    case "zcode": return Color.FromArgb(236, 72, 153);     // 绮?
                    case "doubao": return Color.FromArgb(245, 101, 72);    // 璞嗗寘姗欑孩
                    case "baidunetdisk": return Color.FromArgb(54, 119, 246); // Baidu blue
                    case "quarknetdisk": return Color.FromArgb(78, 110, 242); // Quark blue-purple
                    case "adrive": return Color.FromArgb(255, 143, 45); // Aliyun Drive orange
                    case "workbuddy": return Color.FromArgb(99, 81, 241); // WorkBuddy violet
                    case "comfyui": return Color.FromArgb(242, 255, 89);   // ComfyUI official neon yellow
                    case "opencode": return Color.FromArgb(244, 244, 245);  // OpenCode off-white/gray brand
                    case "deepseekharness": return Color.FromArgb(77, 107, 254);  // DeepSeek brand blue (#4D6BFE)
                    case "freebuff": return Color.FromArgb(0, 0, 0);       // Freebuff 官方品牌黑（logo 为黑底白星）
                    case "leigod": return Color.FromArgb(255, 122, 0);     // LeiGod orange (#FF7A00)
                    default: return Color.FromArgb(148, 163, 184);          // 涓伆涓伆
                }
            }
        }
    }
}
