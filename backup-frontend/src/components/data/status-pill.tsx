import { Badge } from '@/components/ui/badge'

const statuses: Record<string, { label: string; tone: 'neutral' | 'success' | 'warning' | 'error' | 'info' }> = {
  Pending: { label: 'Sırada · Pending', tone: 'neutral' },
  Leased: { label: 'Agent tarafından alındı · Leased', tone: 'info' },
  Running: { label: 'Çalışıyor · Running', tone: 'info' },
  RetryPending: { label: 'Tekrar deneme bekleniyor · RetryPending', tone: 'warning' },
  CancellationRequested: { label: 'İptal isteniyor · CancellationRequested', tone: 'warning' },
  Succeeded: { label: 'Başarılı · Succeeded', tone: 'success' },
  Failed: { label: 'Başarısız · Failed', tone: 'error' },
  Cancelled: { label: 'İptal edildi · Cancelled', tone: 'neutral' },
  AgentLost: { label: 'Agent bağlantısı kaybedildi', tone: 'error' },
  Active: { label: 'Etkin · Active', tone: 'success' },
  Disabled: { label: 'Devre dışı · Disabled', tone: 'warning' },
  Revoked: { label: 'İptal edilmiş kimlik · Revoked', tone: 'error' },
  online: { label: 'Çevrimiçi', tone: 'success' },
  offline: { label: 'Çevrimdışı', tone: 'neutral' },
  unknown: { label: 'Bağlantı bilinmiyor', tone: 'neutral' },
  Available: { label: 'Kullanılabilir · Available', tone: 'success' },
  DeletePending: { label: 'Silme bekliyor · DeletePending', tone: 'warning' },
  DeleteFailed: { label: 'Silme başarısız · DeleteFailed', tone: 'error' },
  Deleted: { label: 'Silindi · Deleted', tone: 'neutral' },
  Deleting: { label: 'Fiziksel silme sürüyor', tone: 'warning' },
  Verified: { label: 'Doğrulandı', tone: 'success' },
}
export function StatusPill({ status }: { status: string }) {
  const meta = statuses[status] ?? { label: status, tone: 'neutral' as const }
  return <Badge tone={meta.tone}>{meta.label}</Badge>
}
