using BrainSim.Engine;

namespace BrainSim.App.Views;

public partial class ChemistryView : PanelBase
{
    public ChemistryView() => InitializeComponent();

    protected override void Draw(MainViewModel vm)
    {
        var series = vm.SubstanceRows
            .Where(r => r.Shown)
            .Select(r => new Charts.Series(r.Name, Palette.Hex(r.Substance), t => t.C[(int)r.Substance]));
        Charts.Draw(Plot, Window(vm), series, thresholds: [(1.0, "baseline")], legend: false, atLeast: (0.5, 1.5));
    }
}
