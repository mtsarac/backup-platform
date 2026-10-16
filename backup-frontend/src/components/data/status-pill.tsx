import { tx, useLanguage } from '@/lib/i18n'
import { Badge } from '@/components/ui/badge'

const statuses: Record<string, { label: string; tone: 'neutral' | 'success' | 'warning' | 'error' | 'info' }> = {
  Pending: { get label() { return tx("Sırada bekliyor") }, tone: 'neutral' },
  Leased: { get label() { return tx("Agent görevi aldı") }, tone: 'info' },
  Running: { get label() { return tx("Çalışıyor") }, tone: 'info' },
  RetryPending: { get label() { return tx("Yeniden denenecek") }, tone: 'warning' },
  CancellationRequested: { get label() { return tx("İptal isteniyor") }, tone: 'warning' },
  Succeeded: { get label() { return tx("Başarılı") }, tone: 'success' },
  Failed: { get label() { return tx("Başarısız") }, tone: 'error' },
  Cancelled: { get label() { return tx("İptal edildi") }, tone: 'neutral' },
  AgentLost: { get label() { return tx("Agent bağlantısı kaybedildi") }, tone: 'error' },
  Active: { get label() { return tx("Etkin") }, tone: 'success' },
  Disabled: { get label() { return tx("Devre dışı") }, tone: 'warning' },
  Revoked: { get label() { return tx("Erişimi iptal edildi") }, tone: 'error' },
  online: { get label() { return tx("Çevrimiçi") }, tone: 'success' },
  offline: { get label() { return tx("Çevrimdışı") }, tone: 'neutral' },
  unknown: { get label() { return tx("Bağlantı bilinmiyor") }, tone: 'neutral' },
  Available: { get label() { return tx("Kullanılabilir") }, tone: 'success' },
  Quarantined: { get label() { return tx("Karantinada · sahiplik incelemesi gerekli") }, tone: 'warning' },
  DeletePending: { get label() { return tx("Silme bekliyor") }, tone: 'warning' },
  DeleteFailed: { get label() { return tx("Silme başarısız") }, tone: 'error' },
  Deleted: { get label() { return tx("Silindi") }, tone: 'neutral' },
  Deleting: { get label() { return tx("Fiziksel silme sürüyor") }, tone: 'warning' },
  Verified: { get label() { return tx("Doğrulandı") }, tone: 'success' },
}
export function StatusPill({ status }: { status: string }) {
  useLanguage()
  const meta = statuses[status] ?? { label: status, tone: 'neutral' as const }
  return <Badge tone={meta.tone} dot>{tx(meta.label)}</Badge>
}
