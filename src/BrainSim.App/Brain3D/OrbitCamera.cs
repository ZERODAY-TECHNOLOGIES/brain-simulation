using System.Numerics;

namespace BrainSim.App.Brain3D;

/// <summary>
/// Camera math for the 3D brain (D-37), kept apart from Helix so it can be tested without a GPU.
/// The camera orbits a target: position plus look direction is the target, so Helix rotates around it.
/// </summary>
public static class OrbitCamera
{
    /// <summary>A framed sphere fills this share of the half field of view.</summary>
    private const float Fill = 0.75f;
    private const float PoleMargin = 5 * MathF.PI / 180;

    /// <summary>Where to put the camera to see a sphere whole, looking along <paramref name="viewDirection"/>.</summary>
    public static (Vector3 Position, Vector3 Look) Frame(Vector3 center, float radius, Vector3 viewDirection, float fieldOfViewDegrees)
    {
        var halfAngle = Fill * fieldOfViewDegrees / 2 * MathF.PI / 180;
        var distance = radius / MathF.Sin(halfAngle);
        var position = center - Vector3.Normalize(viewDirection) * distance;
        return (position, center - position);
    }

    /// <summary>
    /// Turns the camera around <paramref name="target"/>: <paramref name="yaw"/> around the up axis, then
    /// <paramref name="pitch"/> toward it (positive moves the camera up). The distance stays, and the camera stops
    /// five degrees short of the poles, where the up direction would flip.
    /// </summary>
    public static Vector3 Orbit(Vector3 position, Vector3 target, Vector3 up, float yaw, float pitch)
    {
        var u = Vector3.Normalize(up);
        var offset = Vector3.Transform(position - target, Quaternion.CreateFromAxisAngle(u, yaw));
        var distance = offset.Length();
        var level = offset - Vector3.Dot(offset, u) * u;
        if (level.LengthSquared() < 1e-12f)
        {
            return target + offset; // straight above or below: no level direction to pitch along
        }

        var fromUp = MathF.Acos(Math.Clamp(Vector3.Dot(offset / distance, u), -1, 1));
        var newFromUp = Math.Clamp(fromUp - pitch, PoleMargin, MathF.PI - PoleMargin);
        return target + distance * (MathF.Sin(newFromUp) * Vector3.Normalize(level) + MathF.Cos(newFromUp) * u);
    }

    /// <summary>Where a keyboard move takes the camera. Zoom stays between 0.3 and 12 scene units from the target.</summary>
    public static Vector3 Apply(NavAction action, Vector3 position, Vector3 target, Vector3 up) => action.Kind switch
    {
        NavKind.Orbit => Orbit(position, target, up, action.Yaw, action.Pitch),
        NavKind.Zoom => Zoom(position, target, action.Zoom, 0.3f, 12f),
        _ => position,
    };

    /// <summary>Moves the camera toward or away from the target by <paramref name="factor"/>, within [min, max].</summary>
    public static Vector3 Zoom(Vector3 position, Vector3 target, float factor, float min, float max)
    {
        var offset = position - target;
        var distance = Math.Clamp(offset.Length() * factor, min, max);
        return target + Vector3.Normalize(offset) * distance;
    }
}
