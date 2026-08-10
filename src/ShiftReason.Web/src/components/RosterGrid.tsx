import { memo, useMemo } from 'react'
import type { RunState } from '../lib/runReducer'

const SHIFT_STYLES: Record<string, string> = {
  OFF: 'bg-[var(--color-ward-off)] text-transparent',
  EARLY: 'bg-amber-400/85 text-amber-950',
  LATE: 'bg-indigo-500/85 text-white',
  NIGHT: 'bg-violet-700/90 text-white',
}

interface CellProps {
  shiftId: string
  label: string
  changed: boolean
  highlighted: boolean
  title: string
}

/**
 * Memoised deliberately. A large ward is 2,000 cells and the solver publishes up
 * to five frames a second; re-rendering every cell on every frame drops the grid
 * to single-digit fps, and an improving solution usually moves only a handful.
 */
const Cell = memo(function Cell({ shiftId, label, changed, highlighted, title }: CellProps) {
  const style = SHIFT_STYLES[shiftId] ?? SHIFT_STYLES.OFF
  return (
    <div
      title={title}
      className={[
        'h-6 rounded-[3px] text-[9px] font-semibold leading-6 text-center select-none',
        style,
        changed ? 'cell-changed' : '',
        highlighted ? 'outline outline-2 outline-rose-500 z-10' : '',
      ].join(' ')}
    >
      {label}
    </div>
  )
})

interface Props {
  state: RunState
  /** Flat cell indices to outline, driven by hovering a rule in the explanation. */
  highlight?: Set<number>
  /** Shown over an empty grid when the ward has no roster at all. */
  emptyNote?: string
}

export function RosterGrid({ state, highlight, emptyNote }: Props) {
  const { employeeNames, dates, shifts, cells } = state
  const days = dates.length

  const dayHeaders = useMemo(
    () =>
      dates.map((iso) => {
        const d = new Date(iso + 'T00:00:00')
        return {
          iso,
          dom: d.getDate(),
          dow: ['S', 'M', 'T', 'W', 'T', 'F', 'S'][d.getDay()],
          weekend: d.getDay() === 0 || d.getDay() === 6,
        }
      }),
    [dates],
  )

  if (days === 0) {
    return (
      <div className="grid place-items-center h-64 text-sm opacity-60">
        Pick a ward and press Solve.
      </div>
    )
  }

  return (
    <div className="relative overflow-auto rounded-lg border border-black/10 dark:border-white/10">
      {emptyNote && (
        <div className="pointer-events-none absolute inset-0 z-40 grid place-items-center">
          <span className="rounded-md bg-rose-600/90 px-3 py-1.5 text-xs font-semibold text-white shadow">
            {emptyNote}
          </span>
        </div>
      )}
      <div
        className="grid min-w-full"
        style={{ gridTemplateColumns: `10rem repeat(${days}, minmax(1.1rem, 1fr))` }}
      >
        <div className="sticky left-0 top-0 z-30 bg-white dark:bg-neutral-900 px-2 py-1 text-[10px] font-semibold uppercase tracking-wide opacity-60">
          Nurse
        </div>
        {dayHeaders.map((h) => (
          <div
            key={h.iso}
            className={`sticky top-0 z-20 bg-white dark:bg-neutral-900 text-center text-[9px] leading-tight py-1 ${
              h.weekend ? 'text-rose-500 font-semibold' : 'opacity-60'
            }`}
          >
            <div>{h.dow}</div>
            <div>{h.dom}</div>
          </div>
        ))}

        {employeeNames.map((name, e) => (
          <Row
            key={e}
            name={name}
            e={e}
            days={days}
            cells={cells}
            shifts={shifts}
            dates={dates}
            changed={state.changed}
            highlight={highlight}
          />
        ))}
      </div>
    </div>
  )
}

interface RowProps {
  name: string
  e: number
  days: number
  cells: Int16Array
  shifts: RunState['shifts']
  dates: string[]
  changed: Set<number>
  highlight?: Set<number>
}

function Row({ name, e, days, cells, shifts, dates, changed, highlight }: RowProps) {
  return (
    <>
      <div className="sticky left-0 z-10 bg-white dark:bg-neutral-900 px-2 text-[11px] leading-6 truncate border-r border-black/5 dark:border-white/5">
        {name}
      </div>
      {Array.from({ length: days }, (_, d) => {
        const i = e * days + d
        const shift = shifts[cells[i]] ?? shifts[0]
        return (
          <Cell
            key={d}
            shiftId={shift?.id ?? 'OFF'}
            label={shift && shift.index !== 0 ? shift.name[0] : ''}
            changed={changed.has(i)}
            highlighted={highlight?.has(i) ?? false}
            title={`${name} — ${dates[d]} — ${shift?.name ?? 'Off'}`}
          />
        )
      })}
    </>
  )
}

export function ShiftLegend({ shifts }: { shifts: RunState['shifts'] }) {
  if (shifts.length === 0) return null
  return (
    <div className="flex flex-wrap items-center gap-3 text-[11px]">
      {shifts.map((s) => (
        <span key={s.id} className="flex items-center gap-1.5">
          <span
            className={`inline-block h-3 w-3 rounded-[3px] ${SHIFT_STYLES[s.id] ?? SHIFT_STYLES.OFF}`}
          />
          {s.name}
        </span>
      ))}
    </div>
  )
}
