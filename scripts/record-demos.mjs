#!/usr/bin/env node
// Records real solver runs as replayable traces for the published demo.
//
// The static build on Cloudflare Pages has no backend behind it, so without
// these the demo link is a dead page whenever the container is asleep, cold or
// gone. These are not mock-ups: each one is the exact sequence of hub messages a
// live run emitted, replayed through the same client reducer.
//
//   1. dotnet run --project src/ShiftReason.Api --urls http://localhost:5217
//   2. node scripts/record-demos.mjs
//
// Re-run whenever the model or the presets change, or the demo drifts from what
// the solver actually does now.

import { mkdir, writeFile } from 'node:fs/promises'
import { join } from 'node:path'

const API = process.env.SHIFTREASON_API ?? 'http://localhost:5217'
const OUT = join(process.cwd(), 'src', 'ShiftReason.Web', 'public', 'demo', 'traces')

const DEMOS = [
  {
    label: 'Large ward — watch it improve',
    request: { presetId: 'large-ward', seconds: 20 },
  },
  {
    label: 'Night certification crunch — impossible, explained',
    request: { presetId: 'night-crunch', seconds: 10 },
  },
  {
    label: 'Flu season surge — impossible',
    request: { presetId: 'flu-season', seconds: 15 },
  },
]

const TERMINAL = new Set(['Completed', 'Infeasible', 'Cancelled', 'Failed'])

async function json(url, init) {
  const response = await fetch(url, init)
  if (!response.ok) throw new Error(`${init?.method ?? 'GET'} ${url} -> ${response.status}`)
  return response.json()
}

/** 404 here means "the worker has not picked the job up yet", not a failure. */
async function jsonOrNull(url) {
  const response = await fetch(url)
  if (response.status === 404) return null
  if (!response.ok) throw new Error(`GET ${url} -> ${response.status}`)
  return response.json()
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

async function record({ label, request }) {
  process.stdout.write(`  ${request.presetId} … `)

  const { runId } = await json(`${API}/api/solve`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
  })

  // Poll rather than subscribe: this runs once, offline, and a hub client here
  // would only add a way for the recording to miss its own first frames.
  const deadline = Date.now() + 180_000
  let run
  while (Date.now() < deadline) {
    run = await jsonOrNull(`${API}/api/runs/${runId}`)
    if (run && TERMINAL.has(run.status)) break
    await sleep(500)
  }
  if (!run || !TERMINAL.has(run.status)) throw new Error(`run ${runId} never finished`)

  // An infeasible run publishes its explanation after the verdict, so give the
  // extra solves a moment to land or the recording captures a blank drawer.
  if (run.status === 'Infeasible') {
    const explainBy = Date.now() + 90_000
    while (Date.now() < explainBy && !run.explanationJson) {
      await sleep(500)
      run = (await jsonOrNull(`${API}/api/runs/${runId}`)) ?? run
    }
  }

  const trace = await json(`${API}/api/runs/${runId}/recording`)
  trace.label = label
  trace.id = request.presetId

  console.log(
    `${run.status}, ${trace.frames.length} frames, ` +
      `${trace.explanation ? `${trace.explanation.conflictSet.length} conflicts` : 'no explanation'}`,
  )
  return trace
}

const traces = []
console.log(`Recording demo traces from ${API}`)
for (const demo of DEMOS) traces.push(await record(demo))

await mkdir(OUT, { recursive: true })
await writeFile(join(OUT, 'index.json'), JSON.stringify(traces))

for (const trace of traces) {
  await writeFile(join(OUT, `${trace.id}.json`), JSON.stringify(trace, null, 2))
}

const bytes = Buffer.byteLength(JSON.stringify(traces))
console.log(`\nWrote ${traces.length} traces to ${OUT} (${(bytes / 1024).toFixed(0)} kB)`)
