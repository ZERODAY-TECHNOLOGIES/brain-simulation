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
    [InlineData("pineal", "\nMEL 2.00")]
    [InlineData("amygdala_l", "\nCORT 1.20   NE 1.50\nAEA 1.00   2-AG 1.00   CB1 tone 0.00")]
    [InlineData("thalamus_r", "\nNE 1.50   GABA 1.00")]
    public void Hover_lists_the_substances_of_a_region(string key, string text) =>
        Assert.Equal(text, Structures.SubstancesText(key, Frame()));

    [Fact]
    public void Bounds_contain_every_vertex_and_touch_one()
    {
        var region = Model.Value.Regions.First(r => r.Key == "hippocampus_l");

        var (center, radius) = Structures.Bounds(region.Positions);

        var farthest = region.Positions.Max(p => Vector3.Distance(p, center));
        Assert.Equal(radius, farthest, 5);
    }
}
