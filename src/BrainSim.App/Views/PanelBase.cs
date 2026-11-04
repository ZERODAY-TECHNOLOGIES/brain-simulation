using System.Windows.Controls;
using BrainSim.Engine.Journal;

namespace BrainSim.App.Views;

/// <summary>Panels redraw on the view model's frame, at most every 150 ms and only while visible.</summary>
public abstract class PanelBase : UserControl
{
    private DateTime _lastDraw;
    private MainViewModel? _viewModel;

    protected PanelBase()
    {
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.FrameChanged -= OnFrame;
            }

            _viewModel = DataContext as MainViewModel;
            if (_viewModel is not null)
            {
                _viewModel.FrameChanged += OnFrame;
            }
        };
    }

    protected abstract void Draw(MainViewModel vm);

    /// <summary>The records in the chart window that ends at the cursor.</summary>
    protected static List<TickRecord> Window(MainViewModel vm, int maxPoints = 1500) =>
        vm.History.Window(vm.CursorTick - vm.SelectedWindow.Ticks, vm.CursorTick, maxPoints);

    private void OnFrame()
    {
        var now = DateTime.UtcNow;
        if (!IsVisible || _viewModel is null || (now - _lastDraw).TotalMilliseconds < 150)
        {
            return;
        }

        _lastDraw = now;
        Draw(_viewModel);
    }
}
