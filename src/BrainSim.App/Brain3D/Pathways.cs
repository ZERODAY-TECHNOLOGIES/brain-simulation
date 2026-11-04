using System.Numerics;
using BrainSim.Engine;

namespace BrainSim.App.Brain3D;

/// <summary>A projection from a source nucleus to a target, drawn as a quadratic Bezier curve.</summary>
public sealed record Pathway(Substance Substance, string Source, string Target, Vector3 From, Vector3 Control, Vector3 To, bool Hpa)
{
    public Vector3 At(float t)
    {
        var u = 1 - t;
        return u * u * From + 2 * u * t * Control + t * t * To;
    }
}

/// <summary>The anatomical mapping from docs/ui-design.md, turned into curves between anchors.</summary>
public static class Pathways
{
    private static readonly (Substance Substance, string[] Sources, string[] Targets, bool Hpa)[] Map =
    [
        (Substance.Norepinephrine, ["locus_coeruleus_l"], ["frontal_l", "parietal_l", "temporal_l", "occipital_l", "thalamus_l", "cerebellum"], false),
        (Substance.Norepinephrine, ["locus_coeruleus_r"], ["frontal_r", "parietal_r", "temporal_r", "occipital_r", "thalamus_r", "cerebellum"], false),
        (Substance.Acetylcholine, ["basal_forebrain_l"], ["frontal_l", "parietal_l", "temporal_l", "hippocampus_l"], false),
        (Substance.Acetylcholine, ["basal_forebrain_r"], ["frontal_r", "parietal_r", "temporal_r", "hippocampus_r"], false),
        (Substance.Dopamine, ["vta"], ["caudate_l", "caudate_r", "putamen_l", "putamen_r", "frontal_l", "frontal_r", "orbital"], false),
        (Substance.Serotonin, ["raphe"], ["frontal_l", "frontal_r", "parietal_l", "parietal_r", "hippocampus_l", "hippocampus_r", "amygdala_l", "amygdala_r"], false),
        (Substance.Oxytocin, ["hypothalamus"], ["pituitary", "amygdala_l", "amygdala_r"], false),
        (Substance.Melatonin, ["scn"], ["pineal"], false),
        (Substance.Melatonin, ["pineal"], ["thalamus_l", "thalamus_r", "hypothalamus"], false),
        (Substance.Cortisol, ["hypothalamus"], ["pituitary"], true),
        (Substance.Cortisol, ["pituitary"], ["adrenal"], true),
        (Substance.Cortisol, ["adrenal"], ["hippocampus_l", "hippocampus_r", "frontal_l", "frontal_r"], true),
    ];

    public static List<Pathway> Build(BrainModel model)
    {
        var paths = new List<Pathway>();
        foreach (var (substance, sources, targets, hpa) in Map)
        {
            foreach (var source in sources)
            {
                foreach (var target in targets)
                {
                    var from = model.Anchors[source];
                    var to = model.Anchors[target];

                    // Bend the curve away from the center of the brain so pathways arc around structures.
                    var mid = (from + to) / 2;
                    var outward = mid.LengthSquared() > 1e-6f ? Vector3.Normalize(mid) : Vector3.UnitY;
                    var control = mid + outward * (Vector3.Distance(from, to) * 0.25f) + new Vector3(0, 0.08f, 0);
                    paths.Add(new Pathway(substance, source, target, from, control, to, hpa));
                }
            }
        }

        return paths;
    }
}

/// <summary>
/// Particles flowing along pathways. Each pathway spawns particles at a rate set every frame;
/// a particle runs from source to target in about 1.5 seconds and fades in and out on the way.
/// </summary>
public sealed class ParticleField(IReadOnlyList<Pathway> paths, int maxParticles = 20_000)
{
    private readonly List<(int Path, float T, float Speed)> _live = new(maxParticles);
    private readonly float[] _debt = new float[paths.Count];
    private readonly Random _random = new(1);

    public int Count => _live.Count;

    /// <summary>Advances the particles by <paramref name="dt"/> seconds; <paramref name="rate"/> is particles per second per path.</summary>
    public void Update(float dt, Func<int, float> rate, Func<int, bool> visible)
    {
        for (var i = _live.Count - 1; i >= 0; i--)
        {
            var p = _live[i];
            p.T += p.Speed * dt;
            if (p.T >= 1 || !visible(p.Path))
            {
                _live[i] = _live[^1];
                _live.RemoveAt(_live.Count - 1);
            }
            else
            {
                _live[i] = p;
            }
        }

        for (var path = 0; path < paths.Count; path++)
        {
            if (!visible(path))
            {
                _debt[path] = 0;
                continue;
            }

            _debt[path] += rate(path) * dt;
            while (_debt[path] >= 1 && _live.Count < maxParticles)
            {
                _debt[path] -= 1;
                _live.Add((path, (float)_random.NextDouble() * 0.05f, 0.55f + (float)_random.NextDouble() * 0.3f));
            }

            _debt[path] = Math.Min(_debt[path], 5);
        }
    }

    public void Clear()
    {
        _live.Clear();
        Array.Clear(_debt);
    }

    /// <summary>Positions and colors for the point cloud. Alpha follows sin(pi t), so particles fade at both ends.</summary>
    public void Fill(List<Vector3> positions, List<Vector4> colors, Func<Substance, Vector3> color)
    {
        positions.Clear();
        colors.Clear();
        foreach (var (path, t, _) in _live)
        {
            var p = paths[path];
            positions.Add(p.At(t));
            var c = color(p.Substance);
            colors.Add(new Vector4(c, MathF.Sin(MathF.PI * t)));
        }
    }
}
