using Google.OrTools.Sat;

// Phase 0 spike. Retires the platform risks before any architecture exists.
//
//   (default)        run checks 1-3
//   --prove-abort    run check 4, which is EXPECTED to abort the process (exit 134).
//                    Kept opt-in precisely because it kills the runtime.

var proveAbort = args.Contains("--prove-abort");

Console.WriteLine($"RuntimeInformation : {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}");
Console.WriteLine($"ProcessorCount     : {Environment.ProcessorCount}  (cgroup-aware, unlike CP-SAT's num_workers:0)");
Console.WriteLine();

if (proveAbort)
{
    ProveExactlyOneAborts();
    return 0;
}

var ok = true;
ok &= Check1_NativeLoads();
ok &= Check2_AssumptionCoreIsStrictSubset();
ok &= Check3_EnforcementOnLinearFormIsSafe();
ok &= Check4_ProtoLintCatchesGuardedExactlyOne();

Console.WriteLine();
Console.WriteLine(ok ? "PHASE 0: ALL CHECKS PASSED" : "PHASE 0: FAILURES ABOVE");
return ok ? 0 : 1;


// ---------------------------------------------------------------------------
// Check 1 — the native .so/.dll actually loads and solves on this platform.
// ---------------------------------------------------------------------------
static bool Check1_NativeLoads()
{
    Console.WriteLine("── Check 1: native runtime loads and solves ──");

    var model = new CpModel();
    var x = model.NewIntVar(0, 10, "x");
    var y = model.NewIntVar(0, 10, "y");
    model.Add(x + y == 10);
    model.Add(x - y >= 4);
    model.Maximize(x);

    var solver = new CpSolver { StringParameters = "num_workers:1,max_time_in_seconds:5" };
    var status = solver.Solve(model);

    // NOTE: on CpSolver these are PROPERTIES. On the solution callback they are METHODS.
    Console.WriteLine($"  status={status} x={solver.Value(x)} y={solver.Value(y)} obj={solver.ObjectiveValue}");

    // x+y==10 and x-y>=4 over [0,10] => maximum x is 10 (y=0).
    var pass = status is CpSolverStatus.Optimal
               && solver.Value(x) == 10
               && solver.Value(y) == 0;
    Console.WriteLine(pass ? "  PASS" : "  FAIL");
    return pass;
}


// ---------------------------------------------------------------------------
// Check 2 — the whole product rests on this.
//
// SufficientAssumptionsForInfeasibility() silently degrades to "return ALL
// assumptions" if num_workers > 1, or the model has an objective, or
// enumerate_all_solutions, or interleave_search. There is no status code for
// it — the only signal is a log line. So: assert a STRICT SUBSET, and keep
// this assertion as a permanent regression test.
// ---------------------------------------------------------------------------
static bool Check2_AssumptionCoreIsStrictSubset()
{
    Console.WriteLine("── Check 2: assumption core is a strict subset ──");

    var model = new CpModel();
    var a = model.NewBoolVar("a");

    // Three guarded policy rules. Rules 1 and 2 alone are contradictory;
    // rule 3 is irrelevant and must NOT appear in a minimized core.
    var g1 = model.NewBoolVar("rule::a_must_be_true");
    var g2 = model.NewBoolVar("rule::a_must_be_false");
    var g3 = model.NewBoolVar("rule::irrelevant");

    model.Add(a == 1).OnlyEnforceIf(g1);
    model.Add(a == 0).OnlyEnforceIf(g2);
    var spare = model.NewIntVar(0, 5, "spare");
    model.Add(spare <= 5).OnlyEnforceIf(g3);

    model.AddAssumptions([g1, g2, g3]);

    var byIndex = new Dictionary<int, string>
    {
        [g1.GetIndex()] = "a_must_be_true",
        [g2.GetIndex()] = "a_must_be_false",
        [g3.GetIndex()] = "irrelevant",
    };

    var degraded = false;
    var solver = new CpSolver
    {
        // interleave_search MUST be false and there MUST be no objective.
        // log_to_stdout:false makes SetLogCallback the only sink, which is what
        // the real app wants — CP-SAT's progress log is far too chatty otherwise.
        StringParameters = "num_workers:1,interleave_search:false,max_time_in_seconds:10," +
                           "log_search_progress:true,log_to_stdout:false"
    };
    solver.SetLogCallback(line =>
    {
        if (line.Contains("non-fully supported setting", StringComparison.OrdinalIgnoreCase))
        {
            degraded = true;
            Console.WriteLine($"  !! DEGRADED: {line.Trim()}");
        }
    });

    var status = solver.Solve(model);
    Console.WriteLine($"  status={status}");

    if (status is not CpSolverStatus.Infeasible)
    {
        Console.WriteLine("  FAIL — fixture was supposed to be infeasible");
        return false;
    }

    // Signed proto literal refs in the ORIGINAL model's index space;
    // presolve mapping is already undone for us.
    var core = solver.SufficientAssumptionsForInfeasibility()
                     .Select(i => byIndex.TryGetValue(i, out var r) ? r : $"<neg or unknown:{i}>")
                     .ToList();

    Console.WriteLine($"  core = [{string.Join(", ", core)}]  (count={core.Count} of 3)");

    var pass = !degraded && core.Count < 3 && !core.Contains("irrelevant");
    Console.WriteLine(pass ? "  PASS" : "  FAIL — core was not a strict, relevant subset");
    return pass;
}


// ---------------------------------------------------------------------------
// Check 3 — the linear form Sum(...) == 1 accepts an enforcement literal.
// This is the safe replacement for AddExactlyOne on any *guarded* constraint.
// ---------------------------------------------------------------------------
static bool Check3_EnforcementOnLinearFormIsSafe()
{
    Console.WriteLine("── Check 3: guarded Sum(...) == 1 is safe ──");

    var model = new CpModel();
    var lits = Enumerable.Range(0, 4).Select(i => model.NewBoolVar($"x{i}")).ToArray();
    var guard = model.NewBoolVar("rule::one_shift_per_day");

    // The linear form is guardable. AddExactlyOne(lits).OnlyEnforceIf(guard) is NOT:
    // cp_model_loader.cc / cp_model_presolve.cc CHECK(!HasEnforcementLiteral(ct)),
    // and CHECK is live in release builds -> process abort, exit 134.
    model.Add(Google.OrTools.Sat.LinearExpr.Sum(lits) == 1).OnlyEnforceIf(guard);
    model.Add(guard == 1);

    var solver = new CpSolver { StringParameters = "num_workers:1,max_time_in_seconds:5" };
    var status = solver.Solve(model);
    var total = lits.Sum(l => solver.Value(l));

    Console.WriteLine($"  status={status} sum={total}");
    var pass = status is CpSolverStatus.Optimal or CpSolverStatus.Feasible && total == 1;
    Console.WriteLine(pass ? "  PASS (survived, no abort)" : "  FAIL");
    return pass;
}


// ---------------------------------------------------------------------------
// Check 4 — the permanent safety net.
//
// v9.15's cp_model_loader.cc still contains, in BOTH LoadExactlyOneConstraint
// and LoadAtMostOneConstraint:
//
//     CHECK(!HasEnforcementLiteral(ct)) << "Not supported.";
//
// CHECK is live in release builds, so reaching it kills the process outright —
// exit 134, no managed exception, no stack trace, and CpModel.Validate() returns
// clean. Measured on 9.15.6755, a toy model does NOT abort: presolve folds away
// an enforcement literal it can prove is always true before the loader sees it
// (true even with cp_model_presolve:false, and true when the guard is an
// assumption, since assumptions are fixed at the root).
//
// That makes this hazard LATENT and DATA-DEPENDENT, which is worse than a
// reliable crash: on a real roster where presolve cannot decide a guard, the
// enforced constraint survives to the loader and the process dies — on a big
// scenario, in front of an audience.
//
// So we never rely on presolve rescuing us. We lint the proto instead. This
// function moves into ShiftReason.Solver and runs on every built model.
// ---------------------------------------------------------------------------
static bool Check4_ProtoLintCatchesGuardedExactlyOne()
{
    Console.WriteLine("── Check 4: proto lint rejects guarded exactly_one/at_most_one ──");

    // A model that WOULD be a landmine.
    var bad = new CpModel();
    var lits = Enumerable.Range(0, 4).Select(i => bad.NewBoolVar($"x{i}")).ToArray();
    bad.AddExactlyOne(lits).OnlyEnforceIf(bad.NewBoolVar("rule::guarded"));
    var badViolations = LintGuardedSetConstraints(bad);

    // The safe linear rewrite of the same rule.
    var good = new CpModel();
    var glits = Enumerable.Range(0, 4).Select(i => good.NewBoolVar($"x{i}")).ToArray();
    good.Add(LinearExpr.Sum(glits) == 1).OnlyEnforceIf(good.NewBoolVar("rule::guarded"));
    var goodViolations = LintGuardedSetConstraints(good);

    Console.WriteLine($"  AddExactlyOne + OnlyEnforceIf -> {badViolations} violation(s)  (want 1)");
    Console.WriteLine($"  Sum(...) == 1 + OnlyEnforceIf -> {goodViolations} violation(s)  (want 0)");

    var pass = badViolations == 1 && goodViolations == 0;
    Console.WriteLine(pass ? "  PASS" : "  FAIL");
    return pass;
}

/// Counts constraints carrying an enforcement literal on a set type the CP-SAT
/// loader refuses. Any hit is a latent process abort — fix the model, not this.
static int LintGuardedSetConstraints(CpModel model)
{
    var n = 0;
    foreach (var ct in model.Model.Constraints)
    {
        if (ct.EnforcementLiteral.Count == 0) continue;
        if (ct.ConstraintCase is ConstraintProto.ConstraintOneofCase.ExactlyOne
                              or ConstraintProto.ConstraintOneofCase.AtMostOne)
        {
            n++;
        }
    }
    return n;
}


// ---------------------------------------------------------------------------
// Opt-in experiment that produced the finding documented on Check 4.
// Left in place so the conclusion can be re-derived rather than trusted.
// ---------------------------------------------------------------------------
static void ProveExactlyOneAborts()
{
    Console.WriteLine("── Check 4: AddExactlyOne + OnlyEnforceIf (EXPECTED TO ABORT) ──");

    var model = new CpModel();
    var lits = Enumerable.Range(0, 4).Select(i => model.NewBoolVar($"x{i}")).ToArray();
    var guard = model.NewBoolVar("rule::guarded_exactly_one");

    model.AddExactlyOne(lits).OnlyEnforceIf(guard);

    // Do NOT fix the guard. Add(guard == 1) lets presolve fold an always-true
    // enforcement literal away before the loader ever sees it, which masks the
    // CHECK. The dangerous path is Explain mode: the guard stays free, it goes
    // in as an assumption, and CP-SAT force-sets
    // keep_all_feasible_solutions_in_presolve=true — so presolve is barred from
    // the very fixing that would have saved us.
    model.AddAssumptions([guard]);

    Console.WriteLine($"  Validate() says: '{model.Validate()}'  <- note it does NOT catch this");
    Console.WriteLine("  --- emitted proto ---");
    Console.WriteLine(model.Model.ToString());
    Console.WriteLine("  --- end proto ---");
    Console.WriteLine("  calling Solve() with a free, assumed guard ...");
    Console.Out.Flush();

    // cp_model_presolve:false takes presolve out of the picture, so the raw
    // enforced exactly_one reaches LoadExactlyOneConstraint unmodified. If the
    // CHECK is still live in 9.15, this is where it fires.
    var noPresolve = Environment.GetEnvironmentVariable("SPIKE_NO_PRESOLVE") == "1";
    var p = "num_workers:1,interleave_search:false,max_time_in_seconds:5"
            + (noPresolve ? ",cp_model_presolve:false" : "");
    Console.WriteLine($"  params: {p}");
    Console.Out.Flush();

    var solver = new CpSolver { StringParameters = p };
    var status = solver.Solve(model);

    Console.WriteLine($"  reached this line, status={status} — the abort did NOT reproduce");
}
