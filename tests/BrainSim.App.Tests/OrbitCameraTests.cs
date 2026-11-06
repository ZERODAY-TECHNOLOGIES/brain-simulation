using System.Numerics;
using BrainSim.App.Brain3D;

namespace BrainSim.App.Tests;

public class OrbitCameraTests
{
    private static readonly Vector3 Up = Vector3.UnitY;

    private static float Degrees(float radians) => radians * 180 / MathF.PI;

    private static float AngleFromUp(Vector3 position, Vector3 target) =>
        Degrees(MathF.Acos(Vector3.Dot(Vector3.Normalize(position - target), Up)));

    [Fact]
    public void A_framed_sphere_fits_the_field_of_view_with_a_margin()
    {
        var center = new Vector3(0.3f, -0.2f, 0.5f);
        const float radius = 0.4f;
        const float fov = 40;

        var (position, look) = OrbitCamera.Frame(center, radius, new Vector3(1, 0, 0), fov);

        // The camera looks at the center, along the given direction.
        Assert.True(Vector3.Distance(center, position + look) < 1e-5f);
        Assert.True(Vector3.Distance(Vector3.Normalize(look), Vector3.UnitX) < 1e-5f);

        // The sphere's half-angle seen from the camera is inside the half field of view, but not tiny.
        var halfAngle = Degrees(MathF.Asin(radius / look.Length()));
        Assert.InRange(halfAngle, fov / 2 * 0.6f, fov / 2 * 0.9f);
    }

    [Fact]
    public void Orbit_keeps_the_distance_and_the_target()
    {
        var target = new Vector3(0.2f, 0.1f, -0.3f);
        var position = new Vector3(-4, 0.5f, 0.3f);

        var moved = OrbitCamera.Orbit(position, target, Up, yaw: 0.5f, pitch: 0.3f);

        Assert.Equal(Vector3.Distance(position, target), Vector3.Distance(moved, target), 4);
        Assert.NotEqual(position, moved);
    }

    [Fact]
    public void Yaw_by_a_full_turn_comes_back_to_the_start()
    {
        var position = new Vector3(-4, 0.5f, 0.3f);

        var moved = OrbitCamera.Orbit(position, Vector3.Zero, Up, yaw: 2 * MathF.PI, pitch: 0);

        Assert.True(Vector3.Distance(position, moved) < 1e-4f, $"{moved}");
    }

    [Fact]
    public void Pitch_lifts_the_camera_and_stops_five_degrees_short_of_the_poles()
    {
        var position = new Vector3(-4, 0, 0);

        var lifted = OrbitCamera.Orbit(position, Vector3.Zero, Up, yaw: 0, pitch: 0.2f);
        var over = OrbitCamera.Orbit(position, Vector3.Zero, Up, yaw: 0, pitch: 3f);
        var under = OrbitCamera.Orbit(position, Vector3.Zero, Up, yaw: 0, pitch: -3f);

        Assert.True(lifted.Y > 0, "positive pitch moves the camera up");
        Assert.Equal(5, AngleFromUp(over, Vector3.Zero), 2);
        Assert.Equal(175, AngleFromUp(under, Vector3.Zero), 2);
    }

    [Fact]
    public void A_key_moves_the_camera_the_way_its_action_says()
    {
        var target = new Vector3(0, 0.2f, 0);
        var position = new Vector3(-4, 0.2f, 0);

        var orbited = OrbitCamera.Apply(NavKeys.Map(System.Windows.Input.Key.Right), position, target, Up);
        var zoomed = OrbitCamera.Apply(NavKeys.Map(System.Windows.Input.Key.Add), position, target, Up);
        var ignored = OrbitCamera.Apply(NavKeys.Map(System.Windows.Input.Key.A), position, target, Up);

        Assert.Equal(4, Vector3.Distance(orbited, target), 4);
        Assert.Equal(10, Degrees(MathF.Acos(Vector3.Dot(Vector3.Normalize(orbited - target), Vector3.Normalize(position - target)))), 2);
        Assert.Equal(4 * 0.85f, Vector3.Distance(zoomed, target), 4);
        Assert.Equal(position, ignored);
    }

    [Theory]
    [InlineData(0.5f, 2.0f)]    // closer, not past min
    [InlineData(0.1f, 1.0f)]    // clamped at min
    [InlineData(10f, 8.0f)]     // clamped at max
    public void Zoom_scales_the_distance_within_bounds(float factor, float expected)
    {
        var target = new Vector3(1, 1, 1);
        var position = target + new Vector3(0, 0, 4);

        var zoomed = OrbitCamera.Zoom(position, target, factor, min: 1, max: 8);

        Assert.Equal(expected, Vector3.Distance(zoomed, target), 4);
        Assert.True(Vector3.Distance(Vector3.Normalize(zoomed - target), Vector3.UnitZ) < 1e-5f, "zoom keeps the direction");
    }
}
