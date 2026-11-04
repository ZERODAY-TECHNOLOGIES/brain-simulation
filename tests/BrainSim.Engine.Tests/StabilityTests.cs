using BrainSim.Engine.Chemistry;
using BrainSim.Engine.Config;
using MathNet.Numerics.LinearAlgebra;

namespace BrainSim.Engine.Tests;

/// <summary>Spec validation: all eigenvalues of the linearized system -K + W must be negative.</summary>
public class StabilityTests
{
    private static double[] RealParts(bool asleep)
    {
        var m = new ChemicalState(EngineConfig.LoadDefault()).LinearizedMatrix(asleep);
        return [.. Matrix<double>.Build.DenseOfArray(m).Evd().EigenValues.Select(e => e.Real)];
    }

    [Fact]
    public void All_eigenvalues_are_negative_during_sleep()
    {
        Assert.All(RealParts(asleep: true), re => Assert.True(re < 0, $"eigenvalue real part {re} is not negative"));
    }

    [Fact]
    public void Awake_only_adenosine_is_marginal()
    {
        // ADO has no decay while awake by design, so it contributes one zero eigenvalue. Everything else must be negative.
        var real = RealParts(asleep: false);
        Assert.Single(real, re => Math.Abs(re) < 1e-12);
        Assert.All(real.Where(re => Math.Abs(re) >= 1e-12), re => Assert.True(re < 0, $"eigenvalue real part {re} is not negative"));

        // So the largest one, which the Lab shows, is that zero.
        Assert.Equal(0.0, new ChemicalState(EngineConfig.LoadDefault()).LargestEigenvalue(asleep: false), 12);
    }
}
