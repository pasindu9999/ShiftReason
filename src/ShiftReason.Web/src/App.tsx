import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ExplainDrawer } from './components/ExplainDrawer'
import { PenaltyPanel } from './components/PenaltyPanel'
import { RosterGrid, ShiftLegend } from './components/RosterGrid'
import { SolveBar } from './components/SolveBar'
import { getPresets } from './lib/api'
import { DEMO_ONLY, LIVE_URL, REPO_URL } from './lib/config'
import { cellsForRefs, isRunning } from './lib/runReducer'
import { useSolveRun } from './lib/useSolveRun'
import type { PenaltyLine, PresetSummary, RecordedTrace, RuleDto } from './lib/types'

interface Baseline {
  label: string
  penalties: PenaltyLine[]
  objective: number | null
}

export default function App() {
  const { state, connection, replaying, solve, stop, replay } = useSolveRun({ connect: !DEMO_ONLY })

  const [presets, setPresets] = useState<PresetSummary[]>([])
  const [selected, setSelected] = useState('large-ward')
  const [reproducible, setReproducible] = useState(false)
  const [relaxed, setRelaxed] = useState<RuleDto[]>([])
  const [highlightRefs, setHighlightRefs] = useState<RuleDto['refs'] | null>(null)
  const [baseline, setBaseline] = useState<Baseline | null>(null)
  const [traces, setTraces] = useState<RecordedTrace[]>([])
  const [error, setError] = useState<string | null>(null)

  const lastFeasible = useRef<Baseline | null>(null)
  const autoplayed = useRef(false)

  useEffect(() => {
    if (!DEMO_ONLY) {
      getPresets()
        .then((p) => {
          setPresets(p)
          if (p.length > 0 && !p.some((x) => x.id === selected)) setSelected(p[0].id)
        })
        .catch(() => setPresets([]))
    }

    // Recorded runs, published alongside the static build. These are what make
    // the demo link work whether or not a backend is awake.
    fetch('demo/traces/index.json')
      .then((r) => (r.ok ? r.json() : []))
      .then(setTraces)
      .catch(() => setTraces([]))
  }, [])

  const listedTraces = useMemo(() => traces.filter((t) => !t.followUpOf), [traces])

  useEffect(() => {
    if (!DEMO_ONLY || autoplayed.current || listedTraces.length === 0) return
    autoplayed.current = true
    replay(listedTraces[0])
  }, [listedTraces, replay])

  /** A recorded relaxed re-solve of the ward on screen, if one exists for these rules. */
  const recordedFollowUp = useCallback(
    (ruleIds: string[]) => {
      const wanted = new Set(ruleIds)
      return traces.find(
        (t) =>
          t.followUpOf !== undefined &&
          t.started.scenarioId === state.scenarioId &&
          t.started.relaxedRuleIds.length === wanted.size &&
          t.started.relaxedRuleIds.every((id) => wanted.has(id)),
      )
    },
    [traces, state.scenarioId],
  )

  const live = connection === 'connected'

  // Remember the last roster that actually worked, so a later relaxation can be
  // priced against it rather than against nothing.
  useEffect(() => {
    if (state.status === 'completed' && state.penalties.length > 0) {
      const current: Baseline = {
        label: relaxed.length === 0 ? 'the unrelaxed ward' : `${relaxed.length} rule(s) relaxed`,
        penalties: state.penalties,
        objective: state.objective,
      }
      setBaseline(lastFeasible.current)
      lastFeasible.current = current
    }
  }, [state.status, state.penalties, state.objective])

  const run = useCallback(
    async (relaxRuleIds: string[]) => {
      setError(null)
      try {
        await solve({
          presetId: selected,
          seconds: 30,
          seed: reproducible ? 20260322 : null,
          relaxRuleIds,
          parentRunId: state.runId,
        })
      } catch (e) {
        setError(e instanceof Error ? e.message : String(e))
      }
    },
    [selected, reproducible, state.runId, solve],
  )

  const onSolve = useCallback(() => {
    setRelaxed([])
    setBaseline(null)
    lastFeasible.current = null
    void run([])
  }, [run])

  const onRelax = useCallback(
    (ruleIds: string[]) => {
      const rules = (state.explanation?.fixes.flatMap((f) => f.rules) ?? []).filter((r) =>
        ruleIds.includes(r.ruleId),
      )
      const next = [...relaxed, ...rules.filter((r) => !relaxed.some((x) => x.ruleId === r.ruleId))]
      setRelaxed(next)

      if (live) {
        void run(next.map((r) => r.ruleId))
        return
      }

      // No solver to ask: play the recorded re-solve of exactly this relaxation.
      const followUp = recordedFollowUp(next.map((r) => r.ruleId))
      if (followUp) replay(followUp)
    },
    [relaxed, state.explanation, run, live, recordedFollowUp, replay],
  )

  const canRelax = useCallback(
    (ruleIds: string[]) => live || recordedFollowUp([...relaxed.map((r) => r.ruleId), ...ruleIds]) !== undefined,
    [live, recordedFollowUp, relaxed],
  )

  const highlight = useMemo(
    () => (highlightRefs ? cellsForRefs(state, highlightRefs) : undefined),
    [highlightRefs, state],
  )

  const busy = isRunning(state.status) || replaying

  return (
    <div className="mx-auto max-w-[1400px] p-4 sm:p-6 space-y-4">
      <header className="space-y-1">
        <div className="flex flex-wrap items-baseline gap-x-3">
          <h1 className="text-xl font-bold tracking-tight">
            ShiftReason
            <span className="ml-2 text-sm font-normal opacity-60">
              rostering that explains itself
            </span>
          </h1>
          {REPO_URL && (
            <a href={REPO_URL} className="text-xs underline opacity-60 hover:opacity-100">
              source on GitHub
            </a>
          )}
        </div>
        <p className="max-w-3xl text-sm opacity-70">
          Every rostering tool answers “here is your schedule” or “no feasible
          solution”. This one answers <em>why</em> a ward is impossible — a minimal set
          of colliding rules, in plain English — and what the cheapest thing to give up
          would be.
        </p>
      </header>

      {DEMO_ONLY && (
        <div className="rounded-lg border border-sky-500/30 bg-sky-500/5 px-3 py-2 text-xs">
          <span className="font-semibold">You are watching recorded solves.</span>{' '}
          Every frame below came from the real CP-SAT engine and is replayed through the
          same client code a live run uses — nothing is mocked.
          {LIVE_URL ? (
            <>
              {' '}
              <a href={LIVE_URL} className="font-semibold underline">
                Open the live solver
              </a>{' '}
              <span className="opacity-60">(it scales to zero when idle, so the first load can take up to half a minute)</span>
            </>
          ) : null}
        </div>
      )}

      <SolveBar
        demoOnly={DEMO_ONLY}
        presets={presets}
        selected={selected}
        onSelect={setSelected}
        reproducible={reproducible}
        onReproducibleChange={setReproducible}
        state={state}
        connection={connection}
        replaying={replaying}
        onSolve={onSolve}
        onStop={() => void stop()}
      />

      {listedTraces.length > 0 && (
        <div className="flex flex-wrap items-center gap-2 text-xs">
          <span className="opacity-60">Recorded runs:</span>
          {listedTraces.map((t) => (
            <button
              key={t.id}
              disabled={busy}
              onClick={() => {
                setRelaxed([])
                setBaseline(null)
                replay(t)
              }}
              className="rounded-full border border-black/15 dark:border-white/15 px-2.5 py-1 hover:bg-black/5 dark:hover:bg-white/10 disabled:opacity-40"
            >
              ▶ {t.label}
            </button>
          ))}
          <span className="opacity-45">
            — real solves, replayed frame by frame through the same code path as a live run
          </span>
        </div>
      )}

      {error && (
        <div className="rounded-lg border border-rose-500/30 bg-rose-500/10 px-3 py-2 text-sm text-rose-700 dark:text-rose-300">
          {error}
        </div>
      )}

      {relaxed.length > 0 && (
        <div className="flex flex-wrap items-center gap-2 rounded-lg border border-amber-500/30 bg-amber-500/5 px-3 py-2 text-xs">
          <span className="font-semibold">Given up:</span>
          {relaxed.map((r) => (
            <span
              key={r.ruleId}
              className="rounded-full bg-amber-500/15 px-2 py-0.5"
              title={r.ruleId}
            >
              {r.sentence}
            </span>
          ))}
        </div>
      )}

      <ExplainDrawer
        state={state}
        onHighlight={setHighlightRefs}
        onRelax={onRelax}
        busy={busy}
        canRelax={canRelax}
      />

      <div className="flex items-center justify-between gap-4">
        <ShiftLegend shifts={state.shifts} />
        {state.scenarioName && (
          <span className="text-xs opacity-55">
            {state.scenarioName} · {state.employeeIds.length} nurses × {state.dates.length} days
          </span>
        )}
      </div>

      <RosterGrid
        state={state}
        highlight={highlight}
        emptyNote={
          state.status === 'infeasible' ? 'No roster exists for this ward' : undefined
        }
      />

      <PenaltyPanel state={state} baseline={baseline} />

      <footer className="pt-2 text-[11px] opacity-45">
        CP-SAT via Google OR-Tools. Conflicts are a minimal unsatisfiable subset;
        fixes are a minimum-cost correction set. The two are hitting-set duals —
        every fix intersects every conflict.
      </footer>
    </div>
  )
}
