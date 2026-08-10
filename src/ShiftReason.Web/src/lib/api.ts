import type { PresetSummary, RunStarted, SolveRequest } from './types'

/** Layout of the grid, independent of any run. */
export type ScenarioLayout = Omit<RunStarted, 'runId' | 'relaxedRuleIds'>

async function json<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const body = await response.text()
    throw new Error(`${response.status} ${response.statusText}${body ? ` — ${body}` : ''}`)
  }
  return (await response.json()) as T
}

export const getPresets = () => fetch('/api/presets').then(json<PresetSummary[]>)

const layoutCache = new Map<string, Promise<ScenarioLayout>>()

/**
 * Cached because the layout never changes for a preset, and the grid is drawn
 * from it before a solve is even requested.
 */
export function getLayout(presetId: string): Promise<ScenarioLayout> {
  let cached = layoutCache.get(presetId)
  if (!cached) {
    cached = fetch(`/api/scenarios/${presetId}`).then(json<ScenarioLayout>)
    layoutCache.set(presetId, cached)
  }
  return cached
}

export const postSolve = (request: SolveRequest) =>
  fetch('/api/solve', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  }).then(json<{ runId: string; presetId: string }>)

export async function cancelRun(runId: string): Promise<void> {
  // A 404 here means the run already finished, which is not worth surfacing:
  // the user pressed Stop and the solve is stopped either way.
  await fetch(`/api/runs/${runId}/cancel`, { method: 'POST' })
}
