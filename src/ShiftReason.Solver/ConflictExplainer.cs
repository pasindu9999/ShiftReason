using Google.OrTools.Sat;
using ShiftReason.Domain;

namespace ShiftReason.Solver;

/// <summary>Why a scenario is impossible, and the cheapest ways out of it.</summary>
public sealed record Explanation(
    IReadOnlyList<RuleRef> ConflictSet,
    IReadOnlyList<RelaxationOption> Fixes,
    int ProbeCount,
    double WallSeconds,
    bool CoreWasMinimised)
{
    /// <summary>True when no combination of policy rules can rescue the scenario.</summary>
    public bool IsStructurallyInfeasible => ConflictSet.Count == 0;
}

/// <summary>One way to make the scenario solvable, priced.</summary>
public sealed record RelaxationOption(int Rank, IReadOnlyList<RuleRef> Rules, long TotalCost);

/// <summary>Raised when CP-SAT quietly stopped producing a usable conflict core.</summary>
public sealed class DegradedCoreException(string message) : Exception(message);

/// <summary>
/// Turns an infeasible roster into a minimal set of conflicting rules.
/// </summary>
/// <remarks>
/// <para>
/// CP-SAT returns a core that is "small" but explicitly <em>not guaranteed
/// minimal</em>, so a second pass shrinks it. That pass is plain linear deletion
/// over the returned core rather than QuickXplain, and the choice is deliberate:
/// QuickXplain is O(|MUS|·log(|C|/|MUS|)) solves over the <em>full</em> rule set —
/// roughly 13 large subproblems for 40 rules — whereas deletion restricted to a
/// core CP-SAT already narrowed for free is 4–8 solves on small ones. Every probe
/// pays for weakened presolve and a single worker, so solve count is what matters,
/// and deletion-over-the-core wins at this scale.
/// </para>
/// <para>
/// Every probe reuses one <see cref="CpModel"/>. A rule is switched off by
/// assuming its negation, which lets presolve strip the constraint outright
/// instead of rebuilding the model.
/// </para>
/// </remarks>
public static class ConflictExplainer
{
    private const double CoreSolveSeconds = 15;
    private const double ProbeSeconds = 6;
    private const int FixpointRounds = 3;
    private const int DefaultFixCount = 3;

    public static Explanation Explain(
        Scenario scenario,
        int maxFixes = DefaultFixCount,
        IReadOnlySet<string>? relaxedRuleIds = null,
        CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var model = GuardedRosterModel.Build(scenario, SolveMode.Explain, relaxedRuleIds);
        var probes = 0;

        // --- Stage 1: the sufficient core --------------------------------
        var (status, core) = SolveForCore(model, CoreSolveSeconds, cancellationToken);
        probes++;

        if (status is not CpSolverStatus.Infeasible)
        {
            throw new InvalidOperationException(
                $"Explain was asked about a scenario that is not infeasible (status {status}). " +
                "Run the optimising solve first and only explain when it reports INFEASIBLE.");
        }

        // An empty core means no combination of policy rules can save this: the
        // structural constraints alone are contradictory. Worth surfacing plainly
        // rather than reporting "nothing conflicts".
        if (core.Count == 0)
        {
            return new Explanation([], [], probes, started.Elapsed.TotalSeconds, CoreWasMinimised: false);
        }

        // --- Stage 2: fixpoint pre-shrink --------------------------------
        // Re-asking with assumptions already narrowed to the previous core often
        // returns a strictly smaller one, for no engineering cost at all.
        for (var round = 0; round < FixpointRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var narrowed = Probe(model, core, ProbeSeconds, cancellationToken);
            probes++;

            if (narrowed is null || narrowed.Count >= core.Count) break;
            core = narrowed;
        }

        // --- Stage 3: linear deletion to a true MUS ----------------------
        var mus = core.ToList();
        var minimised = true;

        for (var i = mus.Count - 1; i >= 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var trial = mus.Where((_, j) => j != i).ToList();
            if (trial.Count == 0) break;

            var stillBroken = Probe(model, trial, ProbeSeconds, cancellationToken);
            probes++;

            if (stillBroken is not null)
            {
                // Rule i was never needed for the contradiction.
                mus = trial;
            }
            else if (LastProbeWasInconclusive)
            {
                // UNKNOWN is not SAT. Keep the rule and record that the result is
                // "a conflict set" rather than "a minimal conflict set".
                minimised = false;
            }
        }

        var fixes = RelaxationAdvisor.Suggest(scenario, maxFixes, relaxedRuleIds, cancellationToken);

        return new Explanation(mus, fixes, probes, started.Elapsed.TotalSeconds, minimised);
    }

    [ThreadStatic] private static bool LastProbeWasInconclusive;

    /// <summary>Guards that are still in play — rules already given up are pinned off.</summary>
    private static IEnumerable<(BoolVar Lit, RuleRef Rule)> LiveGuards(GuardedRosterModel model) =>
        model.Guards.Where(g => !model.RelaxedRuleIds.Contains(g.Rule.RuleId));

    /// <summary>Solves the guarded model with every rule assumed on, and reads the core.</summary>
    private static (CpSolverStatus Status, List<RuleRef> Core) SolveForCore(
        GuardedRosterModel model, double seconds, CancellationToken cancellationToken)
    {
        model.Model.ClearAssumptions();
        model.Model.AddAssumptions(LiveGuards(model).Select(g => (ILiteral)g.Lit));

        var solver = NewExplainSolver(seconds, out var degraded);
        var status = RosterSolver.SolveCancellable(model.Model, solver, callback: null, cancellationToken);

        ThrowIfDegraded(degraded);

        return status is CpSolverStatus.Infeasible
            ? (status, MapCore(model, solver))
            : (status, []);
    }

    /// <summary>
    /// Re-solves with only <paramref name="keep"/> active. Returns the new core if
    /// the scenario is still broken, or <see langword="null"/> if it became
    /// solvable (or the probe timed out).
    /// </summary>
    private static List<RuleRef>? Probe(
        GuardedRosterModel model,
        IReadOnlyCollection<RuleRef> keep,
        double seconds,
        CancellationToken cancellationToken)
    {
        var keepIds = keep.Select(r => r.RuleId).ToHashSet(StringComparer.Ordinal);

        model.Model.ClearAssumptions();
        foreach (var (lit, rule) in LiveGuards(model))
        {
            // Assuming the negation switches a rule off and lets presolve delete
            // its constraint, which is far cheaper than rebuilding the model.
            model.Model.AddAssumption(keepIds.Contains(rule.RuleId) ? lit : lit.Not());
        }

        var solver = NewExplainSolver(seconds, out var degraded);
        var status = RosterSolver.SolveCancellable(model.Model, solver, callback: null, cancellationToken);

        ThrowIfDegraded(degraded);

        LastProbeWasInconclusive = status is CpSolverStatus.Unknown;

        return status is CpSolverStatus.Infeasible ? MapCore(model, solver) : null;
    }

    /// <summary>
    /// Maps signed proto literal refs back to rules.
    /// </summary>
    /// <remarks>
    /// CP-SAT undoes its own presolve mapping before returning these, so they are
    /// indices into the original model. Only rules assumed <em>true</em> can
    /// participate in a contradiction, so a negative ref here would mean the
    /// mapping logic is wrong — hence the assertion rather than a silent skip.
    /// </remarks>
    private static List<RuleRef> MapCore(GuardedRosterModel model, CpSolver solver)
    {
        var core = new List<RuleRef>();

        foreach (var reference in solver.SufficientAssumptionsForInfeasibility())
        {
            if (model.ByLiteralIndex.TryGetValue(reference, out var rule))
            {
                core.Add(rule);
                continue;
            }

            throw new InvalidOperationException(
                $"Conflict core contained literal {reference}, which maps to no rule. " +
                "A negative reference here would mean a rule assumed false was blamed, " +
                "which cannot happen — check the guard/literal-index mapping.");
        }

        return core;
    }

    private static CpSolver NewExplainSolver(double seconds, out StrongBox<bool> degraded)
    {
        var flag = new StrongBox<bool>(false);
        degraded = flag;

        var solver = new CpSolver { StringParameters = SolverParameters.Explain(seconds) };
        solver.SetLogCallback(line =>
        {
            if (line.Contains("non-fully supported setting", StringComparison.OrdinalIgnoreCase))
            {
                flag.Value = true;
            }
        });

        return solver;
    }

    private static void ThrowIfDegraded(StrongBox<bool> degraded)
    {
        if (!degraded.Value) return;

        throw new DegradedCoreException(
            "CP-SAT reported that assumptions ran in a non-fully-supported setting, which " +
            "means the returned core is simply every assumption rather than a conflict. " +
            "Check that the Explain model has no objective and that parameters are " +
            "num_workers:1 with interleave_search:false.");
    }
}

/// <summary>Minimal mutable cell — the log callback runs on the solver's thread.</summary>
internal sealed class StrongBox<T>(T value)
{
    public T Value = value;
}
