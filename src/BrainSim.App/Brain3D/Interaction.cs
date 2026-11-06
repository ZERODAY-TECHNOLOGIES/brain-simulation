using System.Numerics;
using System.Windows.Input;

namespace BrainSim.App.Brain3D;

/// <summary>Tells a click from a drag: left-drag rotates the brain, a click selects (D-37).</summary>
public sealed class ClickTracker
{
    private const float MaxMove = 4;
    private static readonly TimeSpan MaxHold = TimeSpan.FromMilliseconds(500);
    private (Vector2 At, TimeSpan When)? _down;

    public void Down(Vector2 at, TimeSpan when) => _down = (at, when);

    /// <summary>True when the press that ends here moved under 4 px and lasted under 500 ms.</summary>
    public bool IsClick(Vector2 at, TimeSpan when)
    {
        if (_down is not { } down)
        {
            return false;
        }

        _down = null;
        return Vector2.Distance(at, down.At) < MaxMove && when - down.When < MaxHold;
    }
}

/// <summary>The slow turn when nobody touches the brain.</summary>
public static class IdleSpin
{
    public const float DegreesPerSecond = 6;
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(5);

    public static bool Due(bool enabled, TimeSpan lastInput, TimeSpan now) => enabled && now - lastInput >= Idle;
}

public enum NavKind
{
    None,
    Orbit,
    Zoom,
    Reset,
}

/// <summary>A camera move from the keyboard. Angles in radians; <see cref="Zoom"/> scales the distance.</summary>
public readonly record struct NavAction(NavKind Kind, float Yaw = 0, float Pitch = 0, float Zoom = 1);

/// <summary>Arrows orbit in 10 degree steps, + and - zoom, Esc goes back to the whole brain.</summary>
public static class NavKeys
{
    private const float Step = 10 * MathF.PI / 180;

    public static NavAction Map(Key key) => key switch
    {
        Key.Left => new(NavKind.Orbit, Yaw: -Step),
        Key.Right => new(NavKind.Orbit, Yaw: Step),
        Key.Up => new(NavKind.Orbit, Pitch: Step),
        Key.Down => new(NavKind.Orbit, Pitch: -Step),
        Key.Add or Key.OemPlus => new(NavKind.Zoom, Zoom: 0.85f),
        Key.Subtract or Key.OemMinus => new(NavKind.Zoom, Zoom: 1.15f),
        Key.Escape => new(NavKind.Reset),
        _ => new(NavKind.None),
    };
}
