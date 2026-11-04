using System.Globalization;
using System.Text;
using BrainSim.Engine;

namespace BrainSim.App.Views;

public partial class SleepView : PanelBase
{
    // Hypnogram rows, top to bottom as in the spec figure: Wake, REM, N1, N2, N3.
    private static readonly Dictionary<string, double> Level = new() { ["Wake"] = 4, ["Rem"] = 3, ["N1"] = 2, ["N2"] = 1, ["N3"] = 0 };

    public SleepView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                Targets.Text = TargetsTable(vm);
            }
        };
    }

    protected override void Draw(MainViewModel vm)
    {
        if (vm.Frame is { } f)
        {
            Status.Text = f.Phase == "Wake" ? "Awake" : $"Asleep in {f.Phase}, cycle {f.Cycle + 1}";
        }

        Charts.Draw(Plot, Window(vm), [new Charts.Series("phase", Palette.Hex(Substance.Melatonin), t => Level.GetValueOrDefault(t.Phase), Step: true)], timeAxis: true);
        Plot.Plot.Axes.Left.SetTicks([0, 1, 2, 3, 4], ["N3", "N2", "N1", "REM", "Wake"]);
        Plot.Plot.Axes.SetLimitsY(-0.5, 4.5);
        Plot.Refresh();
    }

    private static string TargetsTable(MainViewModel vm)
    {
        string[] shown = ["NE", "5-HT", "ACh", "DA", "GH"];
        var text = new StringBuilder("Phase  " + string.Join(" ", shown.Select(s => s.PadLeft(5))) + "\n");
        foreach (var (phase, targets) in vm.Config.Sleep.PhaseTargets)
        {
            var values = shown.Select(s => targets.GetValueOrDefault(s, 1.0).ToString("0.00", CultureInfo.InvariantCulture).PadLeft(5));
            text.Append(phase.PadRight(6)).Append(' ').Append(string.Join(" ", values)).Append('\n');
        }

        return text.Append("\nCORT and MEL follow the clock.").ToString();
    }
}
