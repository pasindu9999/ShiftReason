using Google.OrTools.Sat;
using ShiftReason.Domain;

namespace ShiftReason.Solver;

/// <summary>
/// Answers "what is the cheapest thing to give up?".
/// </summary>
/// <remarks>
/// <para>
/// Where <see cref="ConflictExplainer"/> finds a minimal unsatisfiable subset —
/// rules that cannot all hold — this finds a minimum-cost <em>correction</em>
/// set: the cheapest rules to drop so that a roster exists. The two are hitting-set
/// duals; every correction set intersects every conflict set. Showing both is what
/// turns "no feasible solution" into a decision a ward manager can actually take.
/// </para>
/// <para>
/// This is a plain optimisation problem — no assumptions — so unlike the
/// explanation solve it runs multi-worker at full speed.
/// </para>
/// </remarks>
public static class RelaxationAdvisor
{
    private const double SolveSeconds = 12;

    public static IReadOnlyList<RelaxationOption> Suggest(
        Scenario scenario,
        int maxOptions = 3,
        IReadOnlySet<string>? relaxedRuleIds = null,
        CancellationToken cancellationToken = default)
    {
        var model = GuardedRosterModel.Build(scenario, SolveMode.Relax, relaxedRuleIds);
        var options = new List<RelaxationOption>();

        for (var rank = 1; rank <= maxOptions; rank++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var solver = new CpSolver { StringParameters = SolverParameters.Fast(SolveSeconds) };
            var status = RosterSolver.SolveCancellable(model.Model, solver, callback: null, cancellationToken);

            if (status is not (CpSolverStatus.Optimal or CpSolverStatus.Feasible)) break;

            var relaxed = model.NewlyRelaxedRules(solver.BooleanValue);
            if (relaxed.Count == 0) break; // already satisfiable without giving anything up

            options.Add(new RelaxationOption(rank, relaxed, relaxed.Sum(r => r.RelaxCost)));

            if (rank == maxOptions) break;

            // No-good cut: forbid dropping this exact combination again, so the
            // next solve has to find a genuinely different way out rather than
            // returning the same answer.
            ExcludeCombination(model, relaxed);
        }

        return options;
    }

    /// <summary>
    /// Requires at least one rule from <paramref name="combination"/> to be kept,
    /// which rules that combination out of subsequent solves.
    /// </summary>
    private static void ExcludeCombination(GuardedRosterModel model, IReadOnlyList<RuleRef> combination)
    {
        var ids = combination.Select(r => r.RuleId).ToHashSet(StringComparer.Ordinal);

        var keptSomething = LinearExpr.NewBuilder();
        foreach (var (lit, rule) in model.Guards)
        {
            if (ids.Contains(rule.RuleId)) keptSomething.AddTerm(lit, 1);
        }

        model.Model.Add(keptSomething >= 1);
    }
}
