using BrainSim.Engine.Appraisal;

namespace BrainSim.Engine.Tests;

public class LinearProbeTests
{
    [Fact]
    public void Auc_is_1_for_perfect_order_half_for_ties_and_0_for_reversed()
    {
        bool[] labels = [false, false, true, true];
        Assert.Equal(1.0, LinearProbe.RocAuc([0.1, 0.2, 0.8, 0.9], labels), 9);
        Assert.Equal(0.5, LinearProbe.RocAuc([0.5, 0.5, 0.5, 0.5], labels), 9);
        Assert.Equal(0.0, LinearProbe.RocAuc([0.9, 0.8, 0.2, 0.1], labels), 9);
    }

    [Fact]
    public void Training_separates_an_imbalanced_linearly_separable_set()
    {
        // 10% positives, separated along one direction, plus noise in the others.
        var random = new Random(7);
        var x = new List<float[]>();
        var y = new List<bool>();
        for (var i = 0; i < 2000; i++)
        {
            var positive = i % 10 == 0;
            var v = new float[16];
            for (var j = 0; j < v.Length; j++)
            {
                v[j] = (float)(random.NextDouble() - 0.5);
            }

            v[3] += positive ? 0.6f : -0.6f;
            x.Add(v);
            y.Add(positive);
        }

        var (weights, bias) = LinearProbe.Train(x, y, epochs: 200);
        var probe = new LinearProbe(weights, bias, 0, "synthetic");
        var scores = x.Select(v => probe.Predict(v)).ToList();

        Assert.True(LinearProbe.RocAuc(scores, y) > 0.99);
        // Balanced class weights keep the positives above 0.5 despite being rare.
        Assert.True(scores.Where((_, i) => y[i]).Average() > 0.5);
        Assert.True(scores.Where((_, i) => !y[i]).Average() < 0.5);
    }
}
