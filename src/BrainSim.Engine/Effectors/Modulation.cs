using BrainSim.Engine.Config;
using BrainSim.Engine.Receptors;

namespace BrainSim.Engine.Effectors;

/// <summary>
/// Channel modulations m_k (spec: "Efectori").
/// m_k = sum_i Delta_{k,i} / (1 + g_GABA * max(0, C_GABA - 1)).
/// </summary>
public sealed class Modulation
{
    private readonly ChannelTerm[][] _terms;
    private readonly double _gabaGain;

    public Modulation(EngineConfig config)
    {
        Names = [.. config.Modulation.Channels.Keys];
        _terms = Names.Select(n => config.Modulation.Channels[n].Select(ChannelTerm.From).ToArray()).ToArray();
        _gabaGain = config.Modulation.GabaGain;
        M = new double[Names.Count];
    }

    public IReadOnlyList<string> Names { get; }

    /// <summary>Current m_k, in the order of <see cref="Names"/>.</summary>
    public double[] M { get; }

    public double this[string channel] => M[IndexOf(channel)];

    public int IndexOf(string channel)
    {
        for (var k = 0; k < Names.Count; k++)
        {
            if (Names[k] == channel)
            {
                return k;
            }
        }

        throw new KeyNotFoundException($"No modulation channel named '{channel}'.");
    }

    public void Compute(ReadOnlySpan<double> c, ReceptorState receptors) => Fill(c, receptors, M);

    /// <summary>m_k for any state, such as the projected one (D-38). Leaves <see cref="M"/> as it is.</summary>
    public double[] Evaluate(ReadOnlySpan<double> c, ReceptorState receptors)
    {
        var m = new double[_terms.Length];
        Fill(c, receptors, m);
        return m;
    }

    private void Fill(ReadOnlySpan<double> c, ReceptorState receptors, Span<double> m)
    {
        var gabaExcess = Math.Max(0, c[(int)Substance.Gaba] - receptors.Baseline);
        var damping = 1 + _gabaGain * gabaExcess;
        for (var k = 0; k < _terms.Length; k++)
        {
            var sum = 0.0;
            foreach (var term in _terms[k])
            {
                sum += receptors.Delta(term, c[term.Index]);
            }

            m[k] = sum / damping;
        }
    }
}
