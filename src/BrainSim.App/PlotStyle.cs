using ScottPlot;

namespace BrainSim.App;

/// <summary>Dark chart styling shared by every panel.</summary>
public static class PlotStyle
{
    public static void Apply(Plot plot)
    {
        plot.FigureBackground.Color = Color.FromHex(Palette.Surface);
        plot.DataBackground.Color = Color.FromHex(Palette.Background);
        plot.Axes.Color(Color.FromHex(Palette.TextMuted));
        plot.Grid.MajorLineColor = Color.FromHex(Palette.Border);
        plot.Legend.BackgroundColor = Color.FromHex(Palette.Surface);
        plot.Legend.FontColor = Color.FromHex(Palette.Text);
        plot.Legend.OutlineColor = Color.FromHex(Palette.Border);
    }

    public static Color Of(string hex) => Color.FromHex(hex);
}
