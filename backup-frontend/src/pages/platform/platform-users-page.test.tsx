import { beforeEach, describe, expect, it, vi } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import * as api from '@/features/platform/api'
import { paged, renderPlatform } from '@/features/platform/test-utils'
import type { PlatformUser } from '@/features/platform/types'
import { ApiError } from '@/lib/api/client'
import { PlatformUsersPage } from './platform-users-page'

vi.mock('@/features/platform/api')

const admin: PlatformUser = { userId: 'u1', displayName: 'Root Admin', username: 'root', email: 'root@example.com', role: 'PlatformAdmin', membershipCount: 0 }
const member: PlatformUser = { userId: 'u2', displayName: 'Ayşe Yılmaz', username: 'ayse', email: 'ayse@example.com', role: 'UserAdmin', membershipCount: 3 }
const tenants = [{ tenantId: 't1', name: 'Acme', memberCount: 1, agentCount: 0, enrollmentEnabled: true }, { tenantId: 't2', name: 'Beta', memberCount: 0, agentCount: 0, enrollmentEnabled: true }]

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getUsers).mockResolvedValue(paged([admin, member]))
  vi.mocked(api.getTenants).mockResolvedValue(paged(tenants))
})

async function createForm() {
  await userEvent.click(screen.getByRole('button', { name: 'Yeni kullanıcı' }))
  await userEvent.type(screen.getByLabelText('Kullanıcı adı'), 'deniz')
  await userEvent.type(screen.getByLabelText('E-posta'), 'deniz@example.com')
  await userEvent.type(screen.getByLabelText('Görünen ad'), 'Deniz Kaya')
  await userEvent.type(screen.getByLabelText(/Şifre/), 'secret-password')
}

describe('platform users', () => {
  it('lists account details and opens the create action with an empty list', async () => {
    vi.mocked(api.getUsers).mockResolvedValueOnce(paged([]))
    renderPlatform(<PlatformUsersPage />)
    await screen.findByText('Henüz kullanıcı yok.')
    await userEvent.click(screen.getByRole('button', { name: 'Yeni kullanıcı' }))
    expect(screen.getByRole('dialog')).toHaveTextContent('Yeni kullanıcı')
  })

  it('requires a workspace for UserAdmin and creates memberships in multiple workspaces', async () => {
    vi.mocked(api.createUser).mockResolvedValue(member)
    renderPlatform(<PlatformUsersPage />)
    expect(await screen.findByText('ayse@example.com')).toBeInTheDocument()
    await createForm()
    const submit = screen.getByRole('button', { name: 'Kullanıcı oluştur' })
    expect(submit).toBeDisabled()
    await userEvent.click(await screen.findByRole('checkbox', { name: 'Acme' }))
    await userEvent.click(screen.getByRole('checkbox', { name: 'Beta' }))
    await userEvent.click(submit)
    expect(api.createUser).toHaveBeenCalledWith({ username: 'deniz', email: 'deniz@example.com', displayName: 'Deniz Kaya', password: 'secret-password', role: 'UserAdmin', tenantIds: ['t1', 't2'] })
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('creates PlatformAdmin without memberships after changing roles', async () => {
    vi.mocked(api.createUser).mockResolvedValue(admin)
    renderPlatform(<PlatformUsersPage />)
    await screen.findByText('Root Admin')
    await createForm()
    await userEvent.click(await screen.findByRole('checkbox', { name: 'Acme' }))
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Rol' }), 'PlatformAdmin')
    expect(screen.queryByRole('checkbox', { name: 'Acme' })).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Kullanıcı oluştur' }))
    expect(api.createUser).toHaveBeenCalledWith(expect.objectContaining({ role: 'PlatformAdmin', tenantIds: [] }))
  })

  it('edits an existing account and refreshes the list', async () => {
    vi.mocked(api.updateUser).mockResolvedValue({ ...member, displayName: 'Ayşe Yeni' })
    renderPlatform(<PlatformUsersPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'Ayşe Yılmaz kullanıcısını düzenle' }))
    const dialog = screen.getByRole('dialog')
    await userEvent.clear(within(dialog).getByLabelText('Görünen ad'))
    await userEvent.type(within(dialog).getByLabelText('Görünen ad'), 'Ayşe Yeni')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Kaydet' }))
    expect(api.updateUser).toHaveBeenCalledWith('u2', { displayName: 'Ayşe Yeni', username: 'ayse', email: 'ayse@example.com', role: 'UserAdmin' })
    await waitFor(() => expect(api.getUsers).toHaveBeenCalledTimes(2))
  })

  it('shows API validation errors without displaying the password', async () => {
    vi.mocked(api.createUser).mockRejectedValue(new ApiError(409, { error: 'identity_taken' }))
    renderPlatform(<PlatformUsersPage />)
    await screen.findByText('Root Admin')
    await createForm()
    await userEvent.click(await screen.findByRole('checkbox', { name: 'Acme' }))
    await userEvent.click(screen.getByRole('button', { name: 'Kullanıcı oluştur' }))
    expect(await screen.findByText('Kullanıcı adı veya e-posta zaten kullanılıyor.')).toBeInTheDocument()
    expect(screen.getByRole('dialog')).not.toHaveTextContent('secret-password')
  })

  it('shows update errors and keeps the dialog open', async () => {
    vi.mocked(api.updateUser).mockRejectedValue(new ApiError(400, { error: 'platform_admin_membership_not_allowed' }))
    renderPlatform(<PlatformUsersPage />)
    await userEvent.click(await screen.findByRole('button', { name: 'Ayşe Yılmaz kullanıcısını düzenle' }))
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Rol' }), 'PlatformAdmin')
    await userEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
    expect(await screen.findByText('Platform yöneticisinin çalışma alanı üyeliği olamaz.')).toBeInTheDocument()
    expect(screen.getByRole('dialog')).toBeInTheDocument()
  })

  it('renders the authorization state for 403', async () => {
    vi.mocked(api.getUsers).mockRejectedValue(new ApiError(403))
    renderPlatform(<PlatformUsersPage />)
    expect(await screen.findByText('Bu alan için yetkiniz yok')).toBeInTheDocument()
  })
})
