using System.Numerics;
using BrainSim.Engine;
using BrainSim.Engine.Journal;

namespace BrainSim.App.Brain3D;

/// <summary>
/// What the 3D brain shows about each structure: its name, the substances it reports on hover, its bounding sphere
/// for focusing, and which structures are nuclei. Kept out of BrainView so it can be tested without a GPU (D-37).
/// </summary>
/// <summary>One entry of the "Focus on" list. Nuclei carry their substance, for Open in Lab.</summary>
public sealed record Structure(string Key, string Name, string Group, Substance? Substance)
{
    public override string ToString() => Name;
}

public static class Structures
{
    /// <summary>Regions dense in CB1, besides the frontal cortex: they take the endocannabinoid tint (D-36).</summary>
    private static readonly string[] Cb1Regions =
        ["caudate_l", "caudate_r", "putamen_l", "putamen_r", "cerebellum", "amygdala_l", "amygdala_r", "hippocampus_l", "hippocampus_r"];

    public static bool IsCb1Dense(string key) => Cb1Regions.Contains(key) || key.StartsWith("frontal", StringComparison.Ordinal);

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

    /// <summary>What each region reports on hover, by key without its side. The rest report NE and GABA.</summary>
    private static readonly Dictionary<string, Substance[]> Shown = new()
    {
        ["hypothalamus"] = [Substance.Oxytocin, Substance.Cortisol],
        ["pineal"] = [Substance.Melatonin],
        ["pituitary"] = [Substance.GrowthHormone, Substance.Oxytocin],
        ["amygdala"] = [Substance.Cortisol, Substance.Norepinephrine],
        ["hippocampus"] = [Substance.Acetylcholine, Substance.Cortisol],
        ["caudate"] = [Substance.Dopamine],
        ["putamen"] = [Substance.Dopamine],
        ["frontal"] = [Substance.Dopamine, Substance.Norepinephrine, Substance.Gaba],
    };

    private static readonly Substance[] ShownElsewhere = [Substance.Norepinephrine, Substance.Gaba];

    public static string SubstancesText(string key, TickRecord f)
    {
        var shown = Shown.GetValueOrDefault(Unsided(key), ShownElsewhere);
        var text = "\n" + string.Join("   ", shown.Select(s => $"{s.Abbreviation()} {f.C[(int)s]:0.00}"));
        return IsCb1Dense(key)
            ? text + $"\nAEA {f.C[(int)Substance.Anandamide]:0.00}   2-AG {f.C[(int)Substance.Arachidonoylglycerol]:0.00}   CB1 tone {f.Cb1:+0.00;-0.00;0.00}"
            : text;
    }

    private static readonly Dictionary<string, string> Names = new()
    {
        ["frontal"] = "Frontal lobe",
        ["parietal"] = "Parietal lobe",
        ["temporal"] = "Temporal lobe",
        ["occipital"] = "Occipital lobe",
        ["limbic"] = "Cingulate and parahippocampal gyri",
        ["insula"] = "Insula",
        ["orbital"] = "Orbital gyri",
        ["thalamus"] = "Thalamus",
        ["caudate"] = "Caudate nucleus",
        ["putamen"] = "Putamen",
        ["pallidus"] = "Globus pallidus",
        ["hippocampus"] = "Hippocampus",
        ["amygdala"] = "Amygdala",
        ["hypothalamus"] = "Hypothalamus",
        ["mammillary"] = "Mammillary body",
        ["corpus_callosum"] = "Corpus callosum",
        ["fornix"] = "Fornix",
        ["midbrain"] = "Midbrain",
        ["pons"] = "Pons",
        ["medulla"] = "Medulla oblongata",
        ["cerebellum"] = "Cerebellum",
        ["pineal"] = "Pineal gland",
        ["pituitary"] = "Pituitary gland",
        ["locus_coeruleus"] = "Locus coeruleus",
        ["raphe"] = "Raphe nuclei",
        ["vta"] = "VTA and substantia nigra",
        ["basal_forebrain"] = "Nucleus basalis",
        ["scn"] = "Suprachiasmatic nucleus",
        ["adrenal"] = "Adrenal glands (HPA axis, not to scale)",
    };

    private static string Side(string key) =>
        key.EndsWith("_l", StringComparison.Ordinal) ? " (left)" : key.EndsWith("_r", StringComparison.Ordinal) ? " (right)" : "";

    /// <summary>The key without its _l or _r.</summary>
    private static string Unsided(string key) => Side(key).Length > 0 ? key[..^2] : key;

    /// <summary>The anatomical name, with the side for keys ending in _l or _r. An unknown key comes back as it is.</summary>
    public static string Name(string key) => Names.GetValueOrDefault(Unsided(key), Unsided(key)) + Side(key);

    private static readonly string[] GroupOrder = ["cortex", "subcortical", "brainstem", "gland", "nucleus"];

    /// <summary>
    /// Every structure you can focus on: each region, then the brain's nuclei, by group and then name. The HPA inset
    /// (adrenals) is left out, since it is drawn outside the brain and not to scale.
    /// </summary>
    public static IReadOnlyList<Structure> Catalog(BrainModel model)
    {
        var all = new List<Structure>();
        foreach (var r in model.Regions)
        {
            all.Add(new Structure(r.Key, Name(r.Key), r.Group, null));
        }

        foreach (var n in Nuclei)
        {
            if (!n.Hpa)
            {
                all.Add(new Structure(n.Anchor, Name(n.Anchor), "nucleus", n.Substance));
            }
        }

        all.Sort(CompareForList);
        return all;
    }

    private static int CompareForList(Structure a, Structure b)
    {
        var byGroup = Array.IndexOf(GroupOrder, a.Group).CompareTo(Array.IndexOf(GroupOrder, b.Group));
        return byGroup != 0 ? byGroup : string.CompareOrdinal(a.Name, b.Name);
    }

    /// <summary>The substance a nucleus releases, which "Open in Lab" shows; null for regions.</summary>
    public static Substance? LabSubstance(string key) =>
        Array.Find(Nuclei, n => n.Anchor == key) is { Anchor: not null } n ? n.Substance : null;

    /// <summary>
    /// What a click selects, from the structures under the pointer, nearest first. The cortex is a see-through shell in
    /// front of everything, so a nucleus (drawn on top) wins, then the nearest deep structure, and only then the cortex.
    /// </summary>
    public static string? Pick(IEnumerable<(string Key, string Group)> inDepthOrder)
    {
        var hits = inDepthOrder.ToList();
        return hits.FirstOrDefault(h => h.Group == "nucleus").Key
            ?? hits.FirstOrDefault(h => h.Group != "cortex").Key
            ?? hits.FirstOrDefault().Key;
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
