using BrainSim.Engine.Config;

namespace BrainSim.Engine.Chemistry;

/// <summary>
/// The 10 concentrations and their per-tick dynamics (spec: "Starea chimica si dinamica").
/// C_i(t+1) = C_i + [ -k_i (C_i - b_i) + impulse_i + source_i + sum_j W_ij (C_j - b_j) ], clipped to [min, max].
/// source_i is continuous production per tick, used for MEL from the pineal gland.
/// </summary>
public sealed class ChemicalState
{
    private const int N = Substances.Count;
    private const int Ado = (int)Substance.Adenosine;

    private readonly double[] _k = new double[N];
    private readonly double[,] _w = new double[N, N]; // [target, source]
    private readonly double[][] _pending;             // impulse ring buffer, one slot per future tick
    private readonly double[] _next = new double[N];
    private readonly double _min;
    private readonly double _max;
    private int _slot;

    public ChemicalState(EngineConfig config)
    {
        var halfLives = config.HalfLives();
        for (var i = 0; i < N; i++)
        {
            _k[i] = Math.Log(2) / halfLives[i];
        }

        foreach (var w in config.Interactions)
        {
            _w[(int)Substances.Parse(w.To), (int)Substances.Parse(w.From)] = w.W;
        }

        _pending = new double[config.Time.ImpulseSpreadTicks][];
        for (var i = 0; i < _pending.Length; i++)
        {
            _pending[i] = new double[N];
        }

        _min = config.Concentration.Min;
        _max = config.Concentration.Max;
        RestingBaseline = config.Concentration.Baseline;
        Array.Fill(C, RestingBaseline);
        Array.Fill(Baseline, RestingBaseline);
    }

    /// <summary>Current concentrations, indexed by <see cref="Substance"/>.</summary>
    public double[] C { get; } = new double[N];

    /// <summary>Current targets b_i. Equal to the resting baseline while awake, phase targets during sleep.</summary>
    public double[] Baseline { get; } = new double[N];

    /// <summary>Continuous production per tick. Stays until changed.</summary>
    public double[] Source { get; } = new double[N];

    public double RestingBaseline { get; }

    /// <summary>ADO only decays while asleep.</summary>
    public bool Asleep { get; set; }

    public long Tick { get; private set; }

    public double this[Substance s]
    {
        get => C[(int)s];
        set => C[(int)s] = value;
    }

    /// <summary>Schedules a release of <paramref name="amount"/>, spread evenly over the next impulse_spread_ticks ticks.</summary>
    public void Release(Substance substance, double amount)
    {
        var perTick = amount / _pending.Length;
        foreach (var slot in _pending)
        {
            slot[(int)substance] += perTick;
        }
    }

    public void Step()
    {
        var impulse = _pending[_slot];
        for (var i = 0; i < N; i++)
        {
            var k = i == Ado && !Asleep ? 0.0 : _k[i];
            var d = -k * (C[i] - Baseline[i]) + impulse[i] + Source[i];
            for (var j = 0; j < N; j++)
            {
                d += _w[i, j] * (C[j] - Baseline[j]);
            }

            _next[i] = Math.Clamp(C[i] + d, _min, _max);
        }

        Array.Copy(_next, C, N);
        Array.Clear(impulse);
        _slot = (_slot + 1) % _pending.Length;
        Tick++;
    }

    public ChemistrySnapshot Save() => new([.. C], [.. _pending.Select(p => p.ToArray())], _slot, Tick);

    public void Restore(ChemistrySnapshot snapshot)
    {
        Array.Copy(snapshot.C, C, N);
        for (var i = 0; i < _pending.Length; i++)
        {
            Array.Copy(snapshot.Pending[i], _pending[i], N);
        }

        _slot = snapshot.Slot;
        Tick = snapshot.Tick;
    }

    /// <summary>The linearized system -K + W. All eigenvalues must have negative real parts (spec, validation).</summary>
    public double[,] LinearizedMatrix(bool asleep)
    {
        var m = (double[,])_w.Clone();
        for (var i = 0; i < N; i++)
        {
            m[i, i] -= i == Ado && !asleep ? 0.0 : _k[i];
        }

        return m;
    }
}

/// <summary>Everything needed to resume the chemistry exactly. Baselines and sources are recomputed each tick.</summary>
public sealed record ChemistrySnapshot(double[] C, double[][] Pending, int Slot, long Tick);
