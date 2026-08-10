namespace ShiftReason.Api.Contracts;

/// <summary>One changed cell: employee row, day column, shift index.</summary>
public readonly record struct CellChange(int E, int D, int S);

public sealed record ShiftDto(int Index, string Id, string Name, bool IsNight);

/// <summary>
/// Sent once when a run starts, so the client can lay the grid out before any
/// solutions arrive.
/// </summary>
public sealed record RunStarted(
    string RunId,
    string ScenarioId,
    string ScenarioName,
    string[] EmployeeIds,
    string[] EmployeeNames,
    string[] Dates,
    ShiftDto[] Shifts,
    string[] RelaxedRuleIds);

public sealed record PenaltyLineDto(string Key, string Label, string UnitNoun, long Units, int Weight, long Cost);

/// <summary>
/// An improving solution, as a diff against the previous one.
/// </summary>
/// <remarks>
/// Deltas rather than whole grids: a 72x28 grid serialises to well over SignalR's
/// default 64 KB transport buffer, and sending the full roster five times a second
/// would spend the whole demo on backpressure. The first frame of a run is sent
/// with <see cref="IsFull"/> set so the client has a baseline to diff against.
/// </remarks>
public sealed record RosterDelta(
    string RunId,
    int Seq,
    double Seconds,
    double Objective,
    double BestBound,
    bool IsFull,
    CellChange[] Changes,
    PenaltyLineDto[] Penalties);

public sealed record RunCompleted(
    string RunId,
    string Status,
    double? Objective,
    double? BestBound,
    double WallSeconds,
    int SolutionCount,
    bool Cancelled,
    PenaltyLineDto[] Penalties);

public sealed record EntityRefDto(string Kind, string Id);

public sealed record RuleDto(
    string RuleId,
    string Kind,
    long RelaxCost,
    string Sentence,
    EntityRefDto[] Refs);

public sealed record FixDto(int Rank, long TotalCost, RuleDto[] Rules);

/// <summary>Why the ward is impossible, and the cheapest ways out.</summary>
public sealed record ExplanationDto(
    string RunId,
    RuleDto[] ConflictSet,
    FixDto[] Fixes,
    int Probes,
    double Seconds,
    bool Minimised,
    bool StructurallyInfeasible,
    bool TimedOut);

public sealed record RunFailed(string RunId, string Message);

// ---------------------------------------------------------------------------
// REST payloads
// ---------------------------------------------------------------------------

public sealed record SolveRequest(
    string PresetId,
    double? Seconds = null,
    int? Seed = null,
    string[]? RelaxRuleIds = null,
    string? ParentRunId = null);

public sealed record SolveAccepted(string RunId, string PresetId);

/// <summary>
/// The grid's shape, independent of any run.
/// </summary>
/// <remarks>
/// A client cannot join a run's SignalR group until the enqueue call has returned
/// its id, so it can legitimately miss the <see cref="RunStarted"/> broadcast. This
/// lets the grid be laid out before a solve is even requested, which makes that
/// race harmless instead of fatal.
/// </remarks>
public sealed record ScenarioLayout(
    string ScenarioId,
    string ScenarioName,
    string[] EmployeeIds,
    string[] EmployeeNames,
    string[] Dates,
    ShiftDto[] Shifts);

public sealed record PresetSummary(
    string Id,
    string Name,
    int Employees,
    int Days,
    int Shifts,
    int Variables);

/// <summary>
/// A whole run, replayable without a backend.
/// </summary>
/// <remarks>
/// Deliberately the exact sequence of hub messages a live run emits — start,
/// frames, completion, explanation — so the published demo drives the same client
/// reducer as a live solve rather than being a separate, fakeable rendering path.
/// A recruiter opening the link sees real solver output whether or not anything
/// is awake to serve it.
/// </remarks>
public sealed record RecordedTrace(
    string Id,
    string Label,
    RunStarted Started,
    RosterDelta[] Frames,
    RunCompleted Completed,
    ExplanationDto? Explanation);
