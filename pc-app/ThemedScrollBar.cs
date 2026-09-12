using System;
using System.Drawing;
using System.Windows.Forms;

namespace TapRelay
{
    /// <summary>
    /// 统一的桌面自绘滚动条。使用稳定命中槽、连续轨道及悬停/拖拽反馈，
    /// 避免 WinForms 原生 VScrollBar 在深色界面里仍显示系统浅色条。
    /// </summary>
    internal sealed class ThemedScrollBar : Control
    {
        private int _value;
        private int _maximum = 100;
        private int _largeChange = 10;
        private int _smallChange = 1;
        private int _thumbTop;
        private int _thumbHeight;
        private bool _dragging;
        private int _dragStartY;
        private int _dragStartValue;
        // 默认取浅色主题 muted 语义色；渲染器按交互状态叠加透明度。
        private Color _thumbColor = Color.FromArgb(101, 118, 111);
        private Color _trackColor = Color.FromArgb(101, 118, 111);
        private bool _hovered;

        public event EventHandler? ValueChanged;
        public event ScrollEventHandler? Scroll;

        public ThemedScrollBar()
        {
            SetStyle(
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor,
                true);
            Width = ScrollBarRenderer.PreferredWidth;
            Cursor = Cursors.Default;
            // WinForms transparent child controls can composite translucent brushes against a black buffer.
            // Callers set the hosting surface color; this is the light-theme fallback for first paint.
            BackColor = Color.FromArgb(238, 243, 241);
            TabStop = false;
        }

        public int Value
        {
            get => _value;
            set
            {
                var clamped = ClampValue(value);
                if (_value == clamped)
                {
                    return;
                }

                _value = clamped;
                RecalculateThumb();
                ValueChanged?.Invoke(this, EventArgs.Empty);
                Invalidate();
            }
        }

        public int Maximum
        {
            get => _maximum;
            set
            {
                _maximum = Math.Max(0, value);
                Value = _value;
                RecalculateThumb();
                Invalidate();
            }
        }

        public int LargeChange
        {
            get => _largeChange;
            set
            {
                _largeChange = Math.Max(1, value);
                RecalculateThumb();
                Invalidate();
            }
        }

        public int SmallChange
        {
            get => _smallChange;
            set => _smallChange = Math.Max(1, value);
        }

        public Color ThumbColor
        {
            get => _thumbColor;
            set
            {
                _thumbColor = value;
                Invalidate();
            }
        }

        public Color TrackColor
        {
            get => _trackColor;
            set
            {
                _trackColor = value;
                Invalidate();
            }
        }

        public void ApplyTheme(Color thumb, Color track)
        {
            _thumbColor = thumb;
            _trackColor = track;
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Clear to the hosting surface before compositing the translucent rail and thumb.
            e.Graphics.Clear(BackColor);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            RecalculateThumb();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!CanScroll || _thumbHeight <= 0)
            {
                return;
            }

            var feedback = _dragging
                ? ScrollBarRenderer.Feedback.Dragging
                : _hovered ? ScrollBarRenderer.Feedback.Hovered : ScrollBarRenderer.Feedback.Normal;
            ScrollBarRenderer.Render(
                e.Graphics,
                Width,
                Height,
                _thumbTop,
                _thumbHeight,
                _thumbColor,
                _trackColor,
                feedback);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hovered = true;
            UpdateCursor(PointToClient(Control.MousePosition).Y);
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!_dragging)
            {
                _hovered = false;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!CanScroll || e.Button != MouseButtons.Left)
            {
                return;
            }

            if (IsOverThumb(e.Y))
            {
                _dragging = true;
                _dragStartY = e.Y;
                _dragStartValue = _value;
                Capture = true;
                Cursor = Cursors.SizeNS;
                Invalidate();
                return;
            }

            // 点击轨道：按大步跳转。
            var page = Math.Max(_largeChange, 1);
            var aboveThumb = e.Y < _thumbTop;
            SetValueInternal(
                aboveThumb ? _value - page : _value + page,
                aboveThumb ? ScrollEventType.LargeDecrement : ScrollEventType.LargeIncrement);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging)
            {
                UpdateCursor(e.Y);
                return;
            }

            if (!CanScroll)
            {
                return;
            }

            var travel = TrackHeight - _thumbHeight;
            if (travel <= 0)
            {
                return;
            }

            var delta = e.Y - _dragStartY;
            var valueDelta = (int)Math.Round(delta * (double)_maximum / travel);
            SetValueInternal(_dragStartValue + valueDelta, ScrollEventType.ThumbTrack);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_dragging || e.Button != MouseButtons.Left)
            {
                return;
            }

            EndDrag();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!CanScroll)
            {
                return;
            }

            var steps = Math.Max(1, Math.Abs(e.Delta) / 120);
            var delta = e.Delta > 0 ? -_smallChange * steps : _smallChange * steps;
            SetValueInternal(_value + delta, ScrollEventType.ThumbPosition);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (_dragging && !Capture)
            {
                EndDrag();
            }
        }

        private bool CanScroll => _maximum > 0 && TrackHeight > 8;

        private int TrackTop => ScrollBarRenderer.TrackVerticalInset;

        private int TrackHeight => ScrollBarRenderer.TrackHeightFor(Height);

        private int ClampValue(int value) => Math.Max(0, Math.Min(_maximum, value));

        private bool IsOverThumb(int y)
        {
            return _thumbHeight > 0 && y >= _thumbTop && y < _thumbTop + _thumbHeight;
        }

        private void UpdateCursor(int y)
        {
            Cursor = CanScroll && IsOverThumb(y) ? Cursors.SizeNS : Cursors.Default;
        }

        private void EndDrag()
        {
            if (!_dragging)
            {
                return;
            }

            _dragging = false;
            Capture = false;
            UpdateCursor(PointToClient(Control.MousePosition).Y);
            Invalidate();
            Scroll?.Invoke(this, new ScrollEventArgs(ScrollEventType.EndScroll, _value));
        }

        private void SetValueInternal(int value, ScrollEventType type)
        {
            var clamped = ClampValue(value);
            if (_value == clamped)
            {
                return;
            }

            _value = clamped;
            RecalculateThumb();
            ValueChanged?.Invoke(this, EventArgs.Empty);
            Scroll?.Invoke(this, new ScrollEventArgs(type, _value));
            Invalidate();
        }

        private void RecalculateThumb()
        {
            if (!CanScroll)
            {
                _thumbHeight = 0;
                _thumbTop = 0;
                return;
            }

            // 可视比例近似：LargeChange / (Maximum + LargeChange)，并在轨道上下
            // 留出圆角端点，避免滑块贴边时像一根突兀的进度条。
            var span = _maximum + _largeChange;
            _thumbHeight = Math.Max(ScrollBarRenderer.MinThumbHeight, TrackHeight * _largeChange / Math.Max(1, span));
            _thumbHeight = Math.Min(_thumbHeight, TrackHeight);
            var travel = Math.Max(1, TrackHeight - _thumbHeight);
            _thumbTop = TrackTop + (_maximum > 0
                ? (int)Math.Round(_value * (double)travel / _maximum)
                : 0);
            _thumbTop = Math.Max(TrackTop, Math.Min(TrackTop + travel, _thumbTop));
        }
    }
}
