import { currentLocale, tx } from '@/lib/i18n'
import type { BackupRunStatus, BackupTrigger } from './types'

export const runStatuses: readonly BackupRunStatus[] = ['Pending', 'Leased', 'Running', 'RetryPending', 'CancellationRequested', 'Succeeded', 'Failed', 'Cancelled', 'AgentLost']
export const runStatusLabels: Record<BackupRunStatus, string> = {
  get Pending() { return tx("Bekliyor") }, get Leased() { return tx("Ajan tarafından alındı") }, get Running() { return tx("Çalışıyor") },
  get RetryPending() { return tx("Yeniden deneme bekliyor") }, get CancellationRequested() { return tx("İptal istendi") },
  get Succeeded() { return tx("Başarılı") }, get Failed() { return tx("Başarısız") }, get Cancelled() { return tx("İptal edildi") }, get AgentLost() { return tx("Ajan bağlantısı kayboldu") },
}

export function isActiveRun(status: string): boolean {
  return ['Pending', 'Leased', 'Running', 'RetryPending', 'CancellationRequested'].includes(status)
}

export function canCancelRun(status: string): boolean {
  return ['Pending', 'Leased', 'Running', 'RetryPending'].includes(status)
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

export function formatTimestamp(value?: string | null): string {
  if (!value) return '—'
  return new Intl.DateTimeFormat(currentLocale(), { dateStyle: 'short', timeStyle: 'medium' }).format(new Date(value))
}

export function formatDuration(start?: string | null, end?: string | null): string {
  if (!start) return '—'
  if (!end) return tx("{0} (devam ediyor)", { "0": formatSeconds(Math.max(0, (Date.now() - Date.parse(start)) / 1000)) })
  return formatSeconds(Math.max(0, (Date.parse(end) - Date.parse(start)) / 1000))
}

export function formatSeconds(seconds: number): string {
  const total = Math.round(seconds)
  const hours = Math.floor(total / 3600)
  const minutes = Math.floor((total % 3600) / 60)
  return tx("{0}{1} dk {2} sn", { "0": hours ? tx("{0} sa ", { "0": hours }) : '', "1": minutes, "2": total % 60 })
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
