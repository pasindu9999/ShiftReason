using Google.OrTools.Sat;
using ShiftReason.Domain;

namespace ShiftReason.Solver;

public enum SolveMode
{
    /// <summary>Guards fixed true, objective on, multi-worker. Produces the roster.</summary>
    Optimize,

    /// <summary>Guards free and registered as assumptions, no objective, single worker. Produces the conflict core.</summary>
    Explain,

    /// <summary>Guards free, minimising the cost of the ones switched off. Produces the cheapest fix.</summary>
    Relax,
}

/// <summary>
/// One roster model, built in one of three configurations.
/// </summary>
/// <remarks>
/// There is deliberately no second "feasibility-only" builder. <c>CpModel</c> has
/// only a parameterless constructor — there is no <c>CpModel(CpModelProto)</c> —
/// so loading a proto back would mean proto surgery and would let the explanation
/// drift from the model actually solved. Instead the same code path emits all
/// three configurations, and the only difference is what happens to the guards.
/// </remarks>
public sealed class GuardedRosterModel
{
    private readonly List<(BoolVar Lit, RuleRef Rule)> _guards = [];
    private readonly Dictionary<int, RuleRef> _byLiteralIndex = [];
    private readonly Dictionary<string, BoolVar> _byRuleId = [];
    private readonly List<ShiftType> _shifts;
    private readonly List<DateOnly> _dates;
    private readonly int _offIndex;

    private GuardedRosterModel(Scenario scenario, SolveMode mode, IReadOnlySet<string>? relaxedRuleIds)
    {
        Scenario = scenario;
        Mode = mode;
        RelaxedRuleIds = relaxedRuleIds ?? new HashSet<string>(StringComparer.Ordinal);

        _shifts = scenario.ShiftTypes.OrderBy(s => s.Index).ToList();
        _dates = scenario.Dates.ToList();
        _offIndex = _shifts.Single(s => s.IsOff).Index;

        X = new BoolVar[scenario.Employees.Count, _dates.Count, _shifts.Count];

        CreateVariables();
        AddStructuralConstraints();
        AddPolicyConstraints();
        ApplyMode();

        // Never ship a model to CP-SAT without this. See ModelLint.
        ModelLint.AssertNoGuardedSetConstraints(Model);
    }

    public CpModel Model { get; } = new();
    public Scenario Scenario { get; }
    public SolveMode Mode { get; }

    /// <summary>x[employee, day, shift]. Shift index 0 is OFF.</summary>
    public BoolVar[,,] X { get; }

    public IReadOnlyList<(BoolVar Lit, RuleRef Rule)> Guards => _guards;

    /// <summary>
    /// Maps a signed proto literal reference back to the rule that emitted it.
    /// </summary>
    /// <remarks>
    /// <c>SufficientAssumptionsForInfeasibility()</c> returns signed refs in the
    /// <em>original</em> model's index space — CP-SAT undoes the presolve mapping
    /// for us — so this is a plain lookup on <c>BoolVar.GetIndex()</c>.
    /// </remarks>
    public IReadOnlyDictionary<int, RuleRef> ByLiteralIndex => _byLiteralIndex;

    /// <summary>
    /// Rules the user has chosen to give up. Their guards are pinned off, so the
    /// constraint is present in the model but never enforced.
    /// </summary>
    /// <remarks>
    /// This is how "relax this rule and re-solve" works, and why it is modelled as
    /// a guard rather than by rebuilding a trimmed scenario: the rule keeps its
    /// identity, so the resulting run can still be diffed against its parent and
    /// the UI can say exactly what was given up.
    /// </remarks>
    public IReadOnlySet<string> RelaxedRuleIds { get; }

    public static GuardedRosterModel Build(
        Scenario scenario,
        SolveMode mode,
        IReadOnlySet<string>? relaxedRuleIds = null) => new(scenario, mode, relaxedRuleIds);

    // ------------------------------------------------------------------
    // Variables
    // ------------------------------------------------------------------

    private void CreateVariables()
    {
        for (var e = 0; e < Scenario.Employees.Count; e++)
        {
            var emp = Scenario.Employees[e];
            for (var d = 0; d < _dates.Count; d++)
            {
                for (var s = 0; s < _shifts.Count; s++)
                {
                    X[e, d, s] = Model.NewBoolVar($"x:{emp.Id}:{_dates[d]:yyyy-MM-dd}:{_shifts[s].Id}");
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Structural — never guarded, never relaxable
    // ------------------------------------------------------------------

    private void AddStructuralConstraints()
    {
        // Exactly one shift per nurse per day. AddExactlyOne is the efficient
        // form and is safe *because* this is structural: it never receives an
        // enforcement literal. ModelLint enforces that invariant.
        for (var e = 0; e < Scenario.Employees.Count; e++)
        {
            for (var d = 0; d < _dates.Count; d++)
            {
                var cell = new ILiteral[_shifts.Count];
                for (var s = 0; s < _shifts.Count; s++) cell[s] = X[e, d, s];
                Model.AddExactlyOne(cell);
            }
        }
    }

    // ------------------------------------------------------------------
    // Policy — every one carries a guard literal
    // ------------------------------------------------------------------

    private void AddPolicyConstraints()
    {
        AddCoverage();
        AddSkillMinimums();
        AddApprovedLeave();
        AddMinRest();
        AddMaxConsecutive();
        AddContractHours();
        AddPins();
    }

    private void AddCoverage()
    {
        foreach (var demand in Scenario.Demands)
        {
            var d = IndexOfDate(demand.Date);
            if (d < 0) continue;

            var shift = Scenario.ShiftById(demand.ShiftTypeId);
            var rule = new RuleRef(
                RuleIds.Coverage(demand.Date, demand.ShiftTypeId),
                RuleKind.Coverage, RuleClass.Policy, Hardness.Hard,
                Scenario.Settings.CoverageRelaxCost,
                $"{demand.Date:ddd d MMM} {shift.Name} needs {demand.RequiredHeadcount} " +
                $"{(demand.RequiredHeadcount == 1 ? "nurse" : "nurses")} on duty.",
                [EntityRef.Date(demand.Date), EntityRef.Shift(shift.Id)]);

            var sum = LinearExpr.NewBuilder();
            for (var e = 0; e < Scenario.Employees.Count; e++) sum.AddTerm(X[e, d, shift.Index], 1);

            Model.Add(sum >= demand.RequiredHeadcount).OnlyEnforceIf(Guard(rule));
        }
    }

    private void AddSkillMinimums()
    {
        foreach (var demand in Scenario.Demands)
        {
            var d = IndexOfDate(demand.Date);
            if (d < 0) continue;

            var shift = Scenario.ShiftById(demand.ShiftTypeId);

            foreach (var (skill, min) in demand.SkillMinimums.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var qualified = Enumerable.Range(0, Scenario.Employees.Count)
                                          .Where(e => Scenario.Employees[e].Holds(skill))
                                          .ToList();

                var rule = new RuleRef(
                    RuleIds.SkillMinimum(demand.Date, demand.ShiftTypeId, skill),
                    RuleKind.SkillMinimum, RuleClass.Policy, Hardness.Hard,
                    Scenario.Settings.SkillMinimumRelaxCost,
                    $"{demand.Date:ddd d MMM} {shift.Name} needs {min} " +
                    $"{(min == 1 ? "nurse" : "nurses")} certified in {skill} " +
                    $"(only {qualified.Count} on the ward hold it).",
                    [EntityRef.Date(demand.Date), EntityRef.Shift(shift.Id), EntityRef.Skill(skill)]);

                var sum = LinearExpr.NewBuilder();
                foreach (var e in qualified) sum.AddTerm(X[e, d, shift.Index], 1);

                Model.Add(sum >= min).OnlyEnforceIf(Guard(rule));
            }
        }
    }

    private void AddApprovedLeave()
    {
        foreach (var leave in Scenario.Leave.Where(l => l.Kind is LeaveKind.Approved))
        {
            var d = IndexOfDate(leave.Date);
            if (d < 0) continue;

            var e = IndexOfEmployee(leave.EmployeeId);
            var emp = Scenario.Employees[e];

            var rule = new RuleRef(
                RuleIds.ApprovedLeave(leave.EmployeeId, leave.Date),
                RuleKind.ApprovedLeave, RuleClass.Policy, Hardness.Hard,
                Scenario.Settings.ApprovedLeaveRelaxCost,
                $"{emp.Name} has approved leave on {leave.Date:ddd d MMM}.",
                [EntityRef.Employee(emp.Id), EntityRef.Date(leave.Date)]);

            Model.Add(X[e, d, _offIndex] == 1).OnlyEnforceIf(Guard(rule));
        }
    }

    /// <summary>No shift the day after a night shift.</summary>
    private void AddMinRest()
    {
        var nights = _shifts.Where(s => s.IsNight).ToList();
        if (nights.Count == 0) return;

        for (var e = 0; e < Scenario.Employees.Count; e++)
        {
            var emp = Scenario.Employees[e];

            for (var d = 0; d + 1 < _dates.Count; d++)
            {
                var rule = new RuleRef(
                    RuleIds.MinRest(emp.Id, _dates[d]),
                    RuleKind.MinRest, RuleClass.Policy, Hardness.Hard,
                    Scenario.Settings.MinRestRelaxCost,
                    $"{emp.Name} must rest on {_dates[d + 1]:ddd d MMM} after a night shift " +
                    $"on {_dates[d]:ddd d MMM}.",
                    [EntityRef.Employee(emp.Id), EntityRef.Date(_dates[d]), EntityRef.Date(_dates[d + 1])]);

                // night today + any work tomorrow <= 1
                var sum = LinearExpr.NewBuilder();
                foreach (var n in nights) sum.AddTerm(X[e, d, n.Index], 1);
                foreach (var w in _shifts.Where(s => !s.IsOff)) sum.AddTerm(X[e, d + 1, w.Index], 1);

                Model.Add(sum <= 1).OnlyEnforceIf(Guard(rule));
            }
        }
    }

    /// <summary>At least one day off in every window of MaxConsecutive + 1 days.</summary>
    private void AddMaxConsecutive()
    {
        for (var e = 0; e < Scenario.Employees.Count; e++)
        {
            var emp = Scenario.Employees[e];
            var k = emp.MaxConsecutiveWorkDays;
            if (k <= 0 || k >= _dates.Count) continue;

            for (var start = 0; start + k < _dates.Count; start++)
            {
                var rule = new RuleRef(
                    RuleIds.MaxConsecutive(emp.Id, _dates[start]),
                    RuleKind.MaxConsecutive, RuleClass.Policy, Hardness.Hard,
                    Scenario.Settings.MaxConsecutiveRelaxCost,
                    $"{emp.Name} may not work more than {k} days in a row " +
                    $"(window starting {_dates[start]:ddd d MMM}).",
                    [EntityRef.Employee(emp.Id), EntityRef.Date(_dates[start])]);

                var offDays = LinearExpr.NewBuilder();
                for (var i = start; i <= start + k; i++) offDays.AddTerm(X[e, i, _offIndex], 1);

                Model.Add(offDays >= 1).OnlyEnforceIf(Guard(rule));
            }
        }
    }

    private void AddContractHours()
    {
        for (var e = 0; e < Scenario.Employees.Count; e++)
        {
            var emp = Scenario.Employees[e];

            for (var week = 0; week * 7 < _dates.Count; week++)
            {
                var block = Enumerable.Range(week * 7, Math.Min(7, _dates.Count - week * 7)).ToList();

                // CP-SAT is integral: work in minutes, and scale a trailing
                // partial week pro-rata so a short horizon is not spuriously
                // infeasible.
                var budgetMinutes = (long)Math.Round(emp.ContractHoursPerWeek * 60 * block.Count / 7.0);

                var rule = new RuleRef(
                    RuleIds.ContractHours(emp.Id, week),
                    RuleKind.ContractHours, RuleClass.Policy, Hardness.Hard,
                    Scenario.Settings.ContractHoursRelaxCost,
                    $"{emp.Name} is contracted to at most {emp.ContractHoursPerWeek:0.#}h " +
                    $"in week {week + 1}.",
                    [EntityRef.Employee(emp.Id)]);

                var minutes = LinearExpr.NewBuilder();
                foreach (var d in block)
                {
                    foreach (var s in _shifts.Where(s => !s.IsOff))
                    {
                        minutes.AddTerm(X[e, d, s.Index], (long)Math.Round(s.Hours * 60));
                    }
                }

                Model.Add(minutes <= budgetMinutes).OnlyEnforceIf(Guard(rule));
            }
        }
    }

    private void AddPins()
    {
        foreach (var pin in Scenario.Pins)
        {
            var d = IndexOfDate(pin.Date);
            if (d < 0) continue;

            var e = IndexOfEmployee(pin.EmployeeId);
            var emp = Scenario.Employees[e];
            var shift = Scenario.ShiftById(pin.ShiftTypeId);

            // Never fix a cell with NewConstant or by narrowing the variable's
            // domain: neither can carry a guard, so neither could be relaxed or
            // appear in a conflict core.
            if (pin.Forbidden)
            {
                var rule = new RuleRef(
                    RuleIds.Forbidden(pin.EmployeeId, pin.Date, pin.ShiftTypeId),
                    RuleKind.Forbidden, RuleClass.Policy, Hardness.Hard,
                    Scenario.Settings.PinnedRelaxCost,
                    $"{emp.Name} is blocked from {shift.Name} on {pin.Date:ddd d MMM}.",
                    [EntityRef.Employee(emp.Id), EntityRef.Date(pin.Date), EntityRef.Shift(shift.Id)]);

                Model.Add(X[e, d, shift.Index] == 0).OnlyEnforceIf(Guard(rule));
            }
            else
            {
                var rule = new RuleRef(
                    RuleIds.Pinned(pin.EmployeeId, pin.Date),
                    RuleKind.Pinned, RuleClass.Policy, Hardness.Hard,
                    Scenario.Settings.PinnedRelaxCost,
                    $"{emp.Name} is pinned to {shift.Name} on {pin.Date:ddd d MMM}.",
                    [EntityRef.Employee(emp.Id), EntityRef.Date(pin.Date), EntityRef.Shift(shift.Id)]);

                Model.Add(X[e, d, shift.Index] == 1).OnlyEnforceIf(Guard(rule));
            }
        }
    }

    // ------------------------------------------------------------------
    // Mode
    // ------------------------------------------------------------------

    private void ApplyMode()
    {
        // A rule the user has already given up is pinned off in every mode: it
        // cannot be blamed for a later conflict, and it cannot be "relaxed" twice.
        foreach (var (lit, rule) in _guards.Where(g => RelaxedRuleIds.Contains(g.Rule.RuleId)))
        {
            _ = rule;
            Model.Add(lit == 0);
        }

        var live = _guards.Where(g => !RelaxedRuleIds.Contains(g.Rule.RuleId)).ToList();

        switch (Mode)
        {
            case SolveMode.Optimize:
                // Guards pinned on: the model behaves as if every rule were a
                // plain hard constraint.
                foreach (var (lit, _) in live) Model.Add(lit == 1);
                break;

            case SolveMode.Explain:
                // No objective, ever. An objective silently degrades the core to
                // "all assumptions" with no status code to detect it.
                Model.AddAssumptions(live.Select(g => (ILiteral)g.Lit));
                break;

            case SolveMode.Relax:
                // Minimum-cost correction set: which rules, weighted by what it
                // costs the ward to break them, must give way.
                var cost = LinearExpr.NewBuilder();
                foreach (var (lit, rule) in live) cost.AddTerm(lit.Not(), rule.RelaxCost);
                Model.Minimize(cost);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(Mode), Mode, "Unknown solve mode.");
        }
    }

    private BoolVar Guard(RuleRef rule)
    {
        if (_byRuleId.TryGetValue(rule.RuleId, out var existing)) return existing;

        var lit = Model.NewBoolVar($"rule::{rule.RuleId}");
        _guards.Add((lit, rule));
        _byRuleId[rule.RuleId] = lit;
        _byLiteralIndex[lit.GetIndex()] = rule;
        return lit;
    }

    // ------------------------------------------------------------------
    // Reading a solution back
    // ------------------------------------------------------------------

    /// <summary>
    /// Turns solver output into a <see cref="Roster"/>.
    /// </summary>
    /// <param name="valueOf">
    /// <c>solver.BooleanValue</c> after a solve, or <c>callback.BooleanValue</c>
    /// from inside a solution callback — the two have identical shape but live on
    /// different types.
    /// </param>
    public Roster ExtractRoster(Func<BoolVar, bool> valueOf)
    {
        var assignments = new List<Assignment>(Scenario.Employees.Count * _dates.Count);

        for (var e = 0; e < Scenario.Employees.Count; e++)
        {
            for (var d = 0; d < _dates.Count; d++)
            {
                for (var s = 0; s < _shifts.Count; s++)
                {
                    if (!valueOf(X[e, d, s])) continue;
                    assignments.Add(new Assignment(Scenario.Employees[e].Id, _dates[d], _shifts[s].Id));
                    break;
                }
            }
        }

        return new Roster(assignments);
    }

    /// <summary>
    /// Rules a Relax solve chose to break — excluding any the user had already
    /// given up, whose guards are pinned off and would otherwise read as fresh
    /// recommendations.
    /// </summary>
    public IReadOnlyList<RuleRef> NewlyRelaxedRules(Func<BoolVar, bool> valueOf) =>
        _guards.Where(g => !RelaxedRuleIds.Contains(g.Rule.RuleId) && !valueOf(g.Lit))
               .Select(g => g.Rule)
               .ToList();

    private int IndexOfDate(DateOnly date) => _dates.IndexOf(date);

    private int IndexOfEmployee(string id)
    {
        for (var i = 0; i < Scenario.Employees.Count; i++)
        {
            if (Scenario.Employees[i].Id == id) return i;
        }

        throw new KeyNotFoundException($"No employee '{id}'.");
    }
}
