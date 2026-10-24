import { fireEvent, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { renderPlatform } from '@/features/platform/test-utils'
import { ApiError } from '@/lib/api/client'
import * as api from './api'
import { BreakGlassRequestDialog } from './break-glass-request-dialog'
import { makeBreakGlass } from './test-fixtures'
import { tx } from '@/lib/i18n'
import * as platformApi from '@/features/platform/api'
import * as supportApi from '@/features/platform/support/api'
import * as artifactApi from '@/features/artifacts/api'
import { paged } from '@/features/platform/test-utils'
import { makeArtifact, makeSession } from '@/features/platform/support/test-fixtures'

vi.mock('./api')
vi.mock('@/features/platform/api')
vi.mock('@/features/platform/support/api')
vi.mock('@/features/artifacts/api')
beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(platformApi.getTenants).mockResolvedValue(paged([
    { tenantId, name: 'Acme', memberCount: 1, agentCount: 1, enrollmentEnabled: true },
    { tenantId: 'tenant-other', name: 'Other company', memberCount: 0, agentCount: 0, enrollmentEnabled: false },
  ]))
  vi.mocked(platformApi.getTenant).mockResolvedValue({ tenantId, name: 'Acme', memberCount: 1, enabledBackupJobs: 1, agents: { total: 1, online: 1, offline: 0, unknown: 0, disabled: 0, revoked: 0 }, enrollment: { enabled: true, changedAt: null } })
  vi.mocked(supportApi.getSessions).mockResolvedValue(paged([makeSession({ tenantId, permissions: ['ViewBackupMetadata'] })]))
  vi.mocked(supportApi.getSession).mockResolvedValue(makeSession({ tenantId, permissions: ['ViewBackupMetadata'] }))
  vi.mocked(supportApi.getSupportArtifacts).mockResolvedValue(paged([makeArtifact({ id: artifactId, databaseName: 'Accounts' })]))
  vi.mocked(supportApi.getSupportArtifact).mockResolvedValue(makeArtifact({ id: artifactId, databaseName: 'Accounts' }))
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
    expect(screen.getByText('Break-glass için gerekçe girin.')).toBeInTheDocument()
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

  it('preserves deep-linked targets without granting metadata access when no support session exists', async () => {
    vi.mocked(supportApi.getSessions).mockResolvedValue(paged([]))
    vi.mocked(api.createBreakGlassRequest).mockResolvedValue(makeBreakGlass({ id: 'new-request' }))
    mount({ initialTenantId: tenantId, initialArtifactId: artifactId })
    fireEvent.change(await screen.findByLabelText('Gerekçe'), { target: { value: 'investigate' } })
    fireEvent.click(screen.getByRole('button', { name: 'İstek aç' }))
    await waitFor(() => expect(api.createBreakGlassRequest).toHaveBeenCalledWith({ tenantId, artifactId, reason: 'investigate', ttlMinutes: 60 }))
    expect(supportApi.getSupportArtifacts).not.toHaveBeenCalled()
    expect(artifactApi.listArtifacts).not.toHaveBeenCalled()
  })

  it('shows why the server refused an artifact that is not encrypted', async () => {
    vi.mocked(api.createBreakGlassRequest).mockRejectedValue(new ApiError(409, { error: 'artifact_not_encrypted' }))
    mount({ initialTenantId: tenantId, initialArtifactId: artifactId })
    fireEvent.change(await screen.findByLabelText('Gerekçe'), { target: { value: 'because' } })
    fireEvent.click(screen.getByRole('button', { name: 'İstek aç' }))
    expect(await screen.findByText('Bu Backup şifreli değil; şifre çözme veya break-glass uygulanamaz.')).toBeInTheDocument()
  })

  it('searches names through an authorized support session and sends the unchanged IDs', async () => {
    vi.mocked(api.createBreakGlassRequest).mockResolvedValue(makeBreakGlass({ id: 'new-request' }))
    mount()
    fireEvent.change(await screen.findByRole('combobox', { name: tx('Tenant') }), { target: { value: 'Acme' } })
    fireEvent.click(await screen.findByRole('option', { name: 'Acme' }))
    fireEvent.change(await screen.findByRole('combobox', { name: tx('Yedek dosyası') }), { target: { value: 'Accounts' } })
    fireEvent.click(await screen.findByRole('option', { name: /Accounts/ }))
    fireEvent.change(screen.getByLabelText('Gerekçe'), { target: { value: 'investigate' } })
    fireEvent.click(screen.getByRole('button', { name: 'İstek aç' }))
    await waitFor(() => expect(api.createBreakGlassRequest).toHaveBeenCalledWith({ tenantId, artifactId, reason: 'investigate', ttlMinutes: 60 }))
    expect(supportApi.getSupportArtifacts).toHaveBeenCalledWith('support-a', expect.anything(), expect.any(AbortSignal))
    expect(artifactApi.listArtifacts).not.toHaveBeenCalled()
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

  it.each([
    { permissions: [] },
    { isOwner: false },
    { status: 'Expired' as const },
    { expiresAt: '2026-10-09T11:59:59Z', serverNow: '2026-10-09T12:00:00Z' },
  ])('never loads backup metadata without a usable support session (%j)', async overrides => {
    vi.mocked(supportApi.getSessions).mockResolvedValue(paged([makeSession({ tenantId, ...overrides })]))
    mount({ initialTenantId: tenantId })
    expect(await screen.findByRole('link', { name: tx('Destek erişimi') })).toHaveAttribute('href', expect.stringContaining('/platform/support-sessions'))
    expect(supportApi.getSupportArtifacts).not.toHaveBeenCalled()
    expect(artifactApi.listArtifacts).not.toHaveBeenCalled()
  })
})
