using BrainSim.Engine.Journal;
using ScottPlot;
using ScottPlot.WPF;

namespace BrainSim.App;

/// <summary>Redraws a chart from journal records. Charts are rebuilt each time; at a few points per pixel that is cheap.</summary>
public static class Charts
{
    public sealed record Series(string Label, string Hex, Func<TickRecord, double> Value, bool Step = false);

    public static void Draw(
        WpfPlot control,
        IReadOnlyList<TickRecord> records,
        IEnumerable<Series> series,
        bool timeAxis = false,
        IEnumerable<(double Y, string Label)>? thresholds = null,
        bool legend = false,
        (double Low, double High)? atLeast = null)
    {
        var plot = control.Plot;
        plot.Clear();
        PlotStyle.Apply(plot);

        if (records.Count > 1)
        {
            double[] xs = [.. records.Select(r => timeAxis ? r.Time.ToOADate() : r.Tick)];
            foreach (var s in series)
            {
                var line = plot.Add.ScatterLine(xs, [.. records.Select(s.Value)]);
                line.Color = PlotStyle.Of(s.Hex);
                line.LineWidth = 1.5f;
                line.LegendText = s.Label;
                if (s.Step)
                {
                    line.ConnectStyle = ConnectStyle.StepHorizontal;
                }
            }

            foreach (var (y, label) in thresholds ?? [])
            {
                var h = plot.Add.HorizontalLine(y);
                h.Color = PlotStyle.Of(Palette.TextMuted);
                h.LinePattern = LinePattern.Dashed;
                h.LineWidth = 1;
                h.LegendText = label;
            }

            if (timeAxis)
            {
                // Short labels so the axis stays readable on small panels.
                var span = records[^1].Time - records[0].Time;
                var format = span.TotalDays > 1 ? "ddd HH:mm" : span.TotalMinutes < 10 ? "HH:mm:ss" : "HH:mm";
                var axis = plot.Axes.DateTimeTicksBottom();
                if (axis.TickGenerator is ScottPlot.TickGenerators.DateTimeAutomatic ticks)
                {
                    ticks.LabelFormatter = d => d.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
                }

                // The date axis replaces the bottom axis, so it needs the dark styling again.
                PlotStyle.Apply(plot);
            }

            plot.Legend.IsVisible = legend;
            plot.Axes.AutoScale();

            // Never zoom in so far that rounding noise around the baseline looks like a signal.
            if (atLeast is { } range)
            {
                var y = plot.Axes.GetLimits();
                plot.Axes.SetLimitsY(Math.Min(y.Bottom, range.Low), Math.Max(y.Top, range.High));
            }
        }

        control.Refresh();
    }
}
