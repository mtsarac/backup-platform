import { tx } from '@/lib/i18n'
import type { BackupRunStatus, BackupTrigger } from './types'

export const runStatuses: readonly BackupRunStatus[] = ['Pending', 'Leased', 'Running', 'Paused', 'RetryPending', 'CancellationRequested', 'Succeeded', 'Failed', 'Cancelled', 'AgentLost']

export function isActiveRun(status: string): boolean {
  return ['Pending', 'Leased', 'Running', 'Paused', 'RetryPending', 'CancellationRequested'].includes(status)
}

export function canCancelRun(status: string): boolean {
  return ['Pending', 'Leased', 'Running', 'Paused', 'RetryPending'].includes(status)
}

export function canRetryRun(status: string): boolean {
  return status === 'Failed' || status === 'AgentLost'
}

export function triggerLabel(trigger?: BackupTrigger | null): string {
  const labels: Record<BackupTrigger, string> = {
    Manual: tx('Elle başlatıldı'), Scheduled: tx('Zamanlanmış'), Retry: tx('Yeniden deneme'),
    ManualRetry: tx('Elle yeniden deneme'), AutomaticRetry: tx('Otomatik yeniden deneme'),
  }
  return trigger ? labels[trigger] : '—'
}

/** Date controls use the user's local day; the API filters inclusive UTC instants. */
export function dateFilterInstant(day: string, endOfDay = false): string | undefined {
  if (!day) return undefined
  return new Date(`${day}T${endOfDay ? '23:59:59.999' : '00:00:00.000'}`).toISOString()
}

export function progressStageLabel(stage?: string | null): string {
  if (!stage) return tx('Henüz ilerleme bildirilmedi')
  const labels: Record<string, string> = {
    Preparing: tx('Hazırlanıyor'), Backup: tx('Yedek oluşturuluyor'), BackingUp: tx('Yedek oluşturuluyor'),
    Uploading: tx('Aktarılıyor'), Verifying: tx('Doğrulanıyor'), Finalizing: tx('Tamamlanıyor'), Completed: tx('Tamamlandı'),
  }
  return labels[stage] ?? stage
}

export const retryStateLabels: Record<string, string> = {
  get None() { return tx("Yeniden deneme planlanmadı") }, get Scheduled() { return tx("Otomatik yeniden deneme planlandı") },
  get Exhausted() { return tx("Deneme hakkı tükendi") }, get NotRetryable() { return tx("Otomatik yeniden denemeye uygun değil") },
}
