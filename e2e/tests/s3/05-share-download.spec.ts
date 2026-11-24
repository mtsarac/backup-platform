import { createHash } from 'node:crypto'
import { readFile } from 'node:fs/promises'
import { expect, test } from '@playwright/test'
import { type Api } from '../../api'
import { state } from '../../state'
import { ensureAgent, seedJob, seedS3, triggerAndWaitRun } from './fixture'
import { FILE_SOURCE, seedFileSource } from './files'
import { signIn, tenantPath, watch } from './ui'

let tenantId: string
let tenant: Api
let jobId = ''
let runId = ''
let expectedChecksum = ''
const suffix = Date.now().toString(36)
const jobName = `e2e-s3-share-${suffix}`
const shareReason = `e2e-s3-support-${suffix}`
const fixmeShareReason = `e2e-s3-direct-share-${suffix}`

test.describe.configure({ mode: 'serial', timeout: 180_000 })

test.beforeAll(async () => {
  test.setTimeout(180_000)
  ;({ tenantId, tenant } = await seedS3())
  const agentId = await ensureAgent(tenant, tenantId)
  await seedFileSource(tenant, tenantId)
  jobId = await seedJob(tenant, tenantId, {
    name: jobName,
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
  ;({ runId } = await triggerAndWaitRun(tenant, tenantId, jobId))
})

test.afterAll(async () => {
  if (jobId) await tenant.call('DELETE', `/api/tenants/${tenantId}/backup-jobs/${jobId}`, undefined, [200, 204]).catch(() => {})
  await tenant.dispose()
})

// Lab limitation, not an app bug: libvirt's firewall drops VM->host UDP, so the DTLS path from the Fedora VM agent
// to a browser on the same host cannot connect (ICE: host 192.168.1.25 / srflx hairpin both fail). Run with a browser the VM can reach over UDP.
test.skip('UserAdmin downloads agent-local backup artifact over WebRTC direct channel via UI and SHA-256 matches checksum', async ({ page }) => {
  // Headless Chromium cannot click an OS GTK file-save dialog; provide a File System Access API sink so the real
  // WebRTC RTCPeerConnection ('backup-download-v1') transfer from virtual-fedora runs end-to-end and publishes verified bytes.
  await page.addInitScript(() => {
    const w = window as unknown as {
      __s3SavedFile?: { name: string; chunks: number[][]; closed: boolean }
      showSaveFilePicker?: (opts: { suggestedName: string }) => Promise<unknown>
    }
    w.showSaveFilePicker = async ({ suggestedName }) => {
      const record = { name: suggestedName, chunks: [] as number[][], closed: false }
      w.__s3SavedFile = record
      return {
        async createWritable() {
          return {
            async write(chunk: Uint8Array) { record.chunks.push(Array.from(chunk)) },
            async close() { record.closed = true },
            async abort() { record.chunks.length = 0; record.closed = false },
          }
        },
      }
    }
  })

  const w = watch(page)
  await signIn(page); w.reset()
  await page.goto(tenantPath(tenantId, `backup-runs/${runId}`))
  const runPanel = page.getByRole('region', { name: 'Yedekleme denemesi' })
  await runPanel.locator('summary').filter({ hasText: /^Diğer$/ }).click()
  await runPanel.getByRole('button', { name: 'İndir', exact: true }).click()

  const codeEl = runPanel.locator('code').first()
  await expect(codeEl).toBeVisible()
  expectedChecksum = (await codeEl.textContent())!.trim()
  expect(expectedChecksum).toMatch(/^[0-9a-f]{64}$/)

  await runPanel.getByRole('button', { name: /Yedeklemeyi İndir|Download Backup/ }).click()
  const saveBtn = runPanel.getByRole('button', { name: 'Dosyayı kaydet' })
  await expect(saveBtn).toBeVisible({ timeout: 30_000 })
  await saveBtn.click()
  await expect(runPanel.getByRole('button', { name: 'Doğrulandı ve diske kaydedildi.' })).toBeVisible({ timeout: 60_000 })

  const saved = await page.evaluate(() => {
    const f = (window as unknown as { __s3SavedFile?: { name: string; chunks: number[][]; closed: boolean } }).__s3SavedFile
    if (!f || !f.closed) return null
    const total = f.chunks.reduce((acc, c) => acc + c.length, 0)
    const flat = new Uint8Array(total)
    let offset = 0
    for (const c of f.chunks) { flat.set(c, offset); offset += c.length }
    let binary = ''
    for (let i = 0; i < flat.length; i += 8192) binary += String.fromCharCode(...flat.subarray(i, i + 8192))
    return { name: f.name, base64: btoa(binary) }
  })
  expect(saved).not.toBeNull()
  const bytes = Buffer.from(saved!.base64, 'base64')
  expect(bytes.byteLength).toBeGreaterThan(0)
  expect(createHash('sha256').update(bytes).digest('hex')).toBe(expectedChecksum)
  w.expectClean()
})

test('UserAdmin creates, extends, and revokes a BackupShare; PlatformAdmin sees share, records download attempt, and loses access after revoke', async ({ page, browser }) => {
  const w = watch(page)
  await signIn(page); w.reset()
  await page.goto(tenantPath(tenantId, `backup-runs/${runId}`))
  const runPanel = page.getByRole('region', { name: 'Yedekleme denemesi' })
  await runPanel.locator('summary').filter({ hasText: /^Diğer$/ }).click()
  await runPanel.getByRole('button', { name: 'Paylaş', exact: true }).click()
  await runPanel.getByRole('button', { name: 'Paylaşım oluştur' }).click()

  const createDialog = page.getByRole('dialog')
  await createDialog.getByPlaceholder('Paylaşım gerekçesini yazın').fill(shareReason)
  await createDialog.getByRole('button', { name: 'Paylaşım oluştur' }).click()
  await expect(page.getByText('Paylaşım başarıyla oluşturuldu.')).toBeVisible()
  await expect(page.getByRole('cell', { name: shareReason })).toBeVisible()

  // Verify in tenant /backup-shares tab and extend duration
  await page.goto(tenantPath(tenantId, 'backup-shares'))
  const tenantRow = page.getByRole('row', { name: new RegExp(shareReason) })
  await expect(tenantRow).toContainText('Aktif')
  await tenantRow.getByRole('button', { name: 'Süreyi uzat' }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Süreyi uzat' }).click()
  await expect(page.getByText('Paylaşım başarıyla uzatıldı.')).toBeVisible()

  // PlatformAdmin sees the share on /platform/shares and initiates a download attempt
  const adminCtx = await browser.newContext({ ignoreHTTPSErrors: true })
  const adminPage = await adminCtx.newPage()
  const adminWatch = watch(adminPage)
  try {
    const adminAcc = state().accounts.platformAdmin
    await signIn(adminPage, adminAcc.login, adminAcc.password)
    adminWatch.reset()
    await adminPage.goto('/platform/shares')
    const adminRow = adminPage.getByRole('row', { name: new RegExp(shareReason) })
    await expect(adminRow).toContainText('Aktif')
    const preparedReq = adminPage.waitForResponse(r => r.url().includes('/downloads') && r.request().method() === 'POST')
    await adminRow.getByRole('button', { name: 'İndir' }).click()
    expect((await preparedReq).status()).toBe(200)
    adminWatch.expectClean()

    // UserAdmin revokes the share (Escape cancels dialog first, then confirm revokes)
    await tenantRow.getByRole('button', { name: 'İptal et' }).click()
    await expect(page.getByRole('dialog')).toBeVisible()
    await page.keyboard.press('Escape')
    await expect(tenantRow).toContainText('Aktif')
    await tenantRow.getByRole('button', { name: 'İptal et' }).click()
    await page.getByRole('dialog').getByRole('button', { name: 'İptal et' }).click()
    await expect(page.getByText('Paylaşım başarıyla iptal edildi.')).toBeVisible()
    await expect(tenantRow).toContainText('İptal edildi')
    await expect(tenantRow.getByRole('button', { name: 'İptal et' })).toHaveCount(0)

    // PlatformAdmin reloads /platform/shares: share is revoked with no Download button, and download attempt is recorded
    await adminPage.reload()
    const revokedAdminRow = adminPage.getByRole('row', { name: new RegExp(shareReason) })
    await expect(revokedAdminRow).toContainText('İptal edildi')
    await expect(revokedAdminRow.getByRole('button', { name: 'İndir' })).toHaveCount(0)
    await adminPage.locator('summary', { hasText: /İndirme girişimleri/ }).click()
    await expect(adminPage.getByRole('row', { name: /e2e-s3 tenant/ }).first()).toBeVisible()
  } finally {
    await adminCtx.close()
  }
  w.expectClean()
})

test('PlatformAdmin downloading an agent-direct BackupShare from /platform/shares transfers the real backup instead of {"sdp":null} (#270)', async ({ browser }) => {
  const runDetail = await tenant.call('GET', `/api/tenants/${tenantId}/backup-runs/${runId}`)
  const artifact = runDetail.attempts.flatMap((a: any) => a.artifacts).at(-1)
  await tenant.call('POST', `/api/tenants/${tenantId}/backup-shares`, {
    artifactId: artifact.id,
    durationHours: 24,
    reason: fixmeShareReason,
  })

  const adminCtx = await browser.newContext({ ignoreHTTPSErrors: true })
  const adminPage = await adminCtx.newPage()
  try {
    const adminAcc = state().accounts.platformAdmin
    await signIn(adminPage, adminAcc.login, adminAcc.password)
    await adminPage.goto('/platform/shares')
    const adminRow = adminPage.getByRole('row', { name: new RegExp(fixmeShareReason) })
    const [sharedDownload] = await Promise.all([
      adminPage.waitForEvent('download', { timeout: 30_000 }),
      adminRow.getByRole('button', { name: 'İndir' }).click(),
    ])
    const sharedBytes = await readFile((await sharedDownload.path())!)
    expect(createHash('sha256').update(sharedBytes).digest('hex')).toBe(artifact.artifactChecksum)
  } finally {
    await adminCtx.close()
  }
})
