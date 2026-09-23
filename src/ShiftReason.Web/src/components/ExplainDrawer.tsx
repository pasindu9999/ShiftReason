import { useState } from 'react'
import type { RunState } from '../lib/runReducer'
import type { RuleDto } from '../lib/types'

interface Props {
  state: RunState
  onHighlight: (refs: RuleDto['refs'] | null) => void
  onRelax: (ruleIds: string[], label: string) => void
  busy: boolean
  /**
   * Whether "give this up" can actually be carried out. Always true against the
   * live solver; on the static demo only when that exact relaxation was recorded.
   */
  canRelax?: (ruleIds: string[]) => boolean
}

/**
 * The half of the product that does not exist anywhere else: not "no feasible
 * solution", but which rules collide and what it costs to give each of them up.
 */
export function ExplainDrawer({ state, onHighlight, onRelax, busy, canRelax = () => true }: Props) {
  const [tab, setTab] = useState<'why' | 'fixes'>('why')
  const explanation = state.explanation

  if (state.status !== 'infeasible' && !explanation) return null

  if (!explanation) {
    return (
      <Panel>
        <p className="text-sm opacity-70">
          No roster exists for this ward. Working out which rules collide…
        </p>
      </Panel>
    )
  }

  if (explanation.timedOut) {
    return (
      <Panel>
        <h2 className="text-sm font-semibold text-rose-600 dark:text-rose-400">
          No roster exists for this ward
        </h2>
        <p className="mt-1 text-sm opacity-70">
          Working out <em>which</em> rules collide ran out of time. Proving
          infeasibility is much harder than finding it: CP-SAT forces a single worker
          and weakens presolve whenever assumptions are in play, so a ward it rejected
          in under a second can still take minutes to pin the blame on. Try a smaller
          ward for the explanation.
        </p>
      </Panel>
    )
  }

  if (explanation.structurallyInfeasible) {
    return (
      <Panel>
        <h2 className="text-sm font-semibold text-rose-600 dark:text-rose-400">
          Impossible regardless of policy
        </h2>
        <p className="mt-1 text-sm opacity-70">
          No combination of the ward's rules can be relaxed to make this solvable —
          the conflict is in the shape of the roster itself.
        </p>
      </Panel>
    )
  }

  return (
    <Panel>
      <div className="flex items-center gap-1 border-b border-black/10 dark:border-white/10 pb-2">
        <Tab active={tab === 'why'} onClick={() => setTab('why')}>
          Why it's impossible
          <Count n={explanation.conflictSet.length} />
        </Tab>
        <Tab active={tab === 'fixes'} onClick={() => setTab('fixes')}>
          Cheapest fixes
          <Count n={explanation.fixes.length} />
        </Tab>
        <span className="ml-auto text-[11px] opacity-50 tabular-nums">
          {explanation.probes} probe{explanation.probes === 1 ? '' : 's'} ·{' '}
          {explanation.seconds.toFixed(2)}s
          {explanation.minimised ? ' · minimal' : ' · not fully minimised'}
        </span>
      </div>

      {tab === 'why' ? (
        <div className="pt-3">
          <p className="mb-2 text-xs opacity-60">
            These rules cannot all hold at once. Remove any one of them and the rest
            are satisfiable — that is what makes the set minimal.
          </p>
          <ol className="space-y-1.5">
            {explanation.conflictSet.map((rule, i) => (
              <li
                key={rule.ruleId}
                onMouseEnter={() => onHighlight(rule.refs)}
                onMouseLeave={() => onHighlight(null)}
                className="flex gap-2 rounded-md px-2 py-1.5 text-sm hover:bg-rose-500/10"
              >
                <span className="mt-0.5 grid h-5 w-5 shrink-0 place-items-center rounded-full bg-rose-500/15 text-[11px] font-bold text-rose-600 dark:text-rose-300">
                  {i + 1}
                </span>
                <span>{rule.sentence}</span>
              </li>
            ))}
          </ol>
        </div>
      ) : (
        <div className="space-y-2 pt-3">
          <p className="mb-2 text-xs opacity-60">
            Each option is a set of rules that, given up together, makes the ward
            solvable. Ranked by what it costs the ward, cheapest first.
          </p>
          {explanation.fixes.map((fix) => (
            <div
              key={fix.rank}
              className="rounded-md border border-black/10 dark:border-white/10 p-2"
            >
              <div className="flex items-center gap-2">
                <span className="text-xs font-semibold opacity-60">Option {fix.rank}</span>
                <span className="rounded-full bg-amber-500/15 px-2 py-0.5 text-[11px] font-semibold text-amber-700 dark:text-amber-300">
                  cost {fix.totalCost}
                </span>
                <button
                  disabled={busy || !canRelax(fix.rules.map((r) => r.ruleId))}
                  title={
                    canRelax(fix.rules.map((r) => r.ruleId))
                      ? undefined
                      : 'Only the cheapest option was recorded for the static demo — try the others on the live solver'
                  }
                  onClick={() =>
                    onRelax(
                      fix.rules.map((r) => r.ruleId),
                      `option ${fix.rank}`,
                    )
                  }
                  className="ml-auto rounded-md bg-emerald-600 px-2.5 py-1 text-xs font-semibold text-white hover:bg-emerald-700 disabled:opacity-40"
                >
                  Give this up &amp; re-solve
                </button>
              </div>
              <ul className="mt-1.5 space-y-1">
                {fix.rules.map((rule) => (
                  <li
                    key={rule.ruleId}
                    onMouseEnter={() => onHighlight(rule.refs)}
                    onMouseLeave={() => onHighlight(null)}
                    className="rounded px-1.5 py-0.5 text-xs opacity-80 hover:bg-amber-500/10"
                  >
                    {rule.sentence}
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </div>
      )}
    </Panel>
  )
}

function Panel({ children }: { children: React.ReactNode }) {
  return (
    <section className="rounded-lg border border-rose-500/30 bg-rose-500/5 p-3">{children}</section>
  )
}

function Tab({
  active,
  onClick,
  children,
}: {
  active: boolean
  onClick: () => void
  children: React.ReactNode
}) {
  return (
    <button
      onClick={onClick}
      className={`flex items-center gap-1.5 rounded-md px-2.5 py-1 text-sm font-semibold ${
        active ? 'bg-black/5 dark:bg-white/10' : 'opacity-60 hover:opacity-100'
      }`}
    >
      {children}
    </button>
  )
}

function Count({ n }: { n: number }) {
  return (
    <span className="rounded-full bg-black/10 dark:bg-white/15 px-1.5 text-[10px] font-bold">
      {n}
    </span>
  )
}
