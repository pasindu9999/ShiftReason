/**
 * Build-time switches, set per deployment target.
 *
 * The same source builds two artefacts. Inside the container it is the full app,
 * talking to the solver over REST and SignalR. On Cloudflare Pages there is no
 * backend at all, so `VITE_DEMO_ONLY` turns it into a player for recorded solves —
 * which is what keeps the portfolio link working while the container is scaled to
 * zero, cold, or gone.
 */
export const DEMO_ONLY = import.meta.env.VITE_DEMO_ONLY === 'true'

/** Where the live solver lives, linked from the static demo. Optional. */
export const LIVE_URL: string | undefined = import.meta.env.VITE_LIVE_URL || undefined

/** Link to the source, shown in the header. Optional. */
export const REPO_URL: string | undefined = import.meta.env.VITE_REPO_URL || undefined
