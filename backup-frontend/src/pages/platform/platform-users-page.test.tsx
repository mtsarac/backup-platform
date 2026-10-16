import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import * as api from '@/features/platform/api'
import { paged, renderPlatform } from '@/features/platform/test-utils'
import type { PlatformUser } from '@/features/platform/types'
import { ApiError } from '@/lib/api/client'
import { PlatformUsersPage } from './platform-users-page'

vi.mock('@/features/platform/api')

const admin: PlatformUser = { userId: 'u1', displayName: 'Root Admin', loginIdentifier: 'root', isPlatformAdmin: true, membershipCount: 0 }
const member: PlatformUser = { userId: 'u2', displayName: 'Ayşe Yılmaz', loginIdentifier: 'ayse', isPlatformAdmin: false, membershipCount: 3 }

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getUsers).mockResolvedValue(paged([admin, member]))
})

async function fillForm(login: string, name: string, password: string) {
  await userEvent.click(screen.getByRole('button', { name: 'Yeni kullanıcı' }))
  await userEvent.type(screen.getByLabelText(/Kullanıcı adı/), login)
  await userEvent.type(screen.getByLabelText('Görünen ad'), name)
  await userEvent.type(screen.getByLabelText(/Şifre/), password)
}

describe('platform users', () => {
  it('lists users and shows PlatformAdmin as a badge, never as a tenant role', async () => {
    renderPlatform(<PlatformUsersPage />)
    const adminRow = (await screen.findByText('Root Admin')).closest('tr')!
    expect(within(adminRow).getByText("Platform Yöneticisi")).toBeInTheDocument()
    const memberRow = screen.getByText('Ayşe Yılmaz').closest('tr')!
    expect(within(memberRow).queryByText("Platform Yöneticisi")).not.toBeInTheDocument()
    expect(within(memberRow).getByText('3')).toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: 'Platform rolü' })).toBeInTheDocument()
  })

  it('creates a user, clears the password and refreshes the list', async () => {
    vi.mocked(api.createUser).mockResolvedValue({ userId: 'u3', displayName: 'Deniz Kaya', loginIdentifier: 'deniz', isPlatformAdmin: false, membershipCount: 0 })
    renderPlatform(<PlatformUsersPage />)
    await screen.findByText('Root Admin')
    vi.mocked(api.getUsers).mockResolvedValue(paged([admin, member, { userId: 'u3', displayName: 'Deniz Kaya', loginIdentifier: 'deniz', isPlatformAdmin: false, membershipCount: 0 }]))
    await fillForm('deniz', 'Deniz Kaya', 'a-very-long-password')
    await userEvent.click(screen.getByRole('button', { name: 'Kullanıcı oluştur' }))
    expect(api.createUser).toHaveBeenCalledWith({ loginIdentifier: 'deniz', displayName: 'Deniz Kaya', password: 'a-very-long-password' })
    expect(await screen.findByRole('cell', { name: 'Deniz Kaya' })).toBeInTheDocument()
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(document.body.innerHTML).not.toContain('a-very-long-password')
    await userEvent.click(screen.getByRole('button', { name: 'Yeni kullanıcı' }))
    expect(screen.getByLabelText(/Şifre/)).toHaveValue('')
    expect(screen.getByLabelText(/Kullanıcı adı/)).toHaveValue('')
  })

  it('reports a taken login without echoing the password', async () => {
    vi.mocked(api.createUser).mockRejectedValue(new ApiError(409, { error: 'login_taken' }))
    renderPlatform(<PlatformUsersPage />)
    await screen.findByText('Root Admin')
    await fillForm('root', 'Başka', 'a-very-long-password')
    await userEvent.click(screen.getByRole('button', { name: 'Kullanıcı oluştur' }))
    expect(await screen.findByText('Bu kullanıcı adı zaten kullanılıyor.')).toBeInTheDocument()
    expect(screen.getByLabelText(/Şifre/)).toHaveValue('')
  })

  it('marks the invalid field on a 400', async () => {
    vi.mocked(api.createUser).mockRejectedValue(new ApiError(400, { error: 'invalid_request', field: 'password' }))
    renderPlatform(<PlatformUsersPage />)
    await screen.findByText('Root Admin')
    await fillForm('deniz', 'Deniz', 'short')
    await userEvent.click(screen.getByRole('button', { name: 'Kullanıcı oluştur' }))
    await screen.findByRole('alert')
    expect(screen.getByLabelText(/Şifre/)).toBeInvalid()
    expect(screen.getByLabelText(/Kullanıcı adı/)).toBeValid()
  })

  it('renders the authorization state for 403', async () => {
    vi.mocked(api.getUsers).mockRejectedValue(new ApiError(403))
    renderPlatform(<PlatformUsersPage />)
    expect(await screen.findByText('Bu alan için yetkiniz yok')).toBeInTheDocument()
  })
})
