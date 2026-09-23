import type { ConnectionState } from '../lib/useSolveRun'
import { isRunning, type RunState } from '../lib/runReducer'
import type { PresetSummary } from '../lib/types'

interface Props {
  presets: PresetSummary[]
  selected: string
  onSelect: (id: string) => void
  reproducible: boolean
  onReproducibleChange: (value: boolean) => void
  state: RunState
  connection: ConnectionState
  replaying: boolean
  onSolve: () => void
  onStop: () => void
  /** Static build: no preset picker or Solve button, only replay controls. */
  demoOnly?: boolean
}

export function SolveBar({
  presets,
  selected,
  onSelect,
  reproducible,
  onReproducibleChange,
  state,
  connection,
  replaying,
  onSolve,
  onStop,
  demoOnly = false,
}: Props) {
  const running = isRunning(state.status) || replaying
  const offline = connection === 'offline'

  return (
    <div className="flex flex-wrap items-center gap-3 rounded-lg border border-black/10 dark:border-white/10 bg-white/70 dark:bg-white/5 px-3 py-2">
      {!demoOnly && (
        <select
          value={selected}
          onChange={(e) => onSelect(e.target.value)}
          disabled={running}
          className="rounded-md border border-black/15 dark:border-white/15 bg-transparent px-2 py-1.5 text-sm disabled:opacity-50"
        >
          {presets.map((p) => (
            <option key={p.id} value={p.id}>
              {p.name} — {p.employees} nurses, {p.days} days
            </option>
          ))}
        </select>
      )}

      {running ? (
        <button
          onClick={onStop}
          className="rounded-md bg-rose-600 px-4 py-1.5 text-sm font-semibold text-white hover:bg-rose-700"
        >
          Stop
        </button>
      ) : demoOnly ? null : (
        <button
          onClick={onSolve}
          disabled={offline}
          className="rounded-md bg-emerald-600 px-4 py-1.5 text-sm font-semibold text-white hover:bg-emerald-700 disabled:opacity-40"
          title={offline ? 'No backend connection — use a recorded run instead' : undefined}
        >
          Solve
        </button>
      )}

      {!demoOnly && (
        <label
          className="flex items-center gap-1.5 text-xs opacity-80"
          title="Pins the seed and uses CP-SAT's deterministic search loop, so the same input reproduces the same roster. It costs real throughput — the objective will be noticeably worse for the same time budget."
        >
          <input
            type="checkbox"
            checked={reproducible}
            onChange={(e) => onReproducibleChange(e.target.checked)}
            disabled={running}
          />
          Reproducible
        </label>
      )}

      <div className="ml-auto flex flex-wrap items-center gap-x-4 gap-y-1 text-xs tabular-nums">
        <Stat label="elapsed" value={`${state.seconds.toFixed(1)}s`} />
        <Stat
          label="objective"
          value={state.objective === null ? '—' : state.objective.toLocaleString()}
        />
        <Stat label="improved" value={String(state.improvements)} />
        <StatusPill state={state} replaying={replaying} connection={connection} />
      </div>
    </div>
  )
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <span className="flex items-baseline gap-1">
      <span className="opacity-50">{label}</span>
      <span className="font-semibold">{value}</span>
    </span>
  )
}

function StatusPill({
  state,
  replaying,
  connection,
}: {
  state: RunState
  replaying: boolean
  connection: ConnectionState
}) {
  const [text, tone] = describe(state, replaying, connection)
  return (
    // role="status" makes this a polite live region, so a screen reader announces
    // "solved" or "impossible" without the user having to go looking for it.
    <span role="status" className={`rounded-full px-2 py-0.5 text-[11px] font-semibold ${tone}`}>
      {text}
    </span>
  )
}

function describe(
  state: RunState,
  replaying: boolean,
  connection: ConnectionState,
): [string, string] {
  if (replaying) return ['replaying recording', 'bg-sky-500/15 text-sky-600 dark:text-sky-300']
  if (connection === 'offline' && state.status === 'idle')
    return ['offline — recordings only', 'bg-neutral-500/15 text-neutral-500']

  switch (state.status) {
    case 'queued':
      return ['queued', 'bg-neutral-500/15 text-neutral-500']
    case 'running':
      return ['solving', 'bg-amber-500/15 text-amber-600 dark:text-amber-300']
    case 'completed':
      return ['solved', 'bg-emerald-500/15 text-emerald-600 dark:text-emerald-300']
    case 'infeasible':
      return ['impossible', 'bg-rose-500/15 text-rose-600 dark:text-rose-300']
    case 'cancelled':
      return ['stopped — best so far', 'bg-sky-500/15 text-sky-600 dark:text-sky-300']
    case 'failed':
      return ['failed', 'bg-rose-500/15 text-rose-600 dark:text-rose-300']
    default:
      return ['idle', 'bg-neutral-500/15 text-neutral-500']
  }
}
