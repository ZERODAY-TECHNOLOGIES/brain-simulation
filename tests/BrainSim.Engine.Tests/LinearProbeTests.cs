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

    [Fact]
    public void The_first_Adam_step_moves_every_parameter_by_the_learning_rate_against_its_gradient()
    {
        // One positive at (1, -0.5): the loss falls with w0 and the bias, and rises with w1.
        var (weights, bias) = LinearProbe.Train([[1f, -0.5f]], [true], epochs: 1, learningRate: 0.1);
        Assert.Equal([0.1f, -0.1f], weights, (a, b) => Math.Abs(a - b) < 1e-6);
        Assert.Equal(0.1f, bias, 6);
    }

    [Fact]
    public void A_trained_probe_reproduces_the_class_frequencies()
    {
        // At x = 4 three of four samples are positive, at x = 0 one of four.
        var (x, y) = Overlapping();
        var probe = Probe(LinearProbe.Train(x, y, epochs: 1000));
        Assert.Equal(0.75, probe.Predict([4f]), 2);
        Assert.Equal(0.25, probe.Predict([0f]), 2);
    }

    [Fact]
    public void Strong_l2_flattens_the_slope_but_leaves_the_bias_free()
    {
        // With l2 = 1 the slope settles where the mean gradient 2 p(4) - 1.5 balances l2 * w, at p(4) = 0.62.
        // The bias is not penalized, so the boundary stays halfway between the classes.
        var (x, y) = Overlapping();
        var probe = Probe(LinearProbe.Train(x, y, epochs: 1000, l2: 1));
        Assert.Equal(0.62, probe.Predict([4f]), 2);
        Assert.Equal(0.5, probe.Predict([2f]), 2);
    }

    [Fact]
    public void Balanced_class_weights_make_an_uninformative_probe_say_one_half_not_the_base_rate()
    {
        float[][] x = [.. Enumerable.Range(0, 10).Select(_ => new[] { 0f })];
        bool[] y = [.. Enumerable.Range(0, 10).Select(i => i == 0)];
        Assert.Equal(0.5, Probe(LinearProbe.Train(x, y)).Predict([0f]), 3);
    }

    private static (float[][] X, bool[] Y) Overlapping() =>
        ([[4f], [4f], [4f], [4f], [0f], [0f], [0f], [0f]], [true, true, true, false, true, false, false, false]);

    private static LinearProbe Probe((float[] Weights, float Bias) trained) => new(trained.Weights, trained.Bias, 0, "test");
}
