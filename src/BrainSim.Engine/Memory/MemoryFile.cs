namespace BrainSim.Engine.Memory;

/// <summary>
/// memory.bin (docs/memory.md "Persistence"): a binary file next to the checkpoint. Embeddings would make JSON
/// tens of megabytes. Written through a temporary file and a rename, so a crash never leaves half a memory.
/// </summary>
public static class MemoryFile
{
    private const string Header = "MEM1";

    public static void Save(string path, IReadOnlyList<Chunk> chunks)
    {
        var temp = path + ".tmp";
        using (var writer = new BinaryWriter(File.Create(temp)))
        {
            writer.Write(Header);
            writer.Write(chunks.Count);
            foreach (var c in chunks)
            {
                writer.Write(c.Id);
                writer.Write((byte)c.Kind);
                writer.Write(c.Text);
                writer.Write(c.Embedding?.Length ?? 0);
                foreach (var f in c.Embedding ?? [])
                {
                    writer.Write(f);
                }

                writer.Write(c.Created.ToBinary());
                writer.Write(c.Presentations.Count);
                foreach (var p in c.Presentations)
                {
                    writer.Write(p.Time.ToBinary());
                    writer.Write(p.Weight);
                }

                writer.Write(c.Tag);
                writer.Write(c.Salience);
                writer.Write(c.EpisodeId ?? -1);
                writer.Write(c.Archived);
                writer.Write(c.LastRecalled?.ToBinary() ?? 0L);
                writer.Write(c.LastRecalled.HasValue);
                writer.Write(c.Recalls);
            }
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The chunks in the file, or none when it is missing. A file that cannot be read is renamed to .bad.</summary>
    public static IReadOnlyList<Chunk> Load(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            using var reader = new BinaryReader(File.OpenRead(path));
            if (reader.ReadString() != Header)
            {
                throw new InvalidDataException("not a memory file");
            }

            var chunks = new List<Chunk>();
            for (int i = 0, count = reader.ReadInt32(); i < count; i++)
            {
                var id = reader.ReadInt32();
                var kind = (ChunkKind)reader.ReadByte();
                var text = reader.ReadString();
                var length = reader.ReadInt32();
                float[]? embedding = length > 0 ? new float[length] : null;
                for (var j = 0; j < length; j++)
                {
                    embedding![j] = reader.ReadSingle();
                }

                var created = DateTime.FromBinary(reader.ReadInt64());
                var presentations = new List<Presentation>();
                for (int j = 0, n = reader.ReadInt32(); j < n; j++)
                {
                    presentations.Add(new Presentation(DateTime.FromBinary(reader.ReadInt64()), reader.ReadDouble()));
                }

                var tag = reader.ReadDouble();
                var salience = reader.ReadDouble();
                var episode = reader.ReadInt32();
                var archived = reader.ReadBoolean();
                var lastRecalled = reader.ReadInt64();
                var hasLastRecalled = reader.ReadBoolean();
                chunks.Add(new Chunk
                {
                    Id = id, Kind = kind, Text = text, Embedding = embedding, Created = created, Presentations = presentations,
                    Tag = tag, Salience = salience, EpisodeId = episode < 0 ? null : episode, Archived = archived,
                    LastRecalled = hasLastRecalled ? DateTime.FromBinary(lastRecalled) : null, Recalls = reader.ReadInt32(),
                });
            }

            return chunks;
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException or IOException or ArgumentException)
        {
            // Keep the file for a look later; start with an empty memory rather than fail to start.
            File.Move(path, path + ".bad", overwrite: true);
            return [];
        }
    }
}
