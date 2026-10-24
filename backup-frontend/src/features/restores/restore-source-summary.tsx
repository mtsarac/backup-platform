import { tx, useLanguage } from '@/lib/i18n'
import { StatusPill } from '@/components/data/status-pill'
import { Notice } from '@/components/ui/notice'
import type { BackupArtifact } from '@/features/artifacts/types'
import { formatBytes } from '@/lib/format'

export function RestoreSourceSummary({ artifact, isPg }: { artifact: BackupArtifact; isPg: boolean }) {
  useLanguage()
  return <>
    {/* Artifact Summary */}
    <section aria-label={tx("Kaynak Backup Bilgisi")} className="space-y-2 rounded-xl border border-border bg-surface-2 p-4 text-sm">
      <div className="flex flex-wrap items-center justify-between gap-2 font-medium">
        <span className="min-w-0 break-words">{tx("Kaynak:")}{' '}{artifact.databaseName ?? tx('Bilinmeyen Database')} · {formatBytes(artifact.sizeBytes)}</span>
        <StatusPill status={artifact.state} />
      </div>
      <details>
        <summary className="cursor-pointer text-text-muted">{tx("Kaynak ayrıntıları ·")}{' '}{artifact.replicas.length}{' '}{tx("Replica")}</summary>
        <p className="mt-2 break-all text-sm text-text-muted">{tx("Artifact ID:")}{' '}<code>{artifact.id}</code>{' '}{tx("· Tür:")}{' '}{tx(artifact.backupType)}{' '}{tx("· Replikalar:")}{' '}{artifact.replicas.map((r) => `${r.storageProvider} (${r.status})`).join(', ')}</p>
      </details>
    </section>
    {isPg && <Notice tone="info" title={tx("PostgreSQL Full Restore")}>{tx("Yalnızca güvendiğiniz platform arşivlerini geri yükleyin: pg_restore içindeki SQL, fonksiyonlar ve extension işlemleri hedefte superuser olarak çalışabilir. SHA-256 byte bütünlüğünü doğrular, kaynak SQL'in güvenliğini doğrulamaz. pg_dump cluster rollerini yedeklemez; hedef superuser kimlik bilgisi, kaynak sahiplikleri/ACL için gereken roller ve extension paketleri hazır olmalıdır. Hedef sürümü hem kaynak hem pg_dump sürümüyle aynı veya daha yeni olmalıdır. Önce geçici Database'e geri yüklenir, sonra hedef adı değiştirilir. Onaylı üzerine yazmada eski Database bağlantıları kapalı bp_old_* adıyla kurtarma için korunur; kontrol ve temizliği operatör yapmalıdır. WAL/PITR desteklenmez.")}</Notice>}
  </>
}
