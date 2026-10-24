import { afterEach, describe, expect, it, vi } from 'vitest'
import { canCancelRun, canRetryRun, dateFilterInstant, isActiveRun, runStatuses, triggerLabel } from './presentation'
import { formatDuration } from '@/lib/format'
import { statusLabel } from '@/lib/status'

afterEach(() => vi.useRealTimers())

describe('backup state presentation', () => {
  it('distinguishes all run states, polling eligibility and actionable states', () => {
    expect(new Set(runStatuses.map((status) => statusLabel('general', status))).size).toBe(runStatuses.length)
    for (const status of ['Pending', 'Leased', 'Running', 'RetryPending', 'CancellationRequested']) expect(isActiveRun(status)).toBe(true)
    for (const status of ['Succeeded', 'Failed', 'Cancelled', 'AgentLost']) expect(isActiveRun(status)).toBe(false)
    expect(canCancelRun('Running')).toBe(true)
    expect(canCancelRun('RetryPending')).toBe(true)
    expect(canCancelRun('CancellationRequested')).toBe(false)
    expect(canRetryRun('Failed')).toBe(true)
    expect(canRetryRun('AgentLost')).toBe(true)
    expect(canRetryRun('RetryPending')).toBe(false)
    expect(canRetryRun('Succeeded')).toBe(false)
    expect(canRetryRun('Cancelled')).toBe(false)
  })

  it('never fabricates retry provenance for historical attempts', () => {
    expect(triggerLabel('ManualRetry')).toBe('Elle yeniden deneme')
    expect(triggerLabel('AutomaticRetry')).toBe('Otomatik yeniden deneme')
    expect(triggerLabel('Retry')).toBe('Yeniden deneme')
  })

  it('keeps zero durations and calculates running elapsed time', () => {
    expect(formatDuration(null, null)).toBe('—')
    expect(formatDuration('2026-10-06T10:00:00Z', '2026-10-06T10:00:00Z')).toBe('0 dk 0 sn')
    expect(formatDuration('2026-10-06T10:00:00Z', '2026-10-06T11:02:03Z')).toBe('1 sa 2 dk 3 sn')
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-10-06T10:02:00Z'))
    expect(formatDuration('2026-10-06T10:00:00Z', null)).toBe('2 dk 0 sn (devam ediyor)')
  })

  it('maps local day boundaries to inclusive UTC instants', () => {
    expect(dateFilterInstant('')).toBeUndefined()
    expect(dateFilterInstant('2026-10-06')).toBe(new Date('2026-10-06T00:00:00.000').toISOString())
    expect(dateFilterInstant('2026-10-06', true)).toBe(new Date('2026-10-06T23:59:59.999').toISOString())
  })
})
