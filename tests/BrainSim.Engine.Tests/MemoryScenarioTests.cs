using BrainSim.Engine.Appraisal;
using BrainSim.Engine.Config;
using BrainSim.Engine.Llm;
using BrainSim.Engine.Memory;

namespace BrainSim.Engine.Tests;

/// <summary>
/// Spec T7 against the real models: a fact told once comes back from memory after it has left the 20-message
/// context. Runs against the real LM Studio and probes.json, in real time, and only when BRAINSIM_LMSTUDIO=1.
/// </summary>
[Trait("Category", "LmStudio")]
public class MemoryScenarioTests
{
    private static readonly EngineConfig Config = EngineConfig.LoadDefault();

    [Fact]
    public async Task A_fact_told_early_is_recalled_25_turns_later()
    {
        if (Environment.GetEnvironmentVariable("BRAINSIM_LMSTUDIO") != "1")
        {
            Assert.Skip("Set BRAINSIM_LMSTUDIO=1 with LM Studio running to run the scenario tests.");
        }

        var ct = TestContext.Current.CancellationToken;
        var probes = ProbeFile.LoadDefault()?.Probes ?? throw new InvalidOperationException("probes.json is missing. Run tools/ProbeTrainer first.");
        var agent = new Agent(Config, LmStudioClient.Create(Config.Llm), probes, () => DateTime.Now);
        var log = TestContext.Current.TestOutputHelper;

        await agent.SendAsync("My dog is called Pistachio. He is a beagle.", cancellationToken: ct);
        await agent.Extraction!.WaitAsync(TimeSpan.FromMinutes(2), ct);

        string[] unrelated =
        [
            "What is a good way to learn the guitar?", "Tell me something about octopuses.", "I had pasta for lunch today.",
            "What should I read this winter?", "How do I keep basil alive indoors?", "Is it better to run in the morning or the evening?",
            "Explain what a black hole is in two sentences.", "How long should I steep green tea?", "Give me a tip for public speaking.",
            "What makes bread rise?", "Why is the sky blue?", "How do bees make honey?", "What is a haiku?",
            "Suggest a quick breakfast.", "How do I fold a paper plane?", "What causes thunder?", "Name a calm piece of music.",
            "How many planets are in the solar system?", "What is the tallest mountain?", "How do I clean a cast iron pan?",
            "What is a good stretch for the back?", "Why do leaves change color?", "How do magnets work?",
            "What is the difference between weather and climate?", "Recommend a board game for two.",
        ];
        foreach (var message in unrelated)
        {
            var filler = await agent.SendAsync(message, cancellationToken: ct);
            log?.WriteLine($"{message} -> recalled {filler.Recalled.Count}");
        }

        var facts = agent.Memory.Snapshot().Where(c => c.Kind == ChunkKind.Fact).ToList();
        Assert.Contains(facts, f => f.Text.Contains("Pistachio"));
        Assert.DoesNotContain(agent.History.TakeLast(Config.Llm.HistoryMessages), m => m.Content.Contains("Pistachio"));

        var reply = await agent.SendAsync("What is my dog's name?", cancellationToken: ct);
        log?.WriteLine($"recalled: {string.Join(" | ", reply.Recalled)}; familiarity {reply.Familiarity:F2}; reply: {reply.Text}");
        Assert.Contains(reply.Recalled, f => f.Contains("Pistachio"));
    }
}
