using BrainSim.Engine.Config;
using BrainSim.Engine.Llm;
using BrainSim.Engine.Memory;

namespace BrainSim.Engine.Tests;

public class FactExtractorTests
{
    private static readonly EngineConfig Config = EngineConfig.LoadDefault();
    private static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0);

    [Fact]
    public void Parse_keeps_clean_facts_and_drops_junk()
    {
        var output = "- The user has a black cat.\n2. Octopuses have three hearts\nWhat else?\nFacts:\n\n" + new string('x', 300);

        Assert.Equal(["The user has a black cat.", "Octopuses have three hearts"], FactExtractor.Parse(output, Config.Memory));
    }

    [Fact]
    public void Parse_caps_at_facts_per_episode()
    {
        var output = string.Join("\n", Enumerable.Range(1, 8).Select(i => $"Fact number {i} is true."));

        Assert.Equal(5, FactExtractor.Parse(output, Config.Memory).Count);
    }

    [Fact]
    public void Empty_output_gives_no_facts()
    {
        Assert.Empty(FactExtractor.Parse("", Config.Memory));
        Assert.Empty(FactExtractor.Parse("\n  \n", Config.Memory));
    }

    [Fact]
    public void A_restated_fact_strengthens_the_existing_one()
    {
        var memory = new DeclarativeMemory(Config.Memory);
        var first = memory.AddOrStrengthenFact("The user has a black cat.", [1f, 0f], 1, default, Now);

        // Cosine 0.985: the same fact in other words (real restatements measured 0.984 to 0.990).
        var again = memory.AddOrStrengthenFact("the user owns a black cat", [0.985f, MathF.Sqrt(1 - 0.985f * 0.985f)], 2, default, Now.AddMinutes(1));
        Assert.Same(first, again);
        Assert.Equal(2, first.Presentations.Count);

        // Cosine 0.8: a different fact.
        var other = memory.AddOrStrengthenFact("The user has a dog.", [0.8f, 0.6f], 3, default, Now.AddMinutes(2));
        Assert.NotSame(first, other);
        Assert.Equal(2, memory.Snapshot().Count);
    }

    /// <summary>"The user is 34 years old." and "... 52 years old." measured 0.956: a correction, not a restatement.</summary>
    [Fact]
    public void A_fact_that_differs_in_a_value_is_kept_apart()
    {
        var memory = new DeclarativeMemory(Config.Memory);
        var old = memory.AddOrStrengthenFact("The user is 34 years old.", [1f, 0f], 1, default, Now);

        var corrected = memory.AddOrStrengthenFact("The user is 52 years old.", [0.956f, MathF.Sqrt(1 - 0.956f * 0.956f)], 2, default, Now.AddMinutes(1));

        Assert.NotSame(old, corrected);
        Assert.Single(old.Presentations);
    }

    [Fact]
    public async Task ExtractAsync_sends_the_instruction_at_low_temperature()
    {
        var fake = new FakeLmStudio(replyTokens: 0) { Reply = _ => "- The user's sister is called Ilinca." };
        var llm = new LmStudioClient(new HttpClient(fake) { BaseAddress = new Uri(Config.Llm.BaseUrl) }, Config.Llm);

        var facts = await FactExtractor.ExtractAsync(llm, "My sister is called Ilinca.\nWhat a lovely name.", Config.Memory, TestContext.Current.CancellationToken);

        Assert.Equal(["The user's sister is called Ilinca."], facts);
        var request = fake.ExtractionRequests.Single().RootElement;
        Assert.Equal(0.2, request.GetProperty("temperature").GetDouble(), 9);
        Assert.Equal(150, request.GetProperty("max_tokens").GetInt32());
        var system = request.GetProperty("messages")[0];
        Assert.Equal(("system", FactExtractor.Instruction), (system.GetProperty("role").GetString(), system.GetProperty("content").GetString()));
    }
}
