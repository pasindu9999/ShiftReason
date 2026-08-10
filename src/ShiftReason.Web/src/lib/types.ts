// Mirrors ShiftReason.Api.Contracts. Kept hand-written rather than generated:
// the surface is small, and a generated client would obscure the one thing worth
// understanding here — that deltas and recorded traces carry the same shape.

export interface ShiftDto {
  index: number
  id: string
  name: string
  isNight: boolean
}

export interface CellChange {
  e: number
  d: number
  s: number
}

export interface RunStarted {
  runId: string
  scenarioId: string
  scenarioName: string
  employeeIds: string[]
  employeeNames: string[]
  dates: string[]
  shifts: ShiftDto[]
  relaxedRuleIds: string[]
}

export interface PenaltyLine {
  key: string
  label: string
  unitNoun: string
  units: number
  weight: number
  cost: number
}

export interface RosterDelta {
  runId: string
  seq: number
  seconds: number
  objective: number
  bestBound: number
  /** Baseline frame: reset the grid to all-OFF before applying `changes`. */
  isFull: boolean
  changes: CellChange[]
  penalties: PenaltyLine[]
}

export interface RunCompleted {
  runId: string
  status: string
  objective: number | null
  bestBound: number | null
  wallSeconds: number
  solutionCount: number
  cancelled: boolean
  penalties: PenaltyLine[]
}

export interface EntityRefDto {
  kind: string
  id: string
}

export interface RuleDto {
  ruleId: string
  kind: string
  relaxCost: number
  sentence: string
  refs: EntityRefDto[]
}

export interface FixDto {
  rank: number
  totalCost: number
  rules: RuleDto[]
}

export interface ExplanationDto {
  runId: string
  conflictSet: RuleDto[]
  fixes: FixDto[]
  probes: number
  seconds: number
  minimised: boolean
  structurallyInfeasible: boolean
  /** The analysis ran out of budget: an empty conflict set means "unknown", not "none". */
  timedOut: boolean
}

export interface RunFailed {
  runId: string
  message: string
}

export interface PresetSummary {
  id: string
  name: string
  employees: number
  days: number
  shifts: number
  variables: number
}

export interface SolveRequest {
  presetId: string
  seconds?: number
  seed?: number | null
  relaxRuleIds?: string[]
  parentRunId?: string | null
}

/**
 * Everything one run produced, in replay order.
 *
 * A recorded trace is exactly the sequence of hub messages a live run emits, so
 * the replay player and the SignalR client can drive the identical reducer. That
 * is what lets the published demo work with no backend at all without any of it
 * being faked.
 */
export interface RecordedTrace {
  id: string
  label: string
  started: RunStarted
  frames: RosterDelta[]
  completed: RunCompleted
  explanation: ExplanationDto | null
}
