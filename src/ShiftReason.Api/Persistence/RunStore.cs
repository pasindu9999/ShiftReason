using Microsoft.EntityFrameworkCore;

namespace ShiftReason.Api.Persistence;

/// <summary>A solve that was run. Insert-only: runs are never edited.</summary>
/// <remarks>
/// <see cref="ModelProtoGz"/> is the reason replay can be trusted. Hashing the
/// scenario is not enough — one reordered dictionary in the model builder changes
/// the constraint order, which changes the search, which changes the roster, while
/// the scenario hash stays identical. Storing the serialised model removes that
/// entire class of "replay does not reproduce" bug.
/// </remarks>
public sealed class SolveRun
{
    public required string Id { get; init; }
    public required string ScenarioId { get; init; }
    public required string ScenarioName { get; init; }

    /// <summary>Set when this run came from relaxing a rule on an earlier one.</summary>
    public string? ParentRunId { get; init; }

    /// <summary>Rule ids given up for this run, comma-separated.</summary>
    public string RelaxedRuleIds { get; set; } = "";

    public required string Mode { get; init; }            // Fast | Reproducible
    public int? Seed { get; init; }
    public required string SolverVersion { get; init; }
    public required string Parameters { get; init; }
    public required string InputHash { get; init; }
    public byte[]? ModelProtoGz { get; set; }

    public required string Status { get; set; }           // Queued | Running | Completed | Infeasible | Failed | Cancelled
    public double? Objective { get; set; }
    public double? BestBound { get; set; }
    public double WallSeconds { get; set; }
    public int SolutionCount { get; set; }

    public string? RosterJson { get; set; }
    public string? PenaltiesJson { get; set; }
    public string? ExplanationJson { get; set; }
    public string? Error { get; set; }

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedUtc { get; set; }
}

/// <summary>One improving solution, kept so a run can be replayed frame by frame.</summary>
public sealed class SolveFrame
{
    public long Id { get; init; }
    public required string RunId { get; init; }
    public int Seq { get; init; }
    public double Seconds { get; init; }
    public double Objective { get; init; }
    public double BestBound { get; init; }

    /// <summary>Only the cells that changed since the previous frame.</summary>
    public required string ChangesJson { get; init; }
}

public sealed class RunStore(DbContextOptions<RunStore> options) : DbContext(options)
{
    public DbSet<SolveRun> Runs => Set<SolveRun>();
    public DbSet<SolveFrame> Frames => Set<SolveFrame>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<SolveRun>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => r.CreatedUtc);
            e.HasIndex(r => r.ParentRunId);
        });

        b.Entity<SolveFrame>(e =>
        {
            e.HasKey(f => f.Id);
            e.HasIndex(f => new { f.RunId, f.Seq }).IsUnique();
        });
    }
}
