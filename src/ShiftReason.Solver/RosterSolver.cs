using Google.OrTools.Sat;
using ShiftReason.Domain;

namespace ShiftReason.Solver;

public sealed record SolveOutcome(
    CpSolverStatus Status,
    Roster? Roster,
    double? Objective,
    double? BestBound,
    double WallSeconds,
    PenaltyBreakdown Penalties)
{
    public bool IsFeasible => Status is CpSolverStatus.Optimal or CpSolverStatus.Feasible;
    public bool IsInfeasible => Status is CpSolverStatus.Infeasible;
}

public static class RosterSolver
{
    /// <summary>Produces a roster, or reports that no roster exists.</summary>
    /// <param name="seed">
    /// Supply a seed to get a reproducible solve. Reproducibility costs real
    /// throughput — see <see cref="SolverParameters.Reproducible"/> — so it is an
    /// explicit choice rather than the default.
    /// </param>
    public static SolveOutcome Solve(
        Scenario scenario,
        double seconds = 30,
        int? seed = null,
        IReadOnlySet<string>? relaxedRuleIds = null,
        CancellationToken cancellationToken = default)
    {
        var model = GuardedRosterModel.Build(scenario, SolveMode.Optimize, relaxedRuleIds);

        var solver = new CpSolver
        {
            StringParameters = seed is { } s
                ? SolverParameters.Reproducible(s, deterministicTime: seconds)
                : SolverParameters.Fast(seconds),
        };

        var status = SolveCancellable(model.Model, solver, callback: null, cancellationToken);

        var roster = status is CpSolverStatus.Optimal or CpSolverStatus.Feasible
            ? model.ExtractRoster(solver.BooleanValue)
            : null;

        // A feasible roster still owes the user an explanation of what it cost,
        // so the objective is reported term by term rather than as one number.
        var penalties = roster is null
            ? PenaltyBreakdown.Empty
            : model.Objective.Read(solver.Value);

        return new SolveOutcome(
            status,
            roster,
            roster is null ? null : solver.ObjectiveValue,
            roster is null ? null : solver.BestObjectiveBound,
            solver.WallTime(),
            penalties);
    }

    /// <summary>
    /// Runs a solve that a <see cref="CancellationToken"/> can actually stop.
    /// </summary>
    /// <remarks>
    /// <c>CpSolver.StopSearch()</c> forwards to an internal <c>SolveWrapper</c>
    /// that does not exist until <c>Solve()</c> creates it, and is torn down again
    /// when <c>Solve()</c> returns. A cancellation firing in either window is a
    /// silent no-op and the solve runs to completion regardless — so the token is
    /// checked before registering and again afterwards. The wall-clock limit, not
    /// cancellation, is the actual safety net.
    /// </remarks>
    internal static CpSolverStatus SolveCancellable(
        CpModel model,
        CpSolver solver,
        SolutionCallback? callback,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var registration = cancellationToken.Register(
            static state => ((CpSolver)state!).StopSearch(), solver);

        var status = callback is null
            ? solver.Solve(model)
            : solver.Solve(model, callback);

        // StopSearch() may have landed after the wrapper was released, in which
        // case the status above is a complete solve of a cancelled request.
        cancellationToken.ThrowIfCancellationRequested();

        return status;
    }
}
