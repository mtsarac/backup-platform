import { fireEvent, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { renderPlatform } from '@/features/platform/test-utils'
import { ApiError } from '@/lib/api/client'
import * as api from './api'
import { BreakGlassRequestDialog } from './break-glass-request-dialog'
import { makeBreakGlass } from './test-fixtures'
import { tx } from '@/lib/i18n'
import * as platformApi from '@/features/platform/api'
import { paged } from '@/features/platform/test-utils'

vi.mock('./api')
vi.mock('@/features/platform/api')
beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(platformApi.getTenants).mockResolvedValue(paged([
    { tenantId, name: 'Acme', memberCount: 1, agentCount: 1, enrollmentEnabled: true },
    { tenantId: 'tenant-other', name: 'Other company', memberCount: 0, agentCount: 0, enrollmentEnabled: false },
  ]))
  vi.mocked(platformApi.getTenant).mockResolvedValue({ tenantId, name: 'Acme', memberCount: 1, enabledBackupJobs: 1, agents: { total: 1, online: 1, offline: 0, unknown: 0, disabled: 0, revoked: 0 }, enrollment: { enabled: true, changedAt: null } })
  vi.mocked(api.getBreakGlassArtifacts).mockResolvedValue(paged([{ id: artifactId, databaseName: 'Accounts', jobName: 'Nightly', createdAt: '2026-10-09T10:00:00Z' }]))
})
const tenantId = '22222222-2222-2222-2222-222222222222'
const artifactId = '11111111-1111-1111-1111-111111111111'
const mount = (props: Partial<Parameters<typeof BreakGlassRequestDialog>[0]> = {}) => renderPlatform(<BreakGlassRequestDialog onClose={vi.fn()} {...props} />)

describe('break-glass request dialog', () => {
  it('requires selected records and validates reason and TTL before any request', async () => {
    mount()
    fireEvent.change(await screen.findByRole('combobox', { name: tx('Tenant') }), { target: { value: 'not a choice' } })
    fireEvent.change(screen.getByLabelText('Süre (dakika)'), { target: { value: '500' } })
    fireEvent.click(screen.getByRole('button', { name: 'İstek aç' }))
    expect(await screen.findByText(tx('Tenant seçin'))).toBeInTheDocument()
    expect(screen.getByText(tx('Yedek dosyası seçin'))).toBeInTheDocument()
    expect(screen.getByText('Acil destek erişimi için gerekçe girin.')).toBeInTheDocument()
    expect(screen.getByText('Süre 1 ile 240 dakika arasında tam sayı olmalıdır.')).toBeInTheDocument()
    expect(api.createBreakGlassRequest).not.toHaveBeenCalled()
  })

  it('opens the request with the prefilled target and navigates to it', async () => {
    vi.mocked(api.createBreakGlassRequest).mockResolvedValue(makeBreakGlass({ id: 'new-request' }))
    vi.mocked(api.getBreakGlassRequest).mockResolvedValue(makeBreakGlass({ id: 'new-request' }))
    mount({ initialTenantId: tenantId, initialArtifactId: artifactId })
    fireEvent.change(await screen.findByLabelText('Gerekçe'), { target: { value: '  incident 1  ' } })
    fireEvent.click(screen.getByRole('button', { name: 'İstek aç' }))
    await waitFor(() => expect(api.createBreakGlassRequest).toHaveBeenCalledWith({ tenantId, artifactId, reason: 'incident 1', ttlMinutes: 60 }))
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/platform/break-glass/new-request'))
  })

  it('preserves deep-linked targets', async () => {
    vi.mocked(api.createBreakGlassRequest).mockResolvedValue(makeBreakGlass({ id: 'new-request' }))
    mount({ initialTenantId: tenantId, initialArtifactId: artifactId })
    fireEvent.change(await screen.findByLabelText('Gerekçe'), { target: { value: 'investigate' } })
    fireEvent.click(screen.getByRole('button', { name: 'İstek aç' }))
    await waitFor(() => expect(api.createBreakGlassRequest).toHaveBeenCalledWith({ tenantId, artifactId, reason: 'investigate', ttlMinutes: 60 }))
  })

  it('shows why the server refused an artifact that is not encrypted', async () => {
    vi.mocked(api.createBreakGlassRequest).mockRejectedValue(new ApiError(409, { error: 'artifact_not_encrypted' }))
    mount({ initialTenantId: tenantId, initialArtifactId: artifactId })
    fireEvent.change(await screen.findByLabelText('Gerekçe'), { target: { value: 'because' } })
    fireEvent.click(screen.getByRole('button', { name: 'İstek aç' }))
    expect(await screen.findByText('Bu yedek dosyası şifreli değil; şifre çözme veya acil destek erişimi uygulanamaz.')).toBeInTheDocument()
  })

  it('searches encrypted backups by name without any support session and sends the unchanged IDs', async () => {
    vi.mocked(api.createBreakGlassRequest).mockResolvedValue(makeBreakGlass({ id: 'new-request' }))
    mount()
    fireEvent.change(await screen.findByRole('combobox', { name: tx('Tenant') }), { target: { value: 'Acme' } })
    fireEvent.click(await screen.findByRole('option', { name: 'Acme' }))
    fireEvent.change(await screen.findByRole('combobox', { name: tx('Yedek dosyası') }), { target: { value: 'Accounts' } })
    fireEvent.click(await screen.findByRole('option', { name: /Accounts/ }))
    fireEvent.change(screen.getByLabelText('Gerekçe'), { target: { value: 'investigate' } })
    fireEvent.click(screen.getByRole('button', { name: 'İstek aç' }))
    await waitFor(() => expect(api.createBreakGlassRequest).toHaveBeenCalledWith({ tenantId, artifactId, reason: 'investigate', ttlMinutes: 60 }))
    expect(api.getBreakGlassArtifacts).toHaveBeenCalledWith(tenantId, expect.any(AbortSignal))
  })

  it('clears the selected backup when the tenant changes', async () => {
    mount({ initialTenantId: tenantId, initialArtifactId: artifactId })
    const picker = await screen.findByRole('combobox', { name: tx('Tenant') })
    fireEvent.change(picker, { target: { value: 'Other' } })
    fireEvent.click(await screen.findByRole('option', { name: 'Other company' }))
    expect(screen.getByRole('combobox', { name: tx('Yedek dosyası') })).toHaveValue('')
    fireEvent.change(screen.getByLabelText('Gerekçe'), { target: { value: 'investigate' } })
    fireEvent.click(screen.getByRole('button', { name: 'İstek aç' }))
    expect(api.createBreakGlassRequest).not.toHaveBeenCalled()
  })

  it('says so when the tenant has no encrypted backup', async () => {
    vi.mocked(api.getBreakGlassArtifacts).mockResolvedValue(paged([]))
    mount({ initialTenantId: tenantId })
    expect(await screen.findByText(tx('Bu müşterinin şifreli yedek dosyası yok.'))).toBeInTheDocument()
  })
})
