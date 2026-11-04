using BrainSim.Engine.Config;
using BrainSim.Engine.Dreams;
using BrainSim.Engine.Sleep;

namespace BrainSim.Engine.Memory;

/// <summary>
/// ACT-R declarative memory (docs/memory.md). Activation A = B + S + noise, with the base level
/// B = ln(sum w_j age_j^-d) over the chunk's presentations, spreading S from the cosine of the current message,
/// and logistic noise. E4 sets the encoding weight, the noise scale and the retrieval threshold.
/// Runs on the engine loop; <see cref="Snapshot"/> is safe from any thread.
/// </summary>
public sealed class DeclarativeMemory(MemoryConfig config, int seed = 11)
{
    private const double MinimumAge = 0.05;

    private readonly Lock _gate = new();
    private readonly List<Chunk> _chunks = [];
    private readonly Random _random = new(seed);
    private int _nextId;
    private DateTime _lastReplay = DateTime.MinValue;

    public MemoryConfig Config => config;

    /// <summary>Raised on the engine loop after each NREM replay, with the replayed episode.</summary>
    public event Action<Chunk>? Replayed;

    /// <summary>Copies of every chunk, for the UI thread.</summary>
    public IReadOnlyList<Chunk> Snapshot()
    {
        lock (_gate)
        {
            return [.. _chunks.Select(c => c.Copy())];
        }
    }

    /// <summary>Replaces the memory with chunks read from memory.bin; new chunks continue after the highest id.</summary>
    public void Load(IEnumerable<Chunk> chunks)
    {
        lock (_gate)
        {
            _chunks.Clear();
            _chunks.AddRange(chunks);
            _nextId = _chunks.Count > 0 ? _chunks.Max(c => c.Id) : 0;
        }
    }

    public double EncodingWeight(MemoryModulation m) => Math.Clamp(1 + config.Gain * m.Encoding, 0.2, 3);

    public double NoiseScale(MemoryModulation m) => config.Noise * Math.Clamp(1 + config.Gain * m.Noise, 0.3, 3);

    public double Threshold(MemoryModulation m) => config.Threshold + config.Gain * m.Threshold;

    public double BaseLevel(Chunk c, DateTime now)
    {
        var sum = 0.0;
        foreach (var p in c.Presentations)
        {
            sum += p.Weight * Math.Pow(Math.Max((now - p.Time).TotalSeconds, MinimumAge), -config.Decay);
        }

        return sum > 0 ? Math.Log(sum) : double.NegativeInfinity;
    }

    public double Spreading(float[] query, Chunk c)
    {
        if (c.Embedding is not { } e)
        {
            return 0;
        }

        var cosine = Cosine(query, e);
        return config.Spreading * Math.Max(0, (cosine - config.CosineFloor) / (1 - config.CosineFloor));
    }

    public Chunk AddEpisode(string text, float[]? embedding, IReadOnlyList<double> c, MemoryModulation m, DateTime now)
    {
        var ne = c[(int)Substance.Norepinephrine];
        var chunk = new Chunk
        {
            Id = NextId(),
            Kind = ChunkKind.Episode,
            Text = text,
            Embedding = embedding,
            Created = now,
            Presentations = [new Presentation(now, EncodingWeight(m))],
            Tag = 0.05 + Math.Max(0, ne - 1) + Math.Max(0, c[(int)Substance.Cortisol] - 1),
            Salience = 0.05 + Math.Max(0, c[(int)Substance.Dopamine] - 1) + Math.Max(0, ne - 1),
        };
        Add(chunk, now);
        return chunk;
    }

    /// <summary>Stores a fact as a new chunk. <c>AddOrStrengthenFact</c> merges restated facts on top of this.</summary>
    public Chunk AddFact(string text, float[] embedding, int episodeId, MemoryModulation m, DateTime now)
    {
        var chunk = new Chunk
        {
            Id = NextId(),
            Kind = ChunkKind.Fact,
            Text = text,
            Embedding = embedding,
            Created = now,
            Presentations = [new Presentation(now, EncodingWeight(m))],
            EpisodeId = episodeId,
        };
        Add(chunk, now);
        return chunk;
    }

    /// <summary>
    /// Stores a fact, unless one already in memory says the same thing (cosine above duplicate_cosine):
    /// then that one gets a new presentation instead, so restating strengthens rather than duplicates.
    /// </summary>
    public Chunk AddOrStrengthenFact(string text, float[] embedding, int episodeId, MemoryModulation m, DateTime now)
    {
        lock (_gate)
        {
            var same = _chunks
                .Where(c => c is { Kind: ChunkKind.Fact, Embedding: not null })
                .Select(c => (Chunk: c, Cosine: Cosine(embedding, c.Embedding!)))
                .Where(x => x.Cosine > config.DuplicateCosine)
                .OrderByDescending(x => x.Cosine)
                .Select(x => x.Chunk)
                .FirstOrDefault();
            if (same is not null)
            {
                same.Presentations.Add(new Presentation(now, EncodingWeight(m)));
                same.Archived = false;
                return same;
            }
        }

        return AddFact(text, embedding, episodeId, m, now);
    }

    /// <summary>Up to recall_count facts above the threshold, highest first. Each recall is a new presentation.</summary>
    public IReadOnlyList<Recall> RecallFacts(float[] query, MemoryModulation m, DateTime now)
    {
        var tau = Threshold(m);
        var s = NoiseScale(m);
        lock (_gate)
        {
            // Retrieval is cue driven, as in ACT-R: only facts the message actually points at (S > 0) compete.
            // Without this, every fresh fact passes on base level alone, is recalled for any message, and each
            // recall strengthens it again, so it never fades while the conversation goes on.
            var recalled = _chunks
                .Where(c => c is { Kind: ChunkKind.Fact, Archived: false, Embedding: not null })
                .Select(c => (Chunk: c, Spread: Spreading(query, c)))
                .Where(x => x.Spread > 0)
                .Select(x => new Recall(x.Chunk, BaseLevel(x.Chunk, now) + x.Spread + Noise(s)))
                .Where(r => r.Activation > tau)
                .OrderByDescending(r => r.Activation)
                .Take(config.RecallCount)
                .ToList();

            var weight = EncodingWeight(m);
            foreach (var r in recalled)
            {
                r.Chunk.Presentations.Add(new Presentation(now, weight));
                r.Chunk.LastRecalled = now;
                r.Chunk.Recalls++;
            }

            return recalled;
        }
    }

    /// <summary>How well the message matches something remembered: clip((A_max - tau) / 2, 0, 1), without noise.</summary>
    public double Familiarity(float[] query, MemoryModulation m, DateTime now)
    {
        lock (_gate)
        {
            var best = _chunks
                .Where(c => c is { Archived: false, Embedding: not null })
                .Select(c => (Chunk: c, Spread: Spreading(query, c)))
                .Where(x => x.Spread > 0) // familiar means this message points at something, not that memory is fresh
                .Select(x => BaseLevel(x.Chunk, now) + x.Spread)
                .DefaultIfEmpty(double.NegativeInfinity)
                .Max();
            return double.IsNegativeInfinity(best) ? 0 : Math.Clamp((best - Threshold(m)) / 2, 0, 1);
        }
    }

    /// <summary>Weight of one sleep replay: replay_weight * clip(1 + g m_consolidation, 0, 3), x0.3 in N2, 0 outside NREM.</summary>
    public double ReplayWeight(SleepPhase phase, MemoryModulation m)
    {
        var rate = phase switch
        {
            SleepPhase.N3 => 1.0,
            SleepPhase.N2 => config.N2ReplayRate,
            _ => 0,
        };
        return rate * config.ReplayWeight * Math.Clamp(1 + config.Gain * m.Consolidation, 0, 3);
    }

    /// <summary>
    /// NREM replay (spec: "Replay"): every replay_minutes of N2 or N3, the most salient episode (newest on a tie)
    /// and its facts get a presentation, and its salience decays so the others get a turn.
    /// </summary>
    public void OnTick(Brain brain)
    {
        var weight = ReplayWeight(brain.Phase, MemoryModulation.From(brain.Modulation));
        if (weight <= 0 || (brain.Now - _lastReplay).TotalMinutes < config.ReplayMinutes)
        {
            return;
        }

        if (ReplayMostSalient(brain.Now, weight) is { } episode)
        {
            _lastReplay = brain.Now;
            Replayed?.Invoke(episode);
        }
    }

    /// <summary>The most salient episode (newest on a tie) and its facts get a presentation; its salience decays.</summary>
    private Chunk? ReplayMostSalient(DateTime now, double weight)
    {
        lock (_gate)
        {
            var episode = _chunks
                .Where(c => c is { Kind: ChunkKind.Episode, Archived: false })
                .OrderByDescending(c => c.Salience)
                .ThenByDescending(c => c.Created)
                .FirstOrDefault();
            if (episode is null)
            {
                return null;
            }

            foreach (var chunk in _chunks.Where(c => c == episode || c.EpisodeId == episode.Id))
            {
                chunk.Presentations.Add(new Presentation(now, weight));
            }

            episode.Salience *= config.SalienceDecay;
            return episode;
        }
    }

    /// <summary>
    /// Turns the dream fragments of a checkpoint from before Phase 7 into episodes. They have no embedding,
    /// so they can be dreamed but never recalled or found familiar.
    /// </summary>
    public void Import(IEnumerable<Fragment> fragments)
    {
        foreach (var f in fragments)
        {
            Add(new Chunk
            {
                Id = NextId(),
                Kind = ChunkKind.Episode,
                Text = f.Text,
                Created = f.Time,
                Presentations = [new Presentation(f.Time, 1)],
                Tag = f.Tag,
                Salience = 0.05,
            }, f.Time);
        }
    }

    /// <summary>
    /// Dream seeds (spec: "Visele (REM)"): up to <paramref name="count"/> episodes drawn in proportion to their
    /// emotional tag, without replacement. Each one drawn loses <paramref name="detension"/> of its tag.
    /// </summary>
    public IReadOnlyList<string> DrawByTag(int count, Random random, double detension)
    {
        lock (_gate)
        {
            var pool = _chunks.Where(c => c is { Kind: ChunkKind.Episode, Archived: false }).ToList();
            var seeds = new List<string>();
            while (seeds.Count < count && pool.Count > 0)
            {
                var roll = random.NextDouble() * pool.Sum(c => c.Tag);
                var pick = pool[^1];
                foreach (var c in pool)
                {
                    roll -= c.Tag;
                    if (roll <= 0)
                    {
                        pick = c;
                        break;
                    }
                }

                pool.Remove(pick);
                seeds.Add(pick.Text);
                pick.Tag *= 1 - detension;
            }

            return seeds;
        }
    }

    private void Add(Chunk chunk, DateTime now)
    {
        lock (_gate)
        {
            _chunks.Add(chunk);

            // Nothing is deleted: past the limit, the weakest chunks leave recall but stay saved.
            var active = _chunks.Where(c => !c.Archived).ToList();
            foreach (var weakest in active.OrderBy(c => BaseLevel(c, now)).Take(Math.Max(0, active.Count - config.ArchiveLimit)))
            {
                weakest.Archived = true;
            }
        }
    }

    private int NextId() => Interlocked.Increment(ref _nextId);

    /// <summary>Logistic noise with scale s, from the seeded generator so runs are reproducible.</summary>
    private double Noise(double s)
    {
        var u = Math.Clamp(_random.NextDouble(), 1e-9, 1 - 1e-9);
        return s * Math.Log(u / (1 - u));
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na > 0 && nb > 0 ? dot / Math.Sqrt(na * nb) : 0;
    }
}
