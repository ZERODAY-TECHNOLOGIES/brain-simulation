namespace BrainSim.Engine.Tests;

/// <summary>Embedding vectors for tests.</summary>
internal static class Vectors
{
    /// <summary><paramref name="v"/> scaled to length 1, the way nomic returns embeddings.</summary>
    public static float[] Unit(params float[] v)
    {
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        return [.. v.Select(x => x / norm)];
    }
}
