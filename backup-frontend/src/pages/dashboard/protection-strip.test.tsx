import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { render } from '@/test/render'
import { describe, expect, it } from 'vitest'
import { ProtectionStrip } from './protection-strip'
import type { StripDay } from '@/features/dashboard/protection'

const days = Array.from({ length: 14 }, (_, index): StripDay => ({ date: `2026-11-${String(15 + index).padStart(2, '0')}`, status: index === 3 ? 'Failed' : 'Succeeded', attemptCount: 1 }))

describe('ProtectionStrip', () => {
  it('is one Tab stop, moves with arrow keys and spells out the chosen day for touch', async () => {
    render(<><button>before</button><ProtectionStrip days={days} jobName="Muhasebe" /><button>after</button></>)
    const cells = screen.getAllByRole('listitem')
    expect(cells.filter(cell => cell.tabIndex === 0)).toHaveLength(1)
    await userEvent.tab(); await userEvent.tab()
    expect(cells[13]).toHaveFocus()
    await userEvent.tab()
    expect(screen.getByRole('button', { name: 'after' })).toHaveFocus()
    await userEvent.click(cells[3])
    expect(screen.getByText(cells[3].getAttribute('aria-label')!, { selector: 'p' })).toBeInTheDocument()
  })
})
