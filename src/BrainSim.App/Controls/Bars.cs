using System.Windows;
using System.Windows.Media;

namespace BrainSim.App.Controls;

/// <summary>A horizontal level from 0 to <see cref="Max"/> with a tick at the baseline.</summary>
public sealed class LevelBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = Register(nameof(Value), 1.0);
    public static readonly DependencyProperty BaselineProperty = Register(nameof(Baseline), 1.0);
    public static readonly DependencyProperty MaxProperty = Register(nameof(Max), 5.0);
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(LevelBar), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Track = Palette.Brush(Palette.Border);
    private static readonly Pen BaselinePen = Freeze(new Pen(Palette.Brush(Palette.Text), 1.5));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Baseline { get => (double)GetValue(BaselineProperty); set => SetValue(BaselineProperty, value); }
    public double Max { get => (double)GetValue(MaxProperty); set => SetValue(MaxProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), 2, 2);
        dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, w * Math.Clamp(Value / Max, 0, 1), h), 2, 2);
        var x = w * Math.Clamp(Baseline / Max, 0, 1);
        dc.DrawLine(BaselinePen, new Point(x, -2), new Point(x, h + 2));
    }

    private static DependencyProperty Register(string name, double value) => DependencyProperty.Register(
        name, typeof(double), typeof(LevelBar), new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));

    internal static Pen Freeze(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}

/// <summary>A value in [-Range, Range] drawn from a center line, positive to the right.</summary>
public sealed class BipolarBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(BipolarBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RangeProperty = DependencyProperty.Register(
        nameof(Range), typeof(double), typeof(BipolarBar), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Track = Palette.Brush(Palette.Border);
    private static readonly Brush Positive = Palette.Brush("#34D399");
    private static readonly Brush Negative = Palette.Brush("#EF4444");
    private static readonly Pen CenterPen = LevelBar.Freeze(new Pen(Palette.Brush(Palette.TextMuted), 1));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Range { get => (double)GetValue(RangeProperty); set => SetValue(RangeProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        var mid = w / 2;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), 2, 2);
        var length = mid * Math.Clamp(Math.Abs(Value) / Range, 0, 1);
        var rect = Value >= 0 ? new Rect(mid, 0, length, h) : new Rect(mid - length, 0, length, h);
        dc.DrawRectangle(Value >= 0 ? Positive : Negative, null, rect);
        dc.DrawLine(CenterPen, new Point(mid, -2), new Point(mid, h + 2));
    }
}
