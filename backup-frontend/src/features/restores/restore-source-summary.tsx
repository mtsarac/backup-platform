import { tx, useLanguage } from '@/lib/i18n'
import { StatusPill } from '@/components/data/status-pill'
import { Notice } from '@/components/ui/notice'
import { TechnicalDetails } from '@/components/ui/technical-details'
import type { BackupArtifact } from '@/features/artifacts/types'
import { formatBytes } from '@/lib/format'

export function RestoreSourceSummary({ artifact, isPg, fileArchive = false }: { artifact: BackupArtifact; isPg: boolean; fileArchive?: boolean }) {
  useLanguage()
  return <>
    <section aria-label={tx("Yedek dosyası bilgisi")} className="space-y-2 rounded-xl border border-border bg-surface-2 p-4 text-sm">
      <div className="flex flex-wrap items-center justify-between gap-2 font-medium">
        <span className="min-w-0 break-words">{tx("Yedek dosyası:")}{' '}{artifact.databaseName ?? (fileArchive ? tx('Bilinmeyen dosya kaynağı') : tx('Bilinmeyen veritabanı'))} · {formatBytes(artifact.sizeBytes)}</span>
        <StatusPill status={artifact.state} />
      </div>
      <TechnicalDetails items={[
        { label: tx('Yedek dosyası kimliği'), value: artifact.id },
        ...(artifact.backupType ? [{ label: tx('Yedek türü'), value: artifact.backupType }] : []),
        { label: tx('Kopyalar'), value: artifact.replicas.map((r) => `${r.storageProvider} (${r.status})`).join(', ') },
      ]} />
    </section>
    {isPg && <Notice tone="info" title={tx("PostgreSQL Full Restore")}>{tx("Yalnızca güvendiğiniz platform arşivlerini geri yükleyin: pg_restore içindeki SQL, fonksiyonlar ve extension işlemleri hedefte superuser olarak çalışabilir. SHA-256 byte bütünlüğünü doğrular, kaynak SQL'in güvenliğini doğrulamaz. pg_dump cluster rollerini yedeklemez; hedef superuser kimlik bilgisi, kaynak sahiplikleri/ACL için gereken roller ve extension paketleri hazır olmalıdır. Hedef sürümü hem kaynak hem pg_dump sürümüyle aynı veya daha yeni olmalıdır. Önce geçici Database'e geri yüklenir, sonra hedef adı değiştirilir. Onaylı üzerine yazmada eski Database bağlantıları kapalı bp_old_* adıyla kurtarma için korunur; kontrol ve temizliği operatör yapmalıdır. WAL/PITR desteklenmez.")}</Notice>}
  </>
}
