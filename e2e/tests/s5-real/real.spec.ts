// Real bytes on the isolated bps5 stack (e2e/s5-stack.sh): artifacts whose payload sits in two MinIO buckets, read back by the backend.
import { createHash } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { expect, test, type Page } from '@playwright/test'
import { english, realWorld, signIn, tenantUrl, watch, type RealWorld } from './support'
import { getObject, objectExists, objectKey, putObject, removeObject, sha256 } from '../../s3-artifact'

test.skip(process.env.E2E_STATE_FILE !== '.state-s5.json', 'Requires the isolated S5 stack; run e2e/s5-stack.sh and use its documented environment.')
let w: RealWorld
test.beforeAll(async () => { w = await realWorld() })
test.beforeEach(async ({ context }) => { await english(context) })

const runUrl = (id: string) => tenantUrl(w.t, `/backup-runs/${id}`)
async function openDownload(page: Page, runId: string) {
  await page.goto(runUrl(runId))
  await page.getByText('More', { exact: true }).click()
  await page.getByRole('button', { name: 'Download', exact: true }).click()
}

test.describe('download with SHA-256 check', () => {
  test('UserAdmin downloads the real artifact; file bytes hash to the checksum the page shows', async ({ page }) => {
    const a = await w.mk()
    const health = watch(page)
    await signIn(page, w.t.account)
    await openDownload(page, a.runId)
    await expect(page.getByText(a.checksum, { exact: true })).toBeVisible()
    const download = page.waitForEvent('download')
    await page.getByRole('button', { name: 'Download Backup' }).first().click()
    const file = await download
    expect(file.suggestedFilename()).toMatch(/^backup-[0-9a-f]{32}\.bak$/)
    const path = await file.path()
    const bytes = readFileSync(path)
    expect(bytes.length).toBe(a.payload.length)
    expect(createHash('sha256').update(bytes).digest('hex')).toBe(a.checksum)
    expect(bytes.equals(a.payload)).toBe(true)
    health.expectClean()
  })
})

test.describe('download integrity', () => {
  test('a replica tampered with in storage is not delivered: the download fails and says why', async ({ page }) => {
    const a = await w.mk()
    for (const d of w.dests) putObject(d.bucket, objectKey(w.t, d, a.key), Buffer.alloc(a.payload.length, 7))
    await signIn(page, w.t.account)
    await openDownload(page, a.runId)
    const download = page.waitForEvent('download')
    await page.getByRole('button', { name: 'Download Backup' }).first().click()
    expect(await (await download).failure()).not.toBeNull()
    await expect(page.getByRole('main').getByRole('alert')).toContainText('File checksum verification failed')
  })

  test('with one replica missing the other still serves a bit-identical download', async ({ page }) => {
    const a = await w.mk()
    removeObject(w.dests[0].bucket, objectKey(w.t, w.dests[0], a.key))
    await signIn(page, w.t.account)
    await openDownload(page, a.runId)
    const download = page.waitForEvent('download')
    await page.getByRole('button', { name: 'Download Backup' }).first().click()
    const file = await download
    expect(sha256(readFileSync(await file.path()))).toBe(a.checksum)
  })

  test('every replica gone: no download starts and the page explains', async ({ page }) => {
    const a = await w.mk()
    for (const d of w.dests) removeObject(d.bucket, objectKey(w.t, d, a.key))
    await signIn(page, w.t.account)
    await page.goto(runUrl(a.runId))
    await page.getByText('More', { exact: true }).click()
    await page.getByRole('button', { name: 'Download', exact: true }).click()
    await expect(page.getByText(/Download unavailable|No verified replicas/i).first()).toBeVisible()
  })
})

test.describe('replica repair', () => {
  /** A real lost copy: the object is removed from bucket B, and a download attempt makes the backend notice (that is what marks the replica missing). */
  async function loseReplica() {
    const a = await w.mk()
    removeObject(w.dests[1].bucket, objectKey(w.t, w.dests[1], a.key))
    await w.t.tenant.call('POST', `/api/tenants/${w.t.tenantId}/backup-artifacts/${a.artifactId}/downloads`, {})
    const view = await w.t.tenant.call('GET', `/api/tenants/${w.t.tenantId}/backup-artifacts/${a.artifactId}`)
    expect(view.replicas.map((r: any) => r.verificationMethod).sort()).toEqual(['ObjectMissing', 'ReadBack'])
    return a
  }

  test('lost copy: run shows partial replication, and the repair button is offered on the artifact', async ({ page }) => {
    const a = await loseReplica()
    await signIn(page, w.t.account)
    await page.goto(runUrl(a.runId))
    await expect(page.getByText('Partial replication · policy met').first()).toBeVisible()
    for (const b of await page.getByRole('button', { name: 'Technical details' }).all()) await b.click()
    await page.getByRole('button', { name: 'Repair' }).click()
    await page.getByRole('dialog').getByRole('button', { name: 'Start repair' }).click()
    await expect(page.getByText('Replica repaired and verified successfully.')).toBeVisible({ timeout: 60_000 })
    expect(sha256(getObject(w.dests[1].bucket, objectKey(w.t, w.dests[1], a.key)))).toBe(a.checksum)
  })

  test.fixme('repair started through the API is reflected by the UI once finished: both replicas verified, bytes restored', async ({ page }) => {
    // S3-to-S3 repair requires a lifecycle-capable agent; the protocol fixture does not claim repair tasks.
    const a = await loseReplica()
    const op = await w.t.tenant.call('POST', `/api/tenants/${w.t.tenantId}/backup-artifacts/${a.artifactId}/repairs`, { storageProvider: 's3', storageConnectionId: w.dests[1].connectionId }, [200, 201, 202])
    await expect.poll(async () => (await w.t.tenant.call('GET', `/api/tenants/${w.t.tenantId}/backup-artifacts/${a.artifactId}/repairs/${op.id}`)).status, { timeout: 60_000 }).toBe('Succeeded')
    expect(objectExists(w.dests[1].bucket, objectKey(w.t, w.dests[1], a.key))).toBe(true)
    expect(sha256(getObject(w.dests[1].bucket, objectKey(w.t, w.dests[1], a.key)))).toBe(a.checksum)
    await signIn(page, w.t.account)
    await page.goto(runUrl(a.runId))
    await expect(page.getByText('Replication healthy').first()).toBeVisible()
    await page.getByRole('tab', { name: 'Replication' }).click()
    await expect(page.getByRole('tabpanel').getByText('Verified', { exact: true })).toHaveCount(2)
  })
})
