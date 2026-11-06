using System.Windows.Input;
using BrainSim.App.Brain3D;
using HelixToolkit.Wpf.SharpDX;

namespace BrainSim.App.Tests;

public class NavigationTests
{
    [Fact]
    public void Nothing_fades_until_something_is_selected()
    {
        var selection = new Selection();

        Assert.Equal(1f, selection.Factor("hippocampus_l"));
        Assert.Equal(1f, selection.Factor("vta"));
    }

    [Fact]
    public void Selecting_fades_everything_else_and_selecting_again_moves_the_highlight()
    {
        var selection = new Selection();

        selection.Select("hippocampus_l");
        Assert.Equal(1f, selection.Factor("hippocampus_l"));
        Assert.Equal(Selection.Faded, selection.Factor("amygdala_l"));
        Assert.Equal(Selection.Faded, selection.Factor("vta"));

        selection.Select("vta");
        Assert.Equal(Selection.Faded, selection.Factor("hippocampus_l"));
        Assert.Equal(1f, selection.Factor("vta"));
        Assert.Equal("vta", selection.Key);
    }

    [Fact]
    public void Clearing_restores_every_structure()
    {
        var selection = new Selection();
        selection.Select("hippocampus_l");

        selection.Clear();

        Assert.Null(selection.Key);
        Assert.Equal(1f, selection.Factor("amygdala_l"));
    }

    [Fact]
    public void Left_drag_rotates_and_right_drag_pans()
    {
        // WPF controls need an STA thread. The viewport is never shown, so no GPU is involved.
        List<(MouseAction Action, ICommand Command)> defaults = [];
        List<(MouseAction Action, ICommand Command)> bindings = [];
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var viewport = new Viewport3DX();
                defaults = [.. viewport.InputBindings.OfType<MouseBinding>().Select(b => (((MouseGesture)b.Gesture).MouseAction, b.Command))];
                Navigation.Bind(viewport);
                bindings = [.. viewport.InputBindings.OfType<MouseBinding>().Select(b => (((MouseGesture)b.Gesture).MouseAction, b.Command))];
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(error);

        // Helix's own scheme lives in these bindings (right-drag rotates), so replacing them is what changes it.
        Assert.Contains((MouseAction.RightClick, (ICommand)ViewportCommands.Rotate), defaults);
        Assert.Contains((MouseAction.LeftClick, (ICommand)ViewportCommands.Rotate), bindings);
        Assert.Contains((MouseAction.RightClick, (ICommand)ViewportCommands.Pan), bindings);
        Assert.Single(bindings, b => b.Action == MouseAction.LeftClick);
        Assert.Single(bindings, b => b.Action == MouseAction.RightClick);
    }
}
