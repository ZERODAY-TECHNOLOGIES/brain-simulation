using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace BrainSim.App.Views;

public partial class DreamsView : UserControl
{
    public DreamsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.Dreams.CollectionChanged += (_, _) => Empty.Visibility = vm.Dreams.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        };
    }
}
