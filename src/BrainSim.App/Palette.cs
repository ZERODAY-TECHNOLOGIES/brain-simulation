using System.Windows.Media;
using BrainSim.Engine;

namespace BrainSim.App;

/// <summary>Colors from docs/ui-design.md. One color per substance, used everywhere.</summary>
public static class Palette
{
    public const string Background = "#0B0F14";
    public const string Surface = "#121821";
    public const string Border = "#1F2A36";
    public const string Text = "#E6EDF3";
    public const string TextMuted = "#8B98A5";
    public const string Warn = "#F59E0B";

    public static readonly IReadOnlyList<string> SubstanceHex =
    [
        "#F5A524", // NE
        "#22D3EE", // ACh
        "#D946EF", // DA
        "#3B82F6", // GABA
        "#FDA4AF", // OXT
        "#A78BFA", // 5-HT
        "#EF4444", // CORT
        "#34D399", // GH
        "#C7D2FE", // MEL
        "#A3826B", // ADO
        "#D9F99D", // AEA
        "#84CC16", // 2-AG
    ];

    public static string Hex(Substance s) => SubstanceHex[(int)s];

    public static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush Brush(Substance s) => Brush(Hex(s));
}
