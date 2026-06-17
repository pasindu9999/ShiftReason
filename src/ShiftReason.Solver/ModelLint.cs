using Google.OrTools.Sat;

namespace ShiftReason.Solver;

/// <summary>Raised when a built model would be unsafe to hand to CP-SAT.</summary>
public sealed class ModelLintException(string message) : Exception(message);

/// <summary>
/// Structural checks on the emitted <c>CpModelProto</c>, run before every solve.
/// </summary>
public static class ModelLint
{
    /// <summary>
    /// Fails if any <c>exactly_one</c> or <c>at_most_one</c> carries an
    /// enforcement literal.
    /// </summary>
    /// <remarks>
    /// CP-SAT's loader contains, in both <c>LoadExactlyOneConstraint</c> and
    /// <c>LoadAtMostOneConstraint</c> (still present in v9.15):
    /// <code>CHECK(!HasEnforcementLiteral(ct)) &lt;&lt; "Not supported.";</code>
    /// <c>CHECK</c> is live in release builds, so reaching it terminates the
    /// process — exit 134, no managed exception, no stack trace — and
    /// <c>CpModel.Validate()</c> returns clean beforehand.
    ///
    /// Measured on 9.15.6755, a small model does <em>not</em> abort: presolve
    /// folds away an enforcement literal it can prove always-true before the
    /// loader sees it (still true with <c>cp_model_presolve:false</c>, and true
    /// when the guard is an assumption, since assumptions are fixed at the root).
    ///
    /// Latent and data-dependent is worse than a reliable crash. On a full-size
    /// roster where presolve cannot decide a guard, the constraint reaches the
    /// loader and the process dies — on the biggest scenario, in front of an
    /// audience. So we never rely on presolve rescuing us: the fix is to write
    /// guarded set constraints in linear form, <c>Sum(...) == 1</c>, and to fail
    /// the build here if anyone forgets.
    /// </remarks>
    public static void AssertNoGuardedSetConstraints(CpModel model)
    {
        var offenders = new List<string>();

        for (var i = 0; i < model.Model.Constraints.Count; i++)
        {
            var ct = model.Model.Constraints[i];
            if (ct.EnforcementLiteral.Count == 0) continue;

            if (ct.ConstraintCase is ConstraintProto.ConstraintOneofCase.ExactlyOne
                                  or ConstraintProto.ConstraintOneofCase.AtMostOne)
            {
                offenders.Add($"constraint #{i} ({ct.ConstraintCase})");
            }
        }

        if (offenders.Count > 0)
        {
            throw new ModelLintException(
                "Enforcement literal on a set constraint CP-SAT's loader rejects with a " +
                "release-build CHECK; this would abort the process rather than throw. " +
                "Rewrite as a linear constraint, e.g. Add(LinearExpr.Sum(lits) == 1). " +
                $"Offenders: {string.Join(", ", offenders)}.");
        }
    }
}
