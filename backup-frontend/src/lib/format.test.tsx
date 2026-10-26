import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { RelativeTime } from '@/components/data/relative-time'
import { changeLanguage } from '@/lib/i18n/runtime'
import { formatBytes, formatDateTime, formatDuration, formatRelativeTime, formatSeconds } from './format'

const now = new Date('2026-10-06T12:00:00Z')

describe('formatBytes', () => {
  it('shows one friendly unit: GB, falling back to MB, never KB or bytes', () => {
    expect(formatBytes(5_500_000_000)).toBe('5,5 GB')
    expect(formatBytes(500_000_000)).toBe('500 MB')
    expect(formatBytes(250_000)).toBe('0,25 MB')
    expect(formatBytes(0)).toBe('0 MB')
    expect(formatBytes(-5)).toBe('0 MB')
  })

  it('follows the interface language', () => {
    changeLanguage('en')
    expect(formatBytes(5_500_000_000)).toBe('5.5 GB')
  })
})

describe('formatRelativeTime', () => {
  it('reads recency in everyday Turkish', () => {
    expect(formatRelativeTime('2026-10-06T09:00:00Z', now)).toBe('3 saat önce')
    expect(formatRelativeTime('2026-10-04T12:00:00Z', now)).toBe('2 gün önce')
    expect(formatRelativeTime('2026-10-06T11:55:00Z', now)).toBe('5 dakika önce')
    expect(formatRelativeTime('2026-10-06T15:00:00Z', now)).toBe('3 saat sonra')
    expect(formatRelativeTime('2026-10-06T11:59:50Z', now)).toBe('şimdi')
    expect(formatRelativeTime('2026-10-06T11:58:00Z', now)).toBe('2 dakika önce')
  })

  it('reads recency in English when the language is switched', () => {
    changeLanguage('en')
    expect(formatRelativeTime('2026-10-06T09:00:00Z', now)).toBe('3 hours ago')
  })

  it('shows a dash for a missing or invalid time', () => {
    expect(formatRelativeTime(null, now)).toBe('—')
    expect(formatRelativeTime('not a date', now)).toBe('—')
  })
})

describe('formatDateTime', () => {
  it('gives one exact date and time, or the fallback when missing', () => {
    expect(formatDateTime('2026-10-06T09:30:00Z')).toMatch(/2026/)
    expect(formatDateTime(undefined)).toBe('—')
    expect(formatDateTime(null, 'Henüz bildirilmedi')).toBe('Henüz bildirilmedi')
  })
})

describe('formatDuration', () => {
  it('formats elapsed time between two instants', () => {
    expect(formatDuration(null, null)).toBe('—')
    expect(formatDuration('2026-10-06T10:00:00Z', '2026-10-06T11:02:03Z')).toBe('1 sa 2 dk 3 sn')
    expect(formatSeconds(75)).toBe('1 dk 15 sn')
  })
})

describe('RelativeTime', () => {
  it('shows the recency and keeps the exact time available on hover', () => {
    const at = new Date(Date.now() - 3 * 3_600_000).toISOString()
    render(<RelativeTime value={at} />)
    const time = screen.getByText('3 saat önce')
    expect(time).toHaveAttribute('title', formatDateTime(at))
    expect(time).toHaveAttribute('datetime', at)
  })

  it('renders a dash when there is no time', () => {
    render(<RelativeTime value={null} />)
    expect(screen.getByText('—')).toBeInTheDocument()
  })
})
