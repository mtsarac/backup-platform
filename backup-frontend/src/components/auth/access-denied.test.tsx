import { cleanup, screen } from '@testing-library/react'
import { render } from '@/test/render'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { AccessDenied } from './access-denied'

afterEach(cleanup)
const mount = (ui: React.ReactElement) => render(<MemoryRouter>{ui}</MemoryRouter>)

describe('AccessDenied', () => {
  it('says what is denied, that nothing changed, and points to someone who can grant access', () => {
    mount(<AccessDenied what="Bu sayfayı yalnızca yöneticiler görebilir." />)
    const alert = screen.getByRole('alert')
    expect(screen.getByRole('heading', { name: 'Bu alan için yetkiniz yok' })).toBeInTheDocument()
    expect(alert).toHaveTextContent('Bu sayfayı yalnızca yöneticiler görebilir.')
    expect(alert).toHaveTextContent('Yöneticinize danışın')
    expect(alert).toHaveTextContent('Verileriniz için')
  })
  it('links home by default and accepts a nearer next step', () => {
    mount(<AccessDenied what="x" />)
    expect(screen.getByRole('link', { name: 'Ana sayfaya dön' })).toHaveAttribute('href', '/')
    cleanup()
    mount(<AccessDenied what="x" control={<a href="/agents">Geri</a>} />)
    expect(screen.queryByRole('link', { name: 'Ana sayfaya dön' })).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Geri' })).toBeInTheDocument()
  })
  it('fills the page when standalone', () => {
    mount(<AccessDenied standalone what="x" />)
    expect(screen.getByRole('main')).toContainElement(screen.getByRole('alert'))
  })
})
