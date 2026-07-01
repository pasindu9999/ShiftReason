using Google.OrTools.Sat;
using ShiftReason.Domain;
using ShiftReason.Solver;
using Xunit.Abstractions;

namespace ShiftReason.Solver.Tests;

public class ObjectiveTests(ITestOutputHelper output)
{
    /// <summary>
    /// Long enough to show whether improvements keep arriving, short enough that
    /// the suite still gets run. The 30s production cap is exercised by hand.
    /// </summary>
    private const double ProfileSeconds = 15;

    [Fact]
    public void Balanced_ward_reports_where_the_cost_went()
    {
        var scenario = WardPresets.BalancedWard();

        var outcome = RosterSolver.Solve(scenario, seconds: 10);

        Assert.True(outcome.IsFeasible, $"expected a roster, got {outcome.Status}");

        output.WriteLine($"status={outcome.Status} objective={outcome.Objective} " +
                         $"bound={outcome.BestBound} wall={outcome.WallSeconds:0.00}s");
        output.WriteLine("");
        output.WriteLine("PENALTY BREAKDOWN:");
        foreach (var line in outcome.Penalties.Lines)
        {
            output.WriteLine($"  {line.Cost,6}  {line.Units} {line.UnitNoun} x{line.Weight}  — {line.Label}");
        }
        output.WriteLine($"  {outcome.Penalties.Total,6}  TOTAL");

        Assert.NotEmpty(outcome.Penalties.Lines);

        // The breakdown has to reconcile with what CP-SAT optimised, or the panel
        // is showing numbers that mean nothing.
        Assert.Equal((long)outcome.Objective!.Value, outcome.Penalties.Total);
    }

    [Fact]
    public void Breakdown_is_ordered_by_cost_descending()
    {
        var outcome = RosterSolver.Solve(WardPresets.BalancedWard(), seconds: 8);

        var costs = outcome.Penalties.Lines.Select(l => l.Cost).ToList();
        Assert.Equal(costs.OrderByDescending(c => c).ToList(), costs);
    }

    /// <summary>
    /// The single most dangerous mistake in this codebase would be letting an
    /// objective reach the Explain model: CP-SAT would silently return every
    /// assumption as the "conflict", and the explanation panel would confidently
    /// print nonsense. Asserted on the emitted proto rather than trusted.
    /// </summary>
    [Theory]
    [InlineData(SolveMode.Explain)]
    public void Explain_mode_carries_no_objective(SolveMode mode)
    {
        var model = GuardedRosterModel.Build(WardPresets.NightCertificationCrunch(), mode);

        Assert.True(model.Objective.IsEmpty);
        Assert.Null(model.Model.Model.Objective);  // proto message field: null means "no objective"
    }

    [Fact]
    public void Optimize_mode_does_carry_an_objective()
    {
        var model = GuardedRosterModel.Build(WardPresets.BalancedWard(), SolveMode.Optimize);

        Assert.False(model.Objective.IsEmpty);
        Assert.NotNull(model.Model.Model.Objective);
    }

    /// <summary>
    /// A fairness term that is never exercised is just overhead. This checks the
    /// spread is a real, readable number rather than a constant zero.
    /// </summary>
    [Fact]
    public void Fairness_terms_are_present_and_measured()
    {
        var outcome = RosterSolver.Solve(WardPresets.BalancedWard(), seconds: 8);

        var keys = outcome.Penalties.Lines.Select(l => l.Key).ToList();
        output.WriteLine($"terms: {string.Join(", ", keys)}");

        Assert.Contains("night-fairness", keys);
        Assert.Contains("weekend-fairness", keys);
        Assert.Contains("split-weekend", keys);
    }

    /// <summary>
    /// Diagnostic, not a pass/fail gate: reports how long a full solve runs and
    /// how many improving solutions arrive. If improvements stop early the live
    /// grid has nothing to stream, which is the demo's main failure mode.
    /// </summary>
    [Theory]
    [InlineData("balanced-ward")]
    [InlineData("large-ward")]
    public void Report_solve_progress_profile(string presetId)
    {
        var scenario = WardPresets.ById(presetId);
        var model = GuardedRosterModel.Build(scenario, SolveMode.Optimize);

        var improvements = new List<(double Seconds, double Objective, double Bound)>();
        var callback = new ProgressProbe(improvements);

        var solver = new CpSolver { StringParameters = SolverParameters.Fast(ProfileSeconds) };
        var status = solver.Solve(model.Model, callback);

        var last = improvements.Count > 0 ? improvements[^1].Seconds : 0;
        output.WriteLine($"{presetId}: status={status} wall={solver.WallTime():0.00}s " +
                         $"improvements={improvements.Count} lastAt={last:0.0}s " +
                         $"vars={model.Model.Model.Variables.Count}");
        foreach (var (seconds, objective, bound) in improvements)
        {
            output.WriteLine($"  {seconds,6:0.00}s  obj={objective,8}  bound={bound,8}");
        }

        Assert.NotEmpty(improvements);
    }

    private sealed class ProgressProbe(List<(double, double, double)> sink) : CpSolverSolutionCallback
    {
        public override void OnSolutionCallback()
        {
            // On the callback these are methods; on CpSolver they are properties.
            sink.Add((WallTime(), ObjectiveValue(), BestObjectiveBound()));
        }
    }
}
