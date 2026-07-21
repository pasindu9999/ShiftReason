using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ShiftReason.Api.Solving;

/// <summary>A queued request to solve one scenario.</summary>
public sealed record SolveJob(
    string RunId,
    string PresetId,
    double Seconds,
    int? Seed,
    IReadOnlySet<string> RelaxedRuleIds,
    string? ParentRunId,
    bool ExplainIfInfeasible);

/// <summary>
/// Hands solve work off the request thread.
/// </summary>
/// <remarks>
/// <para>
/// A CP-SAT solve is seconds of saturated CPU. Running one inside a request would
/// hold a Kestrel thread for the duration and let a handful of clicks exhaust the
/// thread pool, so a request only ever enqueues and returns a run id — the result
/// arrives over SignalR.
/// </para>
/// <para>
/// The queue is bounded and rejects when full rather than growing without limit:
/// on a small container an unbounded queue just converts a traffic spike into a
/// long wait and an eventual out-of-memory, instead of an honest "busy" response.
/// </para>
/// </remarks>
public sealed class SolveQueue
{
    private readonly Channel<SolveJob> _channel = Channel.CreateBounded<SolveJob>(
        new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new();

    public bool TryEnqueue(SolveJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<SolveJob> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Registers a run as cancellable, linked to application shutdown.</summary>
    public CancellationTokenSource Register(string runId, CancellationToken linkedTo)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(linkedTo);
        _cancellations[runId] = cts;
        return cts;
    }

    public void Release(string runId)
    {
        if (_cancellations.TryRemove(runId, out var cts)) cts.Dispose();
    }

    /// <summary>
    /// Stops a running solve. Returns false if the run already finished or was
    /// never registered — cancelling something that has already stopped is not an
    /// error worth reporting to the user.
    /// </summary>
    public bool Cancel(string runId)
    {
        if (!_cancellations.TryGetValue(runId, out var cts)) return false;

        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}
