import { useState } from 'react'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { Tabs } from './tabs'

function SessionTabs() {
  const [value, setValue] = useState('overview')
  return <Tabs idPrefix="session" label="Oturum bölümleri" value={value} onChange={setValue} items={[
    { value: 'overview', label: 'Özet' }, { value: 'customer', label: 'Müşteri verisi', disabled: true }, { value: 'audit', label: 'Audit' },
  ]} />
}

describe('Tab keyboard navigation', () => {
  it('skips restricted sections, wraps, and moves focus with selection', async () => {
    render(<SessionTabs />)
    const user = userEvent.setup()
    const overview = screen.getByRole('tab', { name: 'Özet' })
    const audit = screen.getByRole('tab', { name: 'Audit' })
    expect(screen.getByRole('tab', { name: 'Müşteri verisi' })).toBeDisabled()
    await user.click(overview)
    await user.keyboard('{ArrowRight}')
    expect(audit).toHaveFocus()
    expect(audit).toHaveAttribute('aria-selected', 'true')
    expect(overview).toHaveAttribute('tabindex', '-1')
    await user.keyboard('{ArrowRight}')
    expect(overview).toHaveFocus()
    await user.keyboard('{ArrowLeft}')
    expect(audit).toHaveFocus()
    await user.keyboard('{Home}')
    expect(overview).toHaveFocus()
    await user.keyboard('{End}')
    expect(audit).toHaveFocus()
  })
})
