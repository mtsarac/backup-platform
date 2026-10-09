import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ApiError } from '@/lib/api/client'
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

const buttonNames = () => screen.getAllByRole('button').map((button) => button.textContent)
async function facts() {
  const term = await screen.findByText('Etkin depolama limiti')
  const list = term.closest('dl')
  if (!list) throw new Error('Subscription facts not found')
  return within(list)
}
const dialogByName = async () => screen.findByRole('dialog')

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

  it('shows an explicit empty state for a tenant without subscription, with only the assign action', async () => {
    serve(null)
    mount()
    expect(await screen.findByText('Abonelik yok')).toBeInTheDocument()
    expect(buttonNames()).toEqual(['Plan ata'])
    expect(api.getSubscriptionHistory).not.toHaveBeenCalled()
  })
})

describe('subscription details', () => {
  it('shows plan, status, decimal limit with exact bytes and source, validity and assignee', async () => {
    mount()
    const current = await facts()
    expect(current.getByText('Etkin')).toBeInTheDocument()
    expect(current.getAllByText('5 GB')).toHaveLength(3)
    expect(current.getAllByText('5.000.000.000 bayt')).toHaveLength(2)
    expect(current.getByText('Kaynak: Plan')).toBeInTheDocument()
    expect(current.getByText('Süresiz')).toBeInTheDocument()
    expect(current.getByText('Platform Operatörü')).toBeInTheDocument()
  })

  it('reports an override as the limit source and shows the override value', async () => {
    serve(makeSubscription({ storageLimitOverride: { kind: 'limited', bytes: 2_500_000_000 }, effectiveStorageLimit: { unlimited: false, bytes: 2_500_000_000, source: 'override' } }))
    mount()
    const current = await facts()
    expect(current.getByText('Kaynak: Override')).toBeInTheDocument()
    expect(current.getAllByText('2,5 GB')).toHaveLength(2)
    expect(current.getAllByText('2.500.000.000 bayt')).toHaveLength(2)
  })

  it('shows unlimited and system-assigned migration subscriptions', async () => {
    serve(makeSubscription({ planName: 'Legacy Unlimited', planStorageLimitBytes: null, effectiveStorageLimit: { unlimited: true, bytes: null, source: 'plan' }, assignedByUserId: null, assignedByDisplayName: null }))
    mount()
    const current = await facts()
    expect(current.getAllByText('Limitsiz').length).toBeGreaterThan(0)
    expect(current.getByText('Sistem (geçiş)')).toBeInTheDocument()
  })

  it.each([
    ['Active', ['Planı değiştir', 'Askıya al', 'Limit override']],
    ['Suspended', ['Planı değiştir', 'Yeniden etkinleştir', 'Limit override']],
    ['Expired', ['Plan ata']],
  ] as const)('%s offers exactly the valid actions', async (status, expected) => {
    serve(makeSubscription({ status }))
    mount()
    await screen.findByRole('region', { name: 'Abonelik' })
    await screen.findByText('Abonelik geçmişi')
    expect(buttonNames().filter((name) => !name?.includes('Sonraki') && !name?.includes('Önceki'))).toEqual(expected)
  })

  it('renders history newest first with superseded rows labelled and paginates through the URL', async () => {
    const superseded = makeSubscription({ subscriptionId: 'sub-0', planId: 'plan-1', planName: '1 GB', planStorageLimitBytes: 1_000_000_000, status: 'Superseded', endsAt: '2026-10-03T10:00:00Z', effectiveStorageLimit: { unlimited: false, bytes: 1_000_000_000, source: 'plan' } })
    vi.mocked(api.getTenantSubscription).mockResolvedValue({ tenantId: 'tenant-a', subscription: active })
    vi.mocked(api.getSubscriptionHistory).mockResolvedValue(paged([active, superseded], { totalCount: 22, pageSize: 10 }))
    mount('/platform/tenants/tenant-a?subPage=2')
    const history = await screen.findByRole('table')
    const rows = within(history).getAllByRole('row').slice(1)
    expect(within(rows[0]).getByText('Etkin')).toBeInTheDocument()
    expect(within(rows[1]).getByText('Değiştirildi')).toBeInTheDocument()
    expect(within(rows[1]).getAllByText('1 GB')).toHaveLength(2)
    expect(api.getSubscriptionHistory).toHaveBeenCalledWith('tenant-a', { page: 2, pageSize: 10 }, expect.any(AbortSignal))
    expect(screen.getByText('22 kayıt · Sayfa 2 / 3')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Sonraki' }))
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('subPage=3'))
  })
})

describe('assign and change plan', () => {
  it('assigns a plan to a tenant without a subscription; picker renders whatever the API returns and hides system plans by default', async () => {
    serve(null)
    mount()
    await userEvent.click(await screen.findByRole('button', { name: 'Plan ata' }))
    const dialog = await dialogByName()
    expect(api.getPlans).toHaveBeenCalledWith({ enabled: 'true', page: 1, pageSize: 100 }, expect.any(AbortSignal))
    const picker = await within(dialog).findByRole('combobox', { name: /^Plan/ })
    await waitFor(() => expect(within(picker).getByRole('option', { name: 'Kurumsal 50 · 50 GB' })).toBeInTheDocument())
    expect(within(picker).getByRole('option', { name: '5 GB · 5 GB' })).toBeInTheDocument()
    expect(within(picker).queryByRole('option', { name: /Legacy Unlimited/ })).not.toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('checkbox', { name: 'Sistem planlarını göster' }))
    expect(within(picker).getByRole('option', { name: 'Legacy Unlimited · Limitsiz' })).toBeInTheDocument()
    await userEvent.selectOptions(picker, 'plan-50')
    vi.mocked(api.assignSubscriptionPlan).mockResolvedValue({ outcome: 'assigned', subscription: makeSubscription({ planId: 'plan-50', planName: 'Kurumsal 50' }), previousSubscriptionId: null })
    serve(makeSubscription({ planId: 'plan-50', planName: 'Kurumsal 50', planStorageLimitBytes: 50_000_000_000, effectiveStorageLimit: { unlimited: false, bytes: 50_000_000_000, source: 'plan' } }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Plan ata' }))
    await waitFor(() => expect(api.assignSubscriptionPlan).toHaveBeenCalledWith('tenant-a', { planId: 'plan-50' }))
    const current = await facts()
    await waitFor(() => expect(current.getAllByText('Kurumsal 50').length).toBeGreaterThan(0))
    expect(current.getAllByText('50.000.000.000 bayt').length).toBeGreaterThan(0)
  })

  it('sends an exact-byte override and an ISO end date', async () => {
    serve(null)
    vi.mocked(api.assignSubscriptionPlan).mockResolvedValue({ outcome: 'assigned', subscription: active, previousSubscriptionId: null })
    mount()
    await userEvent.click(await screen.findByRole('button', { name: 'Plan ata' }))
    const dialog = await dialogByName()
    await waitFor(() => expect(within(dialog).getByRole('option', { name: '5 GB · 5 GB' })).toBeInTheDocument())
    await userEvent.selectOptions(within(dialog).getByRole('combobox', { name: /^Plan/ }), 'plan-5')
    await userEvent.selectOptions(within(dialog).getByRole('combobox', { name: /override/i }), 'limited')
    await userEvent.type(within(dialog).getByLabelText(/Override boyutu/), '1,5')
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
    await waitFor(() => expect(within(dialog).getByRole('option', { name: '5 GB · 5 GB' })).toBeInTheDocument())
    await userEvent.selectOptions(within(dialog).getByRole('combobox', { name: /^Plan/ }), 'plan-5')
    const override = within(dialog).getByRole('combobox', { name: /override/i })
    await userEvent.selectOptions(override, 'limited')
    await userEvent.type(within(dialog).getByLabelText(/Override boyutu/), 'abc')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Plan ata' }))
    expect(await within(dialog).findByText(/en fazla 3 ondalık/)).toBeInTheDocument()
    expect(api.assignSubscriptionPlan).not.toHaveBeenCalled()
    await userEvent.selectOptions(override, 'unlimited')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Plan ata' }))
    await waitFor(() => expect(api.assignSubscriptionPlan).toHaveBeenCalledWith('tenant-a', { planId: 'plan-5', storageLimitOverride: { kind: 'unlimited' } }))
  })

  it('changes plan: current plan is not selectable and the superseded row appears in history afterwards', async () => {
    const next = makeSubscription({ subscriptionId: 'sub-2', planId: 'plan-50', planName: 'Kurumsal 50', planStorageLimitBytes: 50_000_000_000, effectiveStorageLimit: { unlimited: false, bytes: 50_000_000_000, source: 'plan' } })
    const old = { ...active, status: 'Superseded' as const, endsAt: '2026-10-07T10:00:00Z' }
    vi.mocked(api.assignSubscriptionPlan).mockResolvedValue({ outcome: 'changed', subscription: next, previousSubscriptionId: 'sub-1' })
    mount()
    await userEvent.click(await screen.findByRole('button', { name: 'Planı değiştir' }))
    const dialog = await dialogByName()
    expect(dialog).toHaveTextContent('Override yeni plana taşınmaz')
    await waitFor(() => expect(within(dialog).getByRole('option', { name: '5 GB · 5 GB (mevcut)' })).toBeDisabled())
    await userEvent.selectOptions(within(dialog).getByRole('combobox', { name: /^Plan/ }), 'plan-50')
    serve(next, [next, old])
    await userEvent.click(within(dialog).getByRole('button', { name: 'Planı değiştir' }))
    await waitFor(() => expect(api.assignSubscriptionPlan).toHaveBeenCalledWith('tenant-a', { planId: 'plan-50' }))
    const history = await screen.findByRole('table')
    await waitFor(() => expect(within(history).getByText('Değiştirildi')).toBeInTheDocument())
    expect(within(history).getAllByRole('row')).toHaveLength(3)
  })

  it('maps a disabled-plan conflict to a safe message', async () => {
    vi.mocked(api.assignSubscriptionPlan).mockRejectedValue(new ApiError(409, { error: 'plan_disabled' }))
    mount()
    await userEvent.click(await screen.findByRole('button', { name: 'Planı değiştir' }))
    const dialog = await dialogByName()
    await waitFor(() => expect(within(dialog).getByRole('option', { name: 'Kurumsal 50 · 50 GB' })).toBeInTheDocument())
    await userEvent.selectOptions(within(dialog).getByRole('combobox', { name: /^Plan/ }), 'plan-50')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Planı değiştir' }))
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Bu plan devre dışı; yeni atamalarda seçilemez.')
  })
})

describe('suspend, resume and override', () => {
  it('suspends after confirmation and then offers resume instead', async () => {
    const suspended = { ...active, status: 'Suspended' as const }
    vi.mocked(api.suspendSubscription).mockResolvedValue(suspended)
    mount()
    await userEvent.click(await screen.findByRole('button', { name: 'Askıya al' }))
    const dialog = await dialogByName()
    expect(dialog).toHaveTextContent('Abonelik askıya alınsın mı?')
    expect(api.suspendSubscription).not.toHaveBeenCalled()
    serve(suspended)
    await userEvent.click(within(dialog).getByRole('button', { name: 'Askıya al' }))
    await waitFor(() => expect(api.suspendSubscription).toHaveBeenCalledWith('tenant-a'))
    expect(await screen.findByRole('button', { name: 'Yeniden etkinleştir' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Askıya al' })).not.toBeInTheDocument()
    expect(screen.getAllByText('Askıda').length).toBeGreaterThan(0)
  })

  it('resumes a suspended subscription after confirmation', async () => {
    serve({ ...active, status: 'Suspended' })
    vi.mocked(api.resumeSubscription).mockResolvedValue(active)
    mount()
    await userEvent.click(await screen.findByRole('button', { name: 'Yeniden etkinleştir' }))
    const dialog = await dialogByName()
    serve(active)
    await userEvent.click(within(dialog).getByRole('button', { name: 'Yeniden etkinleştir' }))
    await waitFor(() => expect(api.resumeSubscription).toHaveBeenCalledWith('tenant-a'))
    expect(await screen.findByRole('button', { name: 'Askıya al' })).toBeInTheDocument()
  })

  it('maps a stale-state suspend conflict and refreshes the view', async () => {
    vi.mocked(api.suspendSubscription).mockRejectedValue(new ApiError(409, { error: 'subscription_not_active' }))
    mount()
    await userEvent.click(await screen.findByRole('button', { name: 'Askıya al' }))
    const dialog = await dialogByName()
    const calls = vi.mocked(api.getTenantSubscription).mock.calls.length
    await userEvent.click(within(dialog).getByRole('button', { name: 'Askıya al' }))
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Abonelik etkin değil; askıya alınamaz.')
    await waitFor(() => expect(vi.mocked(api.getTenantSubscription).mock.calls.length).toBeGreaterThan(calls))
  })

  it('sets a limited override with exact bytes, prefilled from the current override', async () => {
    serve(makeSubscription({ storageLimitOverride: { kind: 'limited', bytes: 2_500_000_000 }, effectiveStorageLimit: { unlimited: false, bytes: 2_500_000_000, source: 'override' } }))
    vi.mocked(api.setStorageLimitOverride).mockResolvedValue(active)
    mount()
    await userEvent.click(await screen.findByRole('button', { name: 'Limit override' }))
    const dialog = await dialogByName()
    const size = within(dialog).getByLabelText(/Override boyutu/)
    expect(size).toHaveValue('2,5')
    await userEvent.clear(size)
    await userEvent.type(size, '3')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(api.setStorageLimitOverride).toHaveBeenCalledWith('tenant-a', { kind: 'limited', bytes: 3_000_000_000 }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it.each([['unlimited', { kind: 'unlimited' }], ['none', { kind: 'none' }]] as const)('sets the %s override without bytes', async (kind, body) => {
    serve(kind === 'none' ? makeSubscription({ storageLimitOverride: { kind: 'unlimited', bytes: null }, effectiveStorageLimit: { unlimited: true, bytes: null, source: 'override' } }) : active)
    vi.mocked(api.setStorageLimitOverride).mockResolvedValue(active)
    mount()
    await userEvent.click(await screen.findByRole('button', { name: 'Limit override' }))
    const dialog = await dialogByName()
    await userEvent.selectOptions(within(dialog).getByRole('combobox', { name: /override/i }), kind)
    await userEvent.click(within(dialog).getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(api.setStorageLimitOverride).toHaveBeenCalledWith('tenant-a', body))
  })

  it('maps a not-open override conflict', async () => {
    vi.mocked(api.setStorageLimitOverride).mockRejectedValue(new ApiError(409, { error: 'subscription_not_open' }))
    mount()
    await userEvent.click(await screen.findByRole('button', { name: 'Limit override' }))
    const dialog = await dialogByName()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Kaydet' }))
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Abonelik sona ermiş')
  })
})
