using Microsoft.AspNetCore.SignalR;

namespace ShiftReason.Api.Solving;

/// <summary>
/// Live solve updates, one group per run.
/// </summary>
/// <remarks>
/// No backplane is configured, and that is a deliberate, documented choice rather
/// than an oversight: the app runs capped at a single replica, and SignalR only
/// needs Redis or Azure SignalR Service once messages have to cross replicas. If
/// this were ever scaled out, this hub is the thing that breaks first.
/// </remarks>
public sealed class SolveHub : Hub
{
    /// <summary>Subscribe to a run. Safe to call before the run has started.</summary>
    public Task Watch(string runId) => Groups.AddToGroupAsync(Context.ConnectionId, runId);

    public Task Unwatch(string runId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, runId);
}
