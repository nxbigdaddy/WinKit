using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinKit.App.Controls
{
    /// <summary>
    /// 自绘迷你折线图（CPU / 内存 / 网络 / 磁盘共用）。
    /// 刻意不引任何图表库：整块用 OnRender + DrawingContext 画，保证零第三方依赖。
    ///
    /// 设计要点：
    /// - 数据用环形缓冲保存，容量默认 120 点（1 秒 1 点 ≈ 2 分钟趋势），Push 是 O(1)。
    /// - Push 可以在任意线程调用，非 UI 线程自动 BeginInvoke 回 UI 线程。
    /// - 不做动画：图表每秒更新一次，重绘成本远小于一次布局。
    /// </summary>
    public sealed class SparklineChart : FrameworkElement
    {
        private const double FontSizeValue = 11.5d;
        private const double TopInset = 3d;
        private const double BottomInset = 2d;

        private readonly object _gate = new object();
        private double[] _buffer = new double[120];
        private int _count;
        private int _head;          // 下一个写入位置
        private int _totalPushed;

        // 渲染期临时状态（只在 UI 线程的 OnRender/Y 中使用）
        private double _renderMax = 1d;
        private double _renderTop = TopInset;
        private double _renderBottom = 50d;

        private Pen? _linePen;
        private Brush? _linePenSource;
        private double[]? _snapshotCache;

        public SparklineChart()
        {
            MinHeight = 40d;
            Height = 56d;
            SnapsToDevicePixels = true;
            ClipToBounds = true;
        }

        #region 依赖属性

        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
            nameof(Stroke), typeof(Brush), typeof(SparklineChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>折线颜色。用 DynamicResource 绑定 Chart*Brush，切主题自动生效。</summary>
        public Brush? Stroke
        {
            get => (Brush?)GetValue(StrokeProperty);
            set => SetValue(StrokeProperty, value);
        }

        public static readonly DependencyProperty MaxValueProperty = DependencyProperty.Register(
            nameof(MaxValue), typeof(double), typeof(SparklineChart),
            new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>归一化上限。AutoScale 为 false 时固定用它；传入非正值会被当成 100 处理。</summary>
        public double MaxValue
        {
            get => (double)GetValue(MaxValueProperty);
            set => SetValue(MaxValueProperty, value);
        }

        public static readonly DependencyProperty AutoScaleProperty = DependencyProperty.Register(
            nameof(AutoScale), typeof(bool), typeof(SparklineChart),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>为 true 时按缓冲内最大值自动缩放（网络速率必须开，否则小流量就是一条平线）。</summary>
        public bool AutoScale
        {
            get => (bool)GetValue(AutoScaleProperty);
            set => SetValue(AutoScaleProperty, value);
        }

        public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
            nameof(ShowGrid), typeof(bool), typeof(SparklineChart),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

        public bool ShowGrid
        {
            get => (bool)GetValue(ShowGridProperty);
            set => SetValue(ShowGridProperty, value);
        }

        public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
            nameof(Title), typeof(string), typeof(SparklineChart),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>图表含义说明。当前不绘制（标题由所在卡片的 TextBlock 显示），属性保留给调用方使用。</summary>
        public string Title
        {
            get => (string?)GetValue(TitleProperty) ?? string.Empty;
            set => SetValue(TitleProperty, value);
        }

        public static readonly DependencyProperty ValueTextProperty = DependencyProperty.Register(
            nameof(ValueText), typeof(string), typeof(SparklineChart),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>右上角叠加显示的当前值文本。</summary>
        public string ValueText
        {
            get => (string?)GetValue(ValueTextProperty) ?? string.Empty;
            set => SetValue(ValueTextProperty, value);
        }

        public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
            nameof(Capacity), typeof(int), typeof(SparklineChart),
            new FrameworkPropertyMetadata(120, FrameworkPropertyMetadataOptions.AffectsRender, OnCapacityChanged));

        /// <summary>环形缓冲容量（点数）。改小会丢弃最旧的点。</summary>
        public int Capacity
        {
            get => (int)GetValue(CapacityProperty);
            set => SetValue(CapacityProperty, value);
        }

        private static void OnCapacityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var chart = (SparklineChart)d;
            var requested = (int)e.NewValue;
            if (requested < 2) requested = 2;

            lock (chart._gate)
            {
                if (requested == chart._buffer.Length) return;

                var keep = Math.Min(chart._count, requested);
                var ordered = chart.CopyOrdered(chart._count);
                var resized = new double[requested];
                var offset = chart._count - keep;   // 只保留最新的 keep 个点
                for (var i = 0; i < keep; i++) resized[i] = ordered[offset + i];

                chart._buffer = resized;
                chart._count = keep;
                chart._head = keep % requested;
                chart._snapshotCache = null;
            }

            chart.InvalidateVisual();
        }

        #endregion

        #region 数据写入

        /// <summary>写入一个采样点。可在任意线程调用（非 UI 线程自动封送）。</summary>
        public void Push(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return;

            var dispatcher = Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                // 页面正在卸载时 Dispatcher 已关闭，静默丢弃即可
                if (dispatcher.HasShutdownStarted) return;
                try
                {
                    dispatcher.BeginInvoke(DispatcherPriority.Render, new Action<double>(Push), value);
                }
                catch (Exception)
                {
                    // Dispatcher 关闭过程中的竞态，忽略
                }
                return;
            }

            lock (_gate)
            {
                var capacity = _buffer.Length;
                _buffer[_head] = value;
                _head = (_head + 1) % capacity;
                if (_count < capacity) _count++;
                _snapshotCache = null;
                _totalPushed++;
            }

            InvalidateVisual();
        }

        /// <summary>清空所有采样点。</summary>
        public void Clear()
        {
            lock (_gate)
            {
                Array.Clear(_buffer, 0, _buffer.Length);
                _head = 0;
                _count = 0;
                _snapshotCache = null;
            }

            InvalidateVisual();
        }

        /// <summary>按时间顺序返回缓冲快照（最旧 → 最新）。返回的数组是内部缓存的副本引用，请勿修改。</summary>
        public IReadOnlyList<double> Snapshot()
        {
            lock (_gate)
            {
                var cache = _snapshotCache;
                if (cache != null && cache.Length == _count) return cache;

                var ordered = CopyOrdered(_count);
                _snapshotCache = ordered;
                return ordered;
            }
        }

        /// <summary>已写入的采样点总数（Clear 不归零，便于判断是否已完成基准采样）。</summary>
        public int TotalPushed
        {
            get { lock (_gate) { return _totalPushed; } }
        }

        /// <summary>调用方必须已持有 _gate。返回按时间顺序排列的前 count 个点。</summary>
        private double[] CopyOrdered(int count)
        {
            var result = new double[count];
            if (count == 0) return result;

            var capacity = _buffer.Length;
            if (count < capacity)
            {
                // 还没绕圈：数据在 [0, count)
                Array.Copy(_buffer, 0, result, 0, count);
                return result;
            }

            // 已绕圈：从 head 读到末尾，再从头读到 head
            var tail = capacity - _head;
            Array.Copy(_buffer, _head, result, 0, tail);
            if (_head > 0) Array.Copy(_buffer, 0, result, tail, _head);
            return result;
        }

        #endregion

        #region 自绘

        protected override void OnRender(DrawingContext dc)
        {
            var width = ActualWidth;
            var height = ActualHeight;
            if (width <= 1d || height <= 1d) return;

            // 背景保持透明：卡片本身已有底色，这里再叠一层会脏
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0d, 0d, width, height));

            double[] points;
            lock (_gate)
            {
                points = CopyOrdered(_count);
            }

            PrepareRenderRange(height, points);

            if (ShowGrid)
            {
                var gridPen = CreateGridPen(TryFindBrush("ChartGridBrush"));
                var span = _renderBottom - _renderTop;
                // 3 条水平网格线（含顶部）
                for (var i = 0; i < 3; i++)
                {
                    var y = SnapLine(_renderTop + span * i / 3d);
                    dc.DrawLine(gridPen, new Point(0d, y), new Point(width, y));
                }
            }

            // 数据点少于 2 个时只保留网格（首次采样、刚切页时会碰到）
            if (points.Length >= 2)
            {
                var stroke = ResolveStroke();
                var step = width / (points.Length - 1);

                var line = new StreamGeometry();
                using (var lc = line.Open())
                {
                    lc.BeginFigure(new Point(0d, Y(points[0])), false, false);
                    var segment = new List<Point>(points.Length - 1);
                    for (var i = 1; i < points.Length; i++)
                    {
                        segment.Add(new Point(step * i, Y(points[i])));
                    }
                    lc.PolyLineTo(segment, true, false);
                }
                line.Freeze();

                var area = new StreamGeometry();
                using (var ac = area.Open())
                {
                    ac.BeginFigure(new Point(0d, _renderBottom), true, true);
                    var segment = new List<Point>(points.Length);
                    for (var i = 0; i < points.Length; i++)
                    {
                        segment.Add(new Point(step * i, Y(points[i])));
                    }
                    ac.PolyLineTo(segment, true, false);
                    ac.LineTo(new Point(step * (points.Length - 1), _renderBottom), false, false);
                }
                area.Freeze();

                var fill = CreateAreaBrush(stroke);
                if (fill != null) dc.DrawGeometry(fill, null, area);
                dc.DrawGeometry(null, CreateLinePen(stroke), line);
            }

            DrawValueText(dc, width, height);
        }

        /// <summary>计算本次绘制用的上限与上下边界（顺带保证不会出现除 0）。</summary>
        private void PrepareRenderRange(double height, double[] points)
        {
            _renderTop = TopInset;
            _renderBottom = height - BottomInset;
            if (_renderBottom - _renderTop < 8d) _renderBottom = _renderTop + 8d;

            var configured = MaxValue;
            if (configured <= 0d || double.IsNaN(configured) || double.IsInfinity(configured)) configured = 100d;

            var max = configured;
            if (AutoScale)
            {
                var observed = 0d;
                for (var i = 0; i < points.Length; i++)
                {
                    var v = points[i];
                    if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                    if (v > observed) observed = v;
                }

                if (observed > 0d) max = observed * 1.1d;   // 峰值留 10% 余量，线不贴顶
            }

            if (max <= 0d || double.IsNaN(max) || double.IsInfinity(max)) max = 1d;
            _renderMax = max;
        }

        private double Y(double value)
        {
            var ratio = value / _renderMax;
            if (double.IsNaN(ratio) || ratio < 0d) ratio = 0d;
            if (ratio > 1d) ratio = 1d;
            return _renderTop + (_renderBottom - _renderTop) * (1d - ratio);
        }

        private Brush ResolveStroke()
        {
            var brush = Stroke;
            if (brush == null) brush = TryFindBrush("AccentBrush");
            return brush ?? Brushes.DodgerBlue;
        }

        private Pen CreateLinePen(Brush stroke)
        {
            // 缓存 Pen：主题切换时 DynamicResource 会给出新的 Brush 实例，这里按引用失效
            if (_linePen != null && ReferenceEquals(_linePenSource, stroke)) return _linePen;

            var pen = new Pen(stroke, 1.6d)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                MiterLimit = 2d,
            };
            pen.Freeze();
            _linePen = pen;
            _linePenSource = stroke;
            return pen;
        }

        private static Pen CreateGridPen(Brush? gridBrush)
        {
            var pen = new Pen(gridBrush ?? Brushes.Gray, 1d)
            {
                DashStyle = new DashStyle(new double[] { 2d, 3d }, 0d),
            };
            pen.Freeze();
            return pen;
        }

        /// <summary>折线下方的渐变填充：折线色 25% 不透明度 → 底部 0。</summary>
        private static Brush? CreateAreaBrush(Brush stroke)
        {
            var color = ExtractColor(stroke);
            if (color == null) return null;

            var strong = color.Value;
            strong.A = 64;   // 255 * 0.25 ≈ 64
            var faint = color.Value;
            faint.A = 0;

            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0d, 0d),
                EndPoint = new Point(0d, 1d),
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
            };
            brush.GradientStops.Add(new GradientStop(strong, 0d));
            brush.GradientStops.Add(new GradientStop(faint, 1d));
            brush.Freeze();
            return brush;
        }

        private static Color? ExtractColor(Brush brush)
        {
            if (brush is SolidColorBrush solid) return solid.Color;

            // 非纯色画刷兜底：画到 1x1 位图上取像素。
            // 用 RenderTargetBitmap 而不是 Freeze —— 缓存的画刷是主题里的共享实例，冻结会污染全局资源。
            try
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(brush, null, new Rect(0d, 0d, 1d, 1d));
                }

                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    1, 1, 96d, 96d, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var pixel = new byte[4];
                bitmap.CopyPixels(pixel, 4, 0);
                return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void DrawValueText(DrawingContext dc, double width, double height)
        {
            var text = ValueText;
            if (string.IsNullOrEmpty(text)) return;

            var dpi = 1d;
            try
            {
                // net48 起 VisualTreeHelper.GetDpi 可用；PerMonitorV2 缩放下这个值必须传对，否则文字会糊
                dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            }
            catch (Exception)
            {
                dpi = 1d;
            }
            if (dpi <= 0d) dpi = 1d;

            var brush = TryFindBrush("TextSecondaryBrush") ?? Brushes.Gray;
            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Consolas"),
                FontSizeValue,
                brush,
                dpi);

            var x = width - formatted.Width - 4d;
            var y = 4d;
            if (x < 0d) x = 0d;
            if (y + formatted.Height > height) y = Math.Max(0d, height - formatted.Height);

            dc.DrawText(formatted, new Point(SnapText(x), SnapText(y)));
        }

        /// <summary>
        /// 1px 线条的像素对齐。WPF 没有 WinUI 的 Snapping 辅助类，
        /// 这里用「四舍五入 + 0.5」把线落在像素中心 —— 直接用整数坐标会让 1px 粗的线
        /// 跨在两条像素之间，被抗锯齿摊成 2px 灰线，看起来发虚。
        /// </summary>
        private static double SnapLine(double value) => Math.Round(value) + 0.5;

        /// <summary>文本位置对齐到整数像素即可，不加半像素偏移（那会让字形发虚）。</summary>
        private static double SnapText(double value) => Math.Round(value);

        private Brush? TryFindBrush(string key)
        {
            try
            {
                return TryFindResource(key) as Brush;
            }
            catch (Exception)
            {
                return null;
            }
        }

        #endregion
    }
}
