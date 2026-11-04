namespace BrainSim.Engine.Journal;

/// <summary>
/// Bounded in-memory history for the charts and the timeline. The newest ticks stay at full resolution;
/// when the capacity is reached, every other record in the older half is dropped, so older history gets
/// coarser instead of disappearing. Ticks with a generated token are kept for as long as possible.
/// Thread safe: the engine thread appends, the UI thread reads.
/// </summary>
public sealed class TickHistory(int capacity = 100_000)
{
    private readonly Lock _gate = new();
    private List<TickRecord> _records = new(capacity);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _records.Count;
            }
        }
    }

    public TickRecord? Latest
    {
        get
        {
            lock (_gate)
            {
                return _records.Count > 0 ? _records[^1] : null;
            }
        }
    }

    public TickRecord? First
    {
        get
        {
            lock (_gate)
            {
                return _records.Count > 0 ? _records[0] : null;
            }
        }
    }

    public void Append(TickRecord record)
    {
        lock (_gate)
        {
            _records.Add(record);
            if (_records.Count > capacity)
            {
                Thin();
            }
        }
    }

    /// <summary>The record at <paramref name="tick"/>, or the closest one before it.</summary>
    public TickRecord? At(long tick)
    {
        lock (_gate)
        {
            if (_records.Count == 0)
            {
                return null;
            }

            var i = IndexAtOrBefore(tick);
            return _records[Math.Max(i, 0)];
        }
    }

    /// <summary>At most <paramref name="maxPoints"/> records between the two ticks, evenly strided.</summary>
    public List<TickRecord> Window(long fromTick, long toTick, int maxPoints)
    {
        lock (_gate)
        {
            if (_records.Count == 0)
            {
                return [];
            }

            var start = Math.Max(IndexAtOrBefore(fromTick), 0);
            var end = IndexAtOrBefore(toTick);
            var count = end - start + 1;
            if (count <= 0)
            {
                return [];
            }

            var stride = Math.Max(1, (int)Math.Ceiling(count / (double)maxPoints));
            var result = new List<TickRecord>(count / stride + 1);
            for (var i = start; i <= end; i += stride)
            {
                result.Add(_records[i]);
            }

            if (result[^1] != _records[end])
            {
                result.Add(_records[end]);
            }

            return result;
        }
    }

    private int IndexAtOrBefore(long tick)
    {
        int lo = 0, hi = _records.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_records[mid].Tick <= tick)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }

    private void Thin()
    {
        var half = _records.Count / 2;
        var kept = new List<TickRecord>(capacity);
        for (var i = 0; i < half; i++)
        {
            if (i % 2 == 0 || _records[i].Token is not null)
            {
                kept.Add(_records[i]);
            }
        }

        // A very long conversation can fill the older half with tokens; then thin those too.
        if (kept.Count + (_records.Count - half) > capacity)
        {
            kept = [.. kept.Where((_, i) => i % 2 == 0)];
        }

        kept.AddRange(_records.Skip(half));
        _records = kept;
    }
}
