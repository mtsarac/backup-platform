import { expect, request, test } from '@playwright/test'
import { createHash, randomUUID } from 'node:crypto'
import { Api, apiUrl, createEnrollmentToken } from '../api'
import { state } from '../state'

const s = state()

test('agent logs can be filtered, followed and downloaded', async ({ page }) => {
  const tenant = await Api.signIn(s.accounts.userAdmin)
  const enrollmentToken = await createEnrollmentToken(tenant, s.tenantId)
  const agentApi = await request.newContext({ baseURL: apiUrl, ignoreHTTPSErrors: true })
  try {
    const enrollment = await agentApi.post('/api/agent/enroll', { data: {
      enrollmentToken, installationId: randomUUID(), agentVersion: '1.0.0', protocolVersion: '1.0', platform: 'linux', hostName: `log-test-${randomUUID()}`, capabilities: [],
    } })
    expect(enrollment.status()).toBe(201)
    const { agentId, credential } = await enrollment.json()
    const authorization = `BackupAgent ${credential.credentialId.replaceAll('-', '')}.${credential.secret}`
    const capabilities = ['backup.multi-destination', 'backup.upload-bandwidth-limit', 'file-system.backup.full', 'storage.agent-direct']
      .map(identifier => ({ identifier, support: 'Supported' }))
    const canonical = capabilities.map(c => `${c.identifier.length}:${c.identifier};9:Supported;0;\n`).join('')
    const revision = `c1-${createHash('sha256').update(canonical).digest('hex').slice(0, 48)}`
    const synced = await agentApi.put('/api/agent/capabilities', { headers: { Authorization: authorization, 'X-Backup-Agent-Protocol': '1.0' }, data: { revision, capabilities } })
    expect(synced.status()).toBe(200)
    const job = await tenant.call('POST', `/api/tenants/${s.tenantId}/backup-jobs`, {
      name: `Log test ${randomUUID()}`, agentId,
      configuration: { engine: 'file-system', database: 'test', host: 'localhost', integratedSecurity: true, copyOnly: true,
        checksumPolicy: 'Required', compressionPolicy: 'Preferred', requiredVerifiedReplicaCount: 1, encryptionPolicy: 'Off',
        files: { paths: ['.'], includes: ['**'], excludes: [], symlinkPolicy: 'Skip' },
        destinations: [{ storageProvider: 'local', storageExecutionLocation: 'Agent', isPrimary: true, storageTarget: 'default' }] },
    }, [201], { 'Idempotency-Key': randomUUID() })
    const run = await tenant.call('POST', `/api/tenants/${s.tenantId}/backup-jobs/${job.id}/runs`, {}, [200], { 'Idempotency-Key': randomUUID() })
    const sent = await agentApi.post('/api/agent/logs', {
      headers: { Authorization: authorization },
      data: [
        { id: randomUUID(), timestamp: new Date().toISOString(), code: 'agent.started', jobId: null, runId: null },
        { id: randomUUID(), timestamp: new Date().toISOString(), code: 'backup.stage_failed', jobId: job.id, runId: run.id },
      ],
    })
    expect(sent.status()).toBe(204)
    await page.goto('/login')
    await page.locator('input[autocomplete="username"], input[type="text"]').first().fill(s.accounts.userAdmin.login)
    await page.locator('input[type="password"]').fill(s.accounts.userAdmin.password)
    await page.locator('button[type="submit"]').click()
    await page.waitForURL(new RegExp(`/tenants/${s.tenantId}`))
    await page.goto(`/tenants/${s.tenantId}/agents/${agentId}`)
    const logs = page.getByRole('region', { name: 'Bilgisayar günlükleri' })
    await expect(logs.getByText('Agent started')).toBeVisible()
    await expect(logs.getByText('Backup stage failed')).toBeVisible()
    await logs.getByLabel('Seviye').selectOption('Warning')
    await expect(logs.getByText('Agent started')).toHaveCount(0)
    await expect(logs.getByText('Backup stage failed')).toBeVisible()
    await logs.getByLabel('Metin').fill('no match')
    await expect(logs.getByText('Bu filtrelerle günlük kaydı bulunamadı.')).toBeVisible()
    await logs.getByLabel('Metin').fill('stage')
    await expect(logs.getByText('Backup stage failed')).toBeVisible()
    await logs.getByLabel('Başlangıç').fill('2099-01-01T00:00')
    await expect(logs.getByText('Bu filtrelerle günlük kaydı bulunamadı.')).toBeVisible()
    await logs.getByLabel('Başlangıç').fill('')
    await expect(logs.getByText('Backup stage failed')).toBeVisible()
    await logs.getByLabel('Metin').fill('failed')
    await expect(logs.getByText('Backup stage failed')).toBeVisible()
    await logs.getByLabel('Canlı takip').check()
    const live = await agentApi.post('/api/agent/logs', {
      headers: { Authorization: `BackupAgent ${credential.credentialId.replaceAll('-', '')}.${credential.secret}` },
      data: [{ id: randomUUID(), timestamp: new Date().toISOString(), code: 'backup.cleanup_failed', jobId: null, runId: null }],
    })
    expect(live.status()).toBe(204)
    await expect(logs.getByText('Backup cleanup failed')).toBeVisible({ timeout: 15_000 })
    const download = page.waitForEvent('download')
    await logs.getByRole('button', { name: 'İndir' }).click()
    for (const path of [`backup-jobs/${job.id}`, `backup-runs/${run.id}`]) {
      await page.goto(`/tenants/${s.tenantId}/${path}`)
      await expect(page.getByRole('region', { name: 'Bilgisayar günlükleri' }).getByText('Backup stage failed')).toBeVisible()
    }
    expect((await download).suggestedFilename()).toBe('agent-logs.json')
  } finally {
    await agentApi.dispose()
    await tenant.dispose()
  }
})
