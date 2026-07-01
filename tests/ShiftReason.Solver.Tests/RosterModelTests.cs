using Google.OrTools.Sat;
using ShiftReason.Domain;
using ShiftReason.Solver;

namespace ShiftReason.Solver.Tests;

public class RosterModelTests
{
    /// <summary>
    /// The headline guarantee: a roster the solver calls feasible really does
    /// satisfy every hard rule, checked by code that has never heard of CP-SAT.
    /// </summary>
    [Fact]
    public void BalancedWard_produces_a_roster_with_no_hard_violations()
    {
        var scenario = WardPresets.BalancedWard();

        var outcome = RosterSolver.Solve(scenario, seconds: 10);

        Assert.True(outcome.IsFeasible, $"expected a feasible roster, got {outcome.Status}");
        Assert.NotNull(outcome.Roster);

        var violations = RosterValidator.Validate(scenario, outcome.Roster!);
        Assert.True(violations.Count == 0,
            "solver returned a roster that breaks hard rules:" + Environment.NewLine +
            string.Join(Environment.NewLine, violations.Select(v => "  " + v)));
    }

    [Fact]
    public void BalancedWard_assigns_exactly_one_shift_per_nurse_per_day()
    {
        var scenario = WardPresets.BalancedWard();

        var outcome = RosterSolver.Solve(scenario, seconds: 10);
        Assert.NotNull(outcome.Roster);

        var expected = scenario.Employees.Count * scenario.HorizonDays;
        Assert.Equal(expected, outcome.Roster!.Assignments.Count);

        var duplicated = outcome.Roster.Assignments
            .GroupBy(a => (a.EmployeeId, a.Date))
            .Where(g => g.Count() != 1)
            .ToList();
        Assert.Empty(duplicated);
    }

    /// <summary>
    /// The impossible presets must actually be impossible. If a scenario meant to
    /// demonstrate infeasibility quietly becomes solvable, the explanation half of
    /// the product has nothing to explain and the demo dies on stage.
    /// </summary>
    [Theory]
    [InlineData("night-crunch")]
    [InlineData("flu-season")]
    public void Impossible_presets_are_infeasible(string presetId)
    {
        var scenario = WardPresets.ById(presetId);

        var outcome = RosterSolver.Solve(scenario, seconds: 10);

        Assert.True(outcome.IsInfeasible,
            $"preset '{presetId}' was supposed to be infeasible but came back {outcome.Status}");
    }

    [Fact]
    public void Structural_constraints_are_never_guarded()
    {
        var model = GuardedRosterModel.Build(WardPresets.NightCertificationCrunch(), SolveMode.Explain);

        // Every guard must map to a Policy rule; Structural rules never get one.
        Assert.All(model.Guards, g => Assert.Equal(RuleClass.Policy, g.Rule.Class));
        Assert.NotEmpty(model.Guards);
    }

    /// <summary>
    /// ModelLint is the only thing standing between us and a process abort, so it
    /// is tested against a model that would actually trip the CHECK.
    /// </summary>
    [Fact]
    public void ModelLint_rejects_a_guarded_exactly_one()
    {
        var model = new CpModel();
        var lits = Enumerable.Range(0, 4).Select(i => model.NewBoolVar($"x{i}")).ToArray();
        model.AddExactlyOne(lits).OnlyEnforceIf(model.NewBoolVar("rule::guarded"));

        var ex = Assert.Throws<ModelLintException>(() => ModelLint.AssertNoGuardedSetConstraints(model));
        Assert.Contains("linear", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModelLint_accepts_the_linear_rewrite()
    {
        var model = new CpModel();
        var lits = Enumerable.Range(0, 4).Select(i => model.NewBoolVar($"x{i}")).ToArray();

        var sum = LinearExpr.NewBuilder();
        foreach (var l in lits) sum.AddTerm(l, 1);
        model.Add(sum == 1).OnlyEnforceIf(model.NewBoolVar("rule::guarded"));

        ModelLint.AssertNoGuardedSetConstraints(model); // must not throw
    }

    /// <summary>
    /// Rule ids reach the API payload and the test assertions below, so they are a
    /// contract. A rule with no sentence would surface in the UI as a blank line.
    /// </summary>
    [Fact]
    public void Every_guarded_rule_has_a_unique_id_and_a_sentence()
    {
        var model = GuardedRosterModel.Build(WardPresets.NightCertificationCrunch(), SolveMode.Explain);

        var ids = model.Guards.Select(g => g.Rule.RuleId).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(model.Guards, g => Assert.False(string.IsNullOrWhiteSpace(g.Rule.Sentence)));

        // The literal-index map is how a conflict core becomes English.
        Assert.Equal(model.Guards.Count, model.ByLiteralIndex.Count);
    }

    [Fact]
    public void Validator_catches_a_roster_that_breaks_approved_leave()
    {
        var scenario = WardPresets.NightCertificationCrunch();
        var onLeave = scenario.Leave.First(l => l.Kind is LeaveKind.Approved);

        // Hand-build a roster that rosters someone straight through their leave.
        var assignments = scenario.Employees
            .SelectMany(e => scenario.Dates.Select(d => new Assignment(
                e.Id, d,
                e.Id == onLeave.EmployeeId && d == onLeave.Date ? "EARLY" : ShiftType.Off.Id)))
            .ToList();

        var violations = RosterValidator.Validate(scenario, new Roster(assignments));

        Assert.Contains(violations, v => v.Kind is RuleKind.ApprovedLeave);
    }
}
