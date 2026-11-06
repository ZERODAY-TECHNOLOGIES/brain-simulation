// Trains the appraisal probes (D-05) on embeddings from the backend in config.yaml and writes the shipped probes.json.
// A probe ships only if it clears AUC > 0.85 on the held-out split (spec: "Evaluarea"). The training itself lives in
// the engine (ProbeTraining), so Settings can retrain for any embedding model too (D-39).
//
// Usage, from the repo root, with the embedding model loaded:
//   dotnet run --project tools/ProbeTrainer
//
// Downloads and embedding caches go to data/probes (ignored by git).

using System.Text.Json;
using BrainSim.Engine.Appraisal;
using BrainSim.Engine.Config;
using BrainSim.Engine.Llm;

var cacheDir = Path.GetFullPath(Path.Combine("data", "probes"));
var outputPath = Path.GetFullPath(Path.Combine("src", "BrainSim.Engine", "probes.json"));

var config = EngineConfig.LoadDefault();
using var http = new HttpClient();
var training = new ProbeTraining(http, LmStudioClient.Create(config.Llm), config.Llm, cacheDir);
var result = await training.RunAsync(new ConsoleProgress(), CancellationToken.None);

await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(result.File, ProbeFile.Json));
Console.WriteLine($"Wrote {result.File.Probes.Count} probes to {outputPath}");
return result.Rejected.Count > 0 ? 1 : 0;

internal sealed class ConsoleProgress : IProgress<string>
{
    public void Report(string value) => Console.WriteLine(value);
}
