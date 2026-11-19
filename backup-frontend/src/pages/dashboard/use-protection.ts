import { useQueries, useQuery } from '@tanstack/react-query'
import { getAgents } from '@/features/agents/api'
import { agentQueryKeys } from '@/features/agents/hooks'
import { getBackupJobs, getHealthTimeline } from '@/features/backup-jobs/api'
import { backupJobQueryKeys } from '@/features/backup-jobs/hooks'
import { getBackup, getBackups } from '@/features/backups/api'
import { backupQueryKeys } from '@/features/backups/hooks'
import { isActiveRun } from '@/features/backups/presentation'
import { getDashboard, getHealthSummary } from '@/features/dashboard/api'
import { buildRow } from '@/features/dashboard/protection'
import { getRestores } from '@/features/restores/api'
import { getTenantStorageQuota } from '@/features/storage/api'
import { storageQueryKeys } from '@/features/storage/hooks'

const poll = { refetchInterval: 15_000, refetchIntervalInBackground: false } as const
const latestSuccess = { status: 'Succeeded', page: 1, pageSize: 1 } as const

/** One row per job with its status, shared by the home page and the jobs list; re-requested every 15 s. Each row's status rules are the job panel's. */
export function useJobRows(tenantId: string) {
  const jobs = useQuery({ queryKey: backupJobQueryKeys.all(tenantId), queryFn: ({ signal }) => getBackupJobs(tenantId, signal), ...poll })
  const agents = useQuery({ queryKey: agentQueryKeys.all(tenantId), queryFn: ({ signal }) => getAgents(tenantId, signal), ...poll })
  const health = useQuery({ queryKey: ['tenant', tenantId, 'observability'], queryFn: ({ signal }) => getHealthSummary(tenantId, signal), ...poll })
  const timeline = useQuery({ queryKey: [...backupJobQueryKeys.all(tenantId), 'health-timeline'], queryFn: ({ signal }) => getHealthTimeline(tenantId, signal), ...poll })
  const jobList = jobs.data
  const successes = useQueries({ queries: (jobList ?? []).map(job => {
    const filters = { ...latestSuccess, jobId: job.id }
    return { queryKey: backupQueryKeys.list(tenantId, filters), queryFn: ({ signal }: { signal: AbortSignal }) => getBackups(tenantId, filters, signal), ...poll }
  }) })
  const activeJobs = (jobList ?? []).filter(job => job.lastRun && isActiveRun(job.lastRun.status))
  const running = useQueries({ queries: activeJobs.map(job => {
    const id = job.lastRun!.id
    return { queryKey: backupQueryKeys.detail(tenantId, id), queryFn: ({ signal }: { signal: AbortSignal }) => getBackup(tenantId, id, signal), ...poll }
  }) })
  const core = [jobs, agents, health, timeline]
  const perJob = [...successes, ...running]
  const rows = (jobList ?? []).map((job, index) => buildRow({
    job,
    health: health.data?.jobs.find(item => item.jobId === job.id),
    days: timeline.data?.jobs.find(item => item.jobId === job.id)?.days ?? [],
    hasSucceeded: (successes[index]?.data?.totalCount ?? 0) > 0,
    lastSuccessId: successes[index]?.data?.items[0]?.id,
    lastSuccessAt: successes[index]?.data?.items[0]?.completedAt,
    offline: agents.data?.find(agent => agent.agentId === job.agentId)?.connectivity === 'offline',
    percent: running[activeJobs.indexOf(job)]?.data?.attempts.at(-1)?.progressPercent,
  }))
  return {
    jobList, rows, agents: agents.data, core, perJob,
    error: [...core, ...perJob].find(query => query.error)?.error ?? null,
    isPending: core.some(query => query.isPending) || successes.some(query => query.isPending),
    refetch: () => { for (const query of [...core, ...perJob]) if (query.error) void query.refetch() },
  }
}

/** Everything the home page shows. */
export function useProtection(tenantId: string) {
  const base = useJobRows(tenantId)
  const dashboard = useQuery({ queryKey: ['tenant', tenantId, 'dashboard'], queryFn: ({ signal }) => getDashboard(tenantId, signal), ...poll })
  const lastSuccess = useQuery({ queryKey: backupQueryKeys.list(tenantId, latestSuccess), queryFn: ({ signal }) => getBackups(tenantId, latestSuccess, signal), ...poll })
  const quota = useQuery({ queryKey: storageQueryKeys.quota(tenantId), queryFn: ({ signal }) => getTenantStorageQuota(tenantId, signal), ...poll })
  // Revoked computers' jobs never count towards the first-steps checklist.
  const noLiveJobs = base.jobList?.every(job => job.agentRevoked) ?? false
  const restoreTrial = useQuery({ queryKey: ['tenant', tenantId, 'restores', 'verified'], queryFn: ({ signal }) => getRestores(tenantId, { purpose: 'Verification', status: 'Succeeded', pageSize: 1 }, signal), enabled: noLiveJobs, ...poll })
  const own = [dashboard, lastSuccess]
  const error = base.error ?? own.find(query => query.error)?.error ?? null
  const isPending = base.isPending || own.some(query => query.isPending)
  // A revoked computer's jobs can never run: they are not protected data, so counts, the badge, the band and the next backup leave them out.
  const rows = base.rows.filter(row => !row.job.agentRevoked)
  const revokedRows = base.rows.filter(row => row.job.agentRevoked)
  return {
    isPending, error, rows, revokedRows, agents: (base.agents ?? []).filter(agent => agent.status !== 'Revoked'), dashboard: dashboard.data, lastSuccess: lastSuccess.data?.items[0], hasSucceededEver: rows.some(row => row.hasSucceeded),
    quota: quota.data, hasRestoreTrial: rows.some(row => row.hasSucceeded) && (restoreTrial.data?.totalCount ?? 0) > 0,
    refetch: () => { base.refetch(); for (const query of own) if (query.error) void query.refetch() },
  }
}
