using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Controls;

namespace BrainSim.App.Views;

public partial class ChatView : UserControl
{
    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.Chat.CollectionChanged += (_, e) =>
                {
                    Scroll.ScrollToEnd();
                    foreach (var item in e.NewItems?.OfType<INotifyPropertyChanged>() ?? [])
                    {
                        item.PropertyChanged += (_, _) => Scroll.ScrollToEnd();
                    }
                };
            }
        };
    }
}
