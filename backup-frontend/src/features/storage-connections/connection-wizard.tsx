import { tx, useLanguage } from '@/lib/i18n'
import { TextInput } from '@/components/ui/text-input'
import { RequiredText } from '@/components/ui/field'
import { useEffect, useMemo, useRef, useState, type FormEvent } from 'react'
import { Link, useLocation, useSearchParams } from 'react-router-dom'
import { ArrowLeft, ArrowRight } from 'lucide-react'
import { QueryState } from '@/components/data/query-state'
import { Button } from '@/components/ui/button'
import { buttonVariants } from '@/components/ui/button-variants'
import { ErrorNotice } from '@/components/ui/notice'
import { PageHeader } from '@/components/ui/page-header'
import { Fact, Facts, Section } from '@/components/ui/section'
import { Stepper } from '@/components/ui/stepper'
import { WizardFooter } from '@/components/ui/wizard-footer'
import { TechnicalDetails } from '@/components/ui/technical-details'
import { useTenant } from '@/features/tenancy/use-tenant'
import { activeSecretFields, buildConfiguration, buildSecretValues, configurationComplete, hasSecretInput, initialFieldValues, providerPayload, secretsComplete } from './descriptor-values'
import type { FieldValues } from './descriptor-values'
import { useGoogleOAuth } from './use-google-oauth'
import { useCreateTenantConnection, useTenantConnectionOps, useTenantConnections, useTenantProviders } from './hooks'
import { CapabilityChips } from './connection-parts'
import { errorField, executionLocationLabel, tenantCreateState, testModeLabel, topologyLabel, configurationSummary } from './labels'
import { ProviderIcon } from './provider-icon'
import { ProviderFields } from './provider-form'
import type { ConnectionView, ProviderDescriptor } from './types'
import { describeError } from '@/lib/api/failure-messages'
import { RadioField } from '@/components/ui/radio-field'
import { autoName, mainProviders, otherProviders, providerTitle, wizardProvider, wizardReturn } from './wizard-providers'
import { WizardResult } from './wizard-result'

type StepKey = 'provider' | 'details' | 'review'
const stepLabels = (google: boolean): string[] => [tx("Sağlayıcı"), google ? tx("Google ile bağlan") : tx("Bilgiler"), google ? tx("Özet") : tx("Özet ve test")]
const stepKeys: StepKey[] = ['provider', 'details', 'review']

/** What a person needs to have at hand, in plain words, per provider. Unknown providers just show the descriptor's own fields. */
const prerequisites: Record<string, string> = {
  get sftp() { return tx("Sunucu adresi, kullanıcı adı, sunucu anahtarı parmak izi (SHA256) ve parola ya da özel anahtar.") },
  get 'google-drive'() { return tx("Yedeklerin saklanacağı Google hesabı. Hesap izni açılan Google penceresinde verilir; parola burada girilmez.") },
  get local() { return tx("Yedekleme bilgisayarının diski.") },
  get central() { return tx("Platform tarafından işletilir.") },
  get s3() { return tx("Uç nokta, bucket, bölge, erişim anahtarı ve gizli anahtar.") },
  get smb() { return tx("NAS adı, paylaşım adı, kullanıcı ve parola.") },
}

function ProviderCard({ provider, connections, value, onChange }: { provider: ProviderDescriptor; connections: readonly ConnectionView[]; value: string; onChange: (providerType: string) => void }) {
  const state = tenantCreateState(provider, connections)
  const selected = value === provider.providerType
  return <li>
    <RadioField className={`h-full items-start gap-3 rounded-lg border p-4 transition-colors ${state.creatable ? 'cursor-pointer hover:bg-surface-2' : 'cursor-not-allowed opacity-60'} ${selected ? 'border-primary bg-accent ring-2 ring-primary/30' : 'border-border bg-surface'}`} name="provider" value={provider.providerType} checked={selected} disabled={!state.creatable} onChange={() => onChange(provider.providerType)} label={<>
      <ProviderIcon providerType={provider.providerType} />
      <span className="min-w-0 space-y-1">
        <span className="block font-medium text-text-heading">{providerTitle(provider)}</span>
        <span className="block text-sm text-text-muted">{state.creatable ? prerequisites[provider.providerType] ?? topologyLabel(provider.topology) : `${state.reason}.`}</span>
      </span>
    </>} />
  </li>
}

function ProviderStep({ providers, connections, value, onChange }: { providers: readonly ProviderDescriptor[]; connections: readonly ConnectionView[]; value: string; onChange: (providerType: string) => void }) {
  useLanguage()
  const main = mainProviders.flatMap((type) => providers.filter((provider) => provider.providerType === type))
  const other = [...otherProviders, ...providers.map((provider) => provider.providerType).filter((type) => !mainProviders.includes(type) && !otherProviders.includes(type) && type !== 'central'), 'central']
    .flatMap((type) => providers.filter((provider) => provider.providerType === type))
  const card = (provider: ProviderDescriptor) => <ProviderCard key={provider.providerType} provider={provider} connections={connections} value={value} onChange={onChange} />
  return <fieldset className="space-y-4">
    <legend className="sr-only">{tx("Depolama sağlayıcısı")}</legend>
    <ul className="grid gap-3 md:grid-cols-2">{main.map(card)}</ul>
    {other.length > 0 && <div className="space-y-3">
      <h3 className="text-sm font-semibold text-text-heading">{tx("Diğer depolama")}</h3>
      <ul className="grid gap-3 md:grid-cols-2">{other.map(card)}</ul>
    </div>}
  </fieldset>
}

/** The generated name with a "Adı değiştir" link; the input also opens when the server rejected the name. */
function NameRow({ name, editing, invalid, onEdit, onChange }: { name: string; editing: boolean; invalid: boolean; onEdit: () => void; onChange: (name: string) => void }) {
  useLanguage()
  return editing || invalid
    ? <label className="block space-y-1 text-sm font-medium text-text-heading"><RequiredText>{tx("Ad")}</RequiredText><TextInput className="mt-1" value={name} required aria-label={tx("Ad")} aria-invalid={invalid} onChange={(event) => onChange(event.target.value)} /></label>
    : <div className="flex flex-wrap items-center gap-x-3 text-body text-text">{name}<Button type="button" variant="ghost" size="sm" onClick={onEdit}>{tx("Adı değiştir")}</Button></div>
}

export function ConnectionWizard() {
  useLanguage()
  const { tenantId } = useTenant()
  return <ConnectionWizardForm key={tenantId} />
}

/** Creating and testing remain separate API operations: the connection is saved first, then tested. */
function ConnectionWizardForm() {
  useLanguage()
  const { tenantId } = useTenant()
  const [params] = useSearchParams()
  const back = wizardReturn(useLocation().state)
  const providers = useTenantProviders()
  const connections = useTenantConnections()
  const ops = useTenantConnectionOps()
  const create = useCreateTenantConnection()
  const { start: authorizeGoogle, connect: googleConnect, outcome: googleOutcome } = useGoogleOAuth(ops)
  const reset = create.reset
  useEffect(() => reset, [reset])

  const creatable = useMemo(() => (providers.data?.items ?? []).filter((provider) => provider.availability === 'Available'), [providers.data])
  const [chosen, setChosen] = useState(params.get('provider') ?? '')
  const descriptor = creatable.find((item) => item.providerType === chosen && tenantCreateState(item, connections.data?.items ?? []).creatable)
  const provider = useMemo(() => descriptor && wizardProvider(descriptor), [descriptor])
  // A ?provider= deep link skips the picker once the provider list has loaded; until the person navigates, the step follows the data.
  const [pickedStep, setStep] = useState<StepKey | null>(null)
  const step: StepKey = pickedStep ?? (provider && params.has('provider') ? 'details' : 'provider')
  const [customName, setCustomName] = useState<string | null>(null)
  const [renaming, setRenaming] = useState(false)
  const [edits, setEdits] = useState<FieldValues>({})
  const [secret, setSecret] = useState<Record<string, string>>({})
  const [created, setCreated] = useState<ConnectionView | null>(null)
  const [createError, setCreateError] = useState<unknown>(null)
  const [saving, setSaving] = useState(false)

  const config = useMemo<FieldValues>(() => provider ? { ...initialFieldValues(provider.configFields, {}, provider.providerType), ...edits } : {}, [provider, edits])
  const storage = `/tenants/${tenantId}/storage`
  const google = provider?.providerType === 'google-drive'
  const name = customName ?? (provider ? autoName(provider, config) : '')
  const needsCredentials = provider !== undefined && activeSecretFields(provider.providerType, config, provider.secretFields).length > 0
  const stepIndex = stepKeys.indexOf(step)
  const leave = back ? { to: back.returnTo, state: { returnState: back.returnState } } : { to: storage, state: undefined }

  // Once the Google account is connected the generated name gets its address ("Google Drive (ofis@firma.com)"), unless the person chose a name.
  const renamed = useRef(false)
  useEffect(() => {
    if (googleOutcome.status !== 'connected' || customName !== null || renamed.current || !created || !provider) return
    const email = googleOutcome.account.configuration?.accountEmail
    renamed.current = true
    if (email) ops.update.mutate({ connection: created, input: { name: autoName(provider, config, email) } })
  }, [googleOutcome, customName, created, provider, config, ops.update])

  function choose(providerType: string) {
    setChosen(providerType)
    setEdits({})
    setSecret({})
  }

  const credentialsReady = provider !== undefined && secretsComplete(activeSecretFields(provider.providerType, config, provider.secretFields), secret)
  const detailsReady = Boolean(provider) && name.trim() !== '' && configurationComplete(provider?.configFields ?? [], config) && credentialsReady
  const stepValid: Record<StepKey, boolean> = { provider: Boolean(provider), details: detailsReady, review: detailsReady }

  async function saveNew() {
    if (!descriptor || !provider || saving) return
    const input = providerPayload(descriptor, name, config, secret)
    // Credentials are sent once and leave the form at the moment of submit, whatever the outcome.
    setSecret({})
    setCreateError(null)
    setSaving(true)
    try {
      const connection = await create.mutateAsync(input)
      setCreated(connection)
      setStep('review')
      if (google) authorizeGoogle(connection)
      else ops.test.mutate(connection)
    } catch (error) {
      setCreateError(error)
    } finally {
      reset()
      setSaving(false)
    }
  }

  /** "Bilgileri düzelt": the saved connection is corrected in place, then tested again; no second connection is made. */
  async function saveFix() {
    if (!descriptor || !provider || !created || saving) return
    const values = buildSecretValues(activeSecretFields(provider.providerType, config, provider.secretFields), secret)
    setSecret({})
    setCreateError(null)
    setSaving(true)
    try {
      let connection = await ops.update.mutateAsync({ connection: created, input: { name: name.trim(), configuration: buildConfiguration(descriptor.configFields, config) } })
      if (hasSecretInput(values)) connection = await ops.setSecret.mutateAsync({ connection, values })
      setCreated(connection)
      setStep('review')
      ops.test.mutate(connection)
    } catch (error) {
      setCreateError(error)
    } finally {
      setSaving(false)
    }
  }

  function submit(event: FormEvent) {
    event.preventDefault()
    if (!stepValid[step] || saving) return
    if (step === 'provider') setStep('details')
    else if (step === 'details') {
      if (created) void saveFix()
      else if (google) void saveNew()
      else setStep('review')
    } else void saveNew()
  }

  const crumbs = [{ label: tx('Depolama'), to: storage }, { label: tx('Bağlantı ekle') }]
  const showResult = step === 'review' && created !== null
  const title = step === 'provider' ? tx("Yedekler nerede saklansın?") : step === 'details' ? (google ? tx("Google hesabınızı bağlayın") : tx("Depolamaya nasıl ulaşalım?")) : showResult ? tx("Bağlantı testi") : tx("Bağlantı oluşturulsun mu?")
  const nameRow = <NameRow name={name} editing={renaming} invalid={errorField(createError) === 'name'} onEdit={() => { setCustomName(name); setRenaming(true) }} onChange={setCustomName} />
  const failure = Boolean(createError) && <ErrorNotice title={tx("Bağlantı oluşturulamadı")} {...describeError(createError, 'storageConnection')} {...(needsCredentials ? { action: tx('Güvenlik nedeniyle giriş bilgileri temizlendi; Bilgiler adımında yeniden girin.') } : {})} />

  return <>
    <PageHeader title={tx("Bağlantı ekle")} description={created ? `${created.name} · ${tx(created.providerDisplayName)}` : undefined} crumbs={crumbs} />
    <Stepper steps={stepLabels(Boolean(google))} current={stepIndex} />
    <form onSubmit={submit} noValidate>
      <Section title={title}>
        <div className="max-w-2xl space-y-5">
          <QueryState isPending={providers.isPending || connections.isPending} error={providers.error ?? connections.error}>
            {step === 'provider' && <ProviderStep providers={creatable} connections={connections.data?.items ?? []} value={chosen} onChange={choose} />}
            {step === 'details' && provider && (google
              ? <div className="space-y-4">
                <p className="text-sm text-text-muted">{tx("Yedekler Google hesabınızdaki bir klasörde saklanır. Düğmeye bastığınızda bağlantı kaydedilir ve Google penceresi açılır; izin verilince bağlantı otomatik test edilir. Google parolanızı burada girmeyin.")}</p>
                {!created && nameRow}
                {failure}
              </div>
              : <>
                <ProviderFields provider={provider} config={config} secret={secret} error={createError} onConfig={(field, value) => setEdits((current) => ({ ...current, [field]: value }))} onSecret={(field, value) => setSecret((current) => ({ ...current, [field]: value }))} />
                {failure}
              </>)}
            {step === 'review' && provider && !created && <div className="space-y-5">
              <Facts>
                <Fact label={tx("Depolama sağlayıcısı")}>{providerTitle(provider)}</Fact>
                <Fact label={tx("Ad")}>{nameRow}</Fact>
                <Fact label={tx("Giriş bilgileri")}>{needsCredentials ? tx('Girildi (kaydedildikten sonra gösterilmez)') : tx('Gerekmiyor')}</Fact>
              </Facts>
              <TechnicalDetails items={[
                ...configurationSummary({ configuration: buildConfiguration(provider.configFields, config) }, provider).filter((item) => item.value !== '—').map((item) => ({ label: tx(item.label), value: item.value })),
                { label: tx("Veri yolu"), value: `${topologyLabel(provider.topology)} · ${executionLocationLabel(provider.executionLocation)}` },
                { label: tx("Test yöntemi"), value: testModeLabel(provider.testMode) },
              ]}>
                <CapabilityChips capabilities={provider.capabilities} label={tx("{0} yetenekleri", { "0": providerTitle(provider) })} />
              </TechnicalDetails>
              {failure}
            </div>}
            {showResult && <WizardResult created={created} google={Boolean(google)} ops={ops} googleOutcome={googleOutcome} googleError={googleConnect.error} googlePending={googleConnect.isPending}
              onGoogle={() => authorizeGoogle(created)} onFix={() => setStep('details')} storage={storage} back={back} />}
          </QueryState>
          {!showResult && <WizardFooter className="border-t border-border pt-4"
            back={stepIndex > 0 && <Button type="button" variant="outline" disabled={saving} onClick={() => setStep(created && step === 'details' ? 'review' : stepKeys[stepIndex - 1])}><ArrowLeft aria-hidden="true" className="size-4" />{tx("Geri")}</Button>}
            cancel={<Link to={leave.to} state={leave.state} className={buttonVariants({ variant: 'ghost' })}>{tx("Vazgeç")}</Link>}>
            {step === 'review'
              ? <Button type="submit" disabled={!stepValid.review || saving}>{saving ? tx('Oluşturuluyor…') : tx('Bağlantıyı oluştur ve test et')}</Button>
              : step === 'details' && (google || created)
                ? <Button type="submit" disabled={!stepValid.details || saving}>{saving ? tx('Kaydediliyor…') : google ? tx('Kaydet ve Google ile bağlan') : tx('Kaydet ve yeniden test et')}</Button>
                : <Button type="submit" disabled={!stepValid[step]}>{tx("İleri")}<ArrowRight aria-hidden="true" className="size-4" /></Button>}
          </WizardFooter>}
        </div>
      </Section>
    </form>
    <p className="sr-only" aria-live="polite">{stepLabels(Boolean(google))[stepIndex]}{' '}{tx("adımı")}</p>
  </>
}
