namespace ShiftReason.Domain;

/// <summary>
/// A shift a nurse can be assigned to on a given day.
/// </summary>
/// <remarks>
/// Index 0 is always <c>OFF</c>. That is deliberate and load-bearing: it turns
/// "exactly one shift per nurse per day" into a definitional identity
/// (<c>Sum(x[e,d,*]) == 1</c>) rather than a rule anyone could relax. See
/// <see cref="RuleClass.Structural"/>.
/// </remarks>
public sealed record ShiftType(
    int Index,
    string Id,
    string Name,
    TimeOnly Start,
    TimeOnly End,
    double Hours,
    bool IsNight)
{
    public bool IsOff => Index == 0;

    public static ShiftType Off { get; } =
        new(0, "OFF", "Off", new TimeOnly(0, 0), new TimeOnly(0, 0), 0, false);
}

public sealed record Employee(
    string Id,
    string Name,
    IReadOnlySet<string> Skills,
    double ContractHoursPerWeek,
    int MaxConsecutiveWorkDays)
{
    public bool Holds(string skill) => Skills.Contains(skill);
}

/// <summary>Headcount required on one day for one shift, optionally by skill.</summary>
public sealed record Demand(
    DateOnly Date,
    string ShiftTypeId,
    int RequiredHeadcount,
    IReadOnlyDictionary<string, int> SkillMinimums)
{
    public static Demand Of(DateOnly date, string shiftTypeId, int headcount) =>
        new(date, shiftTypeId, headcount, new Dictionary<string, int>());
}

public enum LeaveKind
{
    /// <summary>Signed off. Modelled hard (but still relaxable as a policy rule).</summary>
    Approved,

    /// <summary>A wish. Modelled soft, as an objective penalty.</summary>
    Preferred,
}

public sealed record LeaveRequest(string EmployeeId, DateOnly Date, LeaveKind Kind);

/// <summary>A cell the user has locked from the grid.</summary>
public sealed record PinnedAssignment(
    string EmployeeId,
    DateOnly Date,
    string ShiftTypeId,
    bool Forbidden = false);

/// <summary>
/// Penalty weights for the soft terms, and the relaxation costs that drive the
/// "cheapest thing to give up" answer. These are business inputs, not solver
/// tuning: the MCS is only as meaningful as these numbers are.
/// </summary>
public sealed record RuleSettings(
    long CoverageRelaxCost = 100,
    long SkillMinimumRelaxCost = 120,
    long ApprovedLeaveRelaxCost = 60,
    long MinRestRelaxCost = 90,
    long MaxConsecutiveRelaxCost = 70,
    long ContractHoursRelaxCost = 50,
    long PinnedRelaxCost = 30,
    int PreferredLeaveViolationPenalty = 8,
    int NightFairnessWeight = 25,
    int WeekendFairnessWeight = 20,
    int SplitWeekendPenalty = 6)
{
    public static RuleSettings Default { get; } = new();
}

public sealed record Scenario(
    string Id,
    string Name,
    DateOnly StartDate,
    int HorizonDays,
    IReadOnlyList<Employee> Employees,
    IReadOnlyList<ShiftType> ShiftTypes,
    IReadOnlyList<Demand> Demands,
    IReadOnlyList<LeaveRequest> Leave,
    IReadOnlyList<PinnedAssignment> Pins,
    RuleSettings Settings)
{
    public IEnumerable<DateOnly> Dates =>
        Enumerable.Range(0, HorizonDays).Select(StartDate.AddDays);

    public ShiftType ShiftById(string id) =>
        ShiftTypes.FirstOrDefault(s => s.Id == id)
        ?? throw new KeyNotFoundException($"No shift type '{id}'.");

    public Employee EmployeeById(string id) =>
        Employees.FirstOrDefault(e => e.Id == id)
        ?? throw new KeyNotFoundException($"No employee '{id}'.");

    /// <summary>Working shifts only — everything except OFF.</summary>
    public IEnumerable<ShiftType> WorkingShifts => ShiftTypes.Where(s => !s.IsOff);
}
