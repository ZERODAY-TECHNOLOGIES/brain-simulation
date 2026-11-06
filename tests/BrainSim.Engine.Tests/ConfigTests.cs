using BrainSim.Engine.Config;

namespace BrainSim.Engine.Tests;

/// <summary>A typo in config.yaml fails at load, with a message that names it, not somewhere mid-run.</summary>
public sealed class ConfigTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"brainsim-config-{Guid.NewGuid():N}.yaml");

    public void Dispose() => File.Delete(_path);

    [Theory]
    [InlineData("  NE: 40", "  NE: -40", "half_life for NE")]
    [InlineData("  - { from: GABA, to: NE,   w: -0.01 }", "  - { from: GABBA, to: NE,   w: -0.01 }", "GABBA'. Expected one of: NE, ACh, DA, GABA")]
    [InlineData("{ substance: CORT, emax: -0.5 }", "{ substance: CORTT, emax: -0.5 }", "CORTT")]
    [InlineData("    surprise:        { NE: 0.8, ACh: 0.6 }", "    suprise:        { NE: 0.8, ACh: 0.6 }", "suprise")]
    [InlineData("    reward_error:    { DA: 1.5 }", "    reward_error:    { DOPA: 1.5 }", "DOPA")]
    [InlineData("    N1:  {", "    N4:  {", "N4")]
    [InlineData("    N2:  { NE: 0.4,", "    N2:  { NEE: 0.4,", "NEE")]
    public void A_typo_fails_at_load_and_names_itself(string line, string typo, string named)
    {
        var yaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config.yaml"));
        var at = yaml.IndexOf(line, StringComparison.Ordinal);
        Assert.True(at >= 0, $"config.yaml no longer has '{line}'");
        File.WriteAllText(_path, yaml[..at] + typo + yaml[(at + line.Length)..]);

        var e = Assert.ThrowsAny<Exception>(() => EngineConfig.Load(_path));

        Assert.Contains(named, e.Message);
    }

    [Fact]
    public void A_mood_channel_must_be_a_modulation_channel()
    {
        var yaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config.yaml"));
        var at = yaml.IndexOf("warm_cold:", yaml.IndexOf("\nmood:", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(at >= 0, "config.yaml no longer has warm_cold under mood");
        File.WriteAllText(_path, yaml[..at] + "warm_cool:" + yaml[(at + "warm_cold:".Length)..]);

        var e = Assert.ThrowsAny<Exception>(() => EngineConfig.Load(_path));

        Assert.Contains("warm_cool", e.Message);
    }

    [Fact]
    public void The_shipped_config_loads() => Assert.Equal(12, EngineConfig.LoadDefault().HalfLives().Length); // the spec's ten and the two endocannabinoids (D-36)
}
