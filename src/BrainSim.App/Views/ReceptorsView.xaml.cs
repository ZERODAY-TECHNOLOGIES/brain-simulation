using BrainSim.Engine;

namespace BrainSim.App.Views;

public partial class ReceptorsView : PanelBase
{
    public ReceptorsView() => InitializeComponent();

    protected override void Draw(MainViewModel vm)
    {
        var series = Substances.All.Select(s => new Charts.Series(s.Abbreviation(), Palette.Hex(s), t => t.Rho[(int)s]));
        Charts.Draw(Plot, Window(vm), series, thresholds: [(1.0, "rho = 1")], legend: false, atLeast: (0.8, 1.2));
    }
}
