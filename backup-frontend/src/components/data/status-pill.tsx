import { Badge } from '@/components/ui/badge'

const statuses: Record<string, { label: string; tone: 'neutral' | 'success' | 'warning' | 'error' | 'info' }> = {
  Pending: { label: 'Sırada bekliyor', tone: 'neutral' },
  Leased: { label: 'Agent görevi aldı', tone: 'info' },
  Running: { label: 'Çalışıyor', tone: 'info' },
  RetryPending: { label: 'Yeniden denenecek', tone: 'warning' },
  CancellationRequested: { label: 'İptal isteniyor', tone: 'warning' },
  Succeeded: { label: 'Başarılı', tone: 'success' },
  Failed: { label: 'Başarısız', tone: 'error' },
  Cancelled: { label: 'İptal edildi', tone: 'neutral' },
  AgentLost: { label: 'Agent bağlantısı kaybedildi', tone: 'error' },
  Active: { label: 'Etkin', tone: 'success' },
  Disabled: { label: 'Devre dışı', tone: 'warning' },
  Revoked: { label: 'Erişimi iptal edildi', tone: 'error' },
  online: { label: 'Çevrimiçi', tone: 'success' },
  offline: { label: 'Çevrimdışı', tone: 'neutral' },
  unknown: { label: 'Bağlantı bilinmiyor', tone: 'neutral' },
  Available: { label: 'Kullanılabilir', tone: 'success' },
  DeletePending: { label: 'Silme bekliyor', tone: 'warning' },
  DeleteFailed: { label: 'Silme başarısız', tone: 'error' },
  Deleted: { label: 'Silindi', tone: 'neutral' },
  Deleting: { label: 'Fiziksel silme sürüyor', tone: 'warning' },
  Verified: { label: 'Doğrulandı', tone: 'success' },
}
export function StatusPill({ status }: { status: string }) {
  const meta = statuses[status] ?? { label: status, tone: 'neutral' as const }
  return <Badge tone={meta.tone}>{meta.label}</Badge>
}
