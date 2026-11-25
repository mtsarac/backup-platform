import { expect, test } from '@playwright/test'
import { setTimeout as sleep } from 'node:timers/promises'
import { type Api } from '../../api'
import { AGENT_NAME, enrollTransientAgent, ensureAgent, seedJob, seedS3, vm, waitOnline } from './fixture'
import { FILE_SOURCE, seedFileSource } from './files'
import { PG, seedSourceDatabase } from './pg'
import { signIn, tenantPath, watch } from './ui'

let tenantId: string
let tenant: Api
let mainAgentId: string
const suffix = Date.now().toString(36)
const failDb = `e2e_s3_fail_${suffix}`
const jobIds: string[] = []
let stoppedDbJobId = ''
let exhaustedJobId = ''
let badCredJobId = ''
let cancelJobId = ''
let revokeAgent: { agentId: string; cleanup: () => void }
let revokeJobId = ''
const revokeAgentName = `e2e-s3-revoke-${suffix}`
let offlineAgent: { agentId: string; enrolledAt: number; cleanup: () => void }
let offlineJobId = ''
const offlineAgentName = `e2e-s3-offline-${suffix}`

test.describe.configure({ mode: 'serial', timeout: 240_000 })

function pgJobPayload(name: string, agentId: string, password: string, maxAttempts = 3) {
  return {
    name,
    agentId,
    overlapPolicy: 'Queue',
    retryPolicy: { maxAttempts, initialDelaySeconds: 5, maxDelaySeconds: 10, backoff: 'Exponential', jitter: 0 },
    configuration: {
      engine: 'postgres', database: failDb, host: '127.0.0.1', port: 5432,
      integratedSecurity: false, databaseCredentialReference: null,
      databaseLogin: { userName: PG.user, password },
      encrypt: false, trustServerCertificate: true, stagingReference: 'default', storagePrefix: '',
      copyOnly: true, checksumPolicy: 'Required', compressionPolicy: 'Preferred',
      requiredVerifiedReplicaCount: 1, encryptionPolicy: 'Off',
      destinations: [{ storageProvider: 'local', storageConnectionId: null, storageExecutionLocation: 'Agent', isPrimary: true }],
    },
  }
}

test.beforeAll(async () => {
  test.setTimeout(180_000)
  ;({ tenantId, tenant } = await seedS3())
  mainAgentId = await ensureAgent(tenant, tenantId)
  await seedFileSource(tenant, tenantId)
  seedSourceDatabase(failDb, 20)

  // Start the offline-target transient agent first so its 120 s OfflineTimeout elapses while the other failure tests run.
  offlineAgent = await enrollTransientAgent(tenant, tenantId, offlineAgentName, { sendHeartbeat: true })
  revokeAgent = await enrollTransientAgent(tenant, tenantId, revokeAgentName)

  stoppedDbJobId = await seedJob(tenant, tenantId, pgJobPayload(`e2e-s3-stopdb-${suffix}`, mainAgentId, PG.password, 3))
  exhaustedJobId = await seedJob(tenant, tenantId, pgJobPayload(`e2e-s3-exhaust-${suffix}`, mainAgentId, PG.password, 1))
  badCredJobId = await seedJob(tenant, tenantId, pgJobPayload(`e2e-s3-badpw-${suffix}`, mainAgentId, 'wrong-password-s3', 3))
  cancelJobId = await seedJob(tenant, tenantId, pgJobPayload(`e2e-s3-cancel-${suffix}`, mainAgentId, PG.password, 3))
  revokeJobId = await seedJob(tenant, tenantId, {
    ...pgJobPayload(`e2e-s3-revoked-${suffix}`, revokeAgent.agentId, PG.password),
    configuration: {
      engine: 'file-system', database: FILE_SOURCE, host: 'localhost', port: null, integratedSecurity: true,
      databaseCredentialReference: null, encrypt: false, trustServerCertificate: false, stagingReference: 'default',
      storagePrefix: '', copyOnly: true, checksumPolicy: 'Required', compressionPolicy: 'Preferred',
      requiredVerifiedReplicaCount: 1, encryptionPolicy: 'Off',
      files: { paths: ['docs'], includes: ['**'], excludes: [], symlinkPolicy: 'Skip' },
      destinations: [{ storageProvider: 'local', storageConnectionId: null, storageExecutionLocation: 'Agent', isPrimary: true }],
    },
  })
  offlineJobId = await seedJob(tenant, tenantId, {
    ...pgJobPayload(`e2e-s3-offline-job-${suffix}`, offlineAgent.agentId, PG.password),
    configuration: {
      engine: 'file-system', database: FILE_SOURCE, host: 'localhost', port: null, integratedSecurity: true,
      databaseCredentialReference: null, encrypt: false, trustServerCertificate: false, stagingReference: 'default',
      storagePrefix: '', copyOnly: true, checksumPolicy: 'Required', compressionPolicy: 'Preferred',
      requiredVerifiedReplicaCount: 1, encryptionPolicy: 'Off',
      files: { paths: ['docs'], includes: ['**'], excludes: [], symlinkPolicy: 'Skip' },
      destinations: [{ storageProvider: 'local', storageConnectionId: null, storageExecutionLocation: 'Agent', isPrimary: true }],
    },
  })
  jobIds.push(stoppedDbJobId, exhaustedJobId, badCredJobId, cancelJobId, revokeJobId, offlineJobId)
})

test.afterAll(async () => {
  vm('sudo -n systemctl start postgresql && sudo -n systemctl start backup-agent.service')
  revokeAgent?.cleanup()
  offlineAgent?.cleanup()
  if (offlineAgent?.agentId) {
    await tenant.call('POST', `/api/tenants/${tenantId}/agents/${offlineAgent.agentId}/revoke`, {}, [200, 204, 409]).catch(() => {})
  }
  for (const id of jobIds) {
    if (id) await tenant.call('DELETE', `/api/tenants/${tenantId}/backup-jobs/${id}`, undefined, [200, 204]).catch(() => {})
  }
  vm(`sudo -n -u postgres psql -q -c "drop database if exists ${failDb}"`)
  await waitOnline(tenant, tenantId, AGENT_NAME, 60_000)
  await tenant.dispose()
})

test('stopped PostgreSQL server surfaces clear UI failure/retry notice, and run recovers once PostgreSQL restarts', async ({ page }) => {
  const w = watch(page)
  await signIn(page); w.reset()
  await page.goto(tenantPath(tenantId, `backup-jobs/${stoppedDbJobId}`))
  vm('sudo -n systemctl stop postgresql')
  try {
    await page.getByRole('button', { name: 'Şimdi yedekle' }).click()
    await page.getByRole('link', { name: 'Denemeyi aç' }).click()
    await expect(page.getByText('Veritabanı bağlantısı geçici olarak kullanılamıyor.')).toBeVisible({ timeout: 90_000 })
  } finally {
    vm('sudo -n systemctl start postgresql && for _ in $(seq 1 30); do pg_isready -h 127.0.0.1 -q && break; sleep 1; done')
  }
  const retryBtn = page.getByRole('button', { name: 'Yeniden dene' }).first()
  if (await retryBtn.isVisible({ timeout: 2000 }).catch(() => false)) await retryBtn.click()
  await expect(page.getByText('Çoğaltma sağlıklı')).toBeVisible({ timeout: 90_000 })
  w.expectClean()
})

test('failed run with exhausted retry policy either allows manual Retry or hides Yeniden dene instead of returning 409 (#271)', async ({ page }) => {
  await signIn(page)
  await page.goto(tenantPath(tenantId, `backup-jobs/${exhaustedJobId}`))
  vm('sudo -n systemctl stop postgresql')
  try {
    await page.getByRole('button', { name: 'Şimdi yedekle' }).click()
    await page.getByRole('link', { name: 'Denemeyi aç' }).click()
    await expect(page.getByText('Yedekleme denemesi başarısız oldu')).toBeVisible({ timeout: 90_000 })
  } finally {
    vm('sudo -n systemctl start postgresql && for _ in $(seq 1 30); do pg_isready -h 127.0.0.1 -q && break; sleep 1; done')
  }
  await expect(page.getByRole('button', { name: 'Yeniden dene' })).toHaveCount(0)
  await expect(page.getByText(/409|conflict/i)).toHaveCount(0)
})

test('backup job with invalid PostgreSQL credential fails run with clear auth error, and manual Yeniden dene succeeds', async ({ page }) => {
  const w = watch(page)
  await signIn(page); w.reset()
  await page.goto(tenantPath(tenantId, `backup-jobs/${badCredJobId}`))
  await page.getByRole('button', { name: 'Şimdi yedekle' }).click()
  await page.getByRole('link', { name: 'Denemeyi aç' }).click()
  await expect(page.getByText('Yedekleme denemesi başarısız oldu')).toBeVisible({ timeout: 90_000 })
  await expect(page.getByRole('alert')).toContainText('Veritabanı kimlik doğrulaması başarısız oldu.')
  await expect(page.getByRole('button', { name: 'Bilgileri düzelt' })).toBeVisible()

  // Fix role password on PostgreSQL so the run's snapshotted credential becomes valid, then test manual Yeniden dene in UI
  vm(`sudo -n -u postgres psql -q -c "alter role ${PG.user} password 'wrong-password-s3'"`)
  try {
    await page.getByRole('button', { name: 'Yeniden dene' }).first().click()
    await expect(page.getByText('Çoğaltma sağlıklı')).toBeVisible({ timeout: 90_000 })
  } finally {
    vm(`sudo -n -u postgres psql -q -c "alter role ${PG.user} password '${PG.password}'"`)
  }
  w.expectClean()
})

test('stopping backup-agent lets UserAdmin cancel a queued run in UI, and restarting restores Online service', async ({ page }) => {
  const w = watch(page)
  await signIn(page); w.reset()
  await page.goto(tenantPath(tenantId, `backup-jobs/${cancelJobId}`))
  vm('sudo -n systemctl stop backup-agent.service')
  try {
    await page.getByRole('button', { name: 'Şimdi yedekle' }).click()
    await page.getByRole('link', { name: 'Denemeyi aç' }).click()
    await page.getByRole('button', { name: 'İptal et' }).click()
    await page.getByRole('dialog').getByRole('button', { name: 'İptali onayla' }).click()
    await expect(page.getByText('Yedekleme denemesi iptal edildi.')).toBeVisible({ timeout: 30_000 })
  } finally {
    vm('sudo -n systemctl start backup-agent.service')
    await waitOnline(tenant, tenantId, AGENT_NAME, 60_000)
  }
  w.expectClean()
})

test('revoking an agent via UI disables its jobs, explains why on job detail, and hides it unless filtered', async ({ page }) => {
  const w = watch(page)
  await signIn(page); w.reset()
  await page.goto(tenantPath(tenantId, `agents/${revokeAgent.agentId}`))
  await page.getByRole('button', { name: 'Bilgisayar kaydını iptal et' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog).toContainText('Bu yedekleme bilgisayarı artık bu çalışma alanına bağlanamaz.')
  await dialog.getByRole('button', { name: 'İptali onayla' }).click()
  await expect(page.getByText('İptal edildi').first()).toBeVisible()

  await page.goto(tenantPath(tenantId, 'agents'))
  await expect(page.getByRole('link', { name: AGENT_NAME, exact: true })).toBeVisible()
  await expect(page.getByRole('link', { name: revokeAgentName })).toHaveCount(0)
  await page.getByLabel('İptal edilenleri göster').check()
  await expect(page.getByRole('row', { name: new RegExp(revokeAgentName) })).toContainText('İptal edildi')

  await page.goto(tenantPath(tenantId, `backup-jobs/${revokeJobId}`))
  await expect(page.getByText('Bu bilgisayar iptal edildi; yeni bir yedekleme işi oluşturun.')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Şimdi yedekle' })).toBeDisabled()
  w.expectClean()
})

test('offline agent displays offline warning on agent detail and attention status on its backup job', async ({ page }) => {
  const remainingMs = Math.max(0, 125_000 - (Date.now() - offlineAgent.enrolledAt))
  if (remainingMs > 0) await sleep(remainingMs)

  await expect.poll(async () => {
    const a = await tenant.call('GET', `/api/tenants/${tenantId}/agents/${offlineAgent.agentId}`)
    return a.connectivity
  }, { timeout: 45_000, intervals: [3000] }).toBe('offline')

  const w = watch(page)
  await signIn(page); w.reset()
  await page.goto(tenantPath(tenantId, `agents/${offlineAgent.agentId}`))
  await expect(page.getByText('Çevrimdışı').first()).toBeVisible()
  await expect(page.getByText(/Bu bilgisayar çevrimdışı; yedeklemeler bilgisayar yeniden bağlanana kadar çalışmaz/)).toBeVisible()

  await page.goto(tenantPath(tenantId, `backup-jobs/${offlineJobId}`))
  await expect(page.getByText('Yedekleme bilgisayarı çevrimdışı.')).toBeVisible()
  await expect(page.getByRole('link', { name: 'Bilgisayarı kontrol et' })).toBeVisible()
  w.expectClean()
})
