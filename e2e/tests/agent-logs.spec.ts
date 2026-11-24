import { expect, request, test } from '@playwright/test'
import { randomUUID } from 'node:crypto'
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
    const sent = await agentApi.post('/api/agent/logs', {
      headers: { Authorization: `BackupAgent ${credential.credentialId}.${credential.secret}` },
      data: [
        { id: randomUUID(), timestamp: new Date().toISOString(), code: 'agent.started', jobId: null, runId: null },
        { id: randomUUID(), timestamp: new Date().toISOString(), code: 'backup.stage_failed', jobId: null, runId: null },
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
    await logs.getByLabel('Canlı takip').check()
    const download = page.waitForEvent('download')
    await logs.getByRole('button', { name: 'İndir' }).click()
    expect((await download).suggestedFilename()).toBe('agent-logs.json')
  } finally {
    await agentApi.dispose()
    await tenant.dispose()
  }
})
