import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { DataList, type Column } from '@/components/data/data-list'
import { Button } from '@/components/ui/button'
import { Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { bareListClass } from '@/features/platform/list-empty'
import { platformPaths } from '@/features/platform/paths'
import { PlatformQueryState } from '@/features/platform/platform-query-state'
import { linkClass } from '@/components/ui/form-styles'
import { useWrapKeyActions, useWrapKeys } from './hooks'
import { TechnicalDetails } from '@/components/ui/technical-details'
import type { WrapKey } from './types'
import { formatDateTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'
import { RelativeTime } from '@/components/data/relative-time'

/** TWK lifecycle of one tenant: versions, state and how many artifacts each version still protects. No key material is ever shown. */
export function WrapKeysSection({ tenantId, tenantName }: { tenantId: string; tenantName: string }) {
  useLanguage()
  const keys = useWrapKeys(tenantId)
  const { rotate, retire } = useWrapKeyActions(tenantId)
  const [rotating, setRotating] = useState(false)
  const [retiring, setRetiring] = useState<WrapKey | null>(null)
  const columns: Column<WrapKey>[] = [
    { key: 'name', card: 'title', header: tx('Anahtar'), render: key => key.state === 'Active' ? tx('Etkin anahtar') : key.state === 'Retired' ? tx('Kullanımdan kaldırılmış anahtar') : tx('Önceki anahtar') },
    { key: 'state', card: 'status', header: tx('Durum'), render: key => <StatusPill domain="wrapKey" status={key.state} /> },
    { key: 'created', header: tx('Oluşturulma'), render: key => <RelativeTime value={key.createdAt} /> },
    { key: 'artifacts', header: tx('Korunan yedek sayısı'), render: key => <span className="tabular-nums">{key.artifactCount}</span> },
    { key: 'actions', card: 'actions', header: tx('İşlemler'), render: key => key.state === 'Retired' ? <span className="text-text-muted">—</span>
      : <Button size="sm" variant="outline" disabled={key.state === 'Active'} title={key.state === 'Active' ? tx('Etkin anahtar önce döndürülmelidir.') : undefined} onClick={() => { retire.reset(); setRetiring(key) }}>{tx("Emekli et")}</Button> },
    { key: 'technical', card: 'actions', header: tx('Teknik ayrıntılar'), wrap: true, render: key => <TechnicalDetails items={[
      { label: tx('Anahtar kimliği'), value: key.keyId },
      { label: tx('Sürüm'), value: String(key.version) },
      { label: tx('Ham durum'), value: key.state },
      { label: tx('Oluşturulma zamanı'), value: formatDateTime(key.createdAt) },
    ]} /> },
  ]
  return <Section title={tx("Şifreleme anahtarları")} description={tx("Anahtarı döndürmek yeni yedeklerde yeni bir anahtar kullanılmasını sağlar. Eski yedekler önceki anahtarla açılmaya devam eder.")} flush className="overflow-hidden"
    actions={<>
      <Link className={`${linkClass} inline-flex min-h-11 items-center text-sm`} to={`${platformPaths.breakGlass}?tenantId=${encodeURIComponent(tenantId)}`}>{tx("Acil destek erişimi istekleri")}</Link>
      <Button onClick={() => { rotate.reset(); setRotating(true) }}>{tx("Anahtarı döndür")}</Button>
    </>}>
    <PlatformQueryState isPending={keys.isPending} error={keys.error}>
      {keys.data && <div className="space-y-3">
        {keys.data.items.length === 0 && <div className="px-5 pt-4"><Notice tone="info">{tx("Bu tenant henüz şifreli yedek almadı; ilk şifreli çalıştırmada etkin bir anahtar oluşturulur.")}</Notice></div>}
        {keys.data.items.length > 0 && <DataList className={bareListClass} rows={keys.data.items} columns={columns} keyExtractor={key => String(key.version)} />}
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
    <ConfirmDialog open={Boolean(retiring)} onClose={() => setRetiring(null)} name={tenantName} title={tx("Anahtar emekli edilsin mi?")}
      consequence={{
        what: retiring && <div className="space-y-3">
          <p>{tx("{0} yedek artık uygulamada açılamaz. Bu dosyalar yalnızca kurtarma özel anahtarıyla çevrimdışı çözülebilir; bu anahtar yoksa erişim kaybolur.", { "0": retiring.artifactCount })}</p>
          <TechnicalDetails items={[
            { label: tx('Anahtar kimliği'), value: retiring.keyId },
            { label: tx('Sürüm'), value: String(retiring.version) },
          ]} />
        </div>,
        who: tx("Bu anahtarla şifrelenmiş yedekleri olan tüm işler ve geri yüklemeler etkilenir."),
        reversible: tx("Hayır. Emekli edilen anahtar yeniden etkinleştirilemez."),
      }}
      confirmLabel={tx("Emekli et")} pending={retire.isPending} error={retire.error}
      confirmDisabled={!retiring || retiring.state !== 'DecryptOnly'}
      onConfirm={() => { if (retiring?.state === 'DecryptOnly') retire.mutate(retiring.version, { onSuccess: () => setRetiring(null) }) }} />
  </Section>
}
