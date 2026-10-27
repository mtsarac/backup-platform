import { screen } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Outlet, Route, Routes } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import { RouteTabs } from './tabs'

function mount(path: string) {
  render(<MemoryRouter initialEntries={[path]}><Routes>
    <Route path="/hub" element={<><RouteTabs label="Bölümler" items={[{ to: '/hub', label: 'Birinci', end: true }, { to: '/hub/two', label: 'İkinci' }]} /><Outlet /></>}>
      <Route index element={<p>birinci içerik</p>} /><Route path="two" element={<p>ikinci içerik</p>} />
    </Route>
  </Routes></MemoryRouter>)
}

describe('RouteTabs in a layout route', () => {
  it('exposes tabs as links in a labelled navigation, marks the current one and swaps the outlet', async () => {
    mount('/hub')
    const nav = screen.getByRole('navigation', { name: 'Bölümler' })
    expect(screen.getByRole('link', { name: 'Birinci' })).toHaveAttribute('aria-current', 'page')
    expect(screen.getByRole('link', { name: 'İkinci' })).not.toHaveAttribute('aria-current')
    await userEvent.click(screen.getByRole('link', { name: 'İkinci' }))
    expect(screen.getByText('ikinci içerik')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'İkinci' })).toHaveAttribute('aria-current', 'page')
    expect(screen.getByRole('link', { name: 'Birinci' })).not.toHaveAttribute('aria-current')
    expect(nav).toBeInTheDocument()
  })

  it('opens a deep link on the matching tab', () => {
    mount('/hub/two')
    expect(screen.getByRole('link', { name: 'İkinci' })).toHaveAttribute('aria-current', 'page')
    expect(screen.getByText('ikinci içerik')).toBeInTheDocument()
  })
})
