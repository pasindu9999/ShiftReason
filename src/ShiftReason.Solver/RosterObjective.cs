using Google.OrTools.Sat;
using ShiftReason.Domain;

namespace ShiftReason.Solver;

/// <summary>One weighted component of the objective, kept addressable after the solve.</summary>
public sealed record ObjectiveTerm(string Key, string Label, string UnitNoun, int Weight, LinearExpr Units);

/// <summary>What one soft rule actually cost this roster.</summary>
public sealed record PenaltyLine(string Key, string Label, string UnitNoun, long Units, int Weight, long Cost);

public sealed record PenaltyBreakdown(IReadOnlyList<PenaltyLine> Lines, long Total)
{
    public static PenaltyBreakdown Empty { get; } = new([], 0);
}

/// <summary>
/// The soft half of the model: preferences and fairness, priced.
/// </summary>
/// <remarks>
/// <para>
/// Two jobs. The obvious one is producing a better roster. The less obvious one is
/// that a <em>feasible</em> solve still has to explain itself — "night-shift spread
/// of 4, two split weekends" — so every term stays individually readable after the
/// solve instead of collapsing into a single number.
/// </para>
/// <para>
/// The fairness terms are min-max rather than sum-of-deviations, which is
/// deliberate on two counts. It is the more defensible rule: a ward manager cares
/// about the worst-treated nurse, not the average one. And min-max objectives have
/// a weak LP relaxation, so CP-SAT has to grind the dual bound instead of finding a
/// good roster in 40ms and going silent — a demo that streams improving solutions
/// needs the search to keep finding them.
/// </para>
/// </remarks>
public sealed class RosterObjective
{
    private RosterObjective(IReadOnlyList<ObjectiveTerm> terms, LinearExpr total)
    {
        Terms = terms;
        Total = total;
    }

    public IReadOnlyList<ObjectiveTerm> Terms { get; }

    /// <summary>The weighted sum handed to Minimize.</summary>
    public LinearExpr Total { get; }

    public bool IsEmpty => Terms.Count == 0;

    /// <summary>No soft terms at all — what Explain and Relax modes carry.</summary>
    public static RosterObjective None { get; } = new([], LinearExpr.NewBuilder());

    /// <summary>Reads each term back, so the UI can show where the cost went.</summary>
    /// <param name="valueOf">solver.Value, or callback.Value mid-solve.</param>
    public PenaltyBreakdown Read(Func<LinearExpr, long> valueOf)
    {
        var lines = new List<PenaltyLine>(Terms.Count);
        long total = 0;

        foreach (var term in Terms)
        {
            var units = valueOf(term.Units);
            var cost = units * term.Weight;
            total += cost;
            lines.Add(new PenaltyLine(term.Key, term.Label, term.UnitNoun, units, term.Weight, cost));
        }

        // Biggest contributor first: that is the line a manager would act on.
        return new PenaltyBreakdown(lines.OrderByDescending(l => l.Cost).ToList(), total);
    }

    internal static RosterObjective Build(
        CpModel model,
        Scenario scenario,
        BoolVar[,,] x,
        IReadOnlyList<ShiftType> shifts,
        IReadOnlyList<DateOnly> dates,
        int offIndex)
    {
        var terms = new List<ObjectiveTerm>();

        AddPreferredLeave(scenario, x, dates, offIndex, terms);
        AddNightFairness(model, scenario, x, shifts, dates, terms);
        AddWeekendFairness(model, scenario, x, dates, offIndex, terms);
        AddSplitWeekends(model, scenario, x, dates, offIndex, terms);

        var total = LinearExpr.NewBuilder();
        foreach (var term in terms) total.AddTerm(term.Units, term.Weight);

        return new RosterObjective(terms, total);
    }

    /// <summary>Requested-but-not-approved days off. A wish, not a right.</summary>
    private static void AddPreferredLeave(
        Scenario scenario, BoolVar[,,] x, IReadOnlyList<DateOnly> dates, int offIndex,
        List<ObjectiveTerm> terms)
    {
        var requests = scenario.Leave.Where(l => l.Kind is LeaveKind.Preferred).ToList();
        if (requests.Count == 0) return;

        var violations = LinearExpr.NewBuilder();

        foreach (var request in requests)
        {
            var d = IndexOfDate(dates, request.Date);
            if (d < 0) continue;

            var e = IndexOf(scenario, request.EmployeeId);
            // Working on a requested day off is simply "not on the OFF shift".
            violations.AddTerm(x[e, d, offIndex].Not(), 1);
        }

        terms.Add(new ObjectiveTerm(
            "preferred-leave",
            "Requested days off not granted",
            "request(s)",
            scenario.Settings.PreferredLeaveViolationPenalty,
            violations));
    }

    /// <summary>Spread between the nurse with the most nights and the one with the fewest.</summary>
    private static void AddNightFairness(
        CpModel model, Scenario scenario, BoolVar[,,] x,
        IReadOnlyList<ShiftType> shifts, IReadOnlyList<DateOnly> dates,
        List<ObjectiveTerm> terms)
    {
        var nights = shifts.Where(s => s.IsNight).ToList();
        if (nights.Count == 0 || scenario.Employees.Count < 2) return;

        var counts = new IntVar[scenario.Employees.Count];

        for (var e = 0; e < scenario.Employees.Count; e++)
        {
            var worked = LinearExpr.NewBuilder();
            for (var d = 0; d < dates.Count; d++)
            {
                foreach (var n in nights) worked.AddTerm(x[e, d, n.Index], 1);
            }

            counts[e] = model.NewIntVar(0, dates.Count, $"nights:{scenario.Employees[e].Id}");
            model.Add(counts[e] == worked);
        }

        terms.Add(new ObjectiveTerm(
            "night-fairness",
            "Night-shift spread (most nights minus fewest)",
            "night(s)",
            scenario.Settings.NightFairnessWeight,
            Spread(model, counts, dates.Count, "nights")));
    }

    /// <summary>The same idea for weekend duty, which is what staff actually complain about.</summary>
    private static void AddWeekendFairness(
        CpModel model, Scenario scenario, BoolVar[,,] x,
        IReadOnlyList<DateOnly> dates, int offIndex,
        List<ObjectiveTerm> terms)
    {
        var weekendDays = Enumerable.Range(0, dates.Count)
            .Where(d => dates[d].DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            .ToList();

        if (weekendDays.Count == 0 || scenario.Employees.Count < 2) return;

        var counts = new IntVar[scenario.Employees.Count];

        for (var e = 0; e < scenario.Employees.Count; e++)
        {
            var worked = LinearExpr.NewBuilder();
            foreach (var d in weekendDays) worked.AddTerm(x[e, d, offIndex].Not(), 1);

            counts[e] = model.NewIntVar(0, weekendDays.Count, $"weekends:{scenario.Employees[e].Id}");
            model.Add(counts[e] == worked);
        }

        terms.Add(new ObjectiveTerm(
            "weekend-fairness",
            "Weekend-duty spread (most weekend days minus fewest)",
            "day(s)",
            scenario.Settings.WeekendFairnessWeight,
            Spread(model, counts, weekendDays.Count, "weekends")));
    }

    /// <summary>
    /// Working one half of a weekend but not the other. Universally disliked, and
    /// invisible to a fairness term that only totals weekend days.
    /// </summary>
    private static void AddSplitWeekends(
        CpModel model, Scenario scenario, BoolVar[,,] x,
        IReadOnlyList<DateOnly> dates, int offIndex,
        List<ObjectiveTerm> terms)
    {
        var pairs = new List<(int Sat, int Sun)>();
        for (var d = 0; d + 1 < dates.Count; d++)
        {
            if (dates[d].DayOfWeek is DayOfWeek.Saturday && dates[d + 1].DayOfWeek is DayOfWeek.Sunday)
            {
                pairs.Add((d, d + 1));
            }
        }

        if (pairs.Count == 0) return;

        var splits = LinearExpr.NewBuilder();

        for (var e = 0; e < scenario.Employees.Count; e++)
        {
            foreach (var (sat, sun) in pairs)
            {
                var split = model.NewBoolVar($"split:{scenario.Employees[e].Id}:{dates[sat]:yyyy-MM-dd}");

                var onSat = x[e, sat, offIndex].Not();
                var onSun = x[e, sun, offIndex].Not();

                // split >= |onSat - onSun|. Only the lower bounds are needed: the
                // objective pushes split down, so it settles on the true absolute
                // difference without also constraining it from above.
                model.Add(LinearExpr.NewBuilder().AddTerm(onSat, 1).AddTerm(onSun, -1) <= split);
                model.Add(LinearExpr.NewBuilder().AddTerm(onSun, 1).AddTerm(onSat, -1) <= split);

                splits.AddTerm(split, 1);
            }
        }

        terms.Add(new ObjectiveTerm(
            "split-weekend",
            "Split weekends (one day on, one day off)",
            "weekend(s)",
            scenario.Settings.SplitWeekendPenalty,
            splits));
    }

    /// <summary>max(counts) - min(counts) as an integer expression.</summary>
    private static LinearExpr Spread(CpModel model, IntVar[] counts, int upperBound, string name)
    {
        var hi = model.NewIntVar(0, upperBound, $"max:{name}");
        var lo = model.NewIntVar(0, upperBound, $"min:{name}");

        model.AddMaxEquality(hi, counts);
        model.AddMinEquality(lo, counts);

        var spread = model.NewIntVar(0, upperBound, $"spread:{name}");
        model.Add(spread == hi - lo);
        return spread;
    }

    private static int IndexOfDate(IReadOnlyList<DateOnly> dates, DateOnly date)
    {
        for (var i = 0; i < dates.Count; i++)
        {
            if (dates[i] == date) return i;
        }

        return -1;
    }

    private static int IndexOf(Scenario scenario, string employeeId)
    {
        for (var i = 0; i < scenario.Employees.Count; i++)
        {
            if (scenario.Employees[i].Id == employeeId) return i;
        }

        throw new KeyNotFoundException($"No employee '{employeeId}'.");
    }
}
