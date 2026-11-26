import { describe, expect, it } from 'vitest'
import { MemoryRouter, Route, Routes, Link } from 'react-router-dom'
import userEvent from '@testing-library/user-event'
import { render, screen, waitFor } from '@/test/render'
import { PageHeader } from './page-header'

describe('PageHeader', () => {
  it('puts the page title in the browser tab title', () => {
    const view = render(<MemoryRouter><PageHeader title="Yedek dosyaları" /></MemoryRouter>)
    expect(document.title).toBe('Yedek dosyaları · Yedekleme Platformu')
    view.rerender(<MemoryRouter><PageHeader title="Yedekleme geçmişi" /></MemoryRouter>)
    expect(document.title).toBe('Yedekleme geçmişi · Yedekleme Platformu')
  })

  it('moves focus to the new heading when navigation mounts it, but not on the first load or inside a layout', async () => {
    const user = userEvent.setup()
    const Home = () => <PageHeader title="Ana sayfa" />
    const Agents = () => <PageHeader title="Aracılar" />
    const Layout = () => <><PageHeader title="Yedeklemeler" /><Link to="/b">sekme</Link></>
    render(<MemoryRouter initialEntries={['/a']}><Link to="/c">git</Link><Routes>
      <Route path="/a" element={<Home />} />
      <Route path="/b" element={<Layout />} />
      <Route path="/c" element={<Agents />} />
    </Routes></MemoryRouter>)
    expect(screen.getByRole('heading', { name: 'Ana sayfa' })).not.toHaveFocus()
    await user.click(screen.getByRole('link', { name: 'git' }))
    await waitFor(() => expect(screen.getByRole('heading', { name: 'Aracılar' })).toHaveFocus())
  })

  it('keeps focus on a tab link while the layout heading stays mounted', async () => {
    const user = userEvent.setup()
    const Layout = () => <><PageHeader title="Yedeklemeler" /><Link to="/tab2">sekme</Link><Routes><Route path="/tab2" element={<p>iki</p>} /></Routes></>
    render(<MemoryRouter initialEntries={['/tab1']}><Routes><Route path="/*" element={<Layout />} /></Routes></MemoryRouter>)
    await user.click(screen.getByRole('link', { name: 'sekme' }))
    await screen.findByText('iki')
    expect(screen.getByRole('link', { name: 'sekme' })).toHaveFocus()
  })
})
