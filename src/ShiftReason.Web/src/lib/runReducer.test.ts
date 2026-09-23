import { readFileSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'
import { cellsForRefs, initialRunState, runReducer, type RunState } from './runReducer'
import type { RecordedTrace } from './types'

const traces: RecordedTrace[] = JSON.parse(
  readFileSync(join(__dirname, '../../public/demo/traces/index.json'), 'utf8'),
)

const byId = (id: string) => {
  const trace = traces.find((t) => t.id === id)
  if (!trace) throw new Error(`no recorded trace '${id}'`)
  return trace
}

/** Drives a whole recording through the reducer, exactly as the player does. */
function replayAll(trace: RecordedTrace): RunState {
  let state = runReducer(initialRunState, { type: 'started', payload: trace.started })
  for (const frame of trace.frames) state = runReducer(state, { type: 'improved', payload: frame })
  state = runReducer(state, { type: 'completed', payload: trace.completed })
  if (trace.explanation) {
    state = runReducer(state, { type: 'explained', payload: trace.explanation })
  }
  return state
}

describe('recorded traces', () => {
  it('ship with the build', () => {
    expect(traces.length).toBeGreaterThan(0)
    for (const t of traces) {
      expect(t.started.employeeIds.length).toBeGreaterThan(0)
      expect(t.started.dates.length).toBeGreaterThan(0)
    }
  })

  it('open with a full baseline frame, then diffs', () => {
    const trace = byId('large-ward')
    expect(trace.frames.length).toBeGreaterThan(1)
    expect(trace.frames[0].isFull).toBe(true)
    // Exactly one baseline: every later frame must be a diff, or the delta
    // encoding has silently stopped working and the wire is carrying whole grids.
    expect(trace.frames.filter((f) => f.isFull)).toHaveLength(1)
  })

  it('never report a worse objective than the frame before', () => {
    for (const trace of traces) {
      const objectives = trace.frames.map((f) => f.objective)
      expect(objectives).toEqual([...objectives].sort((a, b) => b - a))
    }
  })
})

describe('follow-up recordings', () => {
  const followUps = traces.filter((t) => t.followUpOf)

  it('exist for every impossible ward, so the static demo can finish the story', () => {
    const impossible = traces.filter((t) => !t.followUpOf && t.completed.status === 'Infeasible')
    expect(impossible.length).toBeGreaterThan(0)
    for (const parent of impossible) {
      expect(followUps.some((f) => f.followUpOf === parent.id)).toBe(true)
    }
  })

  it('relax exactly the cheapest fix their parent recommended, and then solve', () => {
    for (const followUp of followUps) {
      const parent = byId(followUp.followUpOf!)
      const cheapest = parent.explanation!.fixes[0].rules.map((r) => r.ruleId).sort()

      // If these drift apart, pressing "give this up" on the static demo would
      // play a solve of some other relaxation than the one on the button.
      expect([...followUp.started.relaxedRuleIds].sort()).toEqual(cheapest)
      expect(followUp.started.scenarioId).toBe(parent.started.scenarioId)

      const state = replayAll(followUp)
      expect(state.status).toBe('completed')
      expect(state.penalties.length).toBeGreaterThan(0)
    }
  })

  it('leave a feasible recording first in line, since that is what autoplays', () => {
    const first = traces.find((t) => !t.followUpOf)!
    expect(first.completed.status).toBe('Completed')
    expect(first.frames.length).toBeGreaterThan(5)
  })
})

describe('runReducer', () => {
  it('replays a recording to the objective the solver actually reported', () => {
    const trace = byId('large-ward')
    const state = replayAll(trace)

    expect(state.status).toBe('completed')
    expect(state.objective).toBe(trace.completed.objective)
    expect(state.improvements).toBe(trace.frames.length)
  })

  it('produces a grid with one entry per nurse per day, all valid shifts', () => {
    const trace = byId('large-ward')
    const state = replayAll(trace)

    expect(state.cells.length).toBe(state.employeeIds.length * state.dates.length)

    const shiftIndices = new Set(state.shifts.map((s) => s.index))
    for (const cell of state.cells) expect(shiftIndices.has(cell)).toBe(true)

    // A roster where nobody works would technically satisfy the above.
    const worked = [...state.cells].filter((c) => c !== 0).length
    expect(worked).toBeGreaterThan(0)
  })

  it('marks only the cells a diff actually moved, so the pulse means something', () => {
    const trace = byId('large-ward')

    let state = runReducer(initialRunState, { type: 'started', payload: trace.started })
    state = runReducer(state, { type: 'improved', payload: trace.frames[0] })
    // The baseline moves every occupied cell at once; flashing them all is noise.
    expect(state.changed.size).toBe(0)

    state = runReducer(state, { type: 'improved', payload: trace.frames[1] })
    expect(state.changed.size).toBeGreaterThan(0)
    expect(state.changed.size).toBeLessThanOrEqual(trace.frames[1].changes.length)
  })

  it('ends a replay that is stopped part-way, keeping the frames already shown', () => {
    const trace = byId('large-ward')

    let state = runReducer(initialRunState, { type: 'started', payload: trace.started })
    state = runReducer(state, { type: 'improved', payload: trace.frames[0] })
    state = runReducer(state, { type: 'improved', payload: trace.frames[1] })
    state = runReducer(state, { type: 'stopped' })

    // Still "running" here would disable every control on the page.
    expect(state.status).toBe('cancelled')
    expect(state.improvements).toBe(2)
    expect(state.objective).toBe(trace.frames[1].objective)
  })

  it('treats stopping an already finished run as a no-op', () => {
    const state = replayAll(byId('large-ward'))
    expect(runReducer(state, { type: 'stopped' })).toBe(state)
  })

  it('ignores a frame that arrives out of order', () => {
    const trace = byId('large-ward')

    let state = runReducer(initialRunState, { type: 'started', payload: trace.started })
    state = runReducer(state, { type: 'improved', payload: trace.frames[0] })
    state = runReducer(state, { type: 'improved', payload: trace.frames[1] })

    const before = state
    const after = runReducer(state, { type: 'improved', payload: trace.frames[0] })
    expect(after).toBe(before)
  })

  it('surfaces the conflict set for an impossible ward', () => {
    for (const id of ['night-crunch', 'flu-season']) {
      const trace = byId(id)
      const state = replayAll(trace)

      expect(state.status).toBe('infeasible')
      expect(state.explanation).not.toBeNull()
      expect(state.explanation!.timedOut).toBe(false)
      expect(state.explanation!.conflictSet.length).toBeGreaterThan(0)
      expect(state.explanation!.fixes.length).toBeGreaterThan(0)

      // Plain English is the product; a blank sentence renders as an empty row.
      for (const rule of state.explanation!.conflictSet) {
        expect(rule.sentence.trim().length).toBeGreaterThan(0)
      }
    }
  })

  it('keeps every fix intersecting the conflict set', () => {
    const state = replayAll(byId('night-crunch'))
    const conflict = new Set(state.explanation!.conflictSet.map((r) => r.ruleId))

    for (const fix of state.explanation!.fixes) {
      expect(fix.rules.some((r) => conflict.has(r.ruleId))).toBe(true)
    }
  })
})

describe('cellsForRefs', () => {
  it('lights up the cells a rule names', () => {
    const state = replayAll(byId('night-crunch'))
    const rule = state.explanation!.conflictSet.find((r) =>
      r.refs.some((x) => x.kind === 'employee'),
    )!

    const hits = cellsForRefs(state, rule.refs)
    expect(hits.size).toBeGreaterThan(0)
    for (const i of hits) expect(i).toBeLessThan(state.cells.length)
  })

  it('lights up a whole column for a rule that names only a date', () => {
    const state = replayAll(byId('night-crunch'))
    const dateOnly = state.explanation!.conflictSet.find(
      (r) => r.refs.some((x) => x.kind === 'date') && !r.refs.some((x) => x.kind === 'employee'),
    )

    if (!dateOnly) return // preset-dependent; nothing to assert
    expect(cellsForRefs(state, dateOnly.refs).size).toBe(state.employeeIds.length)
  })
})
