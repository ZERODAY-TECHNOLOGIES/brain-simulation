using Parquet;
using Parquet.Schema;

namespace BrainSim.Engine.Journal;

/// <summary>
/// The full state after one tick. It is both a journal row and the frame the UI draws.
/// </summary>
public sealed record TickRecord(
    long Tick,
    DateTime Time,
    string Phase,
    int Cycle,
    double Phi,
    double SleepPressure,
    double[] C,
    double[] Baseline,
    double[] MeanC,
    double[] Rho,
    double[] M,
    string? Token)
{
    public static TickRecord From(Brain brain, string? token) => new(
        brain.Chemistry.Tick,
        brain.Now,
        brain.Phase.ToString(),
        brain.Cycle,
        brain.Clock.Phase(brain.Now),
        brain.SleepPressure,
        [.. brain.Chemistry.C],
        [.. brain.Chemistry.Baseline],
        [.. brain.Receptors.MeanC],
        [.. brain.Receptors.Rho],
        [.. brain.Modulation.M],
        token);
}

/// <summary>
/// Tick journal in Parquet, one row per tick (spec: "jurnal").
/// Rows are buffered and written as a row group every <c>rowGroupSize</c> ticks, so a crash loses at most one group.
/// </summary>
public sealed class TickJournal : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly ParquetWriter _writer;
    private readonly Columns _columns;
    private readonly int _rowGroupSize;
    private readonly List<TickRecord> _buffer;

    private TickJournal(Stream stream, ParquetWriter writer, Columns columns, int rowGroupSize)
    {
        _stream = stream;
        _writer = writer;
        _columns = columns;
        _rowGroupSize = rowGroupSize;
        _buffer = new List<TickRecord>(rowGroupSize);
    }

    public string? Path { get; private init; }

    public static async Task<TickJournal> CreateAsync(string path, IReadOnlyList<string> channelNames, int rowGroupSize = 1000)
    {
        var columns = new Columns(channelNames);
        var stream = File.Create(path);
        var writer = await ParquetWriter.CreateAsync(columns.Schema, stream);
        return new TickJournal(stream, writer, columns, rowGroupSize) { Path = path };
    }

    public async ValueTask AppendAsync(TickRecord record)
    {
        _buffer.Add(record);
        if (_buffer.Count >= _rowGroupSize)
        {
            await FlushAsync();
        }
    }

    /// <summary>
    /// For callers on the engine thread, which has no synchronization context, so blocking here cannot deadlock.
    /// </summary>
    public void Append(TickRecord record) => AppendAsync(record).AsTask().GetAwaiter().GetResult();

    public async ValueTask FlushAsync()
    {
        if (_buffer.Count == 0)
        {
            return;
        }

        using (var group = _writer.CreateRowGroup())
        {
            await group.WriteAsync(_columns.Tick, (ReadOnlyMemory<long>)_buffer.Select(r => r.Tick).ToArray());
            await group.WriteAsync(_columns.Time, (ReadOnlyMemory<long>)_buffer.Select(r => r.Time.Ticks).ToArray());
            await group.WriteAsync(_columns.Phase, _buffer.Select(r => r.Phase).ToArray());
            await group.WriteAsync(_columns.Cycle, (ReadOnlyMemory<int>)_buffer.Select(r => r.Cycle).ToArray());
            await group.WriteAsync(_columns.Phi, (ReadOnlyMemory<double>)_buffer.Select(r => r.Phi).ToArray());
            await group.WriteAsync(_columns.Pressure, (ReadOnlyMemory<double>)_buffer.Select(r => r.SleepPressure).ToArray());
            await WriteVectorAsync(group, _columns.C, r => r.C);
            await WriteVectorAsync(group, _columns.Baseline, r => r.Baseline);
            await WriteVectorAsync(group, _columns.MeanC, r => r.MeanC);
            await WriteVectorAsync(group, _columns.Rho, r => r.Rho);
            await WriteVectorAsync(group, _columns.M, r => r.M);
            await group.WriteAsync(_columns.Token, _buffer.Select(r => r.Token!).ToArray());
        }

        _buffer.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await FlushAsync();
        await _writer.DisposeAsync();
        await _stream.DisposeAsync();
    }

    /// <summary>Streams every row to <paramref name="onRecord"/>, one row group at a time, so memory stays bounded.</summary>
    public static async Task ReadAsync(string path, Action<TickRecord> onRecord)
    {
        await using var reader = await ParquetReader.CreateAsync(path);
        var channelNames = reader.Schema.DataFields
            .Where(f => f.Name.StartsWith("m_", StringComparison.Ordinal)).Select(f => f.Name[2..]).ToArray();
        var columns = new Columns(channelNames);

        for (var g = 0; g < reader.RowGroupCount; g++)
        {
            using var group = reader.OpenRowGroupReader(g);
            var n = (int)group.RowCount;

            var ticks = await ReadColumnAsync<long>(group, columns.Tick, n);
            var times = await ReadColumnAsync<long>(group, columns.Time, n);
            var cycles = await ReadColumnAsync<int>(group, columns.Cycle, n);
            var phis = await ReadColumnAsync<double>(group, columns.Phi, n);
            var pressures = await ReadColumnAsync<double>(group, columns.Pressure, n);
            var phases = new string?[n];
            await group.ReadAsync(columns.Phase, phases.AsMemory());
            var tokens = new string?[n];
            await group.ReadAsync(columns.Token, tokens.AsMemory());
            var c = await ReadVectorAsync(group, columns.C, n);
            var baseline = await ReadVectorAsync(group, columns.Baseline, n);
            var meanC = await ReadVectorAsync(group, columns.MeanC, n);
            var rho = await ReadVectorAsync(group, columns.Rho, n);
            var m = await ReadVectorAsync(group, columns.M, n);

            for (var i = 0; i < n; i++)
            {
                onRecord(new TickRecord(
                    ticks[i], new DateTime(times[i]), phases[i]!, cycles[i], phis[i], pressures[i],
                    c[i], baseline[i], meanC[i], rho[i], m[i], tokens[i]));
            }
        }
    }

    public static async Task<List<TickRecord>> ReadAllAsync(string path)
    {
        var records = new List<TickRecord>();
        await ReadAsync(path, records.Add);
        return records;
    }

    private async Task WriteVectorAsync(ParquetRowGroupWriter group, DataField[] fields, Func<TickRecord, double[]> select)
    {
        for (var j = 0; j < fields.Length; j++)
        {
            await group.WriteAsync(fields[j], (ReadOnlyMemory<double>)_buffer.Select(r => select(r)[j]).ToArray());
        }
    }

    private static async Task<T[]> ReadColumnAsync<T>(ParquetRowGroupReader group, DataField field, int rows)
        where T : struct
    {
        var values = new T[rows];
        await group.ReadAsync(field, values.AsMemory());
        return values;
    }

    private static async Task<double[][]> ReadVectorAsync(ParquetRowGroupReader group, DataField[] fields, int rows)
    {
        var result = new double[rows][];
        for (var i = 0; i < rows; i++)
        {
            result[i] = new double[fields.Length];
        }

        var column = new double[rows];
        for (var j = 0; j < fields.Length; j++)
        {
            await group.ReadAsync(fields[j], column.AsMemory());
            for (var i = 0; i < rows; i++)
            {
                result[i][j] = column[i];
            }
        }

        return result;
    }

    /// <summary>
    /// Column layout: tick, time (DateTime ticks), phase, cycle, phi, sleep_pressure,
    /// c_*, baseline_*, mean_c_*, rho_* (per substance), m_* (per channel), token.
    /// </summary>
    private sealed class Columns
    {
        public Columns(IReadOnlyList<string> channelNames)
        {
            C = PerSubstance("c_");
            Baseline = PerSubstance("baseline_");
            MeanC = PerSubstance("mean_c_");
            Rho = PerSubstance("rho_");
            M = [.. channelNames.Select(n => new DataField<double>("m_" + n))];
            Schema = new ParquetSchema([Tick, Time, Phase, Cycle, Phi, Pressure, .. C, .. Baseline, .. MeanC, .. Rho, .. M, Token]);
        }

        public DataField Tick { get; } = new DataField<long>("tick");
        public DataField Time { get; } = new DataField<long>("time");
        public DataField Phase { get; } = new DataField<string>("phase");
        public DataField Cycle { get; } = new DataField<int>("cycle");
        public DataField Phi { get; } = new DataField<double>("phi");
        public DataField Pressure { get; } = new DataField<double>("sleep_pressure");
        public DataField Token { get; } = new DataField<string>("token", true);
        public DataField[] C { get; }
        public DataField[] Baseline { get; }
        public DataField[] MeanC { get; }
        public DataField[] Rho { get; }
        public DataField[] M { get; }
        public ParquetSchema Schema { get; }

        private static DataField[] PerSubstance(string prefix) =>
            [.. Substances.Abbreviations.Select(a => new DataField<double>(prefix + a))];
    }
}
