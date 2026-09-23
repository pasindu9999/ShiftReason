using ShiftReason.Solver;

namespace ShiftReason.Solver.Tests;

public class SolverParametersTests
{
    /// <summary>
    /// One worker is a cliff, not a slope: with no LNS the 44-nurse ward cannot
    /// find a first roster in ten seconds. A 2-core host must still get two.
    /// </summary>
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(4, 3)]
    [InlineData(16, 15)]
    public void Never_drops_to_a_single_worker(int cores, int expected) =>
        Assert.Equal(expected, SolverParameters.ResolveWorkers(configured: "", processorCount: cores));

    [Fact]
    public void An_explicit_override_wins() =>
        Assert.Equal(1, SolverParameters.ResolveWorkers(configured: "1", processorCount: 16));

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("lots")]
    public void A_nonsense_override_is_ignored(string configured) =>
        Assert.Equal(3, SolverParameters.ResolveWorkers(configured, processorCount: 4));

    [Fact]
    public void The_explanation_solve_always_uses_exactly_one_worker() =>
        Assert.Contains("num_workers:1,", SolverParameters.Explain(5));
}
