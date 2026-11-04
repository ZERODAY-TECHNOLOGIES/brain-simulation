using BrainSim.Engine.Memory;

namespace BrainSim.Engine.Tests;

public sealed class MemoryFileTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Local);
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"brainsim-memory-{Guid.NewGuid():N}.bin");

    public void Dispose()
    {
        File.Delete(_path);
        File.Delete(_path + ".bad");
    }

    [Fact]
    public void Chunks_round_trip_bit_for_bit()
    {
        Chunk[] chunks =
        [
            new() { Id = 1, Kind = ChunkKind.Episode, Text = "hello\nthere", Embedding = [0.1f, -0.25f, 3e-8f], Created = Now,
                    Presentations = [new(Now, 1.48), new(Now.AddSeconds(30.5), 0.3)], Tag = 1.05, Salience = 0.35 },
            new() { Id = 2, Kind = ChunkKind.Fact, Text = "The user has a black cat.", Embedding = [1f, 0f, 0f], Created = Now.AddSeconds(1),
                    Presentations = [new(Now.AddSeconds(1), 1)], EpisodeId = 1, Archived = true, LastRecalled = Now.AddHours(2), Recalls = 3 },
            new() { Id = 3, Kind = ChunkKind.Episode, Text = "an old dream fragment", Embedding = null, Created = Now.AddDays(-1) },
        ];

        MemoryFile.Save(_path, chunks);
        var loaded = MemoryFile.Load(_path);

        Assert.Equal(chunks.Length, loaded.Count);
        for (var i = 0; i < chunks.Length; i++)
        {
            var (a, b) = (chunks[i], loaded[i]);
            Assert.Equal((a.Id, a.Kind, a.Text, a.Created, a.Tag, a.Salience, a.EpisodeId, a.Archived, a.LastRecalled, a.Recalls),
                         (b.Id, b.Kind, b.Text, b.Created, b.Tag, b.Salience, b.EpisodeId, b.Archived, b.LastRecalled, b.Recalls));
            Assert.Equal(a.Embedding, b.Embedding);
            Assert.Equal(a.Presentations, b.Presentations);
        }
    }

    [Fact]
    public void A_corrupt_file_is_set_aside()
    {
        File.WriteAllBytes(_path, [1, 2, 3, 4, 5, 6, 7, 8, 9]);

        var loaded = MemoryFile.Load(_path);

        Assert.Empty(loaded);
        Assert.True(File.Exists(_path + ".bad"));
        Assert.False(File.Exists(_path));
    }

    [Theory]
    [InlineData("bad string length")]
    [InlineData("huge embedding")]
    public void Any_corrupt_file_is_set_aside(string corruption)
    {
        using (var writer = new BinaryWriter(File.Create(_path)))
        {
            if (corruption == "bad string length")
            {
                writer.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }); // an invalid 7-bit length: FormatException
            }
            else
            {
                writer.Write("MEM1");
                writer.Write(1);         // one chunk
                writer.Write(1);         // id
                writer.Write((byte)0);   // kind
                writer.Write("text");
                writer.Write(int.MaxValue); // embedding length: OutOfMemoryException
            }
        }

        Assert.Empty(MemoryFile.Load(_path));
        Assert.True(File.Exists(_path + ".bad"));
    }

    [Fact]
    public void A_readable_file_of_another_format_is_set_aside()
    {
        using (var writer = new BinaryWriter(File.Create(_path)))
        {
            writer.Write("MEM0"); // a header that reads fine but is not this format
            writer.Write(0);
        }

        Assert.Empty(MemoryFile.Load(_path));
        Assert.True(File.Exists(_path + ".bad"));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void A_second_corrupt_file_replaces_the_one_set_aside_before()
    {
        File.WriteAllBytes(_path + ".bad", [1]);
        File.WriteAllBytes(_path, [9, 9, 9]);

        Assert.Empty(MemoryFile.Load(_path));
        Assert.Equal([9, 9, 9], File.ReadAllBytes(_path + ".bad"));
    }

    [Fact]
    public void A_missing_file_is_an_empty_memory()
    {
        Assert.Empty(MemoryFile.Load(_path));
        Assert.False(File.Exists(_path + ".bad"));
    }

    [Fact]
    public void Loaded_chunks_continue_their_ids()
    {
        var memory = new DeclarativeMemory(Config.Memory);
        memory.Load([new Chunk { Id = 41, Kind = ChunkKind.Fact, Text = "old", Embedding = [1f], Created = Now, Presentations = [new(Now, 1)] }]);

        var next = memory.AddFact("new", [1f], 0, default, Now.AddSeconds(5));

        Assert.Equal(42, next.Id);
        Assert.Equal(2, memory.Snapshot().Count);
    }

    private static readonly Config.EngineConfig Config = BrainSim.Engine.Config.EngineConfig.LoadDefault();
}
