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

// Order matters: the first listed recording is the one the static demo autoplays.
// A follow-up relaxes its parent's cheapest fix and re-solves for real, so the
// static demo can play the whole impossible -> explained -> relaxed -> solved
// story when someone presses "give this up", with no backend behind it.
const DEMOS = [
  {
    id: 'large-ward',
    label: 'Large ward — watch it improve',
    request: { presetId: 'large-ward', seconds: 20 },
  },
  {
    id: 'night-crunch',
    label: 'Night certification crunch — impossible, explained',
    request: { presetId: 'night-crunch', seconds: 10 },
  },
  {
    id: 'night-crunch+cheapest-fix',
    label: 'Night certification crunch — cheapest fix applied',
    followUpOf: 'night-crunch',
  },
  {
    id: 'flu-season',
    label: 'Flu season surge — impossible, explained',
    request: { presetId: 'flu-season', seconds: 15 },
  },
  {
    id: 'flu-season+cheapest-fix',
    label: 'Flu season surge — cheapest fix applied',
    followUpOf: 'flu-season',
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

/** Builds a follow-up's request from what its parent recording was told to give up. */
function followUpRequest(parent) {
  const cheapest = parent.explanation?.fixes?.[0]
  if (!cheapest) throw new Error(`recording '${parent.id}' has no fix to apply`)
  return {
    presetId: parent.started.scenarioId,
    seconds: 15,
    relaxRuleIds: cheapest.rules.map((r) => r.ruleId),
    parentRunId: parent.started.runId,
  }
}

async function record({ id, label, request }) {
  process.stdout.write(`  ${id} … `)

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
  trace.id = id

  console.log(
    `${run.status}, ${trace.frames.length} frames, ` +
      `${trace.explanation ? `${trace.explanation.conflictSet.length} conflicts` : 'no explanation'}`,
  )
  return trace
}

const traces = []
console.log(`Recording demo traces from ${API}`)
for (const demo of DEMOS) {
  if (demo.followUpOf) {
    const parent = traces.find((t) => t.id === demo.followUpOf)
    if (!parent) throw new Error(`'${demo.id}' follows '${demo.followUpOf}', which was not recorded first`)
    const trace = await record({ ...demo, request: followUpRequest(parent) })
    trace.followUpOf = demo.followUpOf
    if (trace.completed.status !== 'Completed') {
      throw new Error(`applying the cheapest fix to '${parent.id}' did not produce a roster`)
    }
    traces.push(trace)
  } else {
    traces.push(await record(demo))
  }
}

// One minified file: the client needs every recording on load anyway, and it is
// shipped on the static site, so pretty-printed per-trace copies would only
// multiply the payload without anything reading them.
await mkdir(OUT, { recursive: true })
await writeFile(join(OUT, 'index.json'), JSON.stringify(traces))

const bytes = Buffer.byteLength(JSON.stringify(traces))
console.log(`\nWrote ${traces.length} traces to ${OUT} (${(bytes / 1024).toFixed(0)} kB)`)
