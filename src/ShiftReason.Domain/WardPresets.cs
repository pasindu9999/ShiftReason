namespace ShiftReason.Domain;

/// <summary>
/// The seeded hospital-ward scenarios the demo runs on.
/// </summary>
/// <remarks>
/// These replace an employee-CRUD screen entirely. Generation is deterministic —
/// a fixed seed and stable ordering everywhere — because a scenario that differs
/// between runs would make reproducible solves meaningless.
/// </remarks>
public static class WardPresets
{
    public const string Icu = "ICU";
    public const string Paediatric = "Paediatric";
    public const string NightLead = "NightLead";

    public static readonly DateOnly DefaultStart = new(2026, 3, 2); // a Monday

    public static IReadOnlyList<ShiftType> Shifts { get; } =
    [
        ShiftType.Off,
        new(1, "EARLY", "Early", new TimeOnly(7, 0), new TimeOnly(15, 0), 8, false),
        new(2, "LATE", "Late", new TimeOnly(15, 0), new TimeOnly(23, 0), 8, false),
        new(3, "NIGHT", "Night", new TimeOnly(23, 0), new TimeOnly(7, 0), 8, true),
    ];

    public static IReadOnlyList<string> Ids { get; } = ["balanced-ward", "flu-season", "night-crunch"];

    public static Scenario ById(string id) => id switch
    {
        "balanced-ward" => BalancedWard(),
        "flu-season" => FluSeason(),
        "night-crunch" => NightCertificationCrunch(),
        _ => throw new KeyNotFoundException($"No preset '{id}'."),
    };

    /// <summary>
    /// Feasible, and deliberately big enough that solving it is visibly work.
    /// </summary>
    /// <remarks>
    /// 44 nurses over 28 days across 4 shift types is ~4,900 booleans. A toy ward
    /// solves in milliseconds, which leaves the live-streaming grid with nothing
    /// to show — the demo has to be a real optimisation problem to look like one.
    /// </remarks>
    public static Scenario BalancedWard()
    {
        var staff = GenerateStaff(44, seed: 20260302);
        var dates = Dates(DefaultStart, 28);

        // 19 nurse-shifts a day against ~151 shifts of weekly contract capacity:
        // about 88% utilisation. Tight enough that CP-SAT has to work for a good
        // roster, with enough slack that one exists. Demand above ~21/day makes
        // the ward infeasible outright — contract hours, not headcount, are the
        // binding constraint here.
        var demands = dates.SelectMany(d => new[]
        {
            Cover(d, "EARLY", 8, (Icu, 2), (Paediatric, 1)),
            Cover(d, "LATE", 7, (Icu, 2)),
            Cover(d, "NIGHT", 4, (Icu, 1), (NightLead, 1)),
        }).ToList();

        var leave = new List<LeaveRequest>
        {
            new(staff[3].Id, DefaultStart.AddDays(9), LeaveKind.Approved),
            new(staff[11].Id, DefaultStart.AddDays(10), LeaveKind.Approved),
            new(staff[20].Id, DefaultStart.AddDays(17), LeaveKind.Preferred),
        };

        return new Scenario(
            "balanced-ward", "Balanced ward",
            DefaultStart, 28,
            staff, Shifts, demands, leave, [], RuleSettings.Default);
    }

    /// <summary>
    /// Infeasible: demand rises just as the people who could meet it go off sick.
    /// </summary>
    public static Scenario FluSeason()
    {
        var staff = GenerateStaff(44, seed: 20260302);
        var dates = Dates(DefaultStart, 28);

        // Surge week: everything needs more bodies than the ward can field.
        var surge = dates.Skip(7).Take(7).ToHashSet();

        var demands = dates.SelectMany(d => surge.Contains(d)
            ? new[]
            {
                Cover(d, "EARLY", 14, (Icu, 4), (Paediatric, 2)),
                Cover(d, "LATE", 13, (Icu, 4)),
                Cover(d, "NIGHT", 9, (Icu, 3), (NightLead, 1)),
            }
            : new[]
            {
                Cover(d, "EARLY", 8, (Icu, 2), (Paediatric, 1)),
                Cover(d, "LATE", 7, (Icu, 2)),
                Cover(d, "NIGHT", 4, (Icu, 1), (NightLead, 1)),
            }).ToList();

        // ...and three of the ICU-certified nurses are signed off that same week.
        var icuStaff = staff.Where(s => s.Holds(Icu)).Take(3).ToList();
        var leave = icuStaff
            .SelectMany(s => surge.Select(d => new LeaveRequest(s.Id, d, LeaveKind.Approved)))
            .ToList();

        return new Scenario(
            "flu-season", "Flu season surge",
            DefaultStart, 28,
            staff, Shifts, demands, leave, [], RuleSettings.Default);
    }

    /// <summary>
    /// Small, sharp, and impossible for exactly one reason — the scenario the
    /// whole product exists to explain.
    /// </summary>
    /// <remarks>
    /// Twelve nurses, fourteen days, and only two people on the ward hold the
    /// night-lead certification. One of them has approved leave on the Friday;
    /// the other is pinned to the Thursday night shift, so mandated rest takes
    /// them out of Friday too. Friday night needs both. Kept deliberately tiny so
    /// the conflict set is checkable by hand.
    /// </remarks>
    public static Scenario NightCertificationCrunch()
    {
        var staff = GenerateStaff(12, seed: 4242);

        // Force exactly two night-lead holders: Ravi and Nadia. Everyone is
        // full-time here — the point of this preset is that ONE thing is wrong.
        // Leave the mixed part-time contracts in and the ward is also short of
        // hours overall, which buries the certification conflict under a pile of
        // unrelated contract-hour rules and makes the explanation useless.
        staff = staff.Select((s, i) => s with
        {
            Name = i switch { 0 => "Ravi Perera", 1 => "Nadia Haddad", _ => s.Name },
            ContractHoursPerWeek = 37.5,
            Skills = i switch
            {
                0 => new HashSet<string> { Icu, NightLead },
                1 => new HashSet<string> { Icu, NightLead },
                _ => s.Skills.Where(sk => sk != NightLead).ToHashSet(),
            },
        }).ToList();

        var dates = Dates(DefaultStart, 14);
        var friday = DefaultStart.AddDays(11);   // Fri 13 Mar 2026
        var thursday = friday.AddDays(-1);

        // 6 nurse-shifts a day against 12 full-time nurses (~96 shifts of capacity
        // over the fortnight, against 84 of demand). Comfortably staffed, so the
        // ONLY thing that makes this ward impossible is the Friday night
        // certification.
        var demands = dates.Select(d => d == friday
            ? Cover(d, "NIGHT", 2, (NightLead, 2))
            : Cover(d, "NIGHT", 2, (NightLead, 1))).ToList();

        demands.AddRange(dates.Select(d => Cover(d, "EARLY", 2)));
        demands.AddRange(dates.Select(d => Cover(d, "LATE", 2)));

        var leave = new List<LeaveRequest> { new(staff[0].Id, friday, LeaveKind.Approved) };
        var pins = new List<PinnedAssignment> { new(staff[1].Id, thursday, "NIGHT") };

        return new Scenario(
            "night-crunch", "Night certification crunch",
            DefaultStart, 14,
            staff, Shifts, demands, leave, pins, RuleSettings.Default);
    }

    // ------------------------------------------------------------------

    private static List<DateOnly> Dates(DateOnly start, int count) =>
        Enumerable.Range(0, count).Select(start.AddDays).ToList();

    private static Demand Cover(
        DateOnly date, string shift, int headcount, params (string Skill, int Min)[] skills) =>
        new(date, shift, headcount,
            skills.ToDictionary(t => t.Skill, t => t.Min, StringComparer.Ordinal));

    private static readonly string[] FirstNames =
    [
        "Nadia", "Ravi", "Mei", "Tomas", "Aisha", "Liam", "Priya", "Jonas",
        "Ines", "Kwame", "Sofia", "Haruto", "Elena", "Omar", "Grace", "Dmitri",
        "Yara", "Noah", "Lucia", "Kenji", "Amara", "Felix", "Zara", "Mateo",
    ];

    private static readonly string[] LastNames =
    [
        "Haddad", "Perera", "Lin", "Novak", "Okafor", "Byrne", "Sharma", "Weber",
        "Costa", "Mensah", "Rossi", "Tanaka", "Petrova", "Farouk", "Mwangi", "Volkov",
    ];

    /// <summary>Deterministic for a given seed — same staff, same order, every run.</summary>
    private static List<Employee> GenerateStaff(int count, int seed)
    {
        var rng = new Random(seed);
        var staff = new List<Employee>(count);

        for (var i = 0; i < count; i++)
        {
            var skills = new HashSet<string>(StringComparer.Ordinal);

            // Roughly 45% ICU, 25% paediatric, 30% night-lead — enough slack to
            // be solvable, little enough that the surge scenario really does break.
            if (rng.NextDouble() < 0.45) skills.Add(Icu);
            if (rng.NextDouble() < 0.25) skills.Add(Paediatric);
            if (rng.NextDouble() < 0.30) skills.Add(NightLead);

            // A mix of full-time and part-time contracts; part-timers are where
            // contract-hours conflicts actually bite.
            var contract = rng.NextDouble() switch
            {
                < 0.60 => 37.5,
                < 0.85 => 30.0,
                _ => 22.5,
            };

            staff.Add(new Employee(
                Id: $"n{i:D2}",
                Name: $"{FirstNames[i % FirstNames.Length]} {LastNames[(i * 7 + 3) % LastNames.Length]}",
                Skills: skills,
                ContractHoursPerWeek: contract,
                MaxConsecutiveWorkDays: 5));
        }

        return staff;
    }
}
