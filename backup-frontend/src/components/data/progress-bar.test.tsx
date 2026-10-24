import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { ProgressBar } from './progress-bar'

describe('ProgressBar', () => {
  it('announces its value, including zero', () => {
    render(<ProgressBar label="Yükleme" value={0} />)
    expect(screen.getByRole('progressbar', { name: 'Yükleme' })).toHaveAttribute('aria-valuenow', '0')
  })

  it('announces no value when none was reported', () => {
    render(<ProgressBar label="Yükleme" />)
    expect(screen.getByRole('progressbar', { name: 'Yükleme' })).not.toHaveAttribute('aria-valuenow')
  })

  it('announces the given value when it differs from what is drawn', () => {
    render(<ProgressBar label="Kota" value={40} reserved={20} now={60} valueText="6 / 10 GB" />)
    const bar = screen.getByRole('progressbar', { name: 'Kota' })
    expect(bar).toHaveAttribute('aria-valuenow', '60')
    expect(bar).toHaveAttribute('aria-valuetext', '6 / 10 GB')
  })
})
