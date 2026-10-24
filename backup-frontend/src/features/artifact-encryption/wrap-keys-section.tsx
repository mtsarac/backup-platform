import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { DataTable, type Column } from '@/components/data/data-table'
import { Button } from '@/components/ui/button'
import { Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { bareTableClass } from '@/features/platform/list-empty'
import { platformPaths } from '@/features/platform/paths'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { linkClass } from '@/features/platform/styles'
import { useWrapKeyActions, useWrapKeys } from './hooks'
import { KeyId } from './presentation'
import type { WrapKey } from './types'
import { formatDateTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'

/** TWK lifecycle of one tenant: versions, state and how many artifacts each version still protects. No key material is ever shown. */
export function WrapKeysSection({ tenantId, tenantName }: { tenantId: string; tenantName: string }) {
  useLanguage()
  const keys = useWrapKeys(tenantId)
  const { rotate, retire } = useWrapKeyActions(tenantId)
  const [rotating, setRotating] = useState(false)
  const [retiring, setRetiring] = useState<WrapKey | null>(null)
  const columns: Column<WrapKey>[] = [
    { key: 'version', header: tx('Sürüm'), render: key => <span className="font-medium tabular-nums">v{key.version}</span> },
    { key: 'keyId', header: tx('Anahtar kimliği'), render: key => <KeyId value={key.keyId} /> },
    { key: 'state', header: tx('Durum'), render: key => <StatusPill domain="wrapKey" status={key.state} /> },
    { key: 'created', header: tx('Oluşturulma'), render: key => formatDateTime(key.createdAt) },
    { key: 'artifacts', header: tx('Bu anahtarla şifreli Backup'), render: key => <span className="tabular-nums">{key.artifactCount}</span> },
    { key: 'actions', header: tx('İşlemler'), render: key => key.state === 'Retired' ? <span className="text-text-muted">—</span>
      : <Button size="sm" variant="outline" disabled={key.state === 'Active'} title={key.state === 'Active' ? tx('Etkin anahtar önce döndürülmelidir.') : undefined} onClick={() => { retire.reset(); setRetiring(key) }}>{tx("Emekli et")}</Button> },
  ]
  return <Section title={tx("Şifreleme anahtarları")} description={tx("Tenant wrap key (TWK) sürümleri. Döndürme yeni Backup'lar için yeni anahtarı etkin yapar; eski anahtar yalnızca çözme için kalır ve eski dosyalar okunmaya devam eder.")} flush className="overflow-hidden"
    actions={<>
      <Link className={`${linkClass} text-sm`} to={`${platformPaths.breakGlass}?tenantId=${encodeURIComponent(tenantId)}`}>{tx("Break-glass istekleri")}</Link>
      <Button onClick={() => { rotate.reset(); setRotating(true) }}>{tx("Anahtarı döndür")}</Button>
    </>}>
    <PlatformQueryState isPending={keys.isPending} error={keys.error}>
      {keys.data && <div className="space-y-3">
        {keys.data.items.length === 0 && <div className="px-5 pt-4"><Notice tone="info">{tx("Bu tenant henüz şifreli yedek almadı; ilk şifreli çalıştırmada etkin bir anahtar oluşturulur.")}</Notice></div>}
        {keys.data.items.length > 0 && <DataTable className={bareTableClass} rows={keys.data.items} columns={columns} keyExtractor={key => String(key.version)} />}
      </div>}
    </PlatformQueryState>
    <ConfirmDialog open={rotating} onClose={() => setRotating(false)} title={tx("Anahtar döndürülsün mü?")}
      consequence={{
        what: tx("“{0}” için yeni bir anahtar sürümü etkin olur; şu anki anahtar yalnızca çözme için kalır.", { "0": tenantName }),
        who: tx("Bundan sonra alınacak şifreli yedekler yeni anahtarı kullanır; eski yedekler değişmez ve okunmaya devam eder."),
        reversible: tx("Hayır. Yeni anahtar sürümü silinemez; ancak eski dosyalarınız etkilenmez."),
      }}
      confirmLabel={tx("Döndür")} pending={rotate.isPending} error={rotate.error}
      onConfirm={() => rotate.mutate(undefined, { onSuccess: () => setRotating(false) })} />
    <ConfirmDialog open={Boolean(retiring)} onClose={() => setRetiring(null)} title={tx("Anahtar emekli edilsin mi?")}
      consequence={{
        what: retiring && (retiring.artifactCount > 0
          ? tx("v{0} hâlâ {1} yedeğin anahtarı. Emekli edilen anahtar hiçbir dosyayı açmaz; bu yedekler yalnızca platform kurtarma anahtarıyla çevrimdışı çözülebilir.", { "0": retiring.version, "1": retiring.artifactCount })
          : tx("v{0} ile şifrelenmiş yedek yok. Emekli edilen anahtar hiçbir dosyayı açmaz.", { "0": retiring.version })),
        who: tx("Bu anahtarla şifrelenmiş yedekleri olan tüm işler ve geri yüklemeler etkilenir."),
        reversible: tx("Hayır. Emekli edilen anahtar yeniden etkinleştirilemez."),
      }}
      confirmLabel={tx("Emekli et")} pending={retire.isPending} error={retire.error}
      onConfirm={() => { if (retiring) retire.mutate(retiring.version, { onSuccess: () => setRetiring(null) }) }} />
  </Section>
}
