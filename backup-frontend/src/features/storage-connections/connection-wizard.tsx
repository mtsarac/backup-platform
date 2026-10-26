import { tx, useLanguage } from '@/lib/i18n'
import { useEffect, useMemo, useState, type FormEvent } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { ArrowLeft, ArrowRight, ExternalLink, RefreshCw } from 'lucide-react'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { buttonVariants } from '@/components/ui/button-variants'
import { ErrorNotice, Notice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Fact, Facts, Section } from '@/components/ui/section'
import { Stepper } from '@/components/ui/stepper'
import { inputClass } from '@/components/ui/form-styles'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { useTenant } from '@/features/tenancy/use-tenant'
import { activeSecretFields, buildConfiguration, configurationComplete, editableConfigFields, initialFieldValues, providerPayload, secretsComplete } from './descriptor-values'
import type { FieldValues } from './descriptor-values'
import { openGoogleOAuthPopup } from './google-oauth'
import { useCreateTenantConnection, useGoogleDriveConnect, useTenantConnectionOps, useTenantConnections, useTenantProviders } from './hooks'
import { CapabilityChips } from './connection-parts'
import { connectionPath } from '@/lib/routes'
import { errorField, executionLocationLabel, tenantCreateState, testMessage, testModeLabel, topologyLabel, configurationSummary } from './labels'
import { ProviderIcon } from './provider-icon'
import { ProviderFields } from './provider-form'
import type { ConnectionView, ProviderDescriptor } from './types'
import { describeError } from '@/lib/api/failure-messages'

type StepKey = 'provider' | 'connection' | 'settings' | 'credentials' | 'review'
const stepLabels: Record<StepKey, string> = {
  get provider() { return tx("Sağlayıcı") },
  get connection() { return tx("Ad") },
  get settings() { return tx("Bağlantı ayarları") },
  get credentials() { return tx("Giriş bilgileri") },
  get review() { return tx("Özet ve test") },
}
const stepQuestions: Record<StepKey, () => string> = {
  provider: () => tx("Yedekler nerede saklansın?"),
  connection: () => tx("Bu depolamaya ne ad verelim?"),
  settings: () => tx("Depolamaya nasıl ulaşalım?"),
  credentials: () => tx("Depolamaya nasıl giriş yapılsın?"),
  review: () => tx("Bağlantı oluşturulsun mu?"),
}
const stepReasons: Record<StepKey, () => string> = {
  provider: () => tx("Neden soruluyor? Yedek dosyalarınızı saklayacak hizmeti seçmeniz gerekir."),
  connection: () => tx("Neden soruluyor? Bu ad, depolamayı listelerde ve yedekleme ayarlarında tanımanızı sağlar."),
  settings: () => tx("Neden soruluyor? Yedeklerin yazılacağı sunucu ve klasöre ulaşmak için bu ayarlar gerekir."),
  credentials: () => tx("Neden soruluyor? Yedekleri saklamak için depolamaya giriş izni gerekir. Girdiğiniz gizli bilgiler kayıttan sonra gösterilmez."),
  review: () => tx("Neden soruluyor? Kaydetmeden önce seçimlerinizi kontrol edin. Önce bağlantı oluşturulur, ardından ayrı bir işlemle test edilir; mevcut yedekleriniz değişmez."),
}

/** What a person needs to have at hand, in plain words, per provider. Unknown providers just show the descriptor's own fields. */
const prerequisites: Record<string, string> = {
  get sftp() { return tx("Sunucu adresi, kullanıcı adı, sunucu anahtarı parmak izi (SHA256) ve parola ya da özel anahtar.") },
  get 'google-drive'() { return tx("Yedeklerin saklanacağı Google hesabı. Hesap izni açılan Google penceresinde verilir; parola burada girilmez.") },
  get local() { return tx("Yedekleme bilgisayarının diski.") },
  get central() { return tx("Platform tarafından işletilir.") },
}

function ProviderStep({ providers, connections, value, onChange }: { providers: readonly ProviderDescriptor[]; connections: readonly ConnectionView[]; value: string; onChange: (providerType: string) => void }) {
  useLanguage()
  return <fieldset className="space-y-3">
    <legend className="sr-only">{tx("Depolama sağlayıcısı")}</legend>
    <ul className="grid gap-3 md:grid-cols-2">{providers.map((provider) => {
      const state = tenantCreateState(provider, connections)
      const selected = value === provider.providerType
      return <li key={provider.providerType}>
        <label className={`flex h-full gap-3 rounded-xl border p-4 transition-colors ${state.creatable ? 'cursor-pointer hover:bg-surface-2' : 'cursor-not-allowed opacity-60'} ${selected ? 'border-primary bg-accent ring-2 ring-primary/30' : 'border-border-strong/50 bg-surface'}`}>
          <input type="radio" name="provider" className="mt-1 size-4 accent-primary" value={provider.providerType} checked={selected} disabled={!state.creatable} onChange={() => onChange(provider.providerType)} />
          <ProviderIcon providerType={provider.providerType} />
          <span className="min-w-0 space-y-1">
            <span className="block font-medium text-text-heading">{tx(provider.displayName)}</span>
            <span className="block text-sm text-text-muted">{state.creatable ? prerequisites[provider.providerType] ?? topologyLabel(provider.topology) : `${state.reason}.`}</span>
          </span>
        </label>
      </li>
    })}</ul>
  </fieldset>
}

export function ConnectionWizard() {
  useLanguage()
  const { tenantId, canAdmin } = useTenant()
  const storage = `/tenants/${tenantId}/storage`
  if (!canAdmin) return <><PageHeader title={tx("Bağlantı ekle")} crumbs={[{ label: tx('Depolama'), to: storage }, { label: tx('Bağlantı ekle') }]} /><Notice tone="warning" title={tx("Yöneticinize danışın")} action={<Link to={storage} className={buttonVariants({ variant: 'outline', size: 'sm' })}>{tx("Depolamaya dön")}</Link>}>{tx("Bağlantı eklemek için çalışma alanı yöneticinizden yardım isteyin.")}</Notice></>
  return <ConnectionWizardForm key={tenantId} />
}

/** Creating and testing remain separate API operations. */
function ConnectionWizardForm() {
  useLanguage()
  const { tenantId } = useTenant()
  const [params] = useSearchParams()
  const providers = useTenantProviders()
  const connections = useTenantConnections()
  const ops = useTenantConnectionOps()
  const create = useCreateTenantConnection()
  const googleConnect = useGoogleDriveConnect()
  const reset = create.reset
  useEffect(() => reset, [reset])

  const creatable = useMemo(() => (providers.data?.items ?? []).filter((provider) => provider.availability === 'Available'), [providers.data])
  const [chosen, setChosen] = useState(params.get('provider') ?? '')
  const provider = creatable.find((item) => item.providerType === chosen && tenantCreateState(item, connections.data?.items ?? []).creatable)
  const [step, setStep] = useState<StepKey>(provider ? 'connection' : 'provider')
  const [name, setName] = useState('')
  const [edits, setEdits] = useState<FieldValues>({})
  const [secret, setSecret] = useState<Record<string, string>>({})
  const [created, setCreated] = useState<ConnectionView | null>(null)
  const [createError, setCreateError] = useState<unknown>(null)

  const config = useMemo<FieldValues>(() => provider ? { ...initialFieldValues(provider.configFields, {}, provider.providerType), ...edits } : {}, [provider, edits])
  const storage = `/tenants/${tenantId}/storage`
  const needsCredentials = provider !== undefined && activeSecretFields(provider.providerType, config, provider.secretFields).length > 0
  const needsSettings = provider && editableConfigFields(provider.providerType, provider.configFields).length > 0
  const steps: StepKey[] = ['provider', 'connection', ...(needsSettings ? ['settings' as const] : []), ...(needsCredentials ? ['credentials' as const] : []), 'review']
  const stepIndex = Math.max(0, steps.indexOf(step))
  const google = (created?.providerType ?? provider?.providerType) === 'google-drive'

  function choose(providerType: string) {
    setChosen(providerType)
    setEdits({})
    setSecret({})
  }

  const stepValid: Record<StepKey, boolean> = {
    provider: Boolean(provider),
    connection: Boolean(provider) && name.trim() !== '',
    settings: Boolean(provider) && configurationComplete(provider?.configFields ?? [], config),
    credentials: Boolean(provider) && secretsComplete(activeSecretFields(provider?.providerType ?? '', config, provider?.secretFields ?? []), secret),
    review: Boolean(provider) && name.trim() !== '' && configurationComplete(provider?.configFields ?? [], config) && secretsComplete(activeSecretFields(provider?.providerType ?? '', config, provider?.secretFields ?? []), secret),
  }

  function next(event?: FormEvent) {
    event?.preventDefault()
    if (stepValid[step] && stepIndex < steps.length - 1) setStep(steps[stepIndex + 1])
  }

  function submit(event: FormEvent) {
    event.preventDefault()
    if (!provider || !stepValid.review || create.isPending) return
    const input = providerPayload(provider, name, config, secret)
    // Credentials are sent once and leave the form at the moment of submit, whatever the outcome.
    setSecret({})
    setCreateError(null)
    create.mutate(input, {
      onSuccess: (connection) => {
        setCreated(connection)
        reset()
        if (connection.providerType !== 'google-drive') ops.test.mutate(connection)
      },
      onError: (error) => { setCreateError(error); reset() },
    })
  }

  function authorizeGoogle(connection: ConnectionView) {
    googleConnect.mutate({ tenantId: connection.tenantId, connectionId: connection.id }, { onSuccess: ({ authorizationUrl }) => openGoogleOAuthPopup(authorizationUrl, () => ops.test.mutate(connection)) })
  }

  const crumbs = [{ label: tx('Depolama'), to: storage }, { label: tx('Bağlantı ekle') }]

  if (created) {
    const test = ops.test.data?.test
    const testing = ops.test.isPending
    return <>
      <PageHeader title={tx("Bağlantı oluşturuldu")} description={`${created.name} · ${tx(created.providerDisplayName)}`} crumbs={crumbs} />
      <Section title={tx("Bağlantı testi")}>
        {created.hasSecret && <p className="mb-4 text-sm text-text-muted">{tx("Kimlik bilgisi kayıtlı")}</p>}
        <div className="space-y-4">
          {google && !test && !testing && <Notice tone="info" title={tx("Google hesabını yetkilendirin")}>{tx("Hesap izni verilmeden bağlantı kullanılamaz. Düğmeye bastığınızda Google penceresi açılır; izin verdikten sonra bağlantı otomatik test edilir.")}</Notice>}
          {Boolean(googleConnect.error) && <ErrorNotice title={tx("Google yetkilendirmesi başlatılamadı")} error={googleConnect.error} scope="storageConnection" />}
          {testing && <Notice tone="info" title={tx("Bağlantı test ediliyor…")}>{tx("Küçük bir test dosyası yazılıp okunur ve silinir; mevcut yedekleriniz değişmez.")}</Notice>}
          {Boolean(ops.test.error) && !testing && <ErrorNotice title={tx("Test tamamlanamadı")} error={ops.test.error} scope="storageConnection" />}
          {test?.status === 'Succeeded' && <Notice tone="success" title={tx("Bağlantı çalışıyor")}>{testMessage(test)}</Notice>}
          {test?.status === 'Failed' && <ErrorNotice title={tx("Test başarısız")} what={testMessage(test)} dataImpact={tx("Bağlantı kaydedildi ama henüz doğrulanmadı; yedekler bu depolamaya yazılamayabilir.")} action={tx("Ayarları düzeltip yeniden test edin.")} />}
          {test?.status === 'NotTestable' && <Notice tone="info" title={tx("Bu depolamayı yedekleme bilgisayarı denetler")}>{testMessage(test)}</Notice>}
          <div className="flex flex-wrap gap-2">
            {google && !testing && test?.status !== 'Succeeded' && <Button disabled={googleConnect.isPending} onClick={() => authorizeGoogle(created)}><ExternalLink aria-hidden="true" className="size-4" />{googleConnect.isPending ? tx('Yönlendiriliyor…') : tx('Google Hesabını Bağla')}</Button>}
            {!google && !testing && (test?.status === 'Failed' || Boolean(ops.test.error)) && <Button variant="outline" onClick={() => ops.test.mutate(created)}><RefreshCw aria-hidden="true" className="size-4" />{tx("Yeniden test et")}</Button>}
            {!google && !testing && (test?.status === 'Failed' || Boolean(ops.test.error)) && <Link to={`${connectionPath(tenantId, created.id)}?tab=configuration`} className={buttonVariants({ variant: 'outline' })}>{tx("Yapılandırmayı düzenle")}</Link>}
            <Link to={connectionPath(tenantId, created.id)} className={buttonVariants({ variant: test?.status === 'Succeeded' ? 'default' : 'outline' })}>{tx("Bağlantıyı görüntüle")}</Link>
            <Link to={storage} className={buttonVariants({ variant: 'ghost' })}>{tx("Listeye dön")}</Link>
          </div>
        </div>
      </Section>
    </>
  }

  const stepNames = steps.map((key) => stepLabels[key])
  return <>
    <PageHeader title={tx("Bağlantı ekle")} description={tx("Yedekleriniz için yeni bir depolama ekleyin. Adım adım ayarlayın, kontrol edin ve test edin.")} crumbs={crumbs} />
    <Stepper steps={stepNames} current={stepIndex} />
    <form onSubmit={step === 'review' ? submit : next} noValidate>
      <Section title={stepQuestions[step]()} description={stepReasons[step]()}>
        <div className="max-w-2xl space-y-5">
          <QueryState isPending={providers.isPending || connections.isPending} error={providers.error ?? connections.error}>
            {step === 'provider' && <ProviderStep providers={creatable} connections={connections.data?.items ?? []} value={chosen} onChange={choose} />}
            {step === 'connection' && provider && <>
              <p className="text-sm text-text-muted">{tx(provider.displayName)}</p>
              <label className="block space-y-1 text-sm font-medium text-text-heading">{tx("Ad")}<input className={inputClass} value={name} required aria-label={tx("Ad")} aria-invalid={errorField(createError) === 'name'} onChange={(event) => setName(event.target.value)} />
                <span className="block text-sm font-normal text-text-muted">{tx("Örneğin: Ofis yedekleri veya Arşiv depolaması.")}</span>
              </label>
            </>}
            {step === 'settings' && provider && <>
              <ProviderFields part="configuration" provider={provider} config={config} secret={secret} error={createError} onConfig={(field, value) => setEdits((current) => ({ ...current, [field]: value }))} onSecret={(field, value) => setSecret((current) => ({ ...current, [field]: value }))} />
              {google && <Notice tone="info">{tx("Hesap izni bir sonraki adımlarda, bağlantı oluşturulunca Google penceresinde istenir.")}</Notice>}
            </>}
            {step === 'credentials' && provider && <ProviderFields part="credentials" provider={provider} config={config} secret={secret} error={createError} onConfig={(field, value) => setEdits((current) => ({ ...current, [field]: value }))} onSecret={(field, value) => setSecret((current) => ({ ...current, [field]: value }))} />}
            {step === 'review' && provider && <div className="space-y-5">
              <Facts>
                <Fact label={tx("Depolama sağlayıcısı")}>{tx(provider.displayName)}</Fact>
                <Fact label={tx("Ad")}>{name.trim()}</Fact>
                <Fact label={tx("Giriş bilgileri")}>{google ? tx('Google hesabı bir sonraki adımda yetkilendirilir') : needsCredentials ? tx('Girildi (kaydedildikten sonra gösterilmez)') : tx('Gerekmiyor')}</Fact>
              </Facts>
              {google && <Notice tone="info">{tx("Bağlantıyı kaydettikten sonra Google hesabınızı açılan pencereden bağlayın. Google parolanızı burada girmeyin.")}</Notice>}
              <TechnicalDetails items={[
                ...configurationSummary({ configuration: buildConfiguration(provider.configFields, config) }, provider).filter((item) => item.value !== '—').map((item) => ({ label: tx(item.label), value: item.value })),
                { label: tx("Veri yolu"), value: `${topologyLabel(provider.topology)} · ${executionLocationLabel(provider.executionLocation)}` },
                { label: tx("Test yöntemi"), value: testModeLabel(provider.testMode) },
                { label: tx("Yetenek kodları"), value: provider.capabilities.join(', ') || '—' },
              ]}>
                <CapabilityChips capabilities={provider.capabilities} label={tx("{0} yetenekleri", { "0": tx(provider.displayName) })} />
              </TechnicalDetails>
              {Boolean(createError) && <ErrorNotice title={tx("Bağlantı oluşturulamadı")} {...describeError(createError, 'storageConnection')} {...(needsCredentials ? { action: tx('Güvenlik nedeniyle giriş bilgileri temizlendi; Giriş bilgileri adımında yeniden girin.') } : {})} />}
            </div>}
          </QueryState>
          <div className="flex flex-wrap items-center justify-between gap-3 border-t border-border pt-4">
            {stepIndex > 0 ? <Button type="button" variant="outline" disabled={create.isPending} onClick={() => setStep(steps[stepIndex - 1])}><ArrowLeft aria-hidden="true" className="size-4" />{tx("Geri")}</Button> : <Link to={storage} className={buttonVariants({ variant: 'ghost' })}>{tx("Vazgeç")}</Link>}
            {step === 'review'
              ? <Button type="submit" disabled={!stepValid.review || create.isPending}>{create.isPending ? tx('Oluşturuluyor…') : tx('Bağlantıyı oluştur ve test et')}</Button>
              : <Button type="submit" disabled={!stepValid[step]}>{tx("İleri")}<ArrowRight aria-hidden="true" className="size-4" /></Button>}
          </div>
        </div>
      </Section>
    </form>
    <p className="sr-only" aria-live="polite">{stepLabels[step]}{' '}{tx("adımı")}</p>
  </>
}

