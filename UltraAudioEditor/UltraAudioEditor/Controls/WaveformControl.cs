using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UltraAudioEditor.Models;

namespace UltraAudioEditor.Controls
{
    public class WaveformControl : FrameworkElement
    {
        public static readonly DependencyProperty ClipsProperty =
            DependencyProperty.Register(nameof(Clips), typeof(IEnumerable<AudioClip>), typeof(WaveformControl),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TrackColorProperty =
            DependencyProperty.Register(nameof(TrackColor), typeof(Color), typeof(WaveformControl),
                new FrameworkPropertyMetadata(Colors.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty PlayheadPositionProperty =
            DependencyProperty.Register(nameof(PlayheadPosition), typeof(double), typeof(WaveformControl),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ProjectDurationProperty =
            DependencyProperty.Register(nameof(ProjectDuration), typeof(double), typeof(WaveformControl),
                new FrameworkPropertyMetadata(30.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ZoomLevelProperty =
            DependencyProperty.Register(nameof(ZoomLevel), typeof(double), typeof(WaveformControl),
                new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public IEnumerable<AudioClip>? Clips { get => (IEnumerable<AudioClip>?)GetValue(ClipsProperty); set => SetValue(ClipsProperty, value); }
        public Color TrackColor { get => (Color)GetValue(TrackColorProperty); set => SetValue(TrackColorProperty, value); }
        public double PlayheadPosition { get => (double)GetValue(PlayheadPositionProperty); set => SetValue(PlayheadPositionProperty, value); }
        public double ProjectDuration { get => (double)GetValue(ProjectDurationProperty); set => SetValue(ProjectDurationProperty, value); }
        public double ZoomLevel { get => (double)GetValue(ZoomLevelProperty); set => SetValue(ZoomLevelProperty, value); }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            // Pozadina trake
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(30, 30, 46)), null, new Rect(0, 0, w, h));

            double duration = Math.Max(1, ProjectDuration);
            double pixelsPerSec = w / duration * ZoomLevel;

            // Klipovi
            if (Clips != null)
            {
                foreach (var clip in Clips)
                {
                    double clipX = clip.StartTime * pixelsPerSec;
                    double clipW = clip.Duration * pixelsPerSec;
                    if (clipX > w || clipX + clipW < 0) continue;

                    // Pozadina klipa
                    var clipColor = Color.FromArgb(60, TrackColor.R, TrackColor.G, TrackColor.B);
                    dc.DrawRectangle(new SolidColorBrush(clipColor),
                        new Pen(new SolidColorBrush(TrackColor), 1),
                        new Rect(clipX, 1, Math.Max(2, clipW), h - 2));

                    // Waveform
                    if (clip.WaveformData != null && clip.WaveformData.Length > 0)
                    {
                        var pen = new Pen(new SolidColorBrush(Color.FromArgb(200, TrackColor.R, TrackColor.G, TrackColor.B)), 1);
                        var geo = new StreamGeometry();
                        using var ctx = geo.Open();
                        int pts = clip.WaveformData.Length;
                        for (int i = 0; i < pts; i++)
                        {
                            double x = clipX + (i / (double)pts) * clipW;
                            if (x < 0 || x > w) continue;
                            double amp = Math.Abs(clip.WaveformData[i]);
                            double yTop = h / 2 - amp * (h / 2 - 4);
                            double yBot = h / 2 + amp * (h / 2 - 4);
                            ctx.BeginFigure(new Point(x, yTop), false, false);
                            ctx.LineTo(new Point(x, yBot), true, false);
                        }
                        dc.DrawGeometry(null, pen, geo);
                    }

                    // Naziv klipa
                    if (clipW > 40)
                    {
                        var ft = new FormattedText(clip.Name,
                            System.Globalization.CultureInfo.CurrentCulture,
                            FlowDirection.LeftToRight,
                            new Typeface("Segoe UI"),
                            11, Brushes.White, 96);
                        dc.DrawText(ft, new Point(clipX + 4, 3));
                    }
                }
            }

            // Playhead
            double phX = PlayheadPosition * pixelsPerSec;
            if (phX >= 0 && phX <= w)
            {
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(226, 75, 74)), 2),
                    new Point(phX, 0), new Point(phX, h));
                // Trokut na vrhu
                var tri = new StreamGeometry();
                using var tctx = tri.Open();
                tctx.BeginFigure(new Point(phX - 6, 0), true, true);
                tctx.LineTo(new Point(phX + 6, 0), true, false);
                tctx.LineTo(new Point(phX, 10), true, false);
                dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(226, 75, 74)), null, tri);
            }

            // Rešetka (grid lines za sekunde)
            var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 0.5);
            double step = pixelsPerSec > 40 ? 1 : pixelsPerSec > 10 ? 5 : 10;
            for (double t = 0; t <= duration; t += step)
            {
                double x = t * pixelsPerSec;
                if (x > w) break;
                dc.DrawLine(gridPen, new Point(x, 0), new Point(x, h));
            }
        }
    }
}
