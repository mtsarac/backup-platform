// S5: artifacts with REAL bytes in a MinIO bucket, so backend-executed paths (download SHA-256, replica missing, repair) run for real.
// The MinIO is the bps5 stack's own (e2e/s5-stack.sh). The protocol agent only reports the upload; the bytes are put by `mc`.
import { createHash, randomBytes, randomUUID } from 'node:crypto'
import { spawnSync } from 'node:child_process'
import { type ProtocolAgent, type S5Tenant } from './protocol-agent'

const container = process.env.E2E_MINIO_CONTAINER ?? 'bps5-minio'
const user = process.env.E2E_MINIO_USER ?? 's5minio'
const pass = process.env.E2E_MINIO_PASSWORD ?? 'S5-minio-pass-1'
const endpoint = process.env.E2E_MINIO_ENDPOINT ?? 'https://bps5-minio:9000' // as the backend container sees it

function mc(args: string[], input?: Buffer): Buffer {
  const r = spawnSync('docker', ['exec', '-i', '-e', `MC_HOST_e2e=https://${user}:${pass}@localhost:9000`, container, 'mc', '--insecure', ...args], { input, maxBuffer: 64 << 20 })
  if (r.status !== 0) throw new Error(`mc ${args.join(' ')} -> ${r.status} ${r.stderr}`)
  return r.stdout
}
export const minioReachable = () => spawnSync('docker', ['exec', container, 'true']).status === 0
export const ensureBucket = (bucket: string) => void mc(['mb', '--ignore-existing', `e2e/${bucket}`])
export const putObject = (bucket: string, key: string, bytes: Buffer) => void mc(['pipe', `e2e/${bucket}/${key}`], bytes)
export const getObject = (bucket: string, key: string) => mc(['cat', `e2e/${bucket}/${key}`])
export const removeObject = (bucket: string, key: string) => void mc(['rm', `e2e/${bucket}/${key}`])
export const objectExists = (bucket: string, key: string) => mc(['ls', `e2e/${bucket}/${key}`]).length > 0
/** The backend stores every object under tenants/<tenant>/connections/<connection>/<key> inside the bucket. */
export const objectKey = (t: S5Tenant, d: S3Dest, key: string) => `tenants/${t.tenantId.replaceAll('-', '')}/connections/${d.connectionId.replaceAll('-', '')}/${key}`
export const sha256 = (b: Buffer) => createHash('sha256').update(b).digest('hex')

export type S3Dest = { connectionId: string; bucket: string }

export async function createS3Connection(t: S5Tenant, name: string, bucket: string): Promise<S3Dest> {
  ensureBucket(bucket)
  const c = await t.tenant.call('POST', `/api/tenants/${t.tenantId}/storage-connections`, {
    providerType: 's3', name, ownership: 'TenantManaged',
    configuration: { endpoint, bucket, region: 'us-east-1' }, secret: { accessKey: user, secretKey: pass },
  })
  return { connectionId: c.id, bucket }
}

export async function createS3Job(t: S5Tenant, agent: ProtocolAgent, name: string, dests: S3Dest[]): Promise<string> {
  const job = await t.tenant.call('POST', `/api/tenants/${t.tenantId}/backup-jobs`, {
    name, agentId: agent.agentId, overlapPolicy: 'Queue',
    configuration: { engine: 'mssql', database: 'master', host: 'sql-server', integratedSecurity: true, requiredVerifiedReplicaCount: 1,
      destinations: dests.map((d, i) => ({ storageProvider: 's3', storageConnectionId: d.connectionId, storageExecutionLocation: 'Backend', isPrimary: i === 0 })) },
  }, [200, 201], { 'Idempotency-Key': randomUUID() })
  return job.id
}

const iso = (ms = 0) => new Date(Date.now() + ms).toISOString()

/** One successful run whose artifact bytes really sit in every destination bucket. Returns run id, artifact id, the bytes and their SHA-256. */
export async function runWithRealBytes(t: S5Tenant, agent: ProtocolAgent, jobId: string, dests: S3Dest[], size = 300_000) {
  const payload = randomBytes(size)
  const checksum = sha256(payload)
  await t.tenant.call('POST', `/api/tenants/${t.tenantId}/backup-jobs/${jobId}/runs`, {}, [200, 201, 202], { 'Idempotency-Key': randomUUID() })
  const backup = (await (await agent.ctx.get('/api/agent/tasks', { headers: agent.headers })).json()).tasks?.[0]?.backup
  if (!backup) throw new Error('no pending run for the protocol agent')
  const { attemptId, leaseToken } = backup
  const post = async (path: string, data: unknown) => {
    const r = await agent.ctx.post(`/api/agent/tasks/${attemptId}/${path}`, { headers: agent.headers, data })
    if (!r.ok()) throw new Error(`${path} -> ${r.status()} ${await r.text()}`)
  }
  await post('start', { leaseToken, softwareVersion: '1.2.3', protocolVersion: '1.0', platform: 'linux' })
  const key = `${attemptId.replaceAll('-', '')}/backup.bak`
  for (const d of dests) putObject(d.bucket, objectKey(t, d, key), payload)
  const created = iso()
  await post('complete', {
    leaseToken, status: 'Succeeded',
    destinations: dests.map((d) => ({ storageProvider: 's3', storageConnectionId: d.connectionId, state: 'Verified', upload: 'Succeeded', verification: 'Verified', failureCode: null, key, objectMayRemain: false, startedAt: iso(-4_000), completedAt: iso(-1_000) })),
    artifact: {
      sizeBytes: size, sourceChecksum: 'a'.repeat(64), artifactChecksum: checksum, checksumAlgorithm: 'sha256', createdAt: created,
      sourceMetadataJson: JSON.stringify({ engine: 'mssql', databaseName: 'master', backupType: 'Full', backupStartedAt: iso(-10_000), backupFinishedAt: iso(-5_000), backupVerification: { state: 'Passed', method: 'VerifyOnly' } }),
      appliedOptionsJson: JSON.stringify({ copyOnly: true, checksum: true, compression: true }),
      replicas: dests.map((d) => ({ storageProvider: 's3', storageConnectionId: d.connectionId, storageTarget: d.connectionId, key, sizeBytes: size, checksum, checksumAlgorithm: 'sha256', verificationState: 'Verified', verificationMethod: 'ReadBack', createdAt: created, verifiedAt: created })),
    },
  })
  const list = await t.tenant.call('GET', `/api/tenants/${t.tenantId}/backup-artifacts?pageSize=50`)
  const artifact = list.items.find((a: any) => a.runId === backup.runId)
  return { runId: backup.runId as string, artifactId: artifact.id as string, key, payload, checksum }
}
