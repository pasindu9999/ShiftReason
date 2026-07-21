using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShiftReason.Api.Contracts;
using ShiftReason.Domain;
using ShiftReason.Solver;

namespace ShiftReason.Api.Solving;

/// <summary>
/// Turns successive snapshots into cell diffs.
/// </summary>
/// <remarks>
/// A 72x28 grid is over 2,000 cells; serialised in full it comfortably exceeds
/// SignalR's default 64 KB transport buffer, and at five frames a second the
/// connection would spend the whole solve on backpressure. In practice an
/// improving solution moves a handful of cells, so the diff is tiny. The first
/// frame is marked <c>IsFull</c> so the client has a baseline.
/// </remarks>
internal sealed class DeltaPump(string runId, int employees, int days)
{
    private int[]? _previous;
    private int _seq = -1;

    public int LastSeq => _seq;

    /// <summary>
    /// Produces the next delta, or null when nothing moved.
    /// </summary>
    /// <remarks>
    /// The final frame a run publishes is usually the same solution the throttle
    /// already sent, so without this every run ends with a duplicate no-op frame.
    /// </remarks>
    public RosterDelta? NextIfChanged(RosterSnapshot snapshot)
    {
        if (_previous is not null && !HasChanges(_previous, snapshot.Cells)) return null;
        return Next(snapshot);
    }

    public RosterDelta Next(RosterSnapshot snapshot)
    {
        var isFull = _previous is null;
        var changes = isFull ? Occupied(snapshot.Cells) : Diff(_previous!, snapshot.Cells);

        _previous = snapshot.Cells;
        _seq++;

        return new RosterDelta(
            runId,
            _seq,
            snapshot.Seconds,
            snapshot.Objective,
            snapshot.BestBound,
            isFull,
            changes,
            snapshot.Penalties.Lines
                .Select(l => new PenaltyLineDto(l.Key, l.Label, l.UnitNoun, l.Units, l.Weight, l.Cost))
                .ToArray());
    }

    /// <summary>
    /// The baseline frame: only the cells that are actually worked.
    /// </summary>
    /// <remarks>
    /// Most of a roster is time off, so sending every cell more than doubles the
    /// one frame that is already the largest. The client starts with an all-OFF
    /// grid and applies these, which is what <c>IsFull</c> signals.
    /// </remarks>
    private CellChange[] Occupied(int[] cells)
    {
        var changes = new List<CellChange>(cells.Length / 2);
        for (var e = 0; e < employees; e++)
        {
            for (var d = 0; d < days; d++)
            {
                var shift = cells[e * days + d];
                if (shift != 0) changes.Add(new CellChange(e, d, shift));
            }
        }

        return changes.ToArray();
    }

    private static bool HasChanges(int[] before, int[] after)
    {
        for (var i = 0; i < before.Length; i++)
        {
            if (before[i] != after[i]) return true;
        }

        return false;
    }

    private CellChange[] Diff(int[] before, int[] after)
    {
        var changes = new List<CellChange>();
        for (var e = 0; e < employees; e++)
        {
            for (var d = 0; d < days; d++)
            {
                var i = e * days + d;
                if (before[i] != after[i]) changes.Add(new CellChange(e, d, after[i]));
            }
        }

        return changes.ToArray();
    }
}

internal static class Gzip
{
    public static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }

    public static byte[] Decompress(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}

/// <summary>
/// A stable fingerprint of everything that determines a run's result.
/// </summary>
/// <remarks>
/// Ordering is forced at every level. A hash computed over dictionary or set
/// iteration order would differ between processes for identical input, which would
/// make the whole reproducibility story a lie. Note this identifies the *input*;
/// the serialised model proto stored alongside the run is what actually guarantees
/// an identical search.
/// </remarks>
internal static class ScenarioHash
{
    public static string Of(Scenario scenario, IReadOnlySet<string> relaxedRuleIds, int? seed)
    {
        var canonical = new
        {
            scenario.Id,
            Start = scenario.StartDate.ToString("yyyy-MM-dd"),
            scenario.HorizonDays,
            Employees = scenario.Employees
                .OrderBy(e => e.Id, StringComparer.Ordinal)
                .Select(e => new
                {
                    e.Id,
                    e.ContractHoursPerWeek,
                    e.MaxConsecutiveWorkDays,
                    Skills = e.Skills.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                }),
            Shifts = scenario.ShiftTypes
                .OrderBy(s => s.Index)
                .Select(s => new { s.Index, s.Id, s.Hours, s.IsNight }),
            Demands = scenario.Demands
                .OrderBy(d => d.Date).ThenBy(d => d.ShiftTypeId, StringComparer.Ordinal)
                .Select(d => new
                {
                    Date = d.Date.ToString("yyyy-MM-dd"),
                    d.ShiftTypeId,
                    d.RequiredHeadcount,
                    Skills = d.SkillMinimums
                        .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => new { kv.Key, kv.Value }),
                }),
            Leave = scenario.Leave
                .OrderBy(l => l.EmployeeId, StringComparer.Ordinal).ThenBy(l => l.Date)
                .Select(l => new { l.EmployeeId, Date = l.Date.ToString("yyyy-MM-dd"), Kind = l.Kind.ToString() }),
            Pins = scenario.Pins
                .OrderBy(p => p.EmployeeId, StringComparer.Ordinal).ThenBy(p => p.Date)
                .Select(p => new { p.EmployeeId, Date = p.Date.ToString("yyyy-MM-dd"), p.ShiftTypeId, p.Forbidden }),
            scenario.Settings,
            Relaxed = relaxedRuleIds.OrderBy(r => r, StringComparer.Ordinal).ToArray(),
            Seed = seed,
        };

        var json = JsonSerializer.Serialize(canonical);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
