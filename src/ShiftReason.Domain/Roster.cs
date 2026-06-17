namespace ShiftReason.Domain;

public sealed record Assignment(string EmployeeId, DateOnly Date, string ShiftTypeId);

/// <summary>
/// A complete roster: exactly one shift (possibly OFF) per nurse per day.
/// </summary>
public sealed class Roster
{
    private readonly Dictionary<(string, DateOnly), string> _byCell;

    public Roster(IReadOnlyList<Assignment> assignments)
    {
        Assignments = assignments;
        _byCell = assignments.ToDictionary(a => (a.EmployeeId, a.Date), a => a.ShiftTypeId);
    }

    public IReadOnlyList<Assignment> Assignments { get; }

    /// <summary>The shift assigned to <paramref name="employeeId"/> on <paramref name="date"/>, or OFF.</summary>
    public string ShiftOn(string employeeId, DateOnly date) =>
        _byCell.TryGetValue((employeeId, date), out var s) ? s : ShiftType.Off.Id;

    public bool IsWorking(string employeeId, DateOnly date) =>
        ShiftOn(employeeId, date) != ShiftType.Off.Id;

    public IEnumerable<string> StaffOn(DateOnly date, string shiftTypeId) =>
        Assignments.Where(a => a.Date == date && a.ShiftTypeId == shiftTypeId)
                   .Select(a => a.EmployeeId);

    public int HeadcountOn(DateOnly date, string shiftTypeId) =>
        StaffOn(date, shiftTypeId).Count();
}

/// <summary>A hard constraint that the produced roster actually breaks.</summary>
public sealed record RosterViolation(string RuleId, RuleKind Kind, string Detail)
{
    public override string ToString() => $"[{Kind}] {RuleId} — {Detail}";
}
