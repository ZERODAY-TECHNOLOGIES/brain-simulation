using System.Numerics;
using System.Windows.Input;
using BrainSim.App.Brain3D;

namespace BrainSim.App.Tests;

public class InteractionTests
{
    private static readonly TimeSpan T0 = TimeSpan.FromSeconds(100);

    [Theory]
    [InlineData(0, 0, 100, true)]     // a still press
    [InlineData(3, 0, 100, true)]     // a little jitter is still a click
    [InlineData(10, 0, 100, false)]   // a drag: the camera rotated
    [InlineData(0, 0, 800, false)]    // held too long
    public void A_click_is_a_short_still_press(float dx, float dy, int ms, bool click)
    {
        var tracker = new ClickTracker();
        tracker.Down(new Vector2(200, 150), T0);

        Assert.Equal(click, tracker.IsClick(new Vector2(200 + dx, 150 + dy), T0 + TimeSpan.FromMilliseconds(ms)));
    }

    [Fact]
    public void A_release_without_a_press_is_not_a_click()
    {
        Assert.False(new ClickTracker().IsClick(new Vector2(1, 1), T0));
    }

    [Theory]
    [InlineData(false, 60, false)]
    [InlineData(true, 4, false)]
    [InlineData(true, 5, true)]
    [InlineData(true, 60, true)]
    public void Idle_spin_waits_five_seconds_after_the_last_input(bool enabled, int secondsSinceInput, bool due)
    {
        Assert.Equal(due, IdleSpin.Due(enabled, lastInput: T0, now: T0 + TimeSpan.FromSeconds(secondsSinceInput)));
    }

    [Theory]
    [InlineData(Key.Left, NavKind.Orbit, -10, 0, 1)]
    [InlineData(Key.Right, NavKind.Orbit, 10, 0, 1)]
    [InlineData(Key.Up, NavKind.Orbit, 0, 10, 1)]
    [InlineData(Key.Down, NavKind.Orbit, 0, -10, 1)]
    [InlineData(Key.Add, NavKind.Zoom, 0, 0, 0.85f)]
    [InlineData(Key.OemPlus, NavKind.Zoom, 0, 0, 0.85f)]
    [InlineData(Key.Subtract, NavKind.Zoom, 0, 0, 1.15f)]
    [InlineData(Key.OemMinus, NavKind.Zoom, 0, 0, 1.15f)]
    [InlineData(Key.Escape, NavKind.Reset, 0, 0, 1)]
    [InlineData(Key.A, NavKind.None, 0, 0, 1)]
    public void Keys_map_to_camera_moves(Key key, NavKind kind, float yawDegrees, float pitchDegrees, float zoom)
    {
        var action = NavKeys.Map(key);

        Assert.Equal(kind, action.Kind);
        Assert.Equal(yawDegrees * MathF.PI / 180, action.Yaw, 5);
        Assert.Equal(pitchDegrees * MathF.PI / 180, action.Pitch, 5);
        Assert.Equal(zoom, action.Zoom, 5);
    }
}
