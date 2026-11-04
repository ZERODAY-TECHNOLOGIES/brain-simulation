// Builds assets/models/brain.bin from BodyParts3D (D-16): downloads the STL parts, welds them, groups them
// into regions, decimates each region to a triangle budget, normalizes the coordinates and computes the
// anchor points for nuclei too small to have meshes of their own.
//
// Usage, from the repo root:  dotnet run --project tools/MeshPrep [--inspect]
// Downloads go to data/bp3d/stl (ignored by git).

using System.Numerics;
using HelixToolkit.Geometry;

const string Source = "https://raw.githubusercontent.com/Kevin-Mattheus-Moerman/BodyParts3D/main/assets/BodyParts3D_data/stl/";
var stlDir = Path.GetFullPath(Path.Combine("data", "bp3d", "stl"));
var output = Path.GetFullPath(Path.Combine("assets", "models", "brain.bin"));
var inspect = args.Contains("--inspect");
Directory.CreateDirectory(stlDir);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);

// Region key, display group, triangle budget, BodyParts3D parts (FMA ids).
(string Key, string Group, int Budget, string[] Parts)[] regions =
[
    ("frontal_l", "cortex", 7000, ["FMA72654", "FMA72656", "FMA72662"]),
    ("frontal_r", "cortex", 7000, ["FMA72653", "FMA72655", "FMA72661"]),
    ("orbital", "cortex", 4000, ["BP51"]),
    ("parietal_l", "cortex", 7000, ["FMA72666", "FMA72668", "FMA72670"]),
    ("parietal_r", "cortex", 7000, ["FMA72665", "FMA72667", "FMA72669"]),
    ("temporal_l", "cortex", 7000, ["FMA72801", "FMA72805", "FMA72686", "FMA72688", "FMA72690"]),
    ("temporal_r", "cortex", 7000, ["FMA72800", "FMA72804", "FMA72685", "FMA72687", "FMA72689"]),
    ("occipital_l", "cortex", 6000, ["FMA72976"]),
    ("occipital_r", "cortex", 6000, ["FMA72975"]),
    ("limbic_l", "cortex", 4000, ["FMA72718", "FMA72706"]),
    ("limbic_r", "cortex", 4000, ["FMA72717", "FMA72705"]),
    ("insula_l", "cortex", 3000, ["FMA72978", "FMA72702"]),
    ("insula_r", "cortex", 3000, ["FMA72977", "FMA72701"]),
    ("thalamus_l", "subcortical", 3000, ["FMA258716"]),
    ("thalamus_r", "subcortical", 3000, ["FMA258714"]),
    ("caudate_l", "subcortical", 3000, ["FMA72827"]),
    ("caudate_r", "subcortical", 3000, ["FMA72826"]),
    ("putamen_l", "subcortical", 3000, ["FMA72829"]),
    ("putamen_r", "subcortical", 3000, ["FMA72828"]),
    ("pallidus_l", "subcortical", 2000, ["FMA72831"]),
    ("pallidus_r", "subcortical", 2000, ["FMA72830"]),
    ("hippocampus_l", "subcortical", 2500, ["FMA72714"]),
    ("hippocampus_r", "subcortical", 2500, ["FMA72713"]),
    ("amygdala_l", "subcortical", 1200, ["FMA72833"]),
    ("amygdala_r", "subcortical", 1200, ["FMA72832"]),
    ("hypothalamus", "subcortical", 2000, ["FMA62008nsn"]),
    ("mammillary", "subcortical", 400, ["FMA74877"]),
    ("corpus_callosum", "subcortical", 6000, ["FMA86464"]),
    ("fornix", "subcortical", 3000, ["FMA72925", "FMA72924", "FMA61970"]),
    ("midbrain", "brainstem", 5000, ["FMA61993nsn", "FMA62394"]),
    ("pons", "brainstem", 6000, ["FMA67943"]),
    ("medulla", "brainstem", 4000, ["FMA62004"]),
    ("cerebellum", "brainstem", 16000, ["FMA67944"]),
    ("pineal", "gland", 600, ["FMA62033"]),
    ("pituitary", "gland", 1000, ["FMA13889"]),
];

using var http = new HttpClient();
var raw = new List<(string Key, string Group, List<Vector3> Positions, List<int> Indices, int Budget)>();
foreach (var (key, group, budget, parts) in regions)
{
    var positions = new List<Vector3>();
    var indices = new List<int>();
    foreach (var part in parts)
    {
        var (p, i) = Weld(ReadStl(await DownloadAsync(part)));
        indices.AddRange(i.Select(x => x + positions.Count));
        positions.AddRange(p);
    }

    raw.Add((key, group, positions, indices, budget));
}

if (inspect)
{
    foreach (var r in raw)
    {
        var (min, max) = Bounds(r.Positions);
        Console.WriteLine($"{r.Key,-16} tris {r.Indices.Count / 3,7}  centroid {Centroid(r.Positions):F1}  min {min:F1}  max {max:F1}");
    }

    return;
}

// BodyParts3D is in millimeters with X = patient's left, Y = posterior, Z = up (checked with --inspect: left
// structures have larger X, the occipital lobe larger Y than the frontal lobe, the pons lower Z than the thalamus).
// The app wants X = right, Y = up, Z = front, about 3.2 units across, centered on the brain.
var (allMin, allMax) = Bounds(raw.SelectMany(r => r.Positions));
var center = (allMin + allMax) / 2;
var scale = 3.2f / MathF.Max(allMax.X - allMin.X, MathF.Max(allMax.Y - allMin.Y, allMax.Z - allMin.Z));
Vector3 ToApp(Vector3 p)
{
    var q = (p - center) * scale;
    return new Vector3(-q.X, q.Z, -q.Y);
}

var meshes = new List<(string Key, string Group, Vector3[] Positions, Vector3[] Normals, int[] Indices)>();
var total = 0;
foreach (var r in raw)
{
    var (positions, indices) = Simplify(r.Positions, r.Indices, r.Budget);
    var app = positions.Select(ToApp).ToArray();
    meshes.Add((r.Key, r.Group, app, Normals(app, indices), indices));
    total += indices.Length / 3;
    Console.WriteLine($"{r.Key,-16} {r.Indices.Count / 3,7} -> {indices.Length / 3,6} triangles");
}

Console.WriteLine($"total {total:N0} triangles");

// Anchors: the nuclei and targets the pathways connect. Positions come from the meshes around each nucleus.
var box = meshes.ToDictionary(m => m.Key, m => Bounds(m.Positions));
var mid = meshes.ToDictionary(m => m.Key, m => Centroid(m.Positions));
Vector3 Lerp3((Vector3 Min, Vector3 Max) b, float x, float y, float z) =>
    new(b.Min.X + (b.Max.X - b.Min.X) * x, b.Min.Y + (b.Max.Y - b.Min.Y) * y, b.Min.Z + (b.Max.Z - b.Min.Z) * z);

var anchors = new Dictionary<string, Vector3>
{
    // Locus coeruleus: dorsal upper pons, either side of the midline, under the floor of the fourth ventricle.
    ["locus_coeruleus_l"] = Lerp3(box["pons"], 0.38f, 0.72f, 0.15f),
    ["locus_coeruleus_r"] = Lerp3(box["pons"], 0.62f, 0.72f, 0.15f),
    // Raphe nuclei: on the midline of the brainstem.
    ["raphe"] = Lerp3(box["pons"], 0.5f, 0.55f, 0.4f),
    // VTA and substantia nigra: ventral midbrain.
    ["vta"] = Lerp3(box["midbrain"], 0.5f, 0.35f, 0.75f),
    // Nucleus basalis: under the globus pallidus, toward the amygdala.
    ["basal_forebrain_l"] = Vector3.Lerp(mid["pallidus_l"], mid["amygdala_l"], 0.45f),
    ["basal_forebrain_r"] = Vector3.Lerp(mid["pallidus_r"], mid["amygdala_r"], 0.45f),
    // Suprachiasmatic nucleus: front and bottom of the hypothalamus, above the optic chiasm.
    ["scn"] = Lerp3(box["hypothalamus"], 0.5f, 0.15f, 0.95f),
    // Adrenal glands are far outside the brain; the HPA inset puts a stand-in below the medulla.
    ["adrenal"] = Lerp3(box["medulla"], 0.5f, 0f, 0.5f) - new Vector3(0, 0.3f, 0),
};

foreach (var m in meshes)
{
    anchors[m.Key] = mid[m.Key];
}

await using (var file = File.Create(output))
using (var writer = new BinaryWriter(file))
{
    writer.Write("BRN1");
    writer.Write(meshes.Count);
    foreach (var m in meshes)
    {
        writer.Write(m.Key);
        writer.Write(m.Group);
        writer.Write(m.Positions.Length);
        foreach (var p in m.Positions) { writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); }
        foreach (var n in m.Normals) { writer.Write(n.X); writer.Write(n.Y); writer.Write(n.Z); }
        writer.Write(m.Indices.Length);
        foreach (var i in m.Indices) { writer.Write(i); }
    }

    writer.Write(anchors.Count);
    foreach (var (name, p) in anchors)
    {
        writer.Write(name);
        writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z);
    }
}

Console.WriteLine($"Wrote {output} ({new FileInfo(output).Length / 1024:N0} KB)");
return;

async Task<string> DownloadAsync(string part)
{
    var path = Path.Combine(stlDir, part + ".stl");
    if (!File.Exists(path))
    {
        Console.WriteLine($"Downloading {part}");
        await File.WriteAllBytesAsync(path, await http.GetByteArrayAsync(Source + part + ".stl"));
    }

    return path;
}

static List<Vector3> ReadStl(string path)
{
    // Binary STL: 80-byte header, triangle count, then 50 bytes per triangle (normal, 3 vertices, attribute).
    using var reader = new BinaryReader(File.OpenRead(path));
    reader.ReadBytes(80);
    var count = reader.ReadUInt32();
    if (reader.BaseStream.Length != 84 + 50L * count)
    {
        throw new InvalidDataException($"{Path.GetFileName(path)} is not a binary STL file.");
    }

    var vertices = new List<Vector3>((int)count * 3);
    for (var t = 0; t < count; t++)
    {
        reader.ReadBytes(12);
        for (var v = 0; v < 3; v++)
        {
            vertices.Add(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
        }

        reader.ReadUInt16();
    }

    return vertices;
}

// STL repeats every vertex per triangle. Merge identical positions (to 0.001 mm) so the mesh is connected.
static (List<Vector3>, List<int>) Weld(List<Vector3> soup)
{
    var index = new Dictionary<(int, int, int), int>();
    var positions = new List<Vector3>();
    var indices = new List<int>(soup.Count);
    foreach (var v in soup)
    {
        var key = ((int)MathF.Round(v.X * 1000), (int)MathF.Round(v.Y * 1000), (int)MathF.Round(v.Z * 1000));
        if (!index.TryGetValue(key, out var i))
        {
            i = positions.Count;
            index[key] = i;
            positions.Add(v);
        }

        indices.Add(i);
    }

    return (positions, indices);
}

static (Vector3[], int[]) Simplify(List<Vector3> positions, List<int> indices, int budget)
{
    if (indices.Count / 3 <= budget)
    {
        return ([.. positions], [.. indices]);
    }

    var builder = new MeshBuilder(false, false, false);
    builder.Positions.AddRange(positions);
    builder.TriangleIndices.AddRange(indices);
    var simplified = new MeshSimplification(builder.ToMesh()).Simplify(budget, 7, false, false);
    return ([.. simplified.Positions!], [.. simplified.TriangleIndices!]);
}

static Vector3[] Normals(Vector3[] positions, int[] indices)
{
    var normals = new Vector3[positions.Length];
    for (var i = 0; i < indices.Length; i += 3)
    {
        var (a, b, c) = (indices[i], indices[i + 1], indices[i + 2]);
        var n = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
        normals[a] += n;
        normals[b] += n;
        normals[c] += n;
    }

    return [.. normals.Select(n => n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitY)];
}

static (Vector3 Min, Vector3 Max) Bounds(IEnumerable<Vector3> points)
{
    var min = new Vector3(float.MaxValue);
    var max = new Vector3(float.MinValue);
    foreach (var p in points)
    {
        min = Vector3.Min(min, p);
        max = Vector3.Max(max, p);
    }

    return (min, max);
}

static Vector3 Centroid(IReadOnlyCollection<Vector3> points) => points.Aggregate(Vector3.Zero, (s, p) => s + p) / points.Count;
