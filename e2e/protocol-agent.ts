// Protocol-only agent for S5: enrolls and plays one agent through the real HTTP agent API, so artifacts, failed runs and offline agents exist without a VM.
// Port of scripts/frontend/quota-smoke-fixture.py. Test DATA only; flows under test still run through the UI.
import { createHash, randomUUID } from 'node:crypto'
import { request, type APIRequestContext } from '@playwright/test'
import { Api, apiUrl, createEnrollmentToken, enableAgentEnrollment, ensureTenant, ensureTenantUser, ensureUnlimitedPlan } from './api'
import type { Account } from './state'

const CAPS = ['backup.file-system', 'backup.multi-destination', 'backup.quota-bounded-upload', 'mssql.backup.full'].sort()

export type ProtocolAgent = { agentId: string; hostName: string; ctx: APIRequestContext; headers: Record<string, string> }
export type S5Tenant = { tenantId: string; tenantName: string; account: Account; tenant: Api; platform: Api }

/** Fresh tenant with one UserAdmin, unlimited plan and enrollment on. `suffix` makes every object name unique to this run. */
export async function seedTenant(platform: Api, suffix: string): Promise<S5Tenant> {
  const tenantName = `e2e-s5-${suffix}`
  const account = { login: `e2e.s5.${suffix}`, password: 'E2e-Passw0rd!2026' }
  const tenantId = await ensureTenant(platform, tenantName)
  await ensureTenantUser(platform, [tenantId], account, { displayName: `S5 ${suffix}` })
  await ensureUnlimitedPlan(platform, tenantId, 'E2E Unlimited')
  await enableAgentEnrollment(platform, tenantId)
  return { tenantId, tenantName, account, tenant: await Api.signIn(account), platform }
}

export async function enrollAgent(t: S5Tenant, hostName: string): Promise<ProtocolAgent> {
  const token = await createEnrollmentToken(t.tenant, t.tenantId)
  const ctx = await request.newContext({ baseURL: apiUrl, ignoreHTTPSErrors: true })
  const r = await ctx.post('/api/agent/enroll', {
    headers: { 'X-Backup-Agent-Protocol': '1.0' },
    data: { enrollmentToken: token, installationId: randomUUID(), agentVersion: '1.2.3', protocolVersion: '1.0', platform: 'linux', hostName, capabilities: CAPS },
  })
  if (r.status() !== 200 && r.status() !== 201) throw new Error(`enroll -> ${r.status()} ${await r.text()}`)
  const e = await r.json()
  const headers = { Authorization: `BackupAgent ${e.credential.credentialId.replaceAll('-', '')}.${e.credential.secret}`, 'X-Backup-Agent-Protocol': '1.0' }
  const canonical = CAPS.map((c) => `${c.length}:${c};9:Supported;0;\n`).join('')
  const caps = await ctx.put('/api/agent/capabilities', {
    headers,
    data: { revision: 'c1-' + createHash('sha256').update(canonical).digest('hex').slice(0, 48), capabilities: CAPS.map((identifier) => ({ identifier, support: 'Supported' })) },
  })
  if (!caps.ok()) throw new Error(`capabilities -> ${caps.status()} ${await caps.text()}`)
  return { agentId: e.agentId, hostName, ctx, headers }
}

export async function createJob(t: S5Tenant, agent: ProtocolAgent, name: string): Promise<string> {
  const job = await t.tenant.call('POST', `/api/tenants/${t.tenantId}/backup-jobs`, {
    name, agentId: agent.agentId, overlapPolicy: 'Queue',
    configuration: { engine: 'mssql', database: 'master', host: 'sql-server', integratedSecurity: true, requiredVerifiedReplicaCount: 1, destinations: [{ storageProvider: 'local', storageConnectionId: null, storageExecutionLocation: 'Backend', isPrimary: true }] },
  }, [200, 201], { 'Idempotency-Key': randomUUID() })
  return job.id
}

const iso = (ms = 0) => new Date(Date.now() + ms).toISOString()

/** Starts a run through the API and plays the agent for it. `failureCode` makes it a failed run; otherwise a Full artifact of `bytes` is recorded. Returns the run id. */
export async function runOnce(t: S5Tenant, agent: ProtocolAgent, jobId: string, opts: { bytes?: number; failureCode?: string } = {}): Promise<string> {
  await t.tenant.call('POST', `/api/tenants/${t.tenantId}/backup-jobs/${jobId}/runs`, {}, [200, 201, 202], { 'Idempotency-Key': randomUUID() })
  const poll = await (await agent.ctx.get('/api/agent/tasks', { headers: agent.headers })).json()
  const backup = poll.tasks?.[0]?.backup
  if (!backup) throw new Error('no pending run for the protocol agent')
  const { attemptId, leaseToken } = backup
  const post = async (path: string, data: unknown) => {
    const r = await agent.ctx.post(`/api/agent/tasks/${attemptId}/${path}`, { headers: agent.headers, data })
    if (!r.ok()) throw new Error(`${path} -> ${r.status()} ${await r.text()}`)
  }
  await post('start', { leaseToken, softwareVersion: '1.2.3', protocolVersion: '1.0', platform: 'linux' })
  if (opts.failureCode) { await post('complete', { leaseToken, status: 'Failed', failureCode: opts.failureCode }); return backup.runId }
  const bytes = opts.bytes ?? 1_048_576
  const created = iso()
  const checksum = 'b'.repeat(64)
  const key = `${attemptId.replaceAll('-', '')}/backup.bak`
  await post('complete', {
    leaseToken, status: 'Succeeded',
    destinations: [{ storageProvider: 'local', storageConnectionId: null, state: 'Verified', upload: 'Succeeded', verification: 'Verified', failureCode: null, key, objectMayRemain: false, startedAt: iso(-4_000), completedAt: iso(-1_000) }],
    artifact: {
      sizeBytes: bytes, sourceChecksum: 'a'.repeat(64), artifactChecksum: checksum, checksumAlgorithm: 'sha256', createdAt: created,
      sourceMetadataJson: JSON.stringify({ engine: 'mssql', databaseName: 'master', backupType: 'Full', backupStartedAt: iso(-10_000), backupFinishedAt: iso(-5_000), backupVerification: { state: 'Passed', method: 'VerifyOnly' } }),
      appliedOptionsJson: JSON.stringify({ copyOnly: true, checksum: true, compression: true }),
      replicas: [{ storageProvider: 'local', storageTarget: 'default', key, sizeBytes: bytes, checksum, checksumAlgorithm: 'sha256', verificationState: 'Verified', verificationMethod: 'ReadBack', createdAt: created, verifiedAt: created }],
    },
  })
  return backup.runId
}
