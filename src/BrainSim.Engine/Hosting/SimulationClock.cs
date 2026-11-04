namespace BrainSim.Engine.Hosting;

/// <summary>
/// The time the brain lives in (spec: "Ceasul circadian", real or simulated mode).
/// Real: the wall clock. Simulated: a clock anchored at some moment and running <see cref="Factor"/> times faster.
/// A factor of 0 pauses simulated time. Thread safe.
/// </summary>
public sealed class SimulationClock(Func<DateTime>? wallClock = null)
{
    private readonly Func<DateTime> _wall = wallClock ?? (() => DateTime.Now);
    private readonly Lock _gate = new();
    private DateTime _simAnchor;
    private DateTime _wallAnchor;

    public bool Simulated { get; private set; }

    /// <summary>Speed of simulated time. 1 in real mode.</summary>
    public double Factor { get; private set; } = 1;

    public DateTime Now
    {
        get
        {
            lock (_gate)
            {
                return Simulated ? _simAnchor + (_wall() - _wallAnchor) * Factor : _wall();
            }
        }
    }

    /// <summary>Switches to simulated time running at <paramref name="factor"/>, continuing from <paramref name="from"/>.</summary>
    public void SetSimulated(double factor, DateTime from)
    {
        lock (_gate)
        {
            _simAnchor = from;
            _wallAnchor = _wall();
            Factor = factor;
            Simulated = true;
        }
    }

    /// <summary>Changes the speed without a jump in simulated time.</summary>
    public void SetFactor(double factor)
    {
        if (Simulated)
        {
            SetSimulated(factor, Now);
        }
    }

    /// <summary>
    /// Back to the wall clock. If simulated time ran ahead of it, the brain waits until real time catches up.
    /// </summary>
    public void SetReal()
    {
        lock (_gate)
        {
            Simulated = false;
            Factor = 1;
        }
    }
}
