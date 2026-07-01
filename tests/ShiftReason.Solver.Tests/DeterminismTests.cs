using System.Security.Cryptography;
using System.Text;
using ShiftReason.Domain;
using ShiftReason.Solver;
using Xunit.Abstractions;

namespace ShiftReason.Solver.Tests;

/// <summary>
/// Reproducibility is a claim the README makes, so it is measured rather than
/// asserted in prose.
/// </summary>
public class DeterminismTests(ITestOutputHelper output)
{
    [Fact]
    public void Same_scenario_and_seed_reproduce_an_identical_roster()
    {
        var first = RosterSolver.Solve(WardPresets.BalancedWard(), seconds: 3, seed: 20260322);
        var second = RosterSolver.Solve(WardPresets.BalancedWard(), seconds: 3, seed: 20260322);

        Assert.True(first.IsFeasible && second.IsFeasible);

        var a = Fingerprint(first.Roster!);
        var b = Fingerprint(second.Roster!);
        output.WriteLine($"run 1: {a}");
        output.WriteLine($"run 2: {b}");
        output.WriteLine($"objective {first.Objective} vs {second.Objective}");

        Assert.Equal(a, b);
        Assert.Equal(first.Objective, second.Objective);
    }

    /// <summary>
    /// Reproducible mode is not the default precisely because it costs throughput,
    /// so this records the gap rather than pretending it is free.
    /// </summary>
    [Fact]
    public void Report_cost_of_reproducibility()
    {
        var scenario = WardPresets.BalancedWard();

        var fast = RosterSolver.Solve(scenario, seconds: 6);
        var repro = RosterSolver.Solve(scenario, seconds: 6, seed: 7);

        output.WriteLine($"fast        objective={fast.Objective} wall={fast.WallSeconds:0.0}s");
        output.WriteLine($"reproducible objective={repro.Objective} wall={repro.WallSeconds:0.0}s");

        Assert.True(fast.IsFeasible);
        Assert.True(repro.IsFeasible);
    }

    private static string Fingerprint(Roster roster)
    {
        var canonical = new StringBuilder();
        foreach (var a in roster.Assignments
                     .OrderBy(a => a.EmployeeId, StringComparer.Ordinal)
                     .ThenBy(a => a.Date))
        {
            canonical.Append(a.EmployeeId).Append('|')
                     .Append(a.Date.ToString("yyyy-MM-dd")).Append('|')
                     .Append(a.ShiftTypeId).Append('\n');
        }

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))[..16];
    }
}
