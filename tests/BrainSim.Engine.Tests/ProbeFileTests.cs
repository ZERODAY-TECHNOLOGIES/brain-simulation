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

    private static ProbeFile Trained(string model, string prefix, string source) =>
        new(model, prefix, DateTime.UtcNow, new() { ["hostility"] = new LinearProbe([1, 0], 0, 0.9, source) });

    [Fact]
    public void Probes_retrained_in_the_data_folder_win_over_the_shipped_ones()
    {
        var dir = Directory.CreateTempSubdirectory("brainsim-probes").FullName;
        try
        {
            var llm = Config.EngineConfig.LoadDefault().Llm;
            Trained(llm.EmbeddingModel, llm.EmbeddingPrefix, "retrained").SaveTo(dir);
            Trained("other-model", "", "other").SaveTo(dir);

            Assert.Equal("retrained", ProbeFile.Pick(dir, llm)!.Probes["hostility"].Source);
            Assert.Equal("other", ProbeFile.Pick(dir, llm with { EmbeddingModel = "other-model", EmbeddingPrefix = "" })!.Probes["hostility"].Source);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Without_a_retrained_file_the_shipped_probes_are_picked_when_they_fit()
    {
        var llm = Config.EngineConfig.LoadDefault().Llm;
        var empty = Path.Combine(Path.GetTempPath(), $"brainsim-no-probes-{Guid.NewGuid():N}");

        Assert.Equal(ProbeFile.LoadDefault()!.TrainedAt, ProbeFile.Pick(empty, llm)!.TrainedAt);
        Assert.Null(ProbeFile.Pick(empty, llm with { EmbeddingModel = "text-embedding-3-small" }));
    }

    [Fact]
    public void Saving_twice_for_the_same_model_replaces_the_file()
    {
        var dir = Directory.CreateTempSubdirectory("brainsim-probes").FullName;
        try
        {
            Trained("m/odel:1", "p: ", "first").SaveTo(dir);
            Trained("m/odel:1", "p: ", "second").SaveTo(dir);

            Assert.Single(Directory.GetFiles(dir));
            Assert.Equal("second", ProbeFile.Pick(dir, new Config.LlmConfig { EmbeddingModel = "m/odel:1", EmbeddingPrefix = "p: " })!.Probes["hostility"].Source);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
