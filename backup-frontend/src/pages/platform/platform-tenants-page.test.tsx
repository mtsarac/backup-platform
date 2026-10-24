import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import * as api from '@/features/platform/api'
import { paged, renderPlatform } from '@/features/platform/test-utils'
import type { PlatformTenant } from '@/features/platform/types'
import { ApiError } from '@/lib/api/client'
import { PlatformTenantsPage } from './platform-tenants-page'
import { setViewport } from '@/test/viewport'

vi.mock('@/features/platform/api')

const acme: PlatformTenant = { tenantId: 'tenant-a', name: 'Acme A.Ş.', memberCount: 4, agentCount: 2, enrollmentEnabled: true }
const globex: PlatformTenant = { tenantId: 'tenant-b', name: 'Globex', memberCount: 1, agentCount: 0, enrollmentEnabled: false }

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getTenants).mockResolvedValue(paged([acme, globex]))
})

describe('platform tenants list', () => {
  it('shows a loading state before rows', async () => {
    vi.mocked(api.getTenants).mockReturnValue(new Promise(() => undefined))
    renderPlatform(<PlatformTenantsPage />)
    expect(await screen.findByRole('status')).toHaveTextContent('Yükleniyor')
  })

  it('renders rows with detail links and enrollment state', async () => {
    renderPlatform(<PlatformTenantsPage />)
    const row = (await screen.findByRole('link', { name: 'Acme A.Ş.' })).closest('tr')!
    expect(screen.getByRole('link', { name: 'Acme A.Ş.' })).toHaveAttribute('href', '/platform/tenants/tenant-a')
    expect(within(row).getByText('4')).toBeInTheDocument()
    expect(within(row).getByText('Kayıt açık')).toBeInTheDocument()
    expect(within(screen.getByRole('link', { name: 'Globex' }).closest('tr')!).getByText('Kayıt kapalı')).toBeInTheDocument()
    expect(api.getTenants).toHaveBeenCalledWith({ search: '', page: 1, pageSize: 25 }, expect.any(AbortSignal))
  })

  it('shows workspace names, people, computers and enrollment as cards on small screens', async () => {
    setViewport(false)
    renderPlatform(<PlatformTenantsPage />)
    const link = await screen.findByRole('link', { name: 'Acme A.Ş.' })
    const card = within(link.closest('li')!)
    expect(screen.getByRole('list', { name: 'Çalışma alanları' })).toBeInTheDocument()
    expect(card.getByText('Kişiler')).toBeInTheDocument()
    expect(card.getByText('4')).toBeInTheDocument()
    expect(card.getByText('Yedekleme bilgisayarları')).toBeInTheDocument()
    expect(card.getByText('2')).toBeInTheDocument()
    expect(card.getByText('Kayıt açık')).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
  })

  it('paginates through the URL and searches from page one', async () => {
    vi.mocked(api.getTenants).mockResolvedValue(paged([acme], { totalCount: 60, pageSize: 25 }))
    renderPlatform(<PlatformTenantsPage />, { path: '/platform/tenants?page=2' })
    expect(await screen.findByText('60 kayıt · Sayfa 2 / 3')).toBeInTheDocument()
    expect(api.getTenants).toHaveBeenCalledWith({ search: '', page: 2, pageSize: 25 }, expect.any(AbortSignal))
    fireEvent.click(screen.getByRole('button', { name: 'Sonraki' }))
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/platform/tenants?page=3'))
    await screen.findByText('60 kayıt · Sayfa 3 / 3')
    expect(screen.getByRole('button', { name: 'Sonraki' })).toBeDisabled()
    await userEvent.type(screen.getByRole('searchbox', { name: "Çalışma alanı ara" }), 'acme')
    await userEvent.click(screen.getByRole('button', { name: 'Ara' }))
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/platform/tenants?search=acme'))
    expect(api.getTenants).toHaveBeenLastCalledWith({ search: 'acme', page: 1, pageSize: 25 }, expect.any(AbortSignal))
  })

  it('renders an empty state', async () => {
    vi.mocked(api.getTenants).mockResolvedValue(paged<PlatformTenant>([]))
    renderPlatform(<PlatformTenantsPage />)
    expect(await screen.findByText("Henüz çalışma alanı yok.")).toBeInTheDocument()
    expect(screen.queryByRole('navigation', { name: "Çalışma alanı sayfaları" })).not.toBeInTheDocument()
  })

  it('renders a server error as an alert', async () => {
    vi.mocked(api.getTenants).mockRejectedValue(new ApiError(500))
    renderPlatform(<PlatformTenantsPage />)
    expect(await screen.findByText('Sunucu isteği tamamlayamadı. Daha sonra tekrar deneyin.')).toBeInTheDocument()
  })

  it('renders the authorization state for a 403 instead of a generic error', async () => {
    vi.mocked(api.getTenants).mockRejectedValue(new ApiError(403))
    renderPlatform(<PlatformTenantsPage />)
    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Bu alan için yetkiniz yok')
    expect(within(alert).getByRole('link', { name: 'Ana sayfaya dön' })).toHaveAttribute('href', '/')
  })

  it('creates a tenant, refreshes the list and clears the form', async () => {
    vi.mocked(api.createTenant).mockResolvedValue({ tenantId: 'tenant-c', name: 'Initech', memberCount: 0, agentCount: 0, enrollmentEnabled: false })
    renderPlatform(<PlatformTenantsPage />)
    await screen.findByRole('link', { name: 'Acme A.Ş.' })
    vi.mocked(api.getTenants).mockResolvedValue(paged([acme, globex, { tenantId: 'tenant-c', name: 'Initech', memberCount: 0, agentCount: 0, enrollmentEnabled: false }]))
    await userEvent.click(screen.getByRole('button', { name: "Yeni çalışma alanı" }))
    await userEvent.type(screen.getByLabelText("Çalışma alanı adı"), '  Initech ')
    await userEvent.click(screen.getByRole('button', { name: "Çalışma alanı oluştur" }))
    expect(await screen.findByRole('link', { name: 'Initech' })).toBeInTheDocument()
    expect(api.createTenant).toHaveBeenCalledWith('Initech')
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(screen.getByRole('status')).toHaveTextContent('Initech')
    await userEvent.click(screen.getByRole('button', { name: "Yeni çalışma alanı" }))
    expect(screen.getByLabelText("Çalışma alanı adı")).toHaveValue('')
  })

  it('shows a validation failure for a rejected name', async () => {
    vi.mocked(api.createTenant).mockRejectedValue(new ApiError(400, { error: 'invalid_request', field: 'name' }))
    renderPlatform(<PlatformTenantsPage />)
    await screen.findByRole('link', { name: 'Acme A.Ş.' })
    await userEvent.click(screen.getByRole('button', { name: "Yeni çalışma alanı" }))
    await userEvent.type(screen.getByLabelText("Çalışma alanı adı"), 'x')
    await userEvent.click(screen.getByRole('button', { name: "Çalışma alanı oluştur" }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Girilen değer geçersiz')
    expect(screen.getByLabelText("Çalışma alanı adı")).toBeInvalid()
  })
})
