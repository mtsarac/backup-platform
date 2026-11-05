import { useState, type FormEvent } from 'react'
import { tx, useLanguage } from '@/lib/i18n'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { TextInput } from '@/components/ui/text-input'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { Section } from '@/components/ui/section'
import { ApiError } from '@/lib/api/client'
import { formatDateTime } from '@/lib/format'
import type { AgentSettings } from './api'
import { useUpdateAgentSettings } from './hooks'

export function AgentSettingsForm({ settings }: { settings: AgentSettings }) {
  useLanguage()
  const save = useUpdateAgentSettings()
  const [serverUrl, setServerUrl] = useState(settings.serverUrl ?? '')
  const [lifetime, setLifetime] = useState(String(settings.enrollmentTokenLifetimeMinutes))
  const { minMinutes, maxMinutes } = settings.limits
  const minutes = Number(lifetime)
  const valid = Number.isInteger(minutes) && minutes >= minMinutes && minutes <= maxMinutes
  const code = save.error instanceof ApiError ? save.error.code : undefined
  const urlError = code === 'invalid_server_url' ? tx('Geçerli bir HTTPS adresi girin: yalnızca https://, sunucu adı ve isteğe bağlı port.') : undefined
  const lifetimeError = code === 'invalid_lifetime' || (lifetime !== '' && !valid) ? tx('Süre {0} ile {1} dakika arasında bir tam sayı olmalı.', { '0': minMinutes, '1': maxMinutes }) : undefined

  function submit(event: FormEvent) {
    event.preventDefault()
    if (valid) save.mutate({ serverUrl: serverUrl.trim() || null, enrollmentTokenLifetimeMinutes: minutes })
  }

  return <Section title={tx("Kurulum ayarları")} description={tx("Tüm çalışma alanları için geçerlidir; çalışma alanı kullanıcıları bu değerleri görmez ve değiştiremez.")}>
    <form onSubmit={submit} noValidate className="space-y-4">
      <Field label={tx("Sunucu HTTPS adresi")} error={urlError} hint={tx("Yedekleme bilgisayarları bu adrese bağlanır. Yalnızca https, sunucu adı ve isteğe bağlı port yazın; yol, sorgu veya kullanıcı bilgisi eklemeyin.")}>
        {(control) => <TextInput {...control} type="url"  value={serverUrl} placeholder="https://backup.example.com" onChange={(event) => setServerUrl(event.target.value)} />}
      </Field>
      <Field label={tx("Kayıt anahtarının geçerlilik süresi (dakika)")} error={lifetimeError} hint={tx("Kullanılmamış bir kurulum komutu bu süre sonunda geçersiz olur. Kurulu bilgisayarlar etkilenmez.")}>
        {(control) => <TextInput {...control} type="number"  min={minMinutes} max={maxMinutes} step={1} value={lifetime} onChange={(event) => setLifetime(event.target.value)} />}
      </Field>
      <p className="text-sm text-text-muted">{tx('Son güncelleme')}: {formatDateTime(settings.updatedAt, tx('Henüz bildirilmedi'))}</p>
      {save.isError && !urlError && !lifetimeError && <ErrorNotice error={save.error} />}
      {save.isSuccess && <Notice tone="success">{tx("Yedekleme bilgisayarı ayarları kaydedildi.")}</Notice>}
      <Button type="submit" disabled={save.isPending || !valid}>{save.isPending ? tx('Kaydediliyor…') : tx('Kaydet')}</Button>
    </form>
  </Section>
}
