import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ApiError } from '@/lib/api/client'
import { changeLanguage } from '@/lib/i18n/runtime'
import * as api from '../api'
import { paged, renderPlatform } from '../test-utils'
import { legacyPlan, makePlan, makeSubscription } from './test-fixtures'
import { SubscriptionSection } from './subscription-section'
import type { Subscription } from './types'

vi.mock('../api')

const five = makePlan()
const fifty = makePlan({ planId: 'plan-50', name: 'Kurumsal 50', storageLimitBytes: 50_000_000_000 })
const active = makeSubscription()

function mount(path = '/platform/tenants/tenant-a') {
  return renderPlatform(<SubscriptionSection tenantId="tenant-a" tenantName="Acme A.Ş." />, { path, route: '/platform/tenants/:tenantId' })
}

function serve(current: Subscription | null, history: Subscription[] = current ? [current] : []) {
  vi.mocked(api.getTenantSubscription).mockResolvedValue({ tenantId: 'tenant-a', subscription: current })
  vi.mocked(api.getSubscriptionHistory).mockResolvedValue(paged(history, { pageSize: 10 }))
}

const section = async () => within(await screen.findByRole('region', { name: 'Plan ve kota' }))
/** Buttons that are always visible in the section (the primary action), not the ones tucked under "Diğer işlemler" or in details. */
async function primaryActions() {
  const region = await section()
  await waitFor(() => expect(region.queryByText(/Yükleniyor/)).not.toBeInTheDocument())
  return region.queryAllByRole('button').filter((button) => !button.closest('details')).map((button) => button.textContent)
}
async function otherActions() {
  const region = await section()
  const disclosure = region.queryByText('Diğer işlemler')
  if (!disclosure) return []
  await userEvent.click(disclosure)
  return within(disclosure.closest('details')!).getAllByRole('button').map((button) => button.textContent)
}
async function openOthers() { await userEvent.click((await section()).getByText('Diğer işlemler')) }
const dialogByName = async () => screen.findByRole('dialog')
async function pickPlan(dialog: HTMLElement, name: string) {
  const picker = within(dialog).getByRole('combobox', { name: 'Plan' })
  fireEvent.change(picker, { target: { value: name } })
  fireEvent.click(await within(dialog).findByRole('option', { name: new RegExp(name) }))
  return picker
}

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getPlans).mockResolvedValue(paged([five, fifty, legacyPlan]))
  serve(active)
})

describe('subscription section states', () => {
  it('shows loading, an error and the authorization state', async () => {
    vi.mocked(api.getTenantSubscription).mockReturnValue(new Promise(() => undefined))
    const first = mount()
    expect(await screen.findByRole('status')).toHaveTextContent('Yükleniyor')
    first.unmount()
    vi.mocked(api.getTenantSubscription).mockRejectedValue(new ApiError(500))
    const second = mount()
    expect(await screen.findByRole('alert')).toHaveTextContent('Sunucu isteği tamamlayamadı')
    second.unmount()
    vi.mocked(api.getTenantSubscription).mockRejectedValue(new ApiError(403))
    mount()
    expect(await screen.findByRole('alert')).toHaveTextContent('Bu alan için yetkiniz yok')
  })

  it('shows an explicit empty state for a workspace without subscription, with only the assign action', async () => {
    serve(null)
    mount()
    expect(await screen.findByText('Bu çalışma alanı için henüz bir plan atanmadı.')).toBeInTheDocument()
    expect(await primaryActions()).toEqual(['Plan ata'])
    expect(await otherActions()).toEqual([])
    expect(api.getSubscriptionHistory).not.toHaveBeenCalled()
  })
})

describe('subscription summary', () => {
  it('shows plan, status and the friendly effective limit; exact values stay collapsed and copyable', async () => {
    mount()
    const region = await section()
    expect(await region.findByText('Etkin')).toBeVisible()
    expect(region.getAllByText('5 GB').length).toBeGreaterThan(0)
    expect(region.getByText('Kaynak: Plan')).toBeVisible()
    region.getAllByText(/5\.000\.000\.000 bayt/).forEach((value) => expect(value).not.toBeVisible())
    const details = region.getAllByText('Teknik ayrıntılar')[0].closest('details')!
    expect(details).not.toHaveAttribute('open')
    await userEvent.click(within(details).getByText('Teknik ayrıntılar'))
    expect(within(details).getAllByText('5.000.000.000 bayt').length).toBeGreaterThan(0)
    expect(within(details).getByText('Platform Operatörü')).toBeVisible()
    expect(within(details).getByText('sub-1')).toBeVisible()
    expect(within(details).getAllByRole('button', { name: /^Kopyala:/ }).length).toBeGreaterThan(3)
  })

  it('reports an override as the limit source', async () => {
    serve(makeSubscription({ storageLimitOverride: { kind: 'limited', bytes: 2_500_000_000 }, effectiveStorageLimit: { unlimited: false, bytes: 2_500_000_000, source: 'override' } }))
    mount()
    const region = await section()
    expect(await region.findByText(/Kaynak: (?:Override|Özel limit)/)).toBeVisible()
    expect(region.getAllByText('2,5 GB').length).toBeGreaterThan(0)
  })

  it('shows unlimited and system-assigned migration subscriptions', async () => {
    serve(makeSubscription({ planName: 'Legacy Unlimited', planStorageLimitBytes: null, effectiveStorageLimit: { unlimited: true, bytes: null, source: 'plan' }, assignedByUserId: null, assignedByDisplayName: null }))
    mount()
    const region = await section()
    expect((await region.findAllByText(/(?:Sınırsız|Limitsiz)/)).length).toBeGreaterThan(0)
    await userEvent.click(region.getAllByText('Teknik ayrıntılar')[0])
    expect(region.getByText('Sistem (geçiş)')).toBeVisible()
  })

  it.each([
    ['Active', ['Planı değiştir'], ['Askıya al', 'Özel limit']],
    ['Suspended', ['Yeniden etkinleştir'], ['Planı değiştir', 'Özel limit']],
    ['Expired', ['Plan ata'], []],
  ] as const)('%s offers exactly the valid actions, one primary and the rest under "Diğer işlemler"', async (status, primary, others) => {
    serve(makeSubscription({ status }))
    mount()
    await screen.findByText('Abonelik geçmişi')
    expect(await primaryActions()).toEqual(primary)
    expect(await otherActions()).toEqual(others)
  })

  it('explains that an expired subscription only allows a new plan', async () => {
    serve(makeSubscription({ status: 'Expired' }))
    mount()
    expect(await screen.findByText('Abonelik süresi doldu. Yeni bir plan atayabilirsiniz.')).toBeVisible()
  })
})

describe('subscription history', () => {
  it('lists history as cards newest first with the rest under details, and paginates through the URL', async () => {
    const superseded = makeSubscription({ subscriptionId: 'sub-0', planId: 'plan-1', planName: '1 GB', planStorageLimitBytes: 1_000_000_000, status: 'Superseded', endsAt: '2026-10-03T10:00:00Z', effectiveStorageLimit: { unlimited: false, bytes: 1_000_000_000, source: 'plan' } })
    vi.mocked(api.getTenantSubscription).mockResolvedValue({ tenantId: 'tenant-a', subscription: active })
    vi.mocked(api.getSubscriptionHistory).mockResolvedValue(paged([active, superseded], { totalCount: 22, pageSize: 10 }))
    mount('/platform/tenants/tenant-a?subPage=2')
    const list = await screen.findByRole('list', { name: 'Abonelik geçmişi' })
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    const cards = within(list).getAllByRole('listitem')
    expect(cards).toHaveLength(2)
    expect(within(cards[0]).getByText('Etkin')).toBeVisible()
    expect(within(cards[1]).getByText('Değiştirildi')).toBeVisible()
    expect(within(cards[1]).getByRole('heading', { name: '1 GB' })).toBeVisible()
    const details = within(cards[1]).getByText('Teknik ayrıntılar').closest('details')!
    expect(details).not.toHaveAttribute('open')
    await userEvent.click(within(details).getByText('Teknik ayrıntılar'))
    expect(within(details).getByText('1.000.000.000 bayt')).toBeVisible()
    expect(within(details).getByText('sub-0')).toBeVisible()
    expect(api.getSubscriptionHistory).toHaveBeenCalledWith('tenant-a', { page: 2, pageSize: 10 }, expect.any(AbortSignal))
    expect(screen.getByText('22 kayıt · Sayfa 2 / 3')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Sonraki' }))
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('subPage=3'))
  })
})

describe('assign and change plan', () => {
  it('assigns a plan by searching its name; hides system plans until asked', async () => {
    serve(null)
    mount()
    await userEvent.click(await screen.findByRole('button', { name: 'Plan ata' }))
    const dialog = await dialogByName()
    const picker = within(dialog).getByRole('combobox', { name: 'Plan' })
    fireEvent.change(picker, { target: { value: 'Kurum' } })
    await waitFor(() => expect(api.getPlans).toHaveBeenLastCalledWith({ enabled: 'true', search: 'Kurum', page: 1, pageSize: 100 }, expect.any(AbortSignal)))
    expect(await within(dialog).findByRole('option', { name: /Kurumsal 50/ })).toHaveTextContent('50 GB')
    expect(within(dialog).queryByRole('option', { name: /Legacy Unlimited/ })).not.toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Sistem planlarını göster' }))
    fireEvent.click(picker)
    expect(await within(dialog).findByRole('option', { name: /Legacy Unlimited/ })).toBeInTheDocument()
    fireEvent.click(within(dialog).getByRole('option', { name: /Kurumsal 50/ }))
    expect(picker).toHaveValue('Kurumsal 50')
    vi.mocked(api.assignSubscriptionPlan).mockResolvedValue({ outcome: 'assigned', subscription: makeSubscription({ planId: 'plan-50', planName: 'Kurumsal 50' }), previousSubscriptionId: null })
    serve(makeSubscription({ planId: 'plan-50', planName: 'Kurumsal 50', planStorageLimitBytes: 50_000_000_000, effectiveStorageLimit: { unlimited: false, bytes: 50_000_000_000, source: 'plan' } }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Plan ata' }))
    await waitFor(() => expect(api.assignSubscriptionPlan).toHaveBeenCalledWith('tenant-a', { planId: 'plan-50' }))
    const region = await section()
    expect(await region.findByText('50 GB')).toBeVisible()
    expect(region.getAllByText('Kurumsal 50').length).toBeGreaterThan(0)
  })

  it('blocks submitting until a plan is picked', async () => {
    serve(null)
    mount()
    await userEvent.click(await screen.findByRole('button', { name: 'Plan ata' }))
    const dialog = await dialogByName()
    expect(within(dialog).getByRole('button', { name: 'Plan ata' })).toBeDisabled()
  })

  it('translates the plan picker placeholder', async () => {
    changeLanguage('en')
    serve(null)
    mount()
    await userEvent.click(await screen.findByRole('button', { name: 'Assign plan' }))
    expect(within(await dialogByName()).getByPlaceholderText('Select a plan')).toBeInTheDocument()
  })

  it('sends an exact-byte override and an ISO end date', async () => {
    serve(null)
    vi.mocked(api.assignSubscriptionPlan).mockResolvedValue({ outcome: 'assigned', subscription: active, previousSubscriptionId: null })
    mount()
    await userEvent.click(await screen.findByRole('button', { name: 'Plan ata' }))
    const dialog = await dialogByName()
    await pickPlan(dialog, '5 GB')
    await userEvent.selectOptions(within(dialog).getByRole('combobox', { name: /(?:override|özel depolama limiti)/i }), 'limited')
    await userEvent.type(within(dialog).getByLabelText(/(?:Override|Özel limit) boyutu/), '1,5')
    fireEvent.change(within(dialog).getByLabelText(/Bitiş tarihi/), { target: { value: '2030-01-02T10:30' } })
    await userEvent.click(within(dialog).getByRole('button', { name: 'Plan ata' }))
    await waitFor(() => expect(api.assignSubscriptionPlan).toHaveBeenCalledWith('tenant-a', {
      planId: 'plan-5', storageLimitOverride: { kind: 'limited', bytes: 1_500_000_000 }, endsAt: new Date('2030-01-02T10:30').toISOString(),
    }))
  })

  it('sends an unlimited override and validates the limited override locally', async () => {
    serve(null)
    vi.mocked(api.assignSubscriptionPlan).mockResolvedValue({ outcome: 'assigned', subscription: active, previousSubscriptionId: null })
    mount()
    await userEvent.click(await screen.findByRole('button', { name: 'Plan ata' }))
    const dialog = await dialogByName()
    await pickPlan(dialog, '5 GB')
    const override = within(dialog).getByRole('combobox', { name: /(?:override|özel depolama limiti)/i })
    await userEvent.selectOptions(override, 'limited')
    await userEvent.type(within(dialog).getByLabelText(/(?:Override|Özel limit) boyutu/), 'abc')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Plan ata' }))
    expect(await within(dialog).findByText(/en fazla 3 ondalık/)).toBeInTheDocument()
    expect(api.assignSubscriptionPlan).not.toHaveBeenCalled()
    await userEvent.selectOptions(override, 'unlimited')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Plan ata' }))
    await waitFor(() => expect(api.assignSubscriptionPlan).toHaveBeenCalledWith('tenant-a', { planId: 'plan-5', storageLimitOverride: { kind: 'unlimited' } }))
  })

  it('changes plan: the current plan is not offered, the previous subscription becomes "Değiştirildi" and the history shows it afterwards', async () => {
    const next = makeSubscription({ subscriptionId: 'sub-2', planId: 'plan-50', planName: 'Kurumsal 50', planStorageLimitBytes: 50_000_000_000, effectiveStorageLimit: { unlimited: false, bytes: 50_000_000_000, source: 'plan' } })
    const old = { ...active, status: 'Superseded' as const, endsAt: '2026-10-07T10:00:00Z' }
    vi.mocked(api.assignSubscriptionPlan).mockResolvedValue({ outcome: 'changed', subscription: next, previousSubscriptionId: 'sub-1' })
    mount()
    await userEvent.click(await screen.findByRole('button', { name: 'Planı değiştir' }))
    const dialog = await dialogByName()
    expect(dialog).toHaveTextContent('Değiştirildi')
    expect(dialog).toHaveTextContent(/(?:Override|Özel limit) yeni plana taşınmaz/)
    fireEvent.click(within(dialog).getByRole('combobox', { name: 'Plan' }))
    expect(await within(dialog).findByRole('option', { name: /Kurumsal 50/ })).toBeInTheDocument()
    expect(within(dialog).queryByRole('option', { name: /^5 GB/ })).not.toBeInTheDocument()
    fireEvent.click(within(dialog).getByRole('option', { name: /Kurumsal 50/ }))
    serve(next, [next, old])
    await userEvent.click(within(dialog).getByRole('button', { name: 'Planı değiştir' }))
    await waitFor(() => expect(api.assignSubscriptionPlan).toHaveBeenCalledWith('tenant-a', { planId: 'plan-50' }))
    const list = await screen.findByRole('list', { name: 'Abonelik geçmişi' })
    await waitFor(() => expect(within(list).getByText('Değiştirildi')).toBeInTheDocument())
    expect(within(list).getAllByRole('listitem')).toHaveLength(2)
  })

  it('offers the previous plan again after an expired subscription', async () => {
    serve(makeSubscription({ status: 'Expired' }))
    mount()
    await userEvent.click(await screen.findByRole('button', { name: 'Plan ata' }))
    const dialog = await dialogByName()
    fireEvent.click(within(dialog).getByRole('combobox', { name: 'Plan' }))
    expect(await within(dialog).findByRole('option', { name: /^5 GB/ })).toBeInTheDocument()
  })

  it.each([
    ['plan_disabled', 'Bu plan devre dışı; yeni atamalarda seçilemez.', 'Etkin bir plan seçin.'],
    ['plan_already_assigned', 'Çalışma alanı zaten bu planı kullanıyor.', 'Farklı bir plan seçin.'],
  ])('shows the %s conflict with what happened, the data impact and the next action', async (code, what, action) => {
    vi.mocked(api.assignSubscriptionPlan).mockRejectedValue(new ApiError(409, { error: code }))
    mount()
    await userEvent.click(await screen.findByRole('button', { name: 'Planı değiştir' }))
    const dialog = await dialogByName()
    await pickPlan(dialog, 'Kurumsal 50')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Planı değiştir' }))
    const alert = await within(dialog).findByRole('alert')
    expect(alert).toHaveTextContent(what)
    expect(alert).toHaveTextContent('Verileriniz için:')
    expect(alert).toHaveTextContent(action)
    expect(within(alert).getByText('Teknik ayrıntılar')).toBeInTheDocument()
  })
})

describe('suspend, resume and override', () => {
  it('suspends after a plain consequence dialog and then offers resume instead', async () => {
    const suspended = { ...active, status: 'Suspended' as const }
    vi.mocked(api.suspendSubscription).mockResolvedValue(suspended)
    mount()
    await screen.findByText('Abonelik geçmişi')
    await openOthers()
    await userEvent.click(screen.getByRole('button', { name: 'Askıya al' }))
    const dialog = await dialogByName()
    expect(dialog).toHaveTextContent('Abonelik askıya alınsın mı?')
    expect(dialog).toHaveTextContent('Ne olacak?')
    expect(dialog).toHaveTextContent('Kimleri etkiler?')
    expect(dialog).toHaveTextContent('Geri alınabilir mi?')
    expect(within(dialog).queryByRole('textbox')).not.toBeInTheDocument()
    expect(api.suspendSubscription).not.toHaveBeenCalled()
    serve(suspended)
    await userEvent.click(within(dialog).getByRole('button', { name: 'Askıya al' }))
    await waitFor(() => expect(api.suspendSubscription).toHaveBeenCalledWith('tenant-a'))
    expect(await screen.findByRole('button', { name: 'Yeniden etkinleştir' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Askıya al' })).not.toBeInTheDocument()
    expect(screen.getAllByText('Askıda').length).toBeGreaterThan(0)
  })

  it('resumes a suspended subscription after a plain consequence dialog', async () => {
    serve({ ...active, status: 'Suspended' })
    vi.mocked(api.resumeSubscription).mockResolvedValue(active)
    mount()
    await userEvent.click(await screen.findByRole('button', { name: 'Yeniden etkinleştir' }))
    const dialog = await dialogByName()
    expect(dialog).toHaveTextContent('Geri alınabilir mi?')
    expect(api.resumeSubscription).not.toHaveBeenCalled()
    serve(active)
    await userEvent.click(within(dialog).getByRole('button', { name: 'Yeniden etkinleştir' }))
    await waitFor(() => expect(api.resumeSubscription).toHaveBeenCalledWith('tenant-a'))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    await screen.findByText('Abonelik geçmişi')
    await openOthers()
    expect(screen.getByRole('button', { name: 'Askıya al' })).toBeInTheDocument()
  })

  it.each([
    ['subscription_not_active', 'Abonelik etkin değil; askıya alınamaz.'],
    ['subscription_not_open', 'Abonelik sona ermiş; yalnızca yeni bir plan atanabilir.'],
  ])('shows the stale %s suspend conflict with a next action and refreshes the view', async (code, what) => {
    vi.mocked(api.suspendSubscription).mockRejectedValue(new ApiError(409, { error: code }))
    mount()
    await screen.findByText('Abonelik geçmişi')
    await openOthers()
    await userEvent.click(screen.getByRole('button', { name: 'Askıya al' }))
    const dialog = await dialogByName()
    const calls = vi.mocked(api.getTenantSubscription).mock.calls.length
    await userEvent.click(within(dialog).getByRole('button', { name: 'Askıya al' }))
    const alert = await within(dialog).findByRole('alert')
    expect(alert).toHaveTextContent(what)
    expect(alert).toHaveTextContent('Verileriniz için:')
    expect(alert).toHaveTextContent('Ne yapmalı:')
    await waitFor(() => expect(vi.mocked(api.getTenantSubscription).mock.calls.length).toBeGreaterThan(calls))
  })

  it('shows the not-suspended resume conflict', async () => {
    serve({ ...active, status: 'Suspended' })
    vi.mocked(api.resumeSubscription).mockRejectedValue(new ApiError(409, { error: 'subscription_not_suspended' }))
    mount()
    await userEvent.click(await screen.findByRole('button', { name: 'Yeniden etkinleştir' }))
    const dialog = await dialogByName()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Yeniden etkinleştir' }))
    const alert = await within(dialog).findByRole('alert')
    expect(alert).toHaveTextContent('Abonelik askıda değil; yeniden etkinleştirilemez.')
    expect(alert).toHaveTextContent('Ne yapmalı:')
  })

  async function openOverride() {
    await screen.findByText('Abonelik geçmişi')
    await openOthers()
    await userEvent.click(screen.getByRole('button', { name: 'Özel limit' }))
    return dialogByName()
  }

  it('sets a limited override with exact bytes after a consequence dialog, prefilled from the current override', async () => {
    serve(makeSubscription({ storageLimitOverride: { kind: 'limited', bytes: 2_500_000_000 }, effectiveStorageLimit: { unlimited: false, bytes: 2_500_000_000, source: 'override' } }))
    vi.mocked(api.setStorageLimitOverride).mockResolvedValue(active)
    mount()
    const dialog = await openOverride()
    expect(dialog).toHaveTextContent('Ne olacak?')
    expect(dialog).toHaveTextContent('Kimleri etkiler?')
    expect(dialog).toHaveTextContent('Geri alınabilir mi?')
    const size = within(dialog).getByLabelText(/(?:Override|Özel limit) boyutu/)
    expect(size).toHaveValue('2,5')
    await userEvent.clear(size)
    await userEvent.type(size, '3')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(api.setStorageLimitOverride).toHaveBeenCalledWith('tenant-a', { kind: 'limited', bytes: 3_000_000_000 }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('validates a limited override locally before sending', async () => {
    mount()
    const dialog = await openOverride()
    await userEvent.selectOptions(within(dialog).getByRole('combobox', { name: /(?:override|özel depolama limiti)/i }), 'limited')
    await userEvent.type(within(dialog).getByLabelText(/(?:Override|Özel limit) boyutu/), 'abc')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Kaydet' }))
    expect(await within(dialog).findByText(/en fazla 3 ondalık/)).toBeInTheDocument()
    expect(api.setStorageLimitOverride).not.toHaveBeenCalled()
  })

  it.each([['unlimited', { kind: 'unlimited' }], ['none', { kind: 'none' }]] as const)('sets the %s override without bytes', async (kind, body) => {
    serve(kind === 'none' ? makeSubscription({ storageLimitOverride: { kind: 'unlimited', bytes: null }, effectiveStorageLimit: { unlimited: true, bytes: null, source: 'override' } }) : active)
    vi.mocked(api.setStorageLimitOverride).mockResolvedValue(active)
    mount()
    const dialog = await openOverride()
    await userEvent.selectOptions(within(dialog).getByRole('combobox', { name: /(?:override|özel depolama limiti)/i }), kind)
    await userEvent.click(within(dialog).getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(api.setStorageLimitOverride).toHaveBeenCalledWith('tenant-a', body))
  })

  it('shows the not-open override conflict with the next action', async () => {
    vi.mocked(api.setStorageLimitOverride).mockRejectedValue(new ApiError(409, { error: 'subscription_not_open' }))
    mount()
    const dialog = await openOverride()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Kaydet' }))
    const alert = await within(dialog).findByRole('alert')
    expect(alert).toHaveTextContent('Abonelik sona ermiş')
    expect(alert).toHaveTextContent('Yeni bir plan atayın.')
  })
})
