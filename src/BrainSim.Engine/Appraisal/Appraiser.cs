using BrainSim.Engine.Chemistry;
using BrainSim.Engine.Config;

namespace BrainSim.Engine.Appraisal;

/// <summary>What the appraisal read from one user message.</summary>
/// <param name="Surprise">S in [0, surprise_max], from embedding novelty (D-19).</param>
/// <param name="Reward">r in [-1, 1] for the previous response, or null when there was none.</param>
/// <param name="RewardError">delta = r - V, or 0 without a reward.</param>
/// <param name="Threat">A in [0, 1].</param>
/// <param name="Warmth">W in [0, 1].</param>
/// <param name="ExpectedReward">V when the message arrived.</param>
public readonly record struct AppraisalSignals(
    double Surprise, double? Reward, double RewardError, double Threat, double Warmth, double ExpectedReward);

/// <summary>
/// Appraisal (spec: "Evaluarea"): turns a message embedding into S, r, delta, A, W and schedules the impulses.
/// Probes that did not pass the AUC gate are missing, and their signal reads as 0.
/// </summary>
public sealed class Appraiser
{
    private readonly AppraisalConfig _config;
    private readonly IReadOnlyDictionary<string, LinearProbe> _probes;
    private readonly (string Signal, Substance Substance, double Gain)[] _gains;
    private readonly Queue<float[]> _recent = new();
    private readonly Queue<double> _novelty = new();
    private double? _pendingReward;

    public Appraiser(AppraisalConfig config, IReadOnlyDictionary<string, LinearProbe> probes)
    {
        _config = config;
        _probes = probes;
        _gains = [.. config.Gains.SelectMany(g => g.Value.Select(s => (g.Key, Substances.Parse(s.Key), s.Value)))];
    }

    /// <summary>Expected reward V, learned slowly.</summary>
    public double ExpectedReward { get; private set; }

    /// <summary>Spec loop step 3. <paramref name="hasPreviousResponse"/> is false for the first message of a conversation.</summary>
    public AppraisalSignals Appraise(float[] embedding, bool hasPreviousResponse)
    {
        var surprise = Surprise(embedding);
        var threat = Rescale(Probe("hostility", embedding));
        var warmth = Rescale(Probe("warmth", embedding));
        double? reward = hasPreviousResponse ? Probe("positive", embedding) - Probe("negative", embedding) : null;
        var error = reward is { } r ? r - ExpectedReward : 0;
        _pendingReward = reward;
        return new AppraisalSignals(surprise, reward, error, threat, warmth, ExpectedReward);
    }

    /// <summary>Spec loop step 4: schedules the impulses, spread over the next ticks.</summary>
    public void Release(AppraisalSignals signals, ChemicalState chemistry)
    {
        foreach (var (signal, substance, gain) in _gains)
        {
            var value = signal switch
            {
                "surprise" => signals.Surprise,
                "reward_error" => signals.RewardError,
                "threat" => signals.Threat,
                "warmth" => signals.Warmth,
                "expected_reward" => Math.Max(signals.ExpectedReward, 0),
                _ => 0,
            };
            if (value != 0)
            {
                chemistry.Release(substance, gain * value);
            }
        }
    }

    /// <summary>Spec loop step 10: V moves toward the reward that came with the last message.</summary>
    public void LearnExpectedReward()
    {
        if (_pendingReward is { } r)
        {
            ExpectedReward += _config.RewardLearningRate * (r - ExpectedReward);
            _pendingReward = null;
        }
    }

    public AppraiserState Save() => new(ExpectedReward, [.. _recent], [.. _novelty], _pendingReward);

    public void Restore(AppraiserState state)
    {
        ExpectedReward = state.ExpectedReward;
        _recent.Clear();
        _novelty.Clear();
        foreach (var e in state.Recent)
        {
            _recent.Enqueue(e);
        }

        foreach (var n in state.Novelty)
        {
            _novelty.Enqueue(n);
        }

        _pendingReward = state.PendingReward;
    }

    /// <summary>
    /// Novelty = 1 - max cosine similarity to the recent messages, z-scored against the recent novelty values,
    /// clipped to [0, surprise_max]. Embeddings from LM Studio are unit length, so the dot product is the cosine.
    /// </summary>
    private double Surprise(float[] embedding)
    {
        var novelty = 1.0;
        foreach (var previous in _recent)
        {
            novelty = Math.Min(novelty, 1 - Dot(embedding, previous));
        }

        var surprise = 0.0;
        if (_novelty.Count >= _config.SurpriseMinSamples)
        {
            var mean = _novelty.Average();
            var std = Math.Sqrt(_novelty.Average(n => (n - mean) * (n - mean)));
            surprise = std > 1e-9 ? Math.Clamp((novelty - mean) / std, 0, _config.SurpriseMax) : 0;
        }

        Push(_recent, embedding);
        if (_recent.Count > 1)
        {
            Push(_novelty, novelty);
        }

        return surprise;
    }

    private void Push<T>(Queue<T> queue, T item)
    {
        queue.Enqueue(item);
        if (queue.Count > _config.SurpriseWindow)
        {
            queue.Dequeue();
        }
    }

    private double Probe(string name, float[] embedding) => _probes.TryGetValue(name, out var p) ? p.Predict(embedding) : 0;

    /// <summary>Only probability above probe_floor counts, rescaled to [0, 1].</summary>
    private double Rescale(double p) => Math.Clamp((p - _config.ProbeFloor) / (1 - _config.ProbeFloor), 0, 1);

    private static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }
}

public sealed record AppraiserState(double ExpectedReward, List<float[]> Recent, List<double> Novelty, double? PendingReward);
