using System.Windows.Input;
using HelixToolkit.Wpf.SharpDX;

namespace BrainSim.App.Brain3D;

/// <summary>Which structure is in focus. Everything else fades to <see cref="Faded"/> of its look (D-37).</summary>
public sealed class Selection
{
    public const float Faded = 0.15f;

    public string? Key { get; private set; }

    public void Select(string key) => Key = key;

    public void Clear() => Key = null;

    /// <summary>The share of its normal alpha and glow a structure keeps: all of it unless another one is in focus.</summary>
    public float Factor(string key) => Key is null || key == Key ? 1f : Faded;
}

/// <summary>The mouse scheme of the 3D brain, as in most 3D viewers: left-drag rotates, right-drag pans (D-37).</summary>
public static class Navigation
{
    /// <summary>Replaces Helix's mouse bindings (right-drag rotates, middle-drag pans). The wheel still zooms.</summary>
    public static void Bind(Viewport3DX viewport)
    {
        foreach (var binding in viewport.InputBindings.OfType<MouseBinding>().ToList())
        {
            viewport.InputBindings.Remove(binding);
        }

        viewport.InputBindings.Add(new MouseBinding(ViewportCommands.Rotate, new MouseGesture(MouseAction.LeftClick)));
        viewport.InputBindings.Add(new MouseBinding(ViewportCommands.Pan, new MouseGesture(MouseAction.RightClick)));
    }
}
