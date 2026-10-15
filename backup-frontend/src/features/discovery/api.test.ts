import { beforeEach, describe, expect, it, vi } from 'vitest'
import { apiRequest } from '@/lib/api/client'
import { getDiscoveryAgents, getDiscoveryInstances, rescanDiscoveryAgent, saveDiscoveryCredential, saveDiscoverySettings, verifyDiscoveryInstance } from './api'

vi.mock('@/lib/api/client', async importOriginal => ({ ...(await importOriginal()), apiRequest: vi.fn().mockResolvedValue({}) }))
beforeEach(() => vi.mocked(apiRequest).mockClear())

describe('discovery api', () => {
  it('builds tenant-scoped paths and filters', async () => {
    await getDiscoveryAgents('t')
    await getDiscoveryInstances('t', { agentId: 'a/1', engine: 'postgres', includeMissing: true })
    await getDiscoveryInstances('t')
    const calls = vi.mocked(apiRequest).mock.calls.map(call => call[0])
    expect(calls).toEqual(['tenants/t/discovery/agents', 'tenants/t/discovery/instances?agentId=a%2F1&engine=postgres&includeMissing=true', 'tenants/t/discovery/instances'])
  })
  it('uses the documented verbs and bodies', async () => {
    await rescanDiscoveryAgent('t', 'a')
    await verifyDiscoveryInstance('t', 'i')
    const credential = { credentialReference: 'ref', integratedSecurity: false, encrypt: true, trustServerCertificate: false }
    await saveDiscoveryCredential('t', 'i', credential)
    const settings = { enabled: true, scanIntervalMinutes: 60, includeDocker: false }
    await saveDiscoverySettings('t', 'a', settings)
    expect(vi.mocked(apiRequest).mock.calls).toEqual([
      ['tenants/t/discovery/agents/a/rescan', { method: 'POST' }],
      ['tenants/t/discovery/instances/i/verify', { method: 'POST' }],
      ['tenants/t/discovery/instances/i/credential', { method: 'PUT', json: credential }],
      ['tenants/t/discovery/agents/a/settings', { method: 'PUT', json: settings }],
    ])
  })
})
