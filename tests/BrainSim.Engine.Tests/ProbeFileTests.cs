using BrainSim.Engine.Appraisal;

namespace BrainSim.Engine.Tests;

public class ProbeFileTests
{
    [Fact]
    public void The_shipped_probes_load_with_one_weight_per_embedding_dimension()
    {
        var file = ProbeFile.LoadDefault();

        Assert.NotNull(file);
        Assert.Equal(["hostility", "negative", "positive", "warmth"], file.Probes.Keys.Order());
        Assert.All(file.Probes.Values, p => Assert.Equal(768, p.Weights.Length));
    }

    [Fact]
    public void A_missing_file_loads_as_null()
    {
        Assert.Null(ProbeFile.Load(Path.Combine(AppContext.BaseDirectory, "no-such-probes.json")));
    }
}
