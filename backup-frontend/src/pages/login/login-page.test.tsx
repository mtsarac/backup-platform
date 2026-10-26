import { screen } from '@testing-library/react'
import { render } from '@/test/render'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import { LoginPage } from './login-page'

vi.mock('@/features/auth/use-auth', () => ({ useAuth: () => ({ login: vi.fn(), isAuthenticated: false, sessionExpired: false }) }))

describe('Sign-in page', () => {
  it('asks for a username, which is what the backend accepts, not an email address', () => {
    render(<MemoryRouter><LoginPage /></MemoryRouter>)
    const field = screen.getByRole('textbox', { name: 'Kullanıcı adı' })
    expect(field).toHaveAttribute('autocomplete', 'username')
    expect(screen.queryByLabelText(/e-posta/i)).not.toBeInTheDocument()
  })
})
