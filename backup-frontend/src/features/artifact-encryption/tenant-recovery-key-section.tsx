import { tx, useLanguage } from '@/lib/i18n'
import { useState } from 'react'
import { cn } from '@/lib/utils'
import { DisclosureChevron, disclosureTriggerClass } from '@/components/ui/disclosure'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Fact, Facts, Section } from '@/components/ui/section'
import { ConfirmDialog } from '@/components/ui/confirm-dialog'
import { TextInput, TextArea } from '@/components/ui/text-input'
import { useTenantRecoveryKeyActions } from './hooks'
import type { TenantEncryption } from './types'
import { formatDateTime } from '@/lib/format'
import { TechnicalDetails } from '@/components/ui/technical-details'

function publicKeyError(value: string) {
  if (/PRIVATE KEY/i.test(value)) return tx('Özel anahtar kabul edilmez. Yalnızca genel anahtarı girin; özel anahtar sizde kalmalıdır.')
  if (!/^-----BEGIN PUBLIC KEY-----\s+[A-Za-z0-9+/=\s]+-----END PUBLIC KEY-----$/.test(value.trim())) return tx('Yalnızca PEM biçiminde bir genel anahtar yükleyin veya yapıştırın.')
  return ''
}

/** Overview of the keys that open this tenant's encrypted Backups, plus registration of the optional tenant recovery public key. `platformView`: the platform page already lists the wrap keys in its own table, so their ids are not repeated here. */
export function TenantKeysSection({ tenantId, tenantName, encryption, platformView = false }: { tenantId: string; tenantName: string; encryption: TenantEncryption; platformView?: boolean }) {
  useLanguage()
  const { set, remove } = useTenantRecoveryKeyActions(tenantId)
  const [pem, setPem] = useState('')
  const [saved, setSaved] = useState(false)
  const [removing, setRemoving] = useState(false)
  const [adding, setAdding] = useState(false)
  const [pemError, setPemError] = useState('')
  const { platformRecovery, tenantRecovery, wrapKey } = encryption

  function submit() {
    setSaved(false)
    const error = publicKeyError(pem)
    setPemError(error)
    if (error) { setPem(''); return }
    set.mutate(pem.trim(), { onSuccess: () => { setPem(''); setSaved(true) } })
  }

  return <>
    <Section title={platformView ? tx("Çalışma alanı yedeklerinin şifreleme koruması") : tx("Yedeklerinizin şifreleme koruması")} description={tx("Bu durum şifreli yedeklerin anahtar korumasını gösterir; tüm yedeklerin şifreli olduğu anlamına gelmez.")}>
      <div className="space-y-4">
        {platformView ? <>
          <Notice tone={wrapKey?.state === 'Retired' ? 'warning' : wrapKey ? 'success' : 'info'}>
            {wrapKey?.state === 'Retired' ? tx('Şifreli yedekler için anahtar desteği gerekiyor') : wrapKey ? tx('Çalışma alanının şifreli yedekleri korunuyor') : tx('Henüz şifreli yedek oluşturulmadı')}
          </Notice>
          <Facts>
            <Fact label={tx("Çalışma alanı kurtarma anahtarı")}>{tenantRecovery ? tx('Kurtarma anahtarı eklenmiş') : tx('Kurtarma anahtarı eklenmemiş')}</Fact>
            {platformRecovery.configured && <Fact label={tx("Platform kurtarma anahtarı")}>{tx('Platform kurtarma anahtarı hazır')}</Fact>}
          </Facts>
          {!platformRecovery.configured && <Notice tone="warning">{tx("Platform kurtarma anahtarı yapılandırılmamış: yeni şifreli yedekler yalnızca uygulamanın anahtarıyla ve varsa çalışma alanının kurtarma anahtarıyla açılabilir. Uygulamanın anahtarı kaybolursa bu dosyalar açılamayabilir.")}</Notice>}
        </> : tenantRecovery
          ? <Notice tone="success">{tx('Kurtarma anahtarınız eklenmiş.')}</Notice>
          : <Notice tone="warning" action={<Button type="button" size="sm" onClick={() => setAdding(true)}>{tx('Kurtarma anahtarı ekle')}</Button>}>{tx('Kendi kurtarma anahtarınız yok.')}</Notice>}
        <TechnicalDetails items={[
          ...(wrapKey && !platformView ? [
            { label: tx('Yedekleme anahtarı kimliği'), value: wrapKey.keyId },
            { label: tx('Yedekleme anahtarı sürümü'), value: String(wrapKey.version) },
            { label: tx('Yedekleme anahtarı durumu'), value: wrapKey.state },
          ] : []),
          ...(platformRecovery.keyId ? [
            { label: tx('Platform kurtarma anahtarı kimliği'), value: platformRecovery.keyId },
            { label: tx('Platform kurtarma anahtarı sürümü'), value: String(platformRecovery.keyVersion) },
          ] : []),
          ...(tenantRecovery ? [
            { label: platformView ? tx('Kurtarma anahtarı kimliği') : tx('Kurtarma anahtarınızın kimliği'), value: tenantRecovery.keyId },
            { label: platformView ? tx('Kurtarma anahtarı sürümü') : tx('Kurtarma anahtarınızın sürümü'), value: String(tenantRecovery.keyVersion) },
            { label: tx('Kurtarma anahtarının eklenme zamanı'), value: formatDateTime(tenantRecovery.createdAt) },
          ] : []),
        ]} />
      </div>
    </Section>

    <Section title={platformView ? tx("Çalışma alanı kurtarma anahtarı") : tx("Kendi kurtarma anahtarınız")} description={platformView ? tx("İsteğe bağlıdır. Genel anahtar yalnızca bundan sonra oluşturulan şifreli yedeklere eklenir; eski dosyalar değişmez. Özel anahtar güvenle saklanmalıdır: platform bu anahtarı görmez ve kaybolursa geri getiremez.") : tx("İsteğe bağlıdır. Genel anahtarınız yalnızca bundan sonra oluşturulan şifreli yedeklere eklenir; eski dosyalar değişmez. Özel anahtarı güvenle saklayın: platform bu anahtarı görmez ve kaybederseniz sizin için geri getiremez.")}
      actions={tenantRecovery && <Button variant="outline" onClick={() => { remove.reset(); setRemoving(true) }}>{tx("Anahtarı kaldır")}</Button>}>
      <details open={adding} className="group text-sm">
        <summary onClick={event => { event.preventDefault(); setAdding(open => !open) }} className={cn(disclosureTriggerClass, 'list-none group-open:text-text-heading [&::-webkit-details-marker]:hidden')}><DisclosureChevron className="group-open:rotate-90" />{platformView ? tx('Kurtarma anahtarını ekleyin veya değiştirin') : tx('Kendi anahtarınızı ekleyin veya değiştirin')}</summary>
      <form className="space-y-3" onSubmit={event => { event.preventDefault(); if (!set.isPending && pem.trim()) submit() }}>
        <Field label={tx('Genel anahtar dosyası')} hint={tx('Yalnızca genel anahtar dosyasını seçin; özel anahtarınızı yüklemeyin.')}>
          {control => <TextInput {...control} type="file" accept=".pem,.pub,text/plain"  disabled={set.isPending} onChange={event => {
            const file = event.target.files?.[0]
            event.target.value = ''
            if (!file) return
            setSaved(false)
            setPem('')
            setPemError('')
            const reader = new FileReader()
            reader.onload = () => {
              const value = String(reader.result ?? '')
              const error = publicKeyError(value)
              setPemError(error)
              setPem(error ? '' : value.trim())
            }
            reader.onerror = () => setPemError(tx('Dosya okunamadı. Genel anahtar dosyasını yeniden seçin.'))
            reader.readAsText(file)
          }} />}
        </Field>
        <Field label={tx("Genel anahtar (PEM)")} hint={tx("“-----BEGIN PUBLIC KEY-----” ile başlayan RSA genel anahtarı, en az 4096 bit. Özel anahtar girmeyin.")} error={pemError} required>
          {control => <TextArea {...control} className="font-mono text-sm" rows={6} required disabled={set.isPending} spellCheck={false} autoComplete="off" value={pem} onChange={event => { setSaved(false); setPemError(''); setPem(event.target.value) }} />}
        </Field>
        {set.error && <ErrorNotice error={set.error} />}
        {saved && <Notice tone="success">{platformView ? tx("Kurtarma anahtarı kaydedildi. Sonraki şifreli yedeklere eklenir.") : tx("Kurtarma anahtarınız kaydedildi. Sonraki şifreli yedeklere eklenir.")}</Notice>}
        <Button type="submit" disabled={set.isPending || !pem.trim()}>{set.isPending ? tx('Kaydediliyor…') : tenantRecovery ? tx('Anahtarı değiştir') : tx('Anahtarı kaydet')}</Button>
      </form>
      </details>
    </Section>

    <ConfirmDialog open={removing} onClose={() => setRemoving(false)} name={tenantName} title={platformView ? tx("Kurtarma anahtarı kaldırılsın mı?") : tx("Kurtarma anahtarınız kaldırılsın mı?")}
      consequence={{
        what: platformView ? tx("Sonraki şifreli yedeklere bu kurtarma anahtarı eklenmez. Bu yeni yedekler özel anahtarla kurtarılamaz.") : tx("Sonraki şifreli yedeklere bu kurtarma anahtarı eklenmez. Kendi özel anahtarınızla acil kurtarma yolunuz bu yeni yedeklerde kullanılamaz."),
        who: platformView ? tx("Yalnızca bundan sonra alınacak yedekler etkilenir; mevcut dosyalardaki kayıt değişmez ve özel anahtarla açılabilir.") : tx("Yalnızca bundan sonra alınacak yedekler etkilenir; mevcut dosyalardaki kayıt değişmez ve özel anahtarınız onları açmaya devam eder."),
        reversible: tx("Evet. Aynı genel anahtarı yeniden yükleyerek tekrar ekleyebilirsiniz."),
      }}
      confirmLabel={tx("Kaldır")} pending={remove.isPending} error={remove.error}
      onConfirm={() => remove.mutate(undefined, { onSuccess: () => setRemoving(false) })} />
  </>
}
