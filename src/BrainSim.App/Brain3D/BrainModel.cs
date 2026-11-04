using System.IO;
using System.Numerics;

namespace BrainSim.App.Brain3D;

/// <summary>One anatomical region from brain.bin. App coordinates: X right, Y up, Z front.</summary>
public sealed record BrainRegion(string Key, string Group, Vector3[] Positions, Vector3[] Normals, int[] Indices)
{
    public bool IsLeft => Key.EndsWith("_l", StringComparison.Ordinal);
    public bool IsRight => Key.EndsWith("_r", StringComparison.Ordinal);
}

/// <summary>The meshes and anchor points written by tools/MeshPrep (assets/models/ATTRIBUTION.md).</summary>
public sealed record BrainModel(IReadOnlyList<BrainRegion> Regions, IReadOnlyDictionary<string, Vector3> Anchors)
{
    public static BrainModel Load(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (reader.ReadString() != "BRN1")
        {
            throw new InvalidDataException($"{path} is not a BrainSim mesh file.");
        }

        var regions = new List<BrainRegion>();
        for (int r = 0, count = reader.ReadInt32(); r < count; r++)
        {
            var key = reader.ReadString();
            var group = reader.ReadString();
            var vertices = reader.ReadInt32();
            var positions = ReadVectors(reader, vertices);
            var normals = ReadVectors(reader, vertices);
            var indices = new int[reader.ReadInt32()];
            for (var i = 0; i < indices.Length; i++)
            {
                indices[i] = reader.ReadInt32();
            }

            regions.Add(new BrainRegion(key, group, positions, normals, indices));
        }

        var anchors = new Dictionary<string, Vector3>();
        for (int a = 0, count = reader.ReadInt32(); a < count; a++)
        {
            anchors[reader.ReadString()] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        return new BrainModel(regions, anchors);
    }

    private static Vector3[] ReadVectors(BinaryReader reader, int count)
    {
        var result = new Vector3[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        return result;
    }
}
