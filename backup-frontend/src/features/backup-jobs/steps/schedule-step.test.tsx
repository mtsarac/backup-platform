import { screen } from '@testing-library/react'
import { expect, it } from 'vitest'
import { render } from '@/test/render'
import type { JobFormState } from '@/features/backup-jobs/use-job-form'
import { ScheduleStep } from './schedule-step'

it('opens advanced settings and exposes an invalid duration for focus', () => {
  const form = {
    values: {
      scheduled: true,
      schedule: { cronExpression: '0 2 * * *', timeZone: 'UTC', missedRunGraceSeconds: 61, enabled: true },
      overlap: 'Skip', retry: { maxAttempts: 1, initialDelaySeconds: -1, maxDelaySeconds: 60, backoff: 'Fixed', jitter: 0 },
      retention: { enabled: true, keepForSeconds: 604800, deleteGracePeriodSeconds: 0 },
    },
    errors: { 'retryPolicy.initialDelaySeconds': 'Invalid delay' }, canAdmin: true, set: () => {}, job: {},
  } as unknown as JobFormState
  render(<ScheduleStep form={form} />)
  const input = screen.getByLabelText('İlk gecikme')
  expect(input.closest('details')).toHaveAttribute('open')
  expect(input).toHaveAttribute('aria-invalid', 'true')
  input.focus()
  expect(input).toHaveFocus()
})
