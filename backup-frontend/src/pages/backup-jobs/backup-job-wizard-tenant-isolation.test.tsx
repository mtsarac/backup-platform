import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes, useNavigate } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { AuthContext } from '@/features/auth/auth-context'
import { TenantProvider } from '@/features/tenancy/tenant-provider'
import { useTenant } from '@/features/tenancy/use-tenant'
import { getTenantConnections, getTenantProviders } from '@/features/storage-connections/api'
import { storageConnectionKeys } from '@/features/storage-connections/hooks'
import { catalog, makeConnection, makeManaged } from '@/features/storage-connections/test-fixtures'
import type { ConnectionView } from '@/features/storage-connections/types'
import { BackupJobWizardPage } from './backup-job-wizard-page'

vi.mock('@/features/agents/api', () => ({ getAgents: vi.fn().mockResolvedValue([]) }))
vi.mock('@/features/storage-connections/api')
vi.mock('@/features/backup-jobs/api', () => ({ getBackupJob: vi.fn(), getOccurrences: vi.fn(), saveBackupJob: vi.fn() }))

function PickerShell() {
  const { switchTenant } = useTenant()
  const navigate = useNavigate()
  return <><button onClick={async () => { await switchTenant('tenant-b') }}>Switch tenant</button><button onClick={() => navigate('/tenants/tenant-b')}>Direct navigation</button><BackupJobWizardPage /></>
}

function mount(cache: QueryClient) {
  const tenants = [
    { tenantId: 'tenant-a', name: 'Tenant A', role: 'Administrator' as const, membershipRole: 'Owner' as const },
    { tenantId: 'tenant-b', name: 'Tenant B', role: 'Administrator' as const, membershipRole: 'Owner' as const },
  ]
  return render(<QueryClientProvider client={cache}><AuthContext.Provider value={{ user: { userId: 'user', displayName: 'User', isPlatformAdmin: false }, tenants, isAuthenticated: true, isLoading: false, error: null, sessionExpired: false, login: vi.fn(), logout: vi.fn(), refresh: vi.fn() }}><MemoryRouter initialEntries={['/tenants/tenant-a']}><Routes><Route path="/tenants/:tenantId/*" element={<TenantProvider><PickerShell /></TenantProvider>} /><Route path="/tenants/:tenantId" element={<TenantProvider><PickerShell /></TenantProvider>} /><Route path="/" element={<p>Root</p>} /></Routes></MemoryRouter></AuthContext.Provider></QueryClientProvider>)
}

beforeEach(() => vi.clearAllMocks())
describe('job connection picker tenant lifecycle', () => {
  it.each(['Switch tenant', 'Direct navigation'])('with %s cancels old requests, removes connection/provider/job caches and remounts the form', async action => {
    const cache = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } })
    const aConnections = [makeConnection(), makeManaged()]
    const lateRequest: { signal?: AbortSignal } = {}
    let resolveLate!: (value: { items: ConnectionView[] }) => void
    const lateResult = new Promise<{ items: ConnectionView[] }>(resolve => { resolveLate = resolve })
    let aCalls = 0
    vi.mocked(getTenantConnections).mockImplementation((tenantId, signal) => {
      if (tenantId === 'tenant-a') {
        if (++aCalls === 1) return Promise.resolve({ items: aConnections })
        lateRequest.signal = signal
        return lateResult
      }
      return Promise.resolve({ items: [makeConnection({ id: 'b-local', tenantId: 'tenant-b', name: 'B private Local' })] })
    })
    vi.mocked(getTenantProviders).mockImplementation(tenantId => Promise.resolve({ items: tenantId === 'tenant-a' ? catalog : [catalog[0]] }))
    cache.setQueryData(['tenant', 'tenant-a', 'backup-jobs', 'job-a'], { name: 'A private job' })
    mount(cache)
    await waitFor(() => expect(screen.getByLabelText('Depolama bağlantısı')).toBeEnabled())
    fireEvent.change(screen.getByLabelText('Depolama bağlantısı'), { target: { value: 'conn-central' } })
    fireEvent.change(screen.getByLabelText('İş adı'), { target: { value: 'A private form' } })
    void cache.refetchQueries({ queryKey: storageConnectionKeys.tenantList('tenant-a') })
    await waitFor(() => expect(lateRequest.signal).toBeDefined())
    await userEvent.click(screen.getByRole('button', { name: action }))
    await waitFor(() => expect(screen.getByRole('option', { name: /B private Local · Local/ })).toBeInTheDocument())
    expect(screen.getByLabelText('Depolama bağlantısı')).toHaveValue('')
    expect(screen.getByLabelText('İş adı')).toHaveValue('')
    expect(screen.getByLabelText('Storage Provider')).toHaveValue('local')
    expect(screen.queryByRole('option', { name: /Managed Storage/ })).not.toBeInTheDocument()
    expect(screen.getByRole('option', { name: /B private Local · Local/ })).toBeInTheDocument()
    expect(lateRequest.signal?.aborted).toBe(true)
    await waitFor(() => expect(cache.getQueryCache().findAll({ queryKey: ['tenant', 'tenant-a'] })).toHaveLength(0))
    expect(cache.getQueryData(storageConnectionKeys.tenantProviders('tenant-a'))).toBeUndefined()
    expect(cache.getQueryData(['tenant', 'tenant-a', 'backup-jobs', 'job-a'])).toBeUndefined()
    await act(async () => { resolveLate({ items: aConnections }); await lateResult })
    expect(cache.getQueryData(storageConnectionKeys.tenantList('tenant-a'))).toBeUndefined()
    expect(screen.queryByRole('option', { name: /Managed Storage/ })).not.toBeInTheDocument()
    expect(getTenantConnections).toHaveBeenLastCalledWith('tenant-b', expect.any(AbortSignal))
    cache.clear()
  })
})
