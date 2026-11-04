using BrainSim.Engine;
using BrainSim.Engine.Circadian;

namespace BrainSim.App.Views;

public partial class ClockView : PanelBase
{
    public ClockView() => InitializeComponent();

    protected override void Draw(MainViewModel vm)
    {
        if (vm.Frame is not { } f)
        {
            return;
        }

        var circadian = vm.Config.Circadian;
        var sleep = vm.Config.Sleep;
        Dial.Phi = f.Phi;
        Dial.DarkStart = circadian.DarkStart;
        Dial.DarkEnd = circadian.DarkEnd;
        Dial.Asleep = f.Phase != "Wake";

        Details.Text =
            $"Clock time  {f.Time:HH:mm:ss}\n" +
            $"Phase phi   {f.Phi:00.00} h\n" +
            $"Alertness   {CircadianClock.Alertness(f.Phi):+0.00;-0.00}\n" +
            $"MEL         {f.C[(int)Substance.Melatonin]:0.00}   sleep needs > {sleep.OnsetMelatonin:0.0}\n" +
            $"Pressure P  {f.SleepPressure:+0.00;-0.00}   sleep > {sleep.OnsetPressure:0.0}, wake < {sleep.WakePressure:0.0}\n" +
            $"ADO         {f.C[(int)Substance.Adenosine]:0.00}\n" +
            $"CORT        {f.C[(int)Substance.Cortisol]:0.00}";

        Charts.Draw(Plot, Window(vm), [
            new Charts.Series("MEL", Palette.Hex(Substance.Melatonin), t => t.C[(int)Substance.Melatonin]),
            new Charts.Series("Sleep pressure P", Palette.Hex(Substance.Adenosine), t => t.SleepPressure),
            new Charts.Series("CORT", Palette.Hex(Substance.Cortisol), t => t.C[(int)Substance.Cortisol]),
        ], timeAxis: true, thresholds: [(sleep.OnsetPressure, "sleep onset P"), (sleep.WakePressure, "wake P")], legend: true, atLeast: (-0.5, 2.5));
    }
}
