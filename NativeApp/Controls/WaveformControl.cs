using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ZStationStudio.Controls
{
    public class WaveformControl : FrameworkElement
    {
        private float[]? _peaks;
        private double _duration;
        private double _currentTime;
        private double _inPosition;
        private double _outPosition;
        private bool _isDraggingRange;
        private Point _dragStartPoint;

        public event Action<double>? SeekRequested;
        public event Action<double, double>? RangeSelected;

        public float[]? Peaks
        {
            get => _peaks;
            set { _peaks = value; InvalidateVisual(); }
        }

        public double Duration
        {
            get => _duration;
            set { _duration = Math.Max(0.001, value); InvalidateVisual(); }
        }

        public double CurrentTime
        {
            get => _currentTime;
            set
            {
                if (Math.Abs(_currentTime - value) > 0.01)
                {
                    _currentTime = value;
                    InvalidateVisual();
                }
            }
        }

        public double InPosition
        {
            get => _inPosition;
            set { _inPosition = Math.Max(0, value); InvalidateVisual(); }
        }

        public double OutPosition
        {
            get => _outPosition;
            set { _outPosition = Math.Max(0, value); InvalidateVisual(); }
        }

        public bool HasSelection => OutPosition > InPosition;

        // Visual Brushes (Netflix Theme: Theater Black #141414, Signature Red #E50914, Neutral Unplayed #383838)
        private static readonly Brush BgBrush = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14));
        private static readonly Brush WaveformUnplayedBrush = new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x38)); // Flat neutral gray
        private static readonly Brush WaveformPlayedBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x09, 0x14)); // Netflix Signature Red
        private static readonly Brush SelectionOverlayBrush = new SolidColorBrush(Color.FromArgb(45, 0xE5, 0x09, 0x14)); // Subtle red selection tint
        private static readonly Pen InPen = new(Brushes.White, 2.0); // Crisp white In marker
        private static readonly Pen OutPen = new(new SolidColorBrush(Color.FromRgb(0xE5, 0x09, 0x14)), 2.0); // Netflix Red Out marker
        private static readonly Pen PlayheadPen = new(Brushes.White, 2.0);
        private static readonly Pen CenterLinePen = new(new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)), 1.0);

        static WaveformControl()
        {
            BgBrush.Freeze();
            WaveformUnplayedBrush.Freeze();
            WaveformPlayedBrush.Freeze();
            SelectionOverlayBrush.Freeze();
            InPen.Freeze();
            OutPen.Freeze();
            PlayheadPen.Freeze();
            CenterLinePen.Freeze();
        }

        public WaveformControl()
        {
            ClipToBounds = true;
            Cursor = Cursors.Hand;
            MouseDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseUp += OnMouseUp;
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && Duration > 0)
            {
                CaptureMouse();
                _dragStartPoint = e.GetPosition(this);
                double clickSec = Math.Clamp((_dragStartPoint.X / ActualWidth) * Duration, 0, Duration);

                if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
                {
                    _isDraggingRange = true;
                }
                else
                {
                    SeekRequested?.Invoke(clickSec);
                }
            }
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (IsMouseCaptured && Duration > 0)
            {
                Point current = e.GetPosition(this);
                double startSec = Math.Clamp((_dragStartPoint.X / ActualWidth) * Duration, 0, Duration);
                double curSec = Math.Clamp((current.X / ActualWidth) * Duration, 0, Duration);

                if (Math.Abs(current.X - _dragStartPoint.X) > 4)
                {
                    _isDraggingRange = true;
                    double min = Math.Min(startSec, curSec);
                    double max = Math.Max(startSec, curSec);
                    InPosition = min;
                    OutPosition = max;
                    RangeSelected?.Invoke(min, max);
                }
                else if (!_isDraggingRange)
                {
                    SeekRequested?.Invoke(curSec);
                }
            }
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
                _isDraggingRange = false;
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            // 1. Background
            dc.DrawRectangle(BgBrush, null, new Rect(0, 0, w, h));

            double midY = h / 2.0;
            dc.DrawLine(CenterLinePen, new Point(0, midY), new Point(w, midY));

            if (_peaks == null || _peaks.Length == 0 || Duration <= 0)
            {
                // Draw subtle placeholder dots if no peaks
                return;
            }

            // 2. Waveform Bars
            int numBars = Math.Min(_peaks.Length, (int)(w / 2.0));
            if (numBars <= 0) numBars = _peaks.Length;

            double barWidth = Math.Max(1.0, w / numBars);
            double playheadX = (CurrentTime / Duration) * w;

            for (int i = 0; i < numBars; i++)
            {
                int peakIndex = (int)((i / (double)numBars) * _peaks.Length);
                float amp = _peaks[peakIndex];
                double barHeight = Math.Max(2.0, amp * (h * 0.85));
                double x = i * barWidth;
                double y = midY - (barHeight / 2.0);

                Brush brush = (x <= playheadX) ? WaveformPlayedBrush : WaveformUnplayedBrush;
                dc.DrawRectangle(brush, null, new Rect(x, y, Math.Max(1.0, barWidth - 1), barHeight));
            }

            // 3. Selection Range (In / Out)
            if (HasSelection)
            {
                double inX = (InPosition / Duration) * w;
                double outX = (OutPosition / Duration) * w;
                double rangeW = Math.Max(1, outX - inX);

                dc.DrawRectangle(SelectionOverlayBrush, null, new Rect(inX, 0, rangeW, h));
                dc.DrawLine(InPen, new Point(inX, 0), new Point(inX, h));
                dc.DrawLine(OutPen, new Point(outX, 0), new Point(outX, h));

                // Draw small [I] and [O] flags
                DrawFlag(dc, inX, 14, "I", InPen.Brush);
                DrawFlag(dc, outX, 14, "O", OutPen.Brush);
            }

            // 4. Playhead Line
            if (playheadX >= 0 && playheadX <= w)
            {
                dc.DrawLine(PlayheadPen, new Point(playheadX, 0), new Point(playheadX, h));

                // Playhead top triangle
                var triangle = new PathGeometry();
                var figure = new PathFigure { StartPoint = new Point(playheadX - 4, 0), IsClosed = true };
                figure.Segments.Add(new LineSegment(new Point(playheadX + 4, 0), false));
                figure.Segments.Add(new LineSegment(new Point(playheadX, 6), false));
                triangle.Figures.Add(figure);
                dc.DrawGeometry(Brushes.White, null, triangle);
            }
        }

        private static void DrawFlag(DrawingContext dc, double x, double size, string text, Brush brush)
        {
            var formattedText = new FormattedText(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI Bold"),
                9,
                Brushes.White,
                1.0);

            double flagW = formattedText.Width + 6;
            double flagH = formattedText.Height + 2;
            double rectX = (text == "I") ? x : x - flagW;

            dc.DrawRoundedRectangle(brush, null, new Rect(rectX, 2, flagW, flagH), 3, 3);
            dc.DrawText(formattedText, new Point(rectX + 3, 3));
        }
    }
}
