using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using ShiftReason.Api.Contracts;
using ShiftReason.Api.Persistence;
using ShiftReason.Domain;
using ShiftReason.Solver;

namespace ShiftReason.Api.Solving;

/// <summary>
/// Runs queued solves, one at a time, off every request thread.
/// </summary>
/// <remarks>
/// <para>
/// Strictly sequential. CP-SAT already saturates every core it is given, so
/// running two solves concurrently on a small container makes both slower and
/// neither finishes sooner.
/// </para>
/// <para>
/// This is a singleton, and <c>DbContext</c> is not thread-safe, so every unit of
/// work resolves its own scoped context from <see cref="IServiceScopeFactory"/>.
/// </para>
/// </remarks>
public sealed class SolveWorker(
    SolveQueue queue,
    IHubContext<SolveHub> hub,
    IServiceScopeFactory scopes,
    ILogger<SolveWorker> log) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RunAsync(job, stoppingToken);
            }
            catch (Exception ex)
            {
                // One bad job must never take the worker down, or every later
                // solve silently queues forever.
                log.LogError(ex, "Solve run {RunId} failed", job.RunId);
                await SafeSend(job.RunId, "runFailed", new RunFailed(job.RunId, ex.Message));
                await MarkFailed(job.RunId, ex.Message);
            }
            finally
            {
                queue.Release(job.RunId);
            }
        }
    }

    private async Task RunAsync(SolveJob job, CancellationToken stoppingToken)
    {
        using var cts = queue.Register(job.RunId, stoppingToken);
        var cancellation = cts.Token;

        var scenario = WardPresets.ById(job.PresetId);
        var dates = scenario.Dates.ToList();
        var shifts = scenario.ShiftTypes.OrderBy(s => s.Index).ToList();

        await SafeSend(job.RunId, "runStarted", new RunStarted(
            job.RunId,
            scenario.Id,
            scenario.Name,
            scenario.Employees.Select(e => e.Id).ToArray(),
            scenario.Employees.Select(e => e.Name).ToArray(),
            dates.Select(d => d.ToString("yyyy-MM-dd")).ToArray(),
            shifts.Select(s => new ShiftDto(s.Index, s.Id, s.Name, s.IsNight)).ToArray(),
            job.RelaxedRuleIds.ToArray()));

        await Upsert(job.RunId, run =>
        {
            run.Status = "Running";
        }, () => NewRunRecord(job, scenario));

        // Capacity 4 + DropOldest: the solver must never wait on a SignalR client,
        // and a live grid only cares about the newest state, so dropping stale
        // intermediate frames is exactly the right loss.
        var frames = Channel.CreateBounded<RosterSnapshot>(new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        var solveTask = Task.Run(() =>
        {
            try
            {
                return RosterSolver.SolveStreaming(
                    scenario,
                    frames.Writer,
                    seconds: job.Seconds,
                    seed: job.Seed,
                    relaxedRuleIds: job.RelaxedRuleIds,
                    minInterval: TimeSpan.FromMilliseconds(200),
                    cancellationToken: cancellation);
            }
            finally
            {
                frames.Writer.TryComplete();
            }
        }, CancellationToken.None);

        // Drained with CancellationToken.None on purpose: when a run is cancelled
        // the frames already produced are still wanted, and the best-so-far roster
        // is the whole point of a Stop button.
        var pump = new DeltaPump(job.RunId, scenario.Employees.Count, dates.Count);
        var persistedFrames = new List<SolveFrame>();

        await foreach (var snapshot in frames.Reader.ReadAllAsync(CancellationToken.None))
        {
            var delta = pump.Next(snapshot);
            await SafeSend(job.RunId, "rosterImproved", delta);
            persistedFrames.Add(ToRecord(job.RunId, delta));
        }

        var result = await solveTask;

        // The last improvement often lands inside the throttle window, so publish
        // the final state explicitly — but only if it actually differs from what
        // the grid already shows, or every run ends on a duplicate no-op frame.
        if (result.Final is { } final && pump.NextIfChanged(final) is { } finalDelta)
        {
            await SafeSend(job.RunId, "rosterImproved", finalDelta);
            persistedFrames.Add(ToRecord(job.RunId, finalDelta));
        }

        var status = result.WasCancelled ? "Cancelled"
            : result.Outcome.IsInfeasible ? "Infeasible"
            : result.Outcome.IsFeasible ? "Completed"
            : "Unknown";

        await Persist(job.RunId, persistedFrames, run =>
        {
            run.Status = status;
            run.Objective = result.Outcome.Objective;
            run.BestBound = result.Outcome.BestBound;
            run.WallSeconds = result.Outcome.WallSeconds;
            run.SolutionCount = result.SolutionCount;
            run.ModelProtoGz = Gzip.Compress(result.ModelProto);
            run.CompletedUtc = DateTimeOffset.UtcNow;
            run.PenaltiesJson = JsonSerializer.Serialize(Map(result.Outcome.Penalties), Json);
            run.RosterJson = result.Outcome.Roster is null
                ? null
                : JsonSerializer.Serialize(result.Outcome.Roster.Assignments, Json);
        });

        await SafeSend(job.RunId, "runCompleted", new RunCompleted(
            job.RunId,
            status,
            result.Outcome.Objective,
            result.Outcome.BestBound,
            result.Outcome.WallSeconds,
            result.SolutionCount,
            result.WasCancelled,
            Map(result.Outcome.Penalties)));

        if (result.Outcome.IsInfeasible && job.ExplainIfInfeasible)
        {
            await ExplainAsync(job, scenario, cancellation);
        }
    }

    /// <summary>Runs the conflict explanation and publishes it.</summary>
    /// <remarks>
    /// Deliberately after the roster result rather than instead of it: the client
    /// gets told "impossible" immediately and the explanation — which costs several
    /// more solves — streams in behind it.
    /// </remarks>
    private async Task ExplainAsync(SolveJob job, Scenario scenario, CancellationToken cancellation)
    {
        var started = Stopwatch.StartNew();

        try
        {
            var explanation = await Task.Run(
                () => ConflictExplainer.Explain(
                    scenario, maxFixes: 3, relaxedRuleIds: job.RelaxedRuleIds, cancellationToken: cancellation),
                CancellationToken.None);

            var dto = new ExplanationDto(
                job.RunId,
                explanation.ConflictSet.Select(Map).ToArray(),
                explanation.Fixes
                    .Select(f => new FixDto(f.Rank, f.TotalCost, f.Rules.Select(Map).ToArray()))
                    .ToArray(),
                explanation.ProbeCount,
                explanation.WallSeconds,
                explanation.CoreWasMinimised,
                explanation.IsStructurallyInfeasible);

            await Persist(job.RunId, [], run => run.ExplanationJson = JsonSerializer.Serialize(dto, Json));
            await SafeSend(job.RunId, "explained", dto);
        }
        catch (OperationCanceledException)
        {
            log.LogInformation("Explanation for {RunId} was cancelled after {Elapsed:0.0}s",
                job.RunId, started.Elapsed.TotalSeconds);
        }
        catch (DegradedCoreException ex)
        {
            // Worth shouting about: it means the core is meaningless, not missing.
            log.LogError(ex, "Conflict core degraded for {RunId}", job.RunId);
            await SafeSend(job.RunId, "runFailed", new RunFailed(job.RunId, ex.Message));
        }
    }

    // ------------------------------------------------------------------
    // Persistence
    // ------------------------------------------------------------------

    private SolveRun NewRunRecord(SolveJob job, Scenario scenario) => new()
    {
        Id = job.RunId,
        ScenarioId = scenario.Id,
        ScenarioName = scenario.Name,
        ParentRunId = job.ParentRunId,
        RelaxedRuleIds = string.Join(',', job.RelaxedRuleIds),
        Mode = job.Seed is null ? "Fast" : "Reproducible",
        Seed = job.Seed,
        SolverVersion = typeof(Google.OrTools.Sat.CpSolver).Assembly.GetName().Version?.ToString() ?? "unknown",
        Parameters = job.Seed is { } s
            ? SolverParameters.Reproducible(s, job.Seconds)
            : SolverParameters.Fast(job.Seconds),
        InputHash = ScenarioHash.Of(scenario, job.RelaxedRuleIds, job.Seed),
        Status = "Queued",
    };

    private async Task Upsert(string runId, Action<SolveRun> update, Func<SolveRun> create)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RunStore>();

        var run = await db.Runs.FindAsync(runId);
        if (run is null)
        {
            run = create();
            db.Runs.Add(run);
        }

        update(run);
        await db.SaveChangesAsync();
    }

    private async Task Persist(string runId, IReadOnlyList<SolveFrame> frames, Action<SolveRun> update)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RunStore>();

        var run = await db.Runs.FindAsync(runId);
        if (run is null) return;

        update(run);
        if (frames.Count > 0) db.Frames.AddRange(frames);
        await db.SaveChangesAsync();
    }

    private async Task MarkFailed(string runId, string message)
    {
        try
        {
            await Persist(runId, [], run =>
            {
                run.Status = "Failed";
                run.Error = message;
                run.CompletedUtc = DateTimeOffset.UtcNow;
            });
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not record failure for run {RunId}", runId);
        }
    }

    private SolveFrame ToRecord(string runId, RosterDelta delta) => new()
    {
        RunId = runId,
        Seq = delta.Seq,
        Seconds = delta.Seconds,
        Objective = delta.Objective,
        BestBound = delta.BestBound,
        ChangesJson = JsonSerializer.Serialize(
            new { delta.IsFull, delta.Changes, delta.Penalties }, Json),
    };

    // ------------------------------------------------------------------
    // Mapping
    // ------------------------------------------------------------------

    private static PenaltyLineDto[] Map(PenaltyBreakdown breakdown) =>
        breakdown.Lines
            .Select(l => new PenaltyLineDto(l.Key, l.Label, l.UnitNoun, l.Units, l.Weight, l.Cost))
            .ToArray();

    private static RuleDto Map(RuleRef rule) => new(
        rule.RuleId,
        rule.Kind.ToString(),
        rule.RelaxCost,
        rule.Sentence,
        rule.Refs.Select(r => new EntityRefDto(r.Kind, r.Id)).ToArray());

    /// <summary>
    /// A dropped SignalR send must never fail a solve — the run and its result are
    /// still recorded, and a reconnecting client reads them back over REST.
    /// </summary>
    private async Task SafeSend<T>(string runId, string method, T payload)
    {
        try
        {
            await hub.Clients.Group(runId).SendAsync(method, payload, CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not push '{Method}' for run {RunId}", method, runId);
        }
    }
}
