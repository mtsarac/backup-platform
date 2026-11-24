import { Api } from '../../api'
import { state } from '../../state'
import { enrollAgent, seedTenant, type ProtocolAgent, type S5Tenant } from '../../protocol-agent'
import { createS3Connection, createS3Job, runWithRealBytes, type S3Dest } from '../../s3-artifact'

export { english, signIn, tenantUrl, watch } from '../s5/support'
export const rand = Math.random().toString(36).slice(2, 7)

export type RealWorld = { t: S5Tenant; agent: ProtocolAgent; dests: [S3Dest, S3Dest]; jobId: string; mk: () => ReturnType<typeof runWithRealBytes> }

let cached: Promise<RealWorld> | undefined
/** One tenant on the isolated bps5 stack with two real S3 destinations (own MinIO buckets); `mk()` records one more artifact whose bytes sit in both. */
export function realWorld(): Promise<RealWorld> {
  cached ??= (async () => {
    const platform = await Api.signIn(state().accounts.platformAdmin)
    const t = await seedTenant(platform, `${rand}r`)
    const agent = await enrollAgent(t, `s5-real-${rand}`)
    const dests: [S3Dest, S3Dest] = [await createS3Connection(t, `s5-a-${rand}`, `e2e-s5-a-${rand}`), await createS3Connection(t, `s5-b-${rand}`, `e2e-s5-b-${rand}`)]
    const jobId = await createS3Job(t, agent, `s5-real-job-${rand}`, dests)
    return { t, agent, dests, jobId, mk: () => runWithRealBytes(t, agent, jobId, dests) }
  })()
  return cached
}
