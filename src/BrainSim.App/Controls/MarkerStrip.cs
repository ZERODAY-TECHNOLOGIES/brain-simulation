using System.Windows;
using System.Windows.Media;

namespace BrainSim.App.Controls;

/// <summary>Event markers above the timeline: messages, Lab actions and sleep transitions.</summary>
public sealed class MarkerStrip : FrameworkElement
{
    public static readonly DependencyProperty MarkersProperty = DependencyProperty.Register(
        nameof(Markers), typeof(IReadOnlyList<Marker>), typeof(MarkerStrip),
        new FrameworkPropertyMetadata(Array.Empty<Marker>(), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FirstProperty = Register(nameof(First));
    public static readonly DependencyProperty LastProperty = Register(nameof(Last));

    public IReadOnlyList<Marker> Markers { get => (IReadOnlyList<Marker>)GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    public double First { get => (double)GetValue(FirstProperty); set => SetValue(FirstProperty, value); }
    public double Last { get => (double)GetValue(LastProperty); set => SetValue(LastProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var span = Math.Max(1, Last - First);
        foreach (var marker in Markers)
        {
            var x = (marker.Tick - First) / span * ActualWidth;
            if (x >= 0 && x <= ActualWidth)
            {
                dc.DrawRectangle(marker.Brush, null, new Rect(x - 1, 0, 2, ActualHeight));
            }
        }
    }

    private static DependencyProperty Register(string name) => DependencyProperty.Register(
        name, typeof(double), typeof(MarkerStrip), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
}
