import type { RunState } from '../lib/runReducer'
import type { PenaltyLine } from '../lib/types'

interface Props {
  state: RunState
  /** The run this one was compared against, when it came from relaxing a rule. */
  baseline?: { label: string; penalties: PenaltyLine[]; objective: number | null } | null
}

/**
 * What a *feasible* roster cost.
 *
 * "No feasible solution" is not the only unhelpful answer a rostering tool gives —
 * so is a schedule with no account of what it traded away. Every soft term is
 * reported separately rather than collapsed into one objective number.
 */
export function PenaltyPanel({ state, baseline }: Props) {
  if (state.penalties.length === 0) return null

  const total = state.penalties.reduce((sum, l) => sum + l.cost, 0)
  const before = baseline ? new Map(baseline.penalties.map((l) => [l.key, l.cost])) : null

  return (
    <section className="rounded-lg border border-black/10 dark:border-white/10 bg-white/70 dark:bg-white/5 p-3">
      <div className="flex items-baseline gap-2">
        <h2 className="text-sm font-semibold">What this roster cost</h2>
        {baseline && (
          <span className="text-[11px] opacity-60">compared with {baseline.label}</span>
        )}
        <span className="ml-auto text-sm font-bold tabular-nums">{total.toLocaleString()}</span>
      </div>

      <table className="mt-2 w-full text-xs">
        <tbody>
          {state.penalties.map((line) => {
            const previous = before?.get(line.key)
            const delta = previous === undefined ? null : line.cost - previous
            return (
              <tr key={line.key} className="border-t border-black/5 dark:border-white/5">
                <td className="py-1 pr-2">{line.label}</td>
                <td className="py-1 pr-2 text-right tabular-nums opacity-60 whitespace-nowrap">
                  {line.units} {line.unitNoun} × {line.weight}
                </td>
                <td className="py-1 text-right font-semibold tabular-nums">
                  {line.cost.toLocaleString()}
                </td>
                {delta !== null && (
                  <td
                    className={`py-1 pl-2 text-right tabular-nums w-14 ${
                      delta < 0
                        ? 'text-emerald-600 dark:text-emerald-400'
                        : delta > 0
                          ? 'text-rose-600 dark:text-rose-400'
                          : 'opacity-40'
                    }`}
                  >
                    {delta === 0 ? '—' : `${delta > 0 ? '+' : ''}${delta}`}
                  </td>
                )}
              </tr>
            )
          })}
        </tbody>
      </table>

      {state.bestBound !== null && state.objective !== null && (
        <BoundNote objective={state.objective} bound={state.bestBound} />
      )}
    </section>
  )
}

/**
 * What the lower bound says about this roster.
 *
 * Worded from the numbers rather than fixed text: small wards are routinely
 * proven optimal, and a note explaining why "the gap is wide" beside a bound that
 * equals the objective would be telling the reader the opposite of the truth.
 */
function BoundNote({ objective, bound }: { objective: number; bound: number }) {
  // The objective is integral, so anything under one unit of gap is closed.
  const gap = objective - bound
  if (gap < 0.5) {
    return (
      <p className="mt-2 text-[11px] opacity-55">
        Proven optimal — CP-SAT closed the gap, so no roster with a lower cost exists
        under these rules.
      </p>
    )
  }

  const percent = objective > 0 ? Math.round((gap / objective) * 100) : 100
  return (
    <p className="mt-2 text-[11px] opacity-55">
      Best proven lower bound {bound.toLocaleString()} ({percent}% gap). A wide gap is
      expected here: min-max fairness has a weak linear relaxation, so CP-SAT finds
      good rosters long before it can prove one optimal.
    </p>
  )
}
