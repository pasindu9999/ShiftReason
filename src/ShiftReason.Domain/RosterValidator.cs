namespace ShiftReason.Domain;

/// <summary>
/// Re-checks every hard constraint against a finished roster, from scratch.
/// </summary>
/// <remarks>
/// This project has no reference to OR-Tools, and that is the point. If the
/// validator shared code (or a mental model) with the CP-SAT builder, the tests
/// would only prove the builder agrees with itself. Written independently, a
/// modelling bug shows up as a disagreement instead of as a passing test.
///
/// It is also what makes the "relax a rule and re-solve" flow trustworthy: after
/// relaxing rule R, every hard constraint *except* R must still hold, and this
/// is what asserts that.
/// </remarks>
public static class RosterValidator
{
    public static IReadOnlyList<RosterViolation> Validate(
        Scenario scenario,
        Roster roster,
        IReadOnlySet<string>? relaxedRuleIds = null)
    {
        relaxedRuleIds ??= new HashSet<string>();
        var violations = new List<RosterViolation>();

        CheckOneShiftPerDay(scenario, roster, violations);
        CheckCoverage(scenario, roster, relaxedRuleIds, violations);
        CheckSkillMinimums(scenario, roster, relaxedRuleIds, violations);
        CheckApprovedLeave(scenario, roster, relaxedRuleIds, violations);
        CheckMinRest(scenario, roster, relaxedRuleIds, violations);
        CheckMaxConsecutive(scenario, roster, relaxedRuleIds, violations);
        CheckContractHours(scenario, roster, relaxedRuleIds, violations);
        CheckPins(scenario, roster, relaxedRuleIds, violations);

        return violations;
    }

    // Structural: never relaxable, so it ignores relaxedRuleIds entirely.
    private static void CheckOneShiftPerDay(Scenario s, Roster r, List<RosterViolation> v)
    {
        foreach (var e in s.Employees)
        {
            foreach (var d in s.Dates)
            {
                var count = r.Assignments.Count(a => a.EmployeeId == e.Id && a.Date == d);
                if (count != 1)
                {
                    v.Add(new RosterViolation(
                        $"structural:oneshift:{e.Id}:{d:yyyy-MM-dd}",
                        RuleKind.OneShiftPerDay,
                        $"{e.Name} has {count} assignments on {d:ddd dd MMM}, expected exactly 1."));
                }
            }
        }
    }

    private static void CheckCoverage(
        Scenario s, Roster r, IReadOnlySet<string> relaxed, List<RosterViolation> v)
    {
        foreach (var demand in s.Demands)
        {
            var id = RuleIds.Coverage(demand.Date, demand.ShiftTypeId);
            if (relaxed.Contains(id)) continue;

            var actual = r.HeadcountOn(demand.Date, demand.ShiftTypeId);
            if (actual < demand.RequiredHeadcount)
            {
                v.Add(new RosterViolation(id, RuleKind.Coverage,
                    $"{demand.Date:ddd dd MMM} {s.ShiftById(demand.ShiftTypeId).Name}: " +
                    $"{actual} rostered, {demand.RequiredHeadcount} required."));
            }
        }
    }

    private static void CheckSkillMinimums(
        Scenario s, Roster r, IReadOnlySet<string> relaxed, List<RosterViolation> v)
    {
        foreach (var demand in s.Demands)
        {
            foreach (var (skill, min) in demand.SkillMinimums)
            {
                var id = RuleIds.SkillMinimum(demand.Date, demand.ShiftTypeId, skill);
                if (relaxed.Contains(id)) continue;

                var actual = r.StaffOn(demand.Date, demand.ShiftTypeId)
                              .Count(eid => s.EmployeeById(eid).Holds(skill));
                if (actual < min)
                {
                    v.Add(new RosterViolation(id, RuleKind.SkillMinimum,
                        $"{demand.Date:ddd dd MMM} {s.ShiftById(demand.ShiftTypeId).Name}: " +
                        $"{actual} staff hold '{skill}', {min} required."));
                }
            }
        }
    }

    private static void CheckApprovedLeave(
        Scenario s, Roster r, IReadOnlySet<string> relaxed, List<RosterViolation> v)
    {
        foreach (var leave in s.Leave.Where(l => l.Kind is LeaveKind.Approved))
        {
            var id = RuleIds.ApprovedLeave(leave.EmployeeId, leave.Date);
            if (relaxed.Contains(id)) continue;

            if (r.IsWorking(leave.EmployeeId, leave.Date))
            {
                v.Add(new RosterViolation(id, RuleKind.ApprovedLeave,
                    $"{s.EmployeeById(leave.EmployeeId).Name} is rostered on " +
                    $"{leave.Date:ddd dd MMM} despite approved leave."));
            }
        }
    }

    /// <summary>A night shift must not be followed by a shift the next day.</summary>
    private static void CheckMinRest(
        Scenario s, Roster r, IReadOnlySet<string> relaxed, List<RosterViolation> v)
    {
        var lastDate = s.StartDate.AddDays(s.HorizonDays - 1);

        foreach (var e in s.Employees)
        {
            foreach (var d in s.Dates.Where(d => d < lastDate))
            {
                var id = RuleIds.MinRest(e.Id, d);
                if (relaxed.Contains(id)) continue;

                var today = r.ShiftOn(e.Id, d);
                if (today == ShiftType.Off.Id || !s.ShiftById(today).IsNight) continue;

                var tomorrow = r.ShiftOn(e.Id, d.AddDays(1));
                if (tomorrow != ShiftType.Off.Id)
                {
                    v.Add(new RosterViolation(id, RuleKind.MinRest,
                        $"{e.Name} works {s.ShiftById(tomorrow).Name} on {d.AddDays(1):ddd dd MMM} " +
                        $"directly after a night shift."));
                }
            }
        }
    }

    private static void CheckMaxConsecutive(
        Scenario s, Roster r, IReadOnlySet<string> relaxed, List<RosterViolation> v)
    {
        foreach (var e in s.Employees)
        {
            var k = e.MaxConsecutiveWorkDays;
            if (k <= 0) continue;

            var dates = s.Dates.ToList();
            // A run of k+1 worked days starting at each window is the violation.
            for (var i = 0; i + k < dates.Count; i++)
            {
                var id = RuleIds.MaxConsecutive(e.Id, dates[i]);
                if (relaxed.Contains(id)) continue;

                var window = dates.Skip(i).Take(k + 1).ToList();
                if (window.All(d => r.IsWorking(e.Id, d)))
                {
                    v.Add(new RosterViolation(id, RuleKind.MaxConsecutive,
                        $"{e.Name} works {k + 1} days straight from {window[0]:ddd dd MMM} " +
                        $"(limit is {k})."));
                }
            }
        }
    }

    private static void CheckContractHours(
        Scenario s, Roster r, IReadOnlySet<string> relaxed, List<RosterViolation> v)
    {
        var dates = s.Dates.ToList();

        foreach (var e in s.Employees)
        {
            // Fixed 7-day blocks from the horizon start; a trailing partial week
            // is scaled pro-rata so a short horizon is not spuriously infeasible.
            for (var week = 0; week * 7 < dates.Count; week++)
            {
                var id = RuleIds.ContractHours(e.Id, week);
                if (relaxed.Contains(id)) continue;

                var block = dates.Skip(week * 7).Take(7).ToList();
                var hours = block.Sum(d =>
                {
                    var shift = r.ShiftOn(e.Id, d);
                    return shift == ShiftType.Off.Id ? 0 : s.ShiftById(shift).Hours;
                });

                var budget = e.ContractHoursPerWeek * block.Count / 7.0;
                if (hours > budget + 1e-6)
                {
                    v.Add(new RosterViolation(id, RuleKind.ContractHours,
                        $"{e.Name} is rostered {hours:0.#}h in week {week + 1}, " +
                        $"over the {budget:0.#}h contract limit."));
                }
            }
        }
    }

    private static void CheckPins(
        Scenario s, Roster r, IReadOnlySet<string> relaxed, List<RosterViolation> v)
    {
        foreach (var pin in s.Pins)
        {
            var actual = r.ShiftOn(pin.EmployeeId, pin.Date);
            var name = s.EmployeeById(pin.EmployeeId).Name;

            if (pin.Forbidden)
            {
                var id = RuleIds.Forbidden(pin.EmployeeId, pin.Date, pin.ShiftTypeId);
                if (relaxed.Contains(id)) continue;

                if (actual == pin.ShiftTypeId)
                {
                    v.Add(new RosterViolation(id, RuleKind.Forbidden,
                        $"{name} is on {s.ShiftById(pin.ShiftTypeId).Name} on " +
                        $"{pin.Date:ddd dd MMM}, which was blocked."));
                }
            }
            else
            {
                var id = RuleIds.Pinned(pin.EmployeeId, pin.Date);
                if (relaxed.Contains(id)) continue;

                if (actual != pin.ShiftTypeId)
                {
                    v.Add(new RosterViolation(id, RuleKind.Pinned,
                        $"{name} on {pin.Date:ddd dd MMM} is " +
                        $"{s.ShiftById(actual).Name}, but was pinned to " +
                        $"{s.ShiftById(pin.ShiftTypeId).Name}."));
                }
            }
        }
    }
}
