namespace ShiftReason.Domain;

/// <summary>
/// Whether a constraint defines what a roster *is*, or expresses a policy
/// someone chose and could therefore choose differently.
/// </summary>
/// <remarks>
/// This split does two jobs.
///
/// 1. It keeps the "cheapest fix" honest. Only <see cref="Policy"/> rules get a
///    relaxation literal, so the MCS can never propose giving up something like
///    "a nurse works one shift a day" — which would be nonsense, and would
///    destroy any trust in the rest of the output.
///
/// 2. It routes constraints away from a latent CP-SAT process abort. CP-SAT's
///    loader still contains <c>CHECK(!HasEnforcementLiteral(ct))</c> for
///    <c>exactly_one</c> and <c>at_most_one</c>; reaching it kills the process
///    with no managed exception. Structural constraints are the ones that want
///    those forms, and they are exactly the ones that never carry a literal.
/// </remarks>
public enum RuleClass
{
    /// <summary>Never guarded, never relaxable. Defines a well-formed roster.</summary>
    Structural,

    /// <summary>User-visible, carries an assumption literal, can be relaxed.</summary>
    Policy,
}

public enum RuleKind
{
    OneShiftPerDay,
    Coverage,
    SkillMinimum,
    ApprovedLeave,
    MinRest,
    MaxConsecutive,
    ContractHours,
    Pinned,
    Forbidden,
}

public enum Hardness
{
    Hard,
    Soft,
}

/// <summary>A pointer back into the scenario, so the UI can highlight the cells a rule touches.</summary>
public readonly record struct EntityRef(string Kind, string Id)
{
    public static EntityRef Employee(string id) => new("employee", id);
    public static EntityRef Date(DateOnly d) => new("date", d.ToString("yyyy-MM-dd"));
    public static EntityRef Shift(string id) => new("shift", id);
    public static EntityRef Skill(string id) => new("skill", id);
}

/// <summary>
/// One user-visible rule, built <em>before</em> any CP-SAT object exists.
/// </summary>
/// <remarks>
/// <see cref="Sentence"/> is authored here, at rule-creation time, rather than
/// reverse-engineered from solver output later. That is the whole trick behind
/// plain-English explanations: by the time CP-SAT hands back a conflict core,
/// turning it into prose is a dictionary lookup.
/// </remarks>
public sealed record RuleRef(
    string RuleId,
    RuleKind Kind,
    RuleClass Class,
    Hardness Hardness,
    long RelaxCost,
    string Sentence,
    IReadOnlyList<EntityRef> Refs)
{
    public bool IsGuardable => Class is RuleClass.Policy;

    public override string ToString() => $"{RuleId}: {Sentence}";
}

/// <summary>
/// Stable, human-readable rule identifiers. These end up in test assertions and
/// in the API payload, so they are treated as a contract rather than as debug
/// strings.
/// </summary>
public static class RuleIds
{
    public static string Coverage(DateOnly d, string shiftId) =>
        $"coverage:{d:yyyy-MM-dd}:{shiftId}";

    public static string SkillMinimum(DateOnly d, string shiftId, string skill) =>
        $"skill:{d:yyyy-MM-dd}:{shiftId}:{skill}";

    public static string ApprovedLeave(string employeeId, DateOnly d) =>
        $"leave:{employeeId}:{d:yyyy-MM-dd}";

    public static string MinRest(string employeeId, DateOnly d) =>
        $"minrest:{employeeId}:{d:yyyy-MM-dd}";

    public static string MaxConsecutive(string employeeId, DateOnly windowStart) =>
        $"maxconsec:{employeeId}:{windowStart:yyyy-MM-dd}";

    public static string ContractHours(string employeeId, int weekIndex) =>
        $"hours:{employeeId}:w{weekIndex}";

    public static string Pinned(string employeeId, DateOnly d) =>
        $"pinned:{employeeId}:{d:yyyy-MM-dd}";

    public static string Forbidden(string employeeId, DateOnly d, string shiftId) =>
        $"forbidden:{employeeId}:{d:yyyy-MM-dd}:{shiftId}";
}
