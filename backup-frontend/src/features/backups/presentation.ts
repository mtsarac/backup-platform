import type { BackupRunStatus, BackupTrigger } from './types'

export const runStatuses: readonly BackupRunStatus[] = ['Pending', 'Leased', 'Running', 'RetryPending', 'CancellationRequested', 'Succeeded', 'Failed', 'Cancelled', 'AgentLost']
export const runStatusLabels: Record<BackupRunStatus, string> = {
  Pending: 'Bekliyor', Leased: 'Ajan tarafından alındı', Running: 'Çalışıyor',
  RetryPending: 'Yeniden deneme bekliyor', CancellationRequested: 'İptal istendi',
  Succeeded: 'Başarılı', Failed: 'Başarısız', Cancelled: 'İptal edildi', AgentLost: 'Ajan bağlantısı kayboldu',
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
    Manual: 'Elle başlatıldı', Scheduled: 'Zamanlanmış', Retry: 'Yeniden deneme',
    ManualRetry: 'Elle yeniden deneme', AutomaticRetry: 'Otomatik yeniden deneme',
  }
  return trigger ? labels[trigger] : '—'
}

export function formatTimestamp(value?: string | null): string {
  if (!value) return '—'
  return new Intl.DateTimeFormat('tr-TR', { dateStyle: 'short', timeStyle: 'medium' }).format(new Date(value))
}

export function formatDuration(start?: string | null, end?: string | null): string {
  if (!start) return '—'
  if (!end) return `${formatSeconds(Math.max(0, (Date.now() - Date.parse(start)) / 1000))} (devam ediyor)`
  return formatSeconds(Math.max(0, (Date.parse(end) - Date.parse(start)) / 1000))
}

export function formatSeconds(seconds: number): string {
  const total = Math.round(seconds)
  const hours = Math.floor(total / 3600)
  const minutes = Math.floor((total % 3600) / 60)
  return `${hours ? `${hours} sa ` : ''}${minutes} dk ${total % 60} sn`
}

/** Date controls use the user's local day; the API filters inclusive UTC instants. */
export function dateFilterInstant(day: string, endOfDay = false): string | undefined {
  if (!day) return undefined
  return new Date(`${day}T${endOfDay ? '23:59:59.999' : '00:00:00.000'}`).toISOString()
}

export function progressStageLabel(stage?: string | null): string {
  if (!stage) return 'Henüz ilerleme bildirilmedi'
  const labels: Record<string, string> = {
    Preparing: 'Hazırlanıyor', Backup: 'Yedek oluşturuluyor', BackingUp: 'Yedek oluşturuluyor',
    Uploading: 'Aktarılıyor', Verifying: 'Doğrulanıyor', Finalizing: 'Tamamlanıyor', Completed: 'Tamamlandı',
  }
  return labels[stage] ?? stage
}

export const retryStateLabels: Record<string, string> = {
  None: 'Yeniden deneme planlanmadı', Scheduled: 'Otomatik yeniden deneme planlandı',
  Exhausted: 'Deneme hakkı tükendi', NotRetryable: 'Otomatik yeniden denemeye uygun değil',
}
