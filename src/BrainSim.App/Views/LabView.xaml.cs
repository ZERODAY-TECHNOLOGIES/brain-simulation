using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BrainSim.Engine;

namespace BrainSim.App.Views;

public partial class LabView : UserControl
{
    public LabView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old)
            {
                old.LabRequested -= BringIntoView;
            }

            if (e.NewValue is MainViewModel vm)
            {
                vm.LabRequested += BringIntoView;
            }
        };
    }

    /// <summary>Scrolls the substance's row into view once the Lab is laid out (it may have just been brought forward).</summary>
    private void BringIntoView(Substance s) =>
        Dispatcher.BeginInvoke(() => (Rows.ItemContainerGenerator.ContainerFromIndex((int)s) as FrameworkElement)?.BringIntoView(),
            DispatcherPriority.Loaded);
}
