using ShiftReason.Domain;
using ShiftReason.Solver;
using Xunit.Abstractions;

namespace ShiftReason.Solver.Tests;

public class ExplanationTests(ITestOutputHelper output)
{
    [Fact]
    public void Night_crunch_conflict_set_is_small_and_readable()
    {
        var scenario = WardPresets.NightCertificationCrunch();

        var explanation = ConflictExplainer.Explain(scenario);

        output.WriteLine($"probes={explanation.ProbeCount} " +
                         $"minimised={explanation.CoreWasMinimised} " +
                         $"{explanation.WallSeconds:0.00}s");
        output.WriteLine("");
        output.WriteLine("WHY IT IS IMPOSSIBLE:");
        foreach (var rule in explanation.ConflictSet) output.WriteLine($"  - {rule.Sentence}");
        output.WriteLine("");
        output.WriteLine("CHEAPEST FIXES:");
        foreach (var fix in explanation.Fixes)
        {
            output.WriteLine($"  #{fix.Rank}  cost {fix.TotalCost}");
            foreach (var rule in fix.Rules) output.WriteLine($"        drop: {rule.Sentence}");
        }

        Assert.NotEmpty(explanation.ConflictSet);

        // A conflict set the size of the rule book explains nothing. This is the
        // assertion that would catch CP-SAT silently returning every assumption.
        var totalRules = GuardedRosterModel
            .Build(scenario, SolveMode.Explain).Guards.Count;
        Assert.True(explanation.ConflictSet.Count < totalRules,
            $"core was not a strict subset: {explanation.ConflictSet.Count} of {totalRules}");
        Assert.True(explanation.ConflictSet.Count <= 8,
            $"conflict set of {explanation.ConflictSet.Count} rules is too big to read");
    }

    /// <summary>
    /// The minimality claim, checked rather than asserted in prose.
    /// </summary>
    /// <remarks>
    /// Minimality is a property of the conflict set <em>in isolation</em>: the set
    /// is unsatisfiable, and every proper subset of it is satisfiable. It is NOT
    /// the claim that dropping one of its rules from the whole scenario makes the
    /// ward solvable — a scenario can hold several independent conflicts, and
    /// removing one member of this one just leaves the others standing. That is
    /// exactly why a conflict set alone is not an answer, and why
    /// <see cref="RelaxationAdvisor"/> exists: the correction set is the half that
    /// guarantees "do this and it will solve".
    /// </remarks>
    [Fact]
    public void Night_crunch_conflict_set_is_genuinely_minimal()
    {
        var scenario = WardPresets.NightCertificationCrunch();
        var explanation = ConflictExplainer.Explain(scenario);

        Assert.True(explanation.CoreWasMinimised, "shrink pass ran out of time; result is not minimal");
        Assert.NotEmpty(explanation.ConflictSet);

        var allRuleIds = GuardedRosterModel.Build(scenario, SolveMode.Explain)
                                           .Guards.Select(g => g.Rule.RuleId)
                                           .ToHashSet(StringComparer.Ordinal);

        // The whole set, alone, must be unsatisfiable.
        Assert.True(SolveWithOnly(scenario, allRuleIds, explanation.ConflictSet.Select(r => r.RuleId)).IsInfeasible,
            "the reported conflict set is satisfiable, so it is not a conflict at all");

        // ...and every proper subset of it must be satisfiable.
        foreach (var dropped in explanation.ConflictSet)
        {
            var subset = explanation.ConflictSet
                                    .Where(r => r.RuleId != dropped.RuleId)
                                    .Select(r => r.RuleId);

            var outcome = SolveWithOnly(scenario, allRuleIds, subset);
            output.WriteLine($"conflict set minus '{dropped.RuleId}' -> {outcome.Status}");

            Assert.True(outcome.IsFeasible,
                $"conflict set is not minimal: it stays {outcome.Status} without " +
                $"'{dropped.RuleId}', so that rule was never required for the contradiction");
        }
    }

    /// <summary>
    /// Every correction set must intersect every conflict set — that is the
    /// hitting-set duality the explanation rests on. A suggested fix that touches
    /// nothing in the conflict set would mean one of the two is wrong.
    /// </summary>
    [Fact]
    public void Each_fix_hits_the_conflict_set()
    {
        var explanation = ConflictExplainer.Explain(WardPresets.NightCertificationCrunch());
        var conflict = explanation.ConflictSet.Select(r => r.RuleId).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(explanation.Fixes);
        foreach (var fix in explanation.Fixes)
        {
            Assert.True(fix.Rules.Any(r => conflict.Contains(r.RuleId)),
                $"fix #{fix.Rank} relaxes nothing in the conflict set, which breaks MUS/MCS duality");
        }
    }

    /// <summary>
    /// A "cheapest fix" that does not actually fix anything is worse than no
    /// answer at all, so every suggestion is applied and re-solved.
    /// </summary>
    [Fact]
    public void Every_suggested_fix_actually_restores_feasibility()
    {
        var scenario = WardPresets.NightCertificationCrunch();

        var fixes = RelaxationAdvisor.Suggest(scenario, maxOptions: 3);

        Assert.NotEmpty(fixes);

        foreach (var fix in fixes)
        {
            var relaxed = fix.Rules.Select(r => r.RuleId).ToHashSet(StringComparer.Ordinal);
            var outcome = SolveWithRelaxations(scenario, relaxed);

            output.WriteLine($"fix #{fix.Rank} (cost {fix.TotalCost}, {fix.Rules.Count} rules) -> {outcome.Status}");

            Assert.True(outcome.IsFeasible,
                $"fix #{fix.Rank} was supposed to restore feasibility but gave {outcome.Status}");

            // And the roster it produces must still honour every rule that was NOT relaxed.
            var violations = RosterValidator.Validate(scenario, outcome.Roster!, relaxed);
            Assert.True(violations.Count == 0,
                "relaxed solve broke rules that were never relaxed:" + Environment.NewLine +
                string.Join(Environment.NewLine, violations.Select(v => "  " + v)));
        }
    }

    [Fact]
    public void Fixes_are_ranked_by_ascending_cost()
    {
        var fixes = RelaxationAdvisor.Suggest(WardPresets.NightCertificationCrunch(), maxOptions: 3);

        var costs = fixes.Select(f => f.TotalCost).ToList();
        output.WriteLine($"costs: {string.Join(", ", costs)}");

        Assert.Equal(costs.OrderBy(c => c).ToList(), costs);
    }

    [Fact]
    public void Explaining_a_solvable_scenario_is_rejected()
    {
        // Asking "why is this impossible?" about a solvable ward is a caller bug,
        // and silently returning an empty core would hide it.
        Assert.Throws<InvalidOperationException>(
            () => ConflictExplainer.Explain(WardPresets.BalancedWard()));
    }

    /// <summary>
    /// Re-solves with the given rules given up — the same code path the "relax
    /// this and re-solve" button uses, rather than a test-only reconstruction of
    /// the scenario.
    /// </summary>
    private static SolveOutcome SolveWithRelaxations(Scenario scenario, IReadOnlySet<string> relaxedRuleIds) =>
        RosterSolver.Solve(scenario, seconds: 20, relaxedRuleIds: relaxedRuleIds);

    /// <summary>
    /// Solves with only <paramref name="keep"/> enforced, by relaxing everything
    /// else. This is the semantics a conflict set is defined against.
    /// </summary>
    private static SolveOutcome SolveWithOnly(
        Scenario scenario, IReadOnlySet<string> allRuleIds, IEnumerable<string> keep)
    {
        var keepSet = keep.ToHashSet(StringComparer.Ordinal);
        var relaxed = allRuleIds.Where(id => !keepSet.Contains(id)).ToHashSet(StringComparer.Ordinal);
        return RosterSolver.Solve(scenario, seconds: 20, relaxedRuleIds: relaxed);
    }
}
