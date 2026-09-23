namespace ShiftReason.Solver;

/// <summary>
/// CP-SAT parameter strings.
/// </summary>
/// <remarks>
/// Parameters are a protobuf text-format string and nothing else: SatParameters
/// is proto2, so the SWIG layer deliberately does not expose
/// <c>SetParameters(SatParameters)</c> to C#. There is no <c>solver.NumWorkers</c>
/// property — only <c>StringParameters</c>.
/// </remarks>
public static class SolverParameters
{
    /// <summary>
    /// Worker count, taken from .NET rather than left to CP-SAT.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>num_workers:0</c> makes CP-SAT call <c>std::thread::hardware_concurrency()</c>,
    /// which reports <em>host</em> cores and ignores the cgroup CPU quota. In a
    /// container limited to one CPU on a 16-core host that spawns 16 workers to
    /// fight over one core. <see cref="Environment.ProcessorCount"/> is
    /// cgroup-aware, so we set the count ourselves.
    /// </para>
    /// <para>
    /// The floor of two is measured, not a guess. A single worker gets one
    /// full-problem subsolver and nothing else — no LNS, no feasibility jump — and
    /// on the 44-nurse ward it cannot find even a <em>first</em> roster inside ten
    /// seconds; eight tests fail. Two workers pass all of them with improvements
    /// spread across the whole solve. So the rule is "leave a core for Kestrel when
    /// there is one to spare", never at the cost of dropping to one worker: a
    /// 2-vCPU container or a 2-core CI runner runs both workers and lets the OS
    /// share the rest, because SignalR's traffic is tiny next to losing the solver.
    /// </para>
    /// <para>
    /// <c>SHIFTREASON_SOLVER_WORKERS</c> overrides all of this, for tuning a
    /// deployment without a rebuild. The explanation solve is unaffected: CP-SAT
    /// only honours assumptions with exactly one worker.
    /// </para>
    /// </remarks>
    public static int Workers { get; } = ResolveWorkers();

    internal static int ResolveWorkers(string? configured = null, int? processorCount = null)
    {
        configured ??= Environment.GetEnvironmentVariable("SHIFTREASON_SOLVER_WORKERS");
        if (int.TryParse(configured, out var n) && n > 0) return n;

        var cores = processorCount ?? Environment.ProcessorCount;
        return Math.Max(2, cores - 1);
    }

    private const string Quiet = "log_search_progress:true,log_to_stdout:false";

    /// <summary>Wall-clock limited, multi-worker. Fast, and NOT reproducible.</summary>
    public static string Fast(double seconds) =>
        $"num_workers:{Workers},max_time_in_seconds:{seconds},{Quiet}";

    /// <summary>
    /// Reproducible: identical input plus identical seed yields an identical roster.
    /// </summary>
    /// <remarks>
    /// Every part of this is load-bearing.
    /// <list type="bullet">
    /// <item><c>interleave_search:true</c> selects <c>DeterministicLoop</c>. The
    /// default (false) runs <c>NonDeterministicLoop</c>, where workers race and
    /// share clauses asynchronously — a fixed seed does not save you.</item>
    /// <item><c>interleave_batch_size</c> is pinned because it otherwise defaults
    /// to <c>num_workers * 3</c>, so the schedule silently shifts when the worker
    /// count changes.</item>
    /// <item><c>max_deterministic_time</c> instead of <c>max_time_in_seconds</c>:
    /// a wall-clock cut-off lands on a different node on a different machine.</item>
    /// </list>
    /// This is marked experimental upstream and is generally slower than the
    /// default portfolio, which is why it is an explicit per-run mode rather
    /// than the default. Determinism and peak throughput are not both available.
    /// </remarks>
    public static string Reproducible(int seed, double deterministicTime) =>
        $"num_workers:{Workers},interleave_search:true,interleave_batch_size:{Workers * 3}," +
        $"random_seed:{seed},max_deterministic_time:{deterministicTime},{Quiet}";

    /// <summary>
    /// The only configuration in which an infeasibility core is meaningful.
    /// </summary>
    /// <remarks>
    /// <c>SufficientAssumptionsForInfeasibility()</c> silently degrades to
    /// "return every assumption" if <c>num_workers &gt; 1</c>, or the model has an
    /// objective, or <c>enumerate_all_solutions</c>, or <c>interleave_search</c>.
    /// There is no status code for that — the only signal is a line in the solver
    /// log, which is why <see cref="ConflictExplainer"/> watches for it.
    /// </remarks>
    public static string Explain(double seconds) =>
        $"num_workers:1,interleave_search:false,max_time_in_seconds:{seconds},{Quiet}";
}
