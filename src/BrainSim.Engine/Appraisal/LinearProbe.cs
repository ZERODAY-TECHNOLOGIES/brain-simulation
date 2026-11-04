namespace BrainSim.Engine.Appraisal;

/// <summary>
/// A linear classifier on a text embedding: p = sigmoid(w . x + b).
/// The spec trains probes on the residual stream; we train them on embeddings (D-05).
/// </summary>
public sealed record LinearProbe(float[] Weights, float Bias, double Auc, string Source)
{
    public double Predict(ReadOnlySpan<float> embedding)
    {
        double z = Bias;
        for (var i = 0; i < Weights.Length; i++)
        {
            z += Weights[i] * embedding[i];
        }

        return 1 / (1 + Math.Exp(-z));
    }

    /// <summary>
    /// L2-regularized logistic regression, full-batch Adam, with classes reweighted to balance.
    /// Small and dependency-free; the inputs are a few thousand 768-dimensional vectors.
    /// </summary>
    public static (float[] Weights, float Bias) Train(
        IReadOnlyList<float[]> x, IReadOnlyList<bool> y, int epochs = 300, double learningRate = 0.05, double l2 = 1e-4)
    {
        var n = x.Count;
        var d = x[0].Length;
        var positives = y.Count(v => v);
        var weightPos = n / (2.0 * Math.Max(1, positives));
        var weightNeg = n / (2.0 * Math.Max(1, n - positives));

        var w = new double[d + 1]; // last entry is the bias
        var m = new double[d + 1];
        var v = new double[d + 1];
        var grad = new double[d + 1];
        const double beta1 = 0.9, beta2 = 0.999, eps = 1e-8;

        for (var epoch = 1; epoch <= epochs; epoch++)
        {
            Array.Clear(grad);
            for (var i = 0; i < n; i++)
            {
                var xi = x[i];
                var z = w[d];
                for (var j = 0; j < d; j++)
                {
                    z += w[j] * xi[j];
                }

                var error = (1 / (1 + Math.Exp(-z)) - (y[i] ? 1 : 0)) * (y[i] ? weightPos : weightNeg);
                for (var j = 0; j < d; j++)
                {
                    grad[j] += error * xi[j];
                }

                grad[d] += error;
            }

            for (var j = 0; j <= d; j++)
            {
                var g = grad[j] / n + (j < d ? l2 * w[j] : 0);
                m[j] = beta1 * m[j] + (1 - beta1) * g;
                v[j] = beta2 * v[j] + (1 - beta2) * g * g;
                var mHat = m[j] / (1 - Math.Pow(beta1, epoch));
                var vHat = v[j] / (1 - Math.Pow(beta2, epoch));
                w[j] -= learningRate * mHat / (Math.Sqrt(vHat) + eps);
            }
        }

        return ([.. w.Take(d).Select(x => (float)x)], (float)w[d]);
    }

    /// <summary>ROC AUC as the Mann-Whitney statistic, with ties counted as half.</summary>
    public static double RocAuc(IReadOnlyList<double> scores, IReadOnlyList<bool> labels)
    {
        var order = Enumerable.Range(0, scores.Count).OrderBy(i => scores[i]).ToArray();
        double rankSumPositive = 0;
        var i = 0;
        while (i < order.Length)
        {
            var j = i;
            while (j + 1 < order.Length && scores[order[j + 1]] == scores[order[i]])
            {
                j++;
            }

            var averageRank = (i + j) / 2.0 + 1;
            for (var k = i; k <= j; k++)
            {
                if (labels[order[k]])
                {
                    rankSumPositive += averageRank;
                }
            }

            i = j + 1;
        }

        double positives = labels.Count(l => l);
        var negatives = labels.Count - positives;
        return (rankSumPositive - positives * (positives + 1) / 2) / (positives * negatives);
    }
}
