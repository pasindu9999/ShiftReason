using System.Diagnostics;
using System.Threading.Channels;
using Google.OrTools.Sat;

namespace ShiftReason.Solver;

/// <summary>One improving solution, captured mid-solve.</summary>
/// <param name="Cells">Flat grid: <c>[employee * dayCount + day] =&gt; shift index</c>.</param>
public sealed record RosterSnapshot(
    int Index,
    double Seconds,
    double Objective,
    double BestBound,
    int[] Cells,
    PenaltyBreakdown Penalties);

/// <summary>
/// Publishes improving solutions as CP-SAT finds them.
/// </summary>
/// <remarks>
/// <para>
/// <c>SharedResponseManager::NewSolution</c> takes the solver's global mutex and
/// invokes this callback while still holding it, so <em>every microsecond spent
/// here blocks every solver worker</em>. That rules out awaiting, blocking on a
/// result, serialising JSON, writing to the database, or logging to a blocking
/// sink. All this does is fill a reused buffer and hand it to a channel.
/// </para>
/// <para>
/// The channel is bounded with <c>DropOldest</c>, so a slow consumer can never
/// stall the solver — it just misses intermediate frames, which is exactly the
/// right trade for a live grid where only the newest state matters. Throttling
/// happens here rather than downstream so the cost of reading the grid is not
/// paid for frames nobody will see.
/// </para>
/// <para>
/// The whole body is wrapped in a catch. SWIG declares this director with no
/// exception feature, so a managed exception escaping would unwind through C++ —
/// undefined behaviour, in practice a process abort. Instead the fault is stashed
/// and rethrown on the calling thread once <c>Solve</c> returns.
/// </para>
/// </remarks>
public sealed class StreamingCallback : CpSolverSolutionCallback
{
    private readonly GuardedRosterModel _model;
    private readonly ChannelWriter<RosterSnapshot> _writer;
    private readonly CancellationToken _cancellationToken;
    private readonly long _minIntervalTicks;
    private readonly int[] _scratch;

    private long _lastEmittedAt;
    private int _emitted;

    public StreamingCallback(
        GuardedRosterModel model,
        ChannelWriter<RosterSnapshot> writer,
        TimeSpan minInterval,
        CancellationToken cancellationToken)
    {
        _model = model;
        _writer = writer;
        _cancellationToken = cancellationToken;
        _minIntervalTicks = (long)(minInterval.TotalSeconds * Stopwatch.Frequency);
        _scratch = new int[model.EmployeeCount * model.DayCount];
    }

    /// <summary>Total improving solutions CP-SAT reported, including throttled ones.</summary>
    public int SolutionCount { get; private set; }

    /// <summary>Set if the callback threw. Rethrow this on the calling thread.</summary>
    public Exception? Fault { get; private set; }

    /// <summary>The most recent solution, whether or not it was published.</summary>
    public RosterSnapshot? Latest { get; private set; }

    public override void OnSolutionCallback()
    {
        try
        {
            SolutionCount++;

            // Cancellation is polled here as well as registered on the solver:
            // StopSearch() is a no-op until Solve() has built its internal wrapper,
            // so an early cancel would otherwise be silently ignored.
            if (_cancellationToken.IsCancellationRequested)
            {
                StopSearch();
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var isFirst = _emitted == 0;
            var due = now - _lastEmittedAt >= _minIntervalTicks;

            // Always capture the latest so the caller can publish a final frame
            // even when the last improvement arrived inside the throttle window.
            var snapshot = Capture();
            Latest = snapshot;

            if (!isFirst && !due) return;

            _lastEmittedAt = now;
            _emitted++;

            // TryWrite on a DropOldest channel never blocks and never fails for
            // capacity, so the solver is never waiting on a SignalR client.
            _writer.TryWrite(snapshot);
        }
        catch (Exception ex)
        {
            Fault ??= ex;
            try { StopSearch(); } catch { /* already tearing down */ }
        }
    }

    private RosterSnapshot Capture()
    {
        _model.ExtractCells(BooleanValue, _scratch);

        // On the callback these are methods; on CpSolver they are properties.
        return new RosterSnapshot(
            Index: _emitted,
            Seconds: WallTime(),
            Objective: ObjectiveValue(),
            BestBound: BestObjectiveBound(),
            Cells: _scratch.ToArray(),
            Penalties: _model.Objective.Read(Value));
    }
}
