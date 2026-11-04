using System.Windows;

namespace BrainSim.App;

public partial class MainWindow : Window
{
    // ponytail: the panel layout is not saved between runs. AvalonDock 5 moved that to Dirkster.AvalonDock.Serializer.Xml; add it when rearranging every start gets old.
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Content inside AvalonDock does not reliably inherit the window's DataContext, so set it on each panel.
        foreach (var panel in new FrameworkElement[] { ChemistryPanel, ChatPanel, ReceptorsPanel, EffectorsPanel, ClockPanel, SleepPanel, LabPanel })
        {
            panel.DataContext = viewModel;
        }
    }
}
