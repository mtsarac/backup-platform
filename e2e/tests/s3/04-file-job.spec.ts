import { expect, test } from '@playwright/test'
import { type Api } from '../../api'
import { AGENT_NAME, ensureAgent, seedJob, seedS3 } from './fixture'
import { FILE_SOURCE, REPLICA_VOLUME, SOURCE_ROOT, seedFileSource, treeHashes } from './files'
import { signIn, tenantPath, watch } from './ui'

let tenantId: string
let tenant: Api
let agentId: string
const suffix = Date.now().toString(36)
const jobName = `e2e-s3-files-${suffix}`
const scheduledName = `e2e-s3-sched-${suffix}`
const renamedScheduledName = `e2e-s3-sched-renamed-${suffix}`
const multiDestJobName = `e2e-s3-multidest-${suffix}`
const secondConnName = `e2e-s3-vol2-${suffix}`
let scheduledJobId = ''
let multiDestJobId = ''
let secondConnId = ''

test.describe.configure({ mode: 'serial', timeout: 180_000 })

test.beforeAll(async () => {
  test.setTimeout(180_000)
  ;({ tenantId, tenant } = await seedS3())
  agentId = await ensureAgent(tenant, tenantId)
  await seedFileSource(tenant, tenantId)
  const conn = await tenant.call('POST', `/api/tenants/${tenantId}/storage-connections`, {
    providerType: 'local',
    name: secondConnName,
    ownership: 'TenantManaged',
    configuration: { volume: REPLICA_VOLUME },
  })
  secondConnId = conn.id
})

test.afterAll(async () => {
  for (const id of [scheduledJobId, multiDestJobId]) {
    if (id) await tenant.call('DELETE', `/api/tenants/${tenantId}/backup-jobs/${id}`, undefined, [200, 204]).catch(() => {})
  }
  const jobs: any[] = await tenant.call('GET', `/api/tenants/${tenantId}/backup-jobs`).catch(() => [])
  for (const j of jobs.filter(x => x.name === jobName)) {
    await tenant.call('DELETE', `/api/tenants/${tenantId}/backup-jobs/${j.id}`, undefined, [200, 204]).catch(() => {})
  }
  if (secondConnId) {
    await tenant.call('DELETE', `/api/tenants/${tenantId}/storage-connections/${secondConnId}`, undefined, [200, 204, 409]).catch(() => {})
  }
  await tenant.dispose()
})

test('job wizard creates a file job, Run now backs up the directory, and file restore matches SHA-256 tree', async ({ page }) => {
  const w = watch(page)
  await signIn(page); w.reset()
  await page.goto(tenantPath(tenantId, 'backup-jobs/new'))
  await page.getByRole('button', { name: 'Elle yapılandır' }).click()
  await page.getByLabel('İş adı').fill(jobName)
  await page.getByLabel('Yedekleme bilgisayarı').selectOption({ label: `${AGENT_NAME} · Çevrimiçi` })
  await page.getByLabel('Dosya / Klasör').check()
  await page.getByLabel('Dosya kaynağı referansı').fill(FILE_SOURCE)
  await page.getByLabel('Yedeklenecek dosya ve klasörler').fill('docs')
  for (let i = 0; i < 3; i++) await page.getByRole('button', { name: 'İleri' }).click()
  await page.getByRole('button', { name: 'Kaydet' }).click()
  await expect(page).toHaveURL(/\/backup-jobs$/)
  await page.getByRole('link', { name: jobName }).click()
  await page.getByRole('button', { name: 'Şimdi yedekle' }).click()
  await page.getByRole('link', { name: 'Denemeyi aç' }).click()
  await expect(page.getByText('Çoğaltma sağlıklı')).toBeVisible({ timeout: 90_000 })
  await page.getByRole('link', { name: 'Geri yükle', exact: true }).click()
  await page.getByRole('button', { name: 'Devam et →' }).click()
  await page.getByLabel('Alternatif konum').check()
  await page.getByRole('button', { name: 'Devam et →' }).click()
  await expect(page.getByText('Tüm ön kontroller başarıyla tamamlandı.')).toBeVisible({ timeout: 60_000 })
  await page.getByRole('button', { name: 'Dosyaları geri yükle' }).click()
  await expect(page).toHaveURL(/\/restores\/[0-9a-f-]+$/)
  await expect(page.getByText('Tamamlandı · %100')).toBeVisible({ timeout: 90_000 })
  expect(treeHashes(`${SOURCE_ROOT}/restored/docs`)).toBe(treeHashes(`${SOURCE_ROOT}/docs`))
  w.expectClean()
})

test('job detail renames a job, fires a scheduled occurrence on time, and pauses/resumes the schedule', async ({ page }) => {
  scheduledJobId = await seedJob(tenant, tenantId, {
    name: scheduledName,
    agentId,
    overlapPolicy: 'Skip',
    schedule: { enabled: true, cronExpression: '*/1 * * * *', timeZone: 'UTC', missedRunGraceSeconds: 1800, overlapPolicy: 'Skip' },
    configuration: {
      engine: 'file-system', database: FILE_SOURCE, host: 'localhost', port: null, integratedSecurity: true,
      databaseCredentialReference: null, encrypt: false, trustServerCertificate: false, stagingReference: 'default',
      storagePrefix: '', copyOnly: true, checksumPolicy: 'Required', compressionPolicy: 'Preferred',
      requiredVerifiedReplicaCount: 1, encryptionPolicy: 'Off',
      files: { paths: ['docs'], includes: ['**'], excludes: [], symlinkPolicy: 'Skip' },
      destinations: [{ storageProvider: 'local', storageConnectionId: null, storageExecutionLocation: 'Agent', isPrimary: true }],
    },
  })
  const w = watch(page)
  await signIn(page); w.reset()
  await page.goto(tenantPath(tenantId, `backup-jobs/${scheduledJobId}`))
  await page.getByRole('button', { name: 'Değiştir: Kaynak' }).click()
  await page.getByLabel('İş adı').fill(renamedScheduledName)
  await page.getByRole('button', { name: 'Kaydet' }).click()
  await expect(page.getByRole('heading', { name: renamedScheduledName })).toBeVisible()

  await expect(async () => {
    await page.goto(tenantPath(tenantId, 'backup-runs'))
    await expect(page.getByRole('link', { name: renamedScheduledName }).first()).toBeVisible({ timeout: 2000 })
  }).toPass({ timeout: 120_000, intervals: [5000] })
  await page.getByRole('link', { name: renamedScheduledName }).first().click()
  await expect(page.getByText('Çoğaltma sağlıklı')).toBeVisible({ timeout: 90_000 })

  await page.goto(tenantPath(tenantId, `backup-jobs/${scheduledJobId}`))
  await page.getByRole('button', { name: 'Zamanlamayı durdur' }).click()
  await expect(page.getByRole('button', { name: 'Zamanlamayı sürdür' })).toBeVisible()
  await page.getByRole('button', { name: 'Zamanlamayı sürdür' }).click()
  await expect(page.getByRole('button', { name: 'Zamanlamayı durdur' })).toBeVisible()
  w.expectClean()
})

test('job detail configures multi-destination replication and retention, verifies 2 replicas, requests/cancels artifact delete, and archives job', async ({ page }) => {
  test.fail(true, 'archiving a job leaves a 404 /health refetch and console error (#275)')
  multiDestJobId = await seedJob(tenant, tenantId, {
    name: multiDestJobName,
    agentId,
    overlapPolicy: 'Skip',
    configuration: {
      engine: 'file-system', database: FILE_SOURCE, host: 'localhost', port: null, integratedSecurity: true,
      databaseCredentialReference: null, encrypt: false, trustServerCertificate: false, stagingReference: 'default',
      storagePrefix: '', copyOnly: true, checksumPolicy: 'Required', compressionPolicy: 'Preferred',
      requiredVerifiedReplicaCount: 1, encryptionPolicy: 'Off',
      files: { paths: ['docs'], includes: ['**'], excludes: [], symlinkPolicy: 'Skip' },
      destinations: [{ storageProvider: 'local', storageConnectionId: null, storageExecutionLocation: 'Agent', isPrimary: true }],
    },
  })

  const w = watch(page)
  await signIn(page); w.reset()
  await page.goto(tenantPath(tenantId, `backup-jobs/${multiDestJobId}`))

  // Add second local volume destination and require 2 verified replicas
  await page.getByRole('button', { name: 'Değiştir: Hedef' }).click()
  await page.locator('summary', { hasText: /Başka hedef ekle/ }).click()
  await page.getByLabel('Yeni Çoğaltma Hedefi Ekle').selectOption(secondConnId)
  await page.getByLabel(/Gerekli Doğrulanmış Kopya Sayısı/).fill('2')
  await page.getByRole('button', { name: 'Kaydet' }).click()
  await expect(page.getByRole('button', { name: 'Değiştir: Hedef' })).toBeVisible()

  // Edit retention period in UI
  await page.getByRole('button', { name: 'Değiştir: Saklama ve şifreleme' }).click()
  await page.getByLabel('Saklama süresi').selectOption('604800')
  await expect(page.getByText(/Yeni süre yalnızca bundan sonraki yedeklere uygulanır/)).toBeVisible()
  await page.getByRole('button', { name: 'Kaydet' }).click()
  await expect(page.getByText('7 gün')).toBeVisible()

  // Trigger run and verify 2 verified replicas in Çoğaltma tab + 2 kopya in list
  await page.getByRole('button', { name: 'Şimdi yedekle' }).click()
  await page.getByRole('link', { name: 'Denemeyi aç' }).click()
  const runPanel = page.getByRole('region', { name: 'Yedekleme denemesi' })
  await expect(runPanel.getByText('Çoğaltma sağlıklı')).toBeVisible({ timeout: 90_000 })
  await runPanel.getByRole('tab', { name: 'Çoğaltma' }).click()
  await expect(runPanel.getByRole('tabpanel').getByText('Doğrulandı', { exact: true })).toHaveCount(2)
  await runPanel.getByRole('tab', { name: 'Özet' }).click()

  // Request artifact deletion (grace period countdown) and cancel deletion
  await runPanel.locator('summary').filter({ hasText: /^Diğer$/ }).click()
  await runPanel.getByRole('button', { name: 'Sil', exact: true }).click()
  const delDialog = page.getByRole('dialog')
  const delLabel = (await delDialog.locator('label').textContent())!
  const requiredArtifactName = delLabel.split(': ').slice(1).join(': ').trim()
  await expect(delDialog.getByRole('button', { name: 'Bekleme süresini başlat' })).toBeDisabled()
  await delDialog.getByRole('textbox').fill(requiredArtifactName)
  await delDialog.getByRole('button', { name: 'Bekleme süresini başlat' }).click()
  await expect(runPanel.getByText(/Silme isteği alındı/)).toBeVisible()
  await expect(runPanel.getByRole('button', { name: 'Silmeyi iptal et' })).toBeVisible()
  await runPanel.getByRole('button', { name: 'Silmeyi iptal et' }).click()
  await expect(runPanel.getByText(/Silme iptal edildi/)).toBeVisible()

  // Archive job via Diğer -> Korunan veriyi kaldır…
  await page.goto(tenantPath(tenantId, `backup-jobs/${multiDestJobId}`))
  await page.getByRole('button', { name: 'Diğer' }).click()
  await page.getByRole('menuitem', { name: 'Korunan veriyi kaldır…' }).click()
  const archiveDialog = page.getByRole('dialog')
  await archiveDialog.getByRole('textbox').fill(multiDestJobName)
  await archiveDialog.getByRole('button', { name: 'Korunan veriyi kaldır' }).click()
  await expect(page).toHaveURL(/\/backup-jobs$/)
  await expect(page.getByRole('link', { name: multiDestJobName })).toHaveCount(0)
  multiDestJobId = ''
  w.expectClean()
})
