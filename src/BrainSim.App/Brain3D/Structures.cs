using System.Numerics;
using BrainSim.Engine;
using BrainSim.Engine.Journal;

namespace BrainSim.App.Brain3D;

/// <summary>
/// What the 3D brain shows about each structure: its name, the substances it reports on hover, its bounding sphere
/// for focusing, and which structures are nuclei. Kept out of BrainView so it can be tested without a GPU (D-37).
/// </summary>
public static class Structures
{
    // Regions dense in CB1. The first ones keep their resting color; the others are recolored every frame first.
    public static readonly string[] Cb1Resting = ["caudate_l", "caudate_r", "putamen_l", "putamen_r", "cerebellum"];
    public static readonly string[] Cb1Recolored = ["amygdala_l", "amygdala_r", "hippocampus_l", "hippocampus_r"];

    public static bool IsCb1Dense(string key) =>
        Cb1Resting.Contains(key) || Cb1Recolored.Contains(key) || key.StartsWith("frontal", StringComparison.Ordinal);

    /// <summary>Proxy spheres for nuclei too small to have meshes, at the anchors MeshPrep computed.</summary>
    public static readonly (string Anchor, Substance Substance, float Radius, bool Hpa)[] Nuclei =
    [
        ("locus_coeruleus_l", Substance.Norepinephrine, 0.045f, false),
        ("locus_coeruleus_r", Substance.Norepinephrine, 0.045f, false),
        ("raphe", Substance.Serotonin, 0.05f, false),
        ("vta", Substance.Dopamine, 0.055f, false),
        ("basal_forebrain_l", Substance.Acetylcholine, 0.045f, false),
        ("basal_forebrain_r", Substance.Acetylcholine, 0.045f, false),
        ("scn", Substance.Melatonin, 0.035f, false),
        ("adrenal", Substance.Cortisol, 0.09f, true),
    ];

    public static string SubstancesText(string key, TickRecord f)
    {
        Substance[] shown = key switch
        {
            "hypothalamus" => [Substance.Oxytocin, Substance.Cortisol],
            "pineal" => [Substance.Melatonin],
            "pituitary" => [Substance.GrowthHormone, Substance.Oxytocin],
            "amygdala_l" or "amygdala_r" => [Substance.Cortisol, Substance.Norepinephrine],
            "hippocampus_l" or "hippocampus_r" => [Substance.Acetylcholine, Substance.Cortisol],
            "caudate_l" or "caudate_r" or "putamen_l" or "putamen_r" => [Substance.Dopamine],
            _ when key.StartsWith("frontal", StringComparison.Ordinal) => [Substance.Dopamine, Substance.Norepinephrine, Substance.Gaba],
            _ => [Substance.Norepinephrine, Substance.Gaba],
        };
        var text = "\n" + string.Join("   ", shown.Select(s => $"{s.Abbreviation()} {f.C[(int)s]:0.00}"));
        var cb1Dense = IsCb1Dense(key);
        return cb1Dense
            ? text + $"\nAEA {f.C[(int)Substance.Anandamide]:0.00}   2-AG {f.C[(int)Substance.Arachidonoylglycerol]:0.00}   CB1 tone {f.Cb1:+0.00;-0.00;0.00}"
            : text;
    }

    /// <summary>The anatomical name, with the side for keys ending in _l or _r.</summary>
    public static string Name(string key)
    {
        var side = key.EndsWith("_l", StringComparison.Ordinal) ? " (left)" : key.EndsWith("_r", StringComparison.Ordinal) ? " (right)" : "";
        var name = (side.Length > 0 ? key[..^2] : key) switch
        {
            "frontal" => "Frontal lobe",
            "parietal" => "Parietal lobe",
            "temporal" => "Temporal lobe",
            "occipital" => "Occipital lobe",
            "limbic" => "Cingulate and parahippocampal gyri",
            "insula" => "Insula",
            "orbital" => "Orbital gyri",
            "thalamus" => "Thalamus",
            "caudate" => "Caudate nucleus",
            "putamen" => "Putamen",
            "pallidus" => "Globus pallidus",
            "hippocampus" => "Hippocampus",
            "amygdala" => "Amygdala",
            "hypothalamus" => "Hypothalamus",
            "mammillary" => "Mammillary body",
            "corpus_callosum" => "Corpus callosum",
            "fornix" => "Fornix",
            "midbrain" => "Midbrain",
            "pons" => "Pons",
            "medulla" => "Medulla oblongata",
            "cerebellum" => "Cerebellum",
            "pineal" => "Pineal gland",
            "pituitary" => "Pituitary gland",
            "locus_coeruleus" => "Locus coeruleus",
            "raphe" => "Raphe nuclei",
            "vta" => "VTA and substantia nigra",
            "basal_forebrain" => "Nucleus basalis",
            "scn" => "Suprachiasmatic nucleus",
            "adrenal" => "Adrenal glands (HPA axis, not to scale)",
            var other => other,
        };
        return name + side;
    }

    /// <summary>A sphere around every vertex: the box center, and the distance to the farthest vertex.</summary>
    public static (Vector3 Center, float Radius) Bounds(IReadOnlyList<Vector3> positions)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        var center = (min + max) / 2;
        return (center, positions.Max(p => Vector3.Distance(p, center)));
    }
}
