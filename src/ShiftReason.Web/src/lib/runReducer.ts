import type {
  ExplanationDto,
  PenaltyLine,
  RosterDelta,
  RunCompleted,
  RunFailed,
  RunStarted,
  ShiftDto,
} from './types'

export type RunStatus =
  | 'idle'
  | 'queued'
  | 'running'
  | 'completed'
  | 'infeasible'
  | 'cancelled'
  | 'failed'

export interface RunState {
  runId: string | null
  scenarioId: string | null
  scenarioName: string | null
  employeeIds: string[]
  employeeNames: string[]
  dates: string[]
  shifts: ShiftDto[]
  relaxedRuleIds: string[]

  /** Flat grid, `employee * dates.length + day` => shift index. 0 is OFF. */
  cells: Int16Array
  /** Flat indices that moved in the most recent frame, so the UI can pulse them. */
  changed: Set<number>

  seq: number
  seconds: number
  objective: number | null
  bestBound: number | null
  penalties: PenaltyLine[]
  improvements: number
  solutionCount: number

  status: RunStatus
  cancelled: boolean
  explanation: ExplanationDto | null
  error: string | null
}

export const initialRunState: RunState = {
  runId: null,
  scenarioId: null,
  scenarioName: null,
  employeeIds: [],
  employeeNames: [],
  dates: [],
  shifts: [],
  relaxedRuleIds: [],
  cells: new Int16Array(0),
  changed: new Set(),
  seq: -1,
  seconds: 0,
  objective: null,
  bestBound: null,
  penalties: [],
  improvements: 0,
  solutionCount: 0,
  status: 'idle',
  cancelled: false,
  explanation: null,
  error: null,
}

export type RunAction =
  | { type: 'reset' }
  | { type: 'queued'; runId: string }
  | { type: 'started'; payload: RunStarted }
  | { type: 'improved'; payload: RosterDelta }
  | { type: 'completed'; payload: RunCompleted }
  | { type: 'explained'; payload: ExplanationDto }
  | { type: 'failed'; payload: RunFailed }

/**
 * The single place roster state changes.
 *
 * Both the SignalR client and the recorded-trace player dispatch into this, which
 * is why the published demo is honest: replay is not a separate rendering path
 * with canned screenshots, it is the same reducer fed the same messages a live
 * solve produced.
 */
export function runReducer(state: RunState, action: RunAction): RunState {
  switch (action.type) {
    case 'reset':
      return initialRunState

    case 'queued':
      return { ...initialRunState, runId: action.runId, status: 'queued' }

    case 'started': {
      const p = action.payload
      return {
        ...initialRunState,
        runId: p.runId,
        scenarioId: p.scenarioId,
        scenarioName: p.scenarioName,
        employeeIds: p.employeeIds,
        employeeNames: p.employeeNames,
        dates: p.dates,
        shifts: p.shifts,
        relaxedRuleIds: p.relaxedRuleIds,
        cells: new Int16Array(p.employeeIds.length * p.dates.length),
        status: 'running',
      }
    }

    case 'improved': {
      const p = action.payload

      // Frames can arrive out of order only if something upstream is broken, but
      // dropping a stale one is cheaper than rendering a roster that goes
      // backwards in front of someone.
      if (p.seq <= state.seq) return state

      const days = state.dates.length
      if (days === 0) return state

      // A full frame is a baseline: everyone off, then apply what is worked.
      const cells = p.isFull
        ? new Int16Array(state.cells.length)
        : (state.cells.slice() as Int16Array)

      const changed = new Set<number>()
      for (const c of p.changes) {
        const i = c.e * days + c.d
        if (cells[i] !== c.s) changed.add(i)
        cells[i] = c.s
      }

      return {
        ...state,
        cells,
        // A baseline frame would otherwise pulse every occupied cell at once,
        // which reads as noise rather than progress.
        changed: p.isFull ? new Set() : changed,
        seq: p.seq,
        seconds: p.seconds,
        objective: p.objective,
        bestBound: p.bestBound,
        penalties: p.penalties,
        improvements: state.improvements + 1,
        status: 'running',
      }
    }

    case 'completed': {
      const p = action.payload
      return {
        ...state,
        status: mapStatus(p.status),
        cancelled: p.cancelled,
        objective: p.objective ?? state.objective,
        bestBound: p.bestBound ?? state.bestBound,
        seconds: p.wallSeconds,
        solutionCount: p.solutionCount,
        penalties: p.penalties.length > 0 ? p.penalties : state.penalties,
        changed: new Set(),
      }
    }

    case 'explained':
      return { ...state, explanation: action.payload }

    case 'failed':
      return { ...state, status: 'failed', error: action.payload.message }

    default:
      return state
  }
}

function mapStatus(status: string): RunStatus {
  switch (status) {
    case 'Completed':
      return 'completed'
    case 'Infeasible':
      return 'infeasible'
    case 'Cancelled':
      return 'cancelled'
    case 'Failed':
      return 'failed'
    default:
      return 'running'
  }
}

export const isRunning = (s: RunStatus) => s === 'queued' || s === 'running'

/** Cells a rule points at, for highlighting the grid from the explanation panel. */
export function cellsForRefs(
  state: RunState,
  refs: { kind: string; id: string }[],
): Set<number> {
  const days = state.dates.length
  const employeeRows = refs
    .filter((r) => r.kind === 'employee')
    .map((r) => state.employeeIds.indexOf(r.id))
    .filter((i) => i >= 0)
  const dayCols = refs
    .filter((r) => r.kind === 'date')
    .map((r) => state.dates.indexOf(r.id))
    .filter((i) => i >= 0)

  const hits = new Set<number>()

  // A rule naming both a nurse and a date means those specific cells. Naming only
  // one means the whole row or column — which is exactly what "Friday night needs
  // two certified nurses" should light up.
  if (employeeRows.length > 0 && dayCols.length > 0) {
    for (const e of employeeRows) for (const d of dayCols) hits.add(e * days + d)
  } else if (employeeRows.length > 0) {
    for (const e of employeeRows) for (let d = 0; d < days; d++) hits.add(e * days + d)
  } else if (dayCols.length > 0) {
    for (let e = 0; e < state.employeeIds.length; e++) {
      for (const d of dayCols) hits.add(e * days + d)
    }
  }

  return hits
}
