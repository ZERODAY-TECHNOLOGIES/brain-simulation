using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace BrainSim.App.Controls;

/// <summary>
/// 24-hour dial: midnight at the top, the dark window as an arc, a hand at the circadian phase phi,
/// and the hand turns warm while a message's light lasts.
/// </summary>
public sealed class ClockDial : FrameworkElement
{
    public static readonly DependencyProperty PhiProperty = Register(nameof(Phi), 0.0);
    public static readonly DependencyProperty DarkStartProperty = Register(nameof(DarkStart), 21.0);
    public static readonly DependencyProperty DarkEndProperty = Register(nameof(DarkEnd), 7.0);
    public static readonly DependencyProperty AsleepProperty = DependencyProperty.Register(
        nameof(Asleep), typeof(bool), typeof(ClockDial), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Pen RingPen = LevelBar.Freeze(new Pen(Palette.Brush(Palette.Border), 10));
    private static readonly Pen DarkPen = LevelBar.Freeze(new Pen(Palette.Brush("#3B4A8C"), 10));
    private static readonly Pen HandPen = LevelBar.Freeze(new Pen(Palette.Brush(Palette.Text), 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Pen SleepHandPen = LevelBar.Freeze(new Pen(Palette.Brush("#C7D2FE"), 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    private static readonly Brush LabelBrush = Palette.Brush(Palette.TextMuted);
    private static readonly Typeface Face = new("Segoe UI Variable");

    public double Phi { get => (double)GetValue(PhiProperty); set => SetValue(PhiProperty, value); }
    public double DarkStart { get => (double)GetValue(DarkStartProperty); set => SetValue(DarkStartProperty, value); }
    public double DarkEnd { get => (double)GetValue(DarkEndProperty); set => SetValue(DarkEndProperty, value); }
    public bool Asleep { get => (bool)GetValue(AsleepProperty); set => SetValue(AsleepProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 40)
        {
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = size / 2 - 22;
        dc.DrawEllipse(null, RingPen, center, radius, radius);
        dc.DrawGeometry(null, DarkPen, Arc(center, radius, DarkStart, DarkEnd));

        foreach (var hour in new[] { 0, 6, 12, 18 })
        {
            var p = At(center, radius + 14, hour);
            var text = new FormattedText(hour.ToString("00", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Face, 11, LabelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(p.X - text.Width / 2, p.Y - text.Height / 2));
        }

        dc.DrawLine(Asleep ? SleepHandPen : HandPen, center, At(center, radius - 8, Phi));
        dc.DrawEllipse(Palette.Brush(Palette.Text), null, center, 3, 3);
    }

    private static Point At(Point center, double radius, double hour)
    {
        var angle = hour / 24 * 2 * Math.PI;
        return new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
    }

    private static Geometry Arc(Point center, double radius, double from, double to)
    {
        var span = ((to - from) % 24 + 24) % 24;
        var figure = new PathFigure { StartPoint = At(center, radius, from) };
        figure.Segments.Add(new ArcSegment(At(center, radius, to), new Size(radius, radius), 0, span > 12, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    private static DependencyProperty Register(string name, double value) => DependencyProperty.Register(
        name, typeof(double), typeof(ClockDial), new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));
}
