using System.Text;
using System.Text.RegularExpressions;
using BrainSim.Engine.Config;
using BrainSim.Engine.Llm;

namespace BrainSim.Engine.Memory;

/// <summary>
/// Turns an episode into a few short facts (docs/memory.md "Encoding"). A 0.8B model is unreliable here, so its
/// output is filtered line by line, and anything that does not look like a single plain statement is dropped.
/// </summary>
public static partial class FactExtractor
{
    public const string Instruction =
        "List the lasting facts in this exchange, one per line. Facts about the user or the world only. If there are none, write nothing.";

    public static IReadOnlyList<string> Parse(string output, MemoryConfig config)
    {
        var facts = new List<string>();
        foreach (var raw in output.Replace("\r", "").Split('\n'))
        {
            var line = ListMark().Replace(raw.Trim(), "").Trim();
            if (line.Length >= config.FactMinChars && line.Length <= config.FactMaxChars && !line.Contains('?') && !line.EndsWith(':'))
            {
                facts.Add(line);
                if (facts.Count == config.FactsPerEpisode)
                {
                    break;
                }
            }
        }

        return facts;
    }

    public static async Task<IReadOnlyList<string>> ExtractAsync(LmStudioClient llm, string episodeText, MemoryConfig config, CancellationToken ct)
    {
        var sampling = new SamplingParameters(config.ExtractionTemperature, 0.95, config.ExtractionTokens);
        var output = new StringBuilder();
        await foreach (var token in llm.StreamChatAsync([ChatMessage.System(Instruction), ChatMessage.User(episodeText)], sampling, ct))
        {
            output.Append(token);
        }

        return Parse(output.ToString(), config);
    }

    // "- ", "* ", "+ ", "1. ", "2) " at the start of a line.
    [GeneratedRegex(@"^([-*+]|\d+[.)])\s+")]
    private static partial Regex ListMark();
}
