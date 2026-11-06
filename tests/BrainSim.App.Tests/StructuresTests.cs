using System.IO;
using System.Numerics;
using BrainSim.App.Brain3D;
using BrainSim.Engine;
using BrainSim.Engine.Journal;

namespace BrainSim.App.Tests;

public class StructuresTests
{
    private static readonly Lazy<BrainModel> Model =
        new(() => BrainModel.Load(Path.Combine(AppContext.BaseDirectory, "assets", "brain.bin")));

    private static TickRecord Frame(double cb1 = 0)
    {
        var c = Enumerable.Repeat(1.0, Substances.Count).ToArray();
        c[(int)Substance.Cortisol] = 1.2;
        c[(int)Substance.Norepinephrine] = 1.5;
        c[(int)Substance.Melatonin] = 2.0;
        return new TickRecord(0, new DateTime(2026, 11, 6), "Wake", 0, 12, 0, c, c, c, c, [], null, cb1);
    }

    // The hover text as it was in BrainView before the move, so the move cannot change it.
    [Theory]
    [InlineData("hippocampus_l", "Hippocampus (left)")]
    [InlineData("frontal_r", "Frontal lobe (right)")]
    [InlineData("vta", "VTA and substantia nigra")]
    [InlineData("locus_coeruleus_r", "Locus coeruleus (right)")]
    [InlineData("cerebellum", "Cerebellum")]
    public void Names_read_like_anatomy(string key, string name) => Assert.Equal(name, Structures.Name(key));

    [Theory]
    [InlineData("pineal", "Pineal gland")]
    [InlineData("orbital", "Orbital gyri")]
    [InlineData("adrenal", "Adrenal glands (HPA axis, not to scale)")]
    public void Keys_that_end_in_l_or_r_keep_their_name(string key, string name) => Assert.Equal(name, Structures.Name(key));

    [Fact]
    public void Every_structure_in_brain_bin_has_a_readable_name()
    {
        var keys = Model.Value.Regions.Select(r => r.Key).Concat(Structures.Nuclei.Select(n => n.Anchor));

        // A raw key comes back lowercase or with underscores when the name table misses it.
        Assert.All(keys, k => Assert.Matches(@"^[A-Z][^_]*$", Structures.Name(k)));
    }

    [Theory]
    [InlineData("pineal", "\nMEL 2.00")]
    [InlineData("amygdala_l", "\nCORT 1.20   NE 1.50\nAEA 1.00   2-AG 1.00   CB1 tone 0.00")]
    [InlineData("thalamus_r", "\nNE 1.50   GABA 1.00")]
    public void Hover_lists_the_substances_of_a_region(string key, string text) =>
        Assert.Equal(text, Structures.SubstancesText(key, Frame()));

    [Fact]
    public void The_catalog_lists_every_region_and_brain_nucleus_by_group_then_name()
    {
        var catalog = Structures.Catalog(Model.Value);

        string[] groups = ["cortex", "subcortical", "brainstem", "gland", "nucleus"];
        Assert.Equal(Model.Value.Regions.Count + Structures.Nuclei.Count(n => !n.Hpa), catalog.Count);
        Assert.Equal(catalog.Count, catalog.Select(s => s.Name).Distinct().Count());
        var order = catalog.Select(s => (Array.IndexOf(groups, s.Group), s.Name)).ToList();
        Assert.All(order, o => Assert.True(o.Item1 >= 0, $"unknown group for {o.Name}"));
        Assert.Equal(order.OrderBy(o => o.Item1).ThenBy(o => o.Name, StringComparer.Ordinal), order);

        // Nuclei carry their substance, for Open in Lab; regions do not. The HPA inset is not a brain structure.
        var vta = Assert.Single(catalog, s => s.Key == "vta");
        Assert.Equal(Substance.Dopamine, vta.Substance);
        Assert.Equal(Substance.Norepinephrine, catalog.Single(s => s.Key == "locus_coeruleus_l").Substance);
        Assert.Null(catalog.Single(s => s.Key == "hippocampus_l").Substance);
        Assert.DoesNotContain(catalog, s => s.Key == "adrenal");
        Assert.Equal("Hippocampus (left)", catalog.Single(s => s.Key == "hippocampus_l").ToString());
    }

    [Theory]
    [InlineData("temporal_l:cortex,hippocampus_l:subcortical,vta:nucleus", "vta")]          // nuclei are drawn on top
    [InlineData("temporal_l:cortex,hippocampus_l:subcortical,thalamus_l:subcortical", "hippocampus_l")]
    [InlineData("frontal_l:cortex,frontal_r:cortex", "frontal_l")]                           // only cortex: the nearest
    [InlineData("", null)]
    public void A_click_picks_a_nucleus_then_the_nearest_deep_structure_then_cortex(string hits, string? picked)
    {
        var inDepthOrder = hits.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(h => h.Split(':')).Select(h => (h[0], h[1]));

        Assert.Equal(picked, Structures.Pick(inDepthOrder));
    }

    [Fact]
    public void Bounds_contain_every_vertex_and_touch_one()
    {
        var region = Model.Value.Regions.First(r => r.Key == "hippocampus_l");

        var (center, radius) = Structures.Bounds(region.Positions);

        var farthest = region.Positions.Max(p => Vector3.Distance(p, center));
        Assert.Equal(radius, farthest, 5);
    }
}
