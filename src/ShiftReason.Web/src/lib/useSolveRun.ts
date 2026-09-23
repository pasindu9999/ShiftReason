import { useCallback, useEffect, useReducer, useRef, useState } from 'react'
import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'
import { cancelRun, getLayout, postSolve } from './api'
import { initialRunState, runReducer, type RunState } from './runReducer'
import type {
  ExplanationDto,
  RecordedTrace,
  RosterDelta,
  RunCompleted,
  RunFailed,
  RunStarted,
  SolveRequest,
} from './types'

export type ConnectionState = 'connecting' | 'connected' | 'offline'

export interface SolveRunController {
  state: RunState
  connection: ConnectionState
  /** True while a recorded trace is being played rather than a live solve. */
  replaying: boolean
  solve: (request: SolveRequest) => Promise<void>
  stop: () => Promise<void>
  replay: (trace: RecordedTrace, speed?: number) => void
  reset: () => void
}

export interface SolveRunOptions {
  /**
   * False on the static demo build. There is no hub to reach, and a doomed
   * WebSocket handshake followed by retries would only put errors in the console
   * of the one page a recruiter is guaranteed to open.
   */
  connect?: boolean
}

/**
 * Owns one run: live over SignalR, or replayed from a recording.
 *
 * Both paths dispatch the same actions into the same reducer, so the grid, the
 * explanation drawer and the penalty panel cannot drift between the two. The
 * published demo is the replay path, which is why it has to be the real one.
 */
export function useSolveRun({ connect = true }: SolveRunOptions = {}): SolveRunController {
  const [state, dispatch] = useReducer(runReducer, initialRunState)
  const [connection, setConnection] = useState<ConnectionState>(connect ? 'connecting' : 'offline')
  const [replaying, setReplaying] = useState(false)

  const hub = useRef<HubConnection | null>(null)
  const watching = useRef<string | null>(null)
  const replayTimers = useRef<number[]>([])
  const replayingRef = useRef(false)

  const clearReplay = useCallback(() => {
    for (const t of replayTimers.current) window.clearTimeout(t)
    replayTimers.current = []
    replayingRef.current = false
    setReplaying(false)
  }, [])

  useEffect(() => {
    if (!connect) return

    const c = new HubConnectionBuilder()
      .withUrl('/hubs/solve')
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    c.on('runStarted', (p: RunStarted) => dispatch({ type: 'started', payload: p }))
    c.on('rosterImproved', (p: RosterDelta) => dispatch({ type: 'improved', payload: p }))
    c.on('runCompleted', (p: RunCompleted) => dispatch({ type: 'completed', payload: p }))
    c.on('explained', (p: ExplanationDto) => dispatch({ type: 'explained', payload: p }))
    c.on('runFailed', (p: RunFailed) => dispatch({ type: 'failed', payload: p }))

    c.onreconnecting(() => setConnection('connecting'))
    c.onreconnected(async () => {
      setConnection('connected')
      // Group membership does not survive a reconnect, so re-subscribe or the
      // grid silently stops updating while appearing healthy.
      if (watching.current) await c.invoke('Watch', watching.current).catch(() => {})
    })
    c.onclose(() => setConnection('offline'))

    hub.current = c
    c.start()
      .then(() => setConnection('connected'))
      // Offline is a legitimate state, not an error: the demo replays recorded
      // runs perfectly well with no backend at all.
      .catch(() => setConnection('offline'))

    return () => {
      hub.current = null
      void c.stop()
    }
  }, [connect])

  const solve = useCallback(
    async (request: SolveRequest) => {
      clearReplay()

      // Lay the grid out from the cached layout first. A client cannot join a
      // run's group until the enqueue call returns its id, so it may miss the
      // server's runStarted entirely — this makes that race harmless.
      const layout = await getLayout(request.presetId)
      const { runId } = await postSolve(request)

      dispatch({
        type: 'started',
        payload: { ...layout, runId, relaxedRuleIds: request.relaxRuleIds ?? [] },
      })

      const c = hub.current
      if (c && c.state === HubConnectionState.Connected) {
        if (watching.current && watching.current !== runId) {
          await c.invoke('Unwatch', watching.current).catch(() => {})
        }
        await c.invoke('Watch', runId).catch(() => {})
        watching.current = runId
      }
    },
    [clearReplay],
  )

  const stop = useCallback(async () => {
    // A recording is stopped locally. Sending it to the cancel endpoint would be
    // wrong twice over: the run id belongs to a solve that finished long ago, and
    // on the static demo there is no server to receive the request.
    if (replayingRef.current) {
      clearReplay()
      dispatch({ type: 'stopped' })
      return
    }

    if (state.runId) await cancelRun(state.runId)
  }, [state.runId, clearReplay])

  /**
   * Plays a recording at the pace the solver actually produced it. Frames carry
   * the solver's own wall-clock stamp, so a 15-second solve replays over 15
   * seconds rather than dumping every frame at once.
   */
  const replay = useCallback(
    (trace: RecordedTrace, speed = 1) => {
      clearReplay()
      replayingRef.current = true
      setReplaying(true)

      dispatch({ type: 'started', payload: trace.started })

      const schedule = (ms: number, run: () => void) => {
        replayTimers.current.push(window.setTimeout(run, Math.max(0, ms / speed)))
      }

      for (const frame of trace.frames) {
        schedule(frame.seconds * 1000, () => dispatch({ type: 'improved', payload: frame }))
      }

      const end = trace.completed.wallSeconds * 1000
      schedule(end, () => dispatch({ type: 'completed', payload: trace.completed }))

      if (trace.explanation) {
        // Matches live behaviour: the verdict lands first, the explanation
        // follows a beat later once the extra solves have run.
        schedule(end + 600, () =>
          dispatch({ type: 'explained', payload: trace.explanation! }),
        )
      }

      schedule(end + 900, () => {
        replayingRef.current = false
        setReplaying(false)
      })
    },
    [clearReplay],
  )

  const reset = useCallback(() => {
    clearReplay()
    dispatch({ type: 'reset' })
  }, [clearReplay])

  useEffect(() => clearReplay, [clearReplay])

  return { state, connection, replaying, solve, stop, replay, reset }
}
