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

    /// <summary>Probe weights only mean something for the embedding model and prefix they were trained on.</summary>
    [Fact]
    public void Probes_fit_only_the_embedding_setup_they_were_trained_on()
    {
        var file = ProbeFile.LoadDefault()!;
        var llm = Config.EngineConfig.LoadDefault().Llm;

        Assert.True(file.Fits(llm));
        Assert.False(file.Fits(llm with { EmbeddingModel = "text-embedding-3-small" }));
        Assert.False(file.Fits(llm with { EmbeddingPrefix = "" }));
    }
}
