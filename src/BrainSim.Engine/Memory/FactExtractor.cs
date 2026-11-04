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

    private const string UserLabel = "User";
    private const string AssistantLabel = "Assistant";

    /// <summary>
    /// The extractor's input: the exchange labeled by speaker, with only the start of the reply. qwen3.5-0.8b given
    /// the bare turn lists facts from a long reply and drops the user's own (D-33, extraction_reply_chars).
    /// </summary>
    public static string Exchange(string message, string reply, MemoryConfig config) =>
        $"{UserLabel}: {message}\n{AssistantLabel}: {reply[..Math.Min(reply.Length, config.ExtractionReplyChars)]}";

    public static IReadOnlyList<string> Parse(string output, MemoryConfig config)
    {
        var facts = new List<string>();
        foreach (var raw in output.Replace("\r", "").Split('\n'))
        {
            var line = SpeakerLabel().Replace(ListMark().Replace(raw.Trim(), ""), "").Trim(); // the model echoes the labels
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

    [GeneratedRegex($"^({UserLabel}|{AssistantLabel}):\\s*", RegexOptions.IgnoreCase)]
    private static partial Regex SpeakerLabel();
}
