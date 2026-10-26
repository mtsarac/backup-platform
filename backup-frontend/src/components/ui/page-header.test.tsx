import { describe, expect, it } from 'vitest'
import { MemoryRouter } from 'react-router-dom'
import { render } from '@/test/render'
import { PageHeader } from './page-header'

describe('PageHeader', () => {
  it('puts the page title in the browser tab title', () => {
    const view = render(<MemoryRouter><PageHeader title="Yedek dosyaları" /></MemoryRouter>)
    expect(document.title).toBe('Yedek dosyaları · Yedekleme Platformu')
    view.rerender(<MemoryRouter><PageHeader title="Yedekleme geçmişi" /></MemoryRouter>)
    expect(document.title).toBe('Yedekleme geçmişi · Yedekleme Platformu')
  })
})
