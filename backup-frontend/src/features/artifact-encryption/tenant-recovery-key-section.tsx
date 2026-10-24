import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Fact, Facts, Section } from '@/components/ui/section'
import { ConfirmDialog } from '@/features/agents/confirm-dialog'
import { inputClass } from '@/components/ui/form-styles'
import { errorMessage } from '@/lib/api/failure-messages'
import { useTenantRecoveryKeyActions } from './hooks'
import { KeyId } from './presentation'
import type { TenantEncryption } from './types'
import { formatDateTime } from '@/lib/format'
import { StatusPill } from '@/components/data/status-pill'

/** Overview of the keys that open this tenant's encrypted Backups, plus registration of the optional tenant recovery public key. */
export function TenantKeysSection({ tenantId, encryption }: { tenantId: string; encryption: TenantEncryption }) {
  useLanguage()
  const { set, remove } = useTenantRecoveryKeyActions(tenantId)
  const [pem, setPem] = useState('')
  const [saved, setSaved] = useState(false)
  const [removing, setRemoving] = useState(false)
  const [pemError, setPemError] = useState('')
  const { platformRecovery, tenantRecovery, wrapKey } = encryption

  function submit() {
    setSaved(false)
    if (/PRIVATE KEY/.test(pem)) { setPem(''); setPemError(tx('Özel anahtar kabul edilmez. Yalnızca genel anahtarı girin; özel anahtar sizde kalmalıdır.')); return }
    setPemError('')
    set.mutate(pem.trim(), { onSuccess: () => { setPem(''); setSaved(true) } })
  }

  return <>
    <Section title={tx("Anahtarlar")} description={tx("Şifreli Backup'ları açabilen anahtarlar. Yalnızca kimlikler gösterilir; anahtar içeriği hiçbir zaman gösterilmez.")}>
      <Facts>
        <Fact label={tx("Tenant anahtarı (backend)")}>{wrapKey ? <>v{wrapKey.version} · <KeyId value={wrapKey.keyId} />{' '}<StatusPill domain="wrapKey" status={wrapKey.state} /></> : tx('Henüz oluşturulmadı; ilk şifreli yedekte oluşur.')}</Fact>
        <Fact label={tx("Platform kurtarma anahtarı")}>{platformRecovery.configured && platformRecovery.keyId ? <>v{platformRecovery.keyVersion} · <KeyId value={platformRecovery.keyId} /></> : tx('Yapılandırılmamış')}</Fact>
        <Fact label={tx("Tenant kurtarma anahtarı")}>{tenantRecovery ? <>v{tenantRecovery.keyVersion} · <KeyId value={tenantRecovery.keyId} /> · {formatDateTime(tenantRecovery.createdAt)}</> : tx('Tanımlı değil')}</Fact>
      </Facts>
      {!platformRecovery.configured && <div className="pt-4"><Notice tone="warning">{tx("Platform kurtarma anahtarı yapılandırılmamış: yeni şifreli Backup'lar yalnızca tenant anahtarıyla (ve tanımlıysa tenant kurtarma anahtarıyla) açılabilir. Backend kaybolursa bu dosyalar çözülemeyebilir.")}</Notice></div>}
    </Section>

    <Section title={tx("Tenant kurtarma anahtarı")} description={tx("İsteğe bağlı. Genel anahtarınız bundan sonra oluşturulan şifreli Backup'lara üçüncü bir alıcı olarak eklenir; eski dosyalar değişmez. Özel anahtar sizde kalır, platform onu hiçbir zaman görmez; kaybederseniz bu alıcı dosyaları açamaz.")}
      actions={tenantRecovery && <Button variant="outline" onClick={() => { remove.reset(); setRemoving(true) }}>{tx("Anahtarı kaldır")}</Button>}>
      <form className="space-y-3" onSubmit={event => { event.preventDefault(); if (!set.isPending && pem.trim()) submit() }}>
        <Field label={tx("Genel anahtar (PEM)")} hint={tx("“-----BEGIN PUBLIC KEY-----” ile başlayan RSA genel anahtarı, en az 4096 bit. Özel anahtar girmeyin.")} error={pemError}>
          {control => <textarea {...control} className={`${inputClass} font-mono text-sm`} rows={6} required spellCheck={false} autoComplete="off" value={pem} onChange={event => { setSaved(false); setPemError(''); setPem(event.target.value) }} />}
        </Field>
        {set.error && <ErrorNotice error={set.error} />}
        {saved && <Notice tone="success">{tx("Tenant kurtarma anahtarı kaydedildi. Sonraki şifreli Backup'lara eklenir.")}</Notice>}
        <Button type="submit" disabled={set.isPending || !pem.trim()}>{set.isPending ? tx('Kaydediliyor…') : tenantRecovery ? tx('Anahtarı değiştir') : tx('Anahtarı kaydet')}</Button>
      </form>
    </Section>

    <ConfirmDialog open={removing} onClose={() => setRemoving(false)} title={tx("Tenant kurtarma anahtarı kaldırılsın mı?")}
      description={tx("Sonraki şifreli Backup'lara bu alıcı eklenmez. Mevcut dosyalardaki alıcı kaydı değişmez; özel anahtarınız onları açmaya devam eder.")}
      confirmLabel={tx("Kaldır")} pending={remove.isPending} error={remove.error ? errorMessage(remove.error) : undefined}
      onConfirm={() => remove.mutate(undefined, { onSuccess: () => setRemoving(false) })} />
  </>
}
