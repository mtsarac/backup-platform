import { beforeEach, describe, expect, it, vi } from 'vitest'
import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import * as api from '@/features/platform/api'
import { legacyPlan, makePlan } from '@/features/platform/plans/test-fixtures'
import { paged, renderPlatform } from '@/features/platform/test-utils'
import { ApiError } from '@/lib/api/client'
import { PlatformPlansPage } from './platform-plans-page'

vi.mock('@/features/platform/api')

const five = makePlan()
const custom = makePlan({ planId: 'plan-25', name: 'Özel 2,5', storageLimitBytes: 2_500_000_000, enabled: false, tenantCount: 0, quotaPolicyReference: 'ref-1' })

function mount(path = '/platform/plans') {
  return renderPlatform(<PlatformPlansPage />, { path, route: '/platform/plans' })
}
function rowOf(name: string) {
  const row = screen.getAllByRole('row').find((candidate) => within(candidate).queryAllByRole('cell')[0]?.textContent?.startsWith(name))
  if (!row) throw new Error(`No row for ${name}`)
  return row
}

async function confirmPlan(name: string) {
  const calls = vi.mocked(api.updatePlan).mock.calls.length
  await userEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
  const dialog = await screen.findByRole('dialog')
  const confirm = within(dialog).getByRole('button', { name: 'Kaydet' })
  expect(confirm).toBeDisabled()
  expect(api.updatePlan).toHaveBeenCalledTimes(calls)
  const input = within(dialog).getByRole('textbox')
  await userEvent.type(input, `${name} `)
  expect(confirm).toBeDisabled()
  await userEvent.clear(input)
  await userEvent.type(input, name)
  await userEvent.click(confirm)
  return dialog
}

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getPlans).mockResolvedValue(paged([five, custom, legacyPlan]))
})

describe('platform plans page states', () => {
  it('shows loading, then rows', async () => {
    vi.mocked(api.getPlans).mockReturnValue(new Promise(() => undefined))
    mount()
    expect(await screen.findByRole('status')).toHaveTextContent('Yükleniyor')
  })

  it('renders an empty state, filtered and unfiltered', async () => {
    vi.mocked(api.getPlans).mockResolvedValue(paged([]))
    const { unmount } = mount()
    expect(await screen.findByText('Henüz plan yok.')).toBeInTheDocument()
    unmount()
    mount('/platform/plans?enabled=false')
    expect(await screen.findByText('Filtreye uygun plan bulunamadı.')).toBeInTheDocument()
  })

  it('renders a server error as an alert and a 403 as the authorization state', async () => {
    vi.mocked(api.getPlans).mockRejectedValue(new ApiError(500))
    const { unmount } = mount()
    expect(await screen.findByText('Sunucu isteği tamamlayamadı. Daha sonra tekrar deneyin.')).toBeInTheDocument()
    unmount()
    vi.mocked(api.getPlans).mockRejectedValue(new ApiError(403))
    mount()
    expect(await screen.findByRole('alert')).toHaveTextContent('Bu alan için yetkiniz yok')
  })
})

describe('platform plans list', () => {
  it('shows decimal GB and tenant count, with exact bytes and policy references in collapsed details and no system mutations', async () => {
    mount()
    await screen.findByText('Legacy Unlimited')
    const fiveRow = rowOf('5 GB')
    expect(within(fiveRow).getAllByText('5 GB')).toHaveLength(2)
    const details = within(fiveRow).getByText('5000000000').closest('details')
    expect(details).not.toHaveAttribute('open')
    await userEvent.click(within(details!).getByText('Teknik ayrıntılar'))
    expect(within(details!).getByRole('button', { name: /Kopyala:.*[Ll]imit/ })).toBeInTheDocument()
    expect(within(fiveRow).getByText('3')).toBeInTheDocument()
    const customRow = rowOf('Özel 2,5')
    expect(within(customRow).getByText('2,5 GB')).toBeInTheDocument()
    expect(within(customRow).getByText('Devre dışı')).toBeInTheDocument()
    expect(within(customRow).getByText('ref-1').closest('details')).not.toHaveAttribute('open')
    const legacyRow = rowOf('Legacy Unlimited')
    expect(within(legacyRow).getByText(/(?:Sınırsız|Limitsiz)/)).toBeInTheDocument()
    expect(within(legacyRow).queryByRole('button', { name: /düzenle|etkinleştir|devre dışı bırak/i })).not.toBeInTheDocument()
    expect(within(legacyRow).getByText('Sistem planı; salt okunur.')).toBeInTheDocument()
    expect(api.getPlans).toHaveBeenCalledWith({ enabled: undefined, search: '', page: 1, pageSize: 25 }, expect.any(AbortSignal))
  })

  it('filters by enabled state and searches from page one through the URL', async () => {
    vi.mocked(api.getPlans).mockResolvedValue(paged([five], { totalCount: 60, pageSize: 25 }))
    mount('/platform/plans?page=2')
    await screen.findByText('60 kayıt · Sayfa 2 / 3')
    fireEvent.change(screen.getByRole('combobox', { name: 'Durum' }), { target: { value: 'true' } })
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/platform/plans?enabled=true'))
    expect(api.getPlans).toHaveBeenLastCalledWith({ enabled: 'true', search: '', page: 1, pageSize: 25 }, expect.any(AbortSignal))
    await userEvent.type(screen.getByRole('searchbox', { name: 'Plan ara' }), '5')
    await userEvent.click(screen.getByRole('button', { name: 'Ara' }))
    await waitFor(() => expect(api.getPlans).toHaveBeenLastCalledWith({ enabled: 'true', search: '5', page: 1, pageSize: 25 }, expect.any(AbortSignal)))
  })
})

describe('plan create', () => {
  async function openCreate() {
    mount()
    await screen.findByText('Legacy Unlimited')
    await userEvent.click(screen.getByRole('button', { name: 'Yeni plan' }))
    return screen.findByRole('dialog')
  }

  it('converts decimal GB to exact bytes and defaults to enabled', async () => {
    vi.mocked(api.createPlan).mockResolvedValue(makePlan({ planId: 'new', name: 'Yeni', storageLimitBytes: 2_500_000_000 }))
    const dialog = await openCreate()
    expect(within(dialog).getByRole('checkbox', { name: /Etkin/ })).toBeChecked()
    await userEvent.type(within(dialog).getByLabelText('Plan adı'), '  Yeni ')
    await userEvent.type(within(dialog).getByLabelText(/(?:Depolama sınırı|Depolama limiti)/), '2,5')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Plan oluştur' }))
    await waitFor(() => expect(api.createPlan).toHaveBeenCalledWith({ name: 'Yeni', storageLimitBytes: 2_500_000_000, enabled: true }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('creates an unlimited, initially disabled plan without a byte value', async () => {
    vi.mocked(api.createPlan).mockResolvedValue(makePlan())
    const dialog = await openCreate()
    await userEvent.type(within(dialog).getByLabelText('Plan adı'), 'Sınırsız')
    await userEvent.click(within(dialog).getByRole('checkbox', { name: /(?:Sınırsız|Limitsiz)/ }))
    expect(within(dialog).queryByLabelText(/(?:Depolama sınırı|Depolama limiti)/)).not.toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('checkbox', { name: /Etkin/ }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Plan oluştur' }))
    await waitFor(() => expect(api.createPlan).toHaveBeenCalledWith({ name: 'Sınırsız', unlimited: true, enabled: false }))
  })

  it('rejects malformed or out-of-range sizes before calling the API', async () => {
    const dialog = await openCreate()
    await userEvent.type(within(dialog).getByLabelText('Plan adı'), 'X')
    const size = within(dialog).getByLabelText(/(?:Depolama sınırı|Depolama limiti)/)
    await userEvent.type(size, '1,2345')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Plan oluştur' }))
    expect(await within(dialog).findByText(/en fazla 3 ondalık/)).toBeInTheDocument()
    await userEvent.clear(size)
    await userEvent.type(size, '0')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Plan oluştur' }))
    expect(await within(dialog).findByText(/en fazla 1.000.000 GB/)).toBeInTheDocument()
    expect(api.createPlan).not.toHaveBeenCalled()
  })

  it('maps a duplicate name to a safe message and keeps the dialog open', async () => {
    vi.mocked(api.createPlan).mockRejectedValue(new ApiError(409, { error: 'plan_name_taken' }))
    const dialog = await openCreate()
    await userEvent.type(within(dialog).getByLabelText('Plan adı'), '5 GB')
    await userEvent.type(within(dialog).getByLabelText(/(?:Depolama sınırı|Depolama limiti)/), '5')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Plan oluştur' }))
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Bu plan adı zaten kullanılıyor.')
  })

  it('marks the name invalid for a 400 name error', async () => {
    vi.mocked(api.createPlan).mockRejectedValue(new ApiError(400, { error: 'invalid_request', field: 'name', code: 'too_long' }))
    const dialog = await openCreate()
    await userEvent.type(within(dialog).getByLabelText('Plan adı'), 'x')
    await userEvent.type(within(dialog).getByLabelText(/(?:Depolama sınırı|Depolama limiti)/), '1')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Plan oluştur' }))
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Girilen değer çok uzun.')
    expect(within(dialog).getByLabelText('Plan adı')).toBeInvalid()
  })
})

describe('plan edit and enable/disable', () => {
  it('edits name and size to exact bytes; unchanged size keeps the original bytes', async () => {
    vi.mocked(api.updatePlan).mockResolvedValue(five)
    mount()
    await screen.findByText('Legacy Unlimited')
    await userEvent.click(screen.getByRole('button', { name: '5 GB planını düzenle' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByLabelText(/(?:Depolama sınırı|Depolama limiti)/)).toHaveValue('5')
    await userEvent.clear(within(dialog).getByLabelText('Plan adı'))
    await userEvent.type(within(dialog).getByLabelText('Plan adı'), 'Beş')
    await confirmPlan('5 GB')
    await waitFor(() => expect(api.updatePlan).toHaveBeenCalledWith('plan-5', { name: 'Beş', storageLimitBytes: 5_000_000_000 }))
  })

  it('edits to a decimal size, to unlimited, and passes an existing policy reference through', async () => {
    vi.mocked(api.updatePlan).mockResolvedValue(custom)
    mount()
    await screen.findByText('Legacy Unlimited')
    await userEvent.click(screen.getByRole('button', { name: 'Özel 2,5 planını düzenle' }))
    let dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByLabelText(/(?:Depolama sınırı|Depolama limiti)/)).toHaveValue('2,5')
    const size = within(dialog).getByLabelText(/(?:Depolama sınırı|Depolama limiti)/)
    await userEvent.clear(size)
    await userEvent.type(size, '7,125')
    await confirmPlan('Özel 2,5')
    await waitFor(() => expect(api.updatePlan).toHaveBeenLastCalledWith('plan-25', { name: 'Özel 2,5', storageLimitBytes: 7_125_000_000, quotaPolicyReference: 'ref-1' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    await userEvent.click(screen.getByRole('button', { name: 'Özel 2,5 planını düzenle' }))
    dialog = await screen.findByRole('dialog')
    await userEvent.click(within(dialog).getByRole('checkbox', { name: /(?:Sınırsız|Limitsiz)/ }))
    await confirmPlan('Özel 2,5')
    await waitFor(() => expect(api.updatePlan).toHaveBeenLastCalledWith('plan-25', { name: 'Özel 2,5', unlimited: true, quotaPolicyReference: 'ref-1' }))
  })

  it('shows a conflict from edit without closing', async () => {
    vi.mocked(api.updatePlan).mockRejectedValue(new ApiError(409, { error: 'concurrent_update' }))
    mount()
    await screen.findByText('Legacy Unlimited')
    await userEvent.click(screen.getByRole('button', { name: '5 GB planını düzenle' }))
    const dialog = await confirmPlan('5 GB')
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('aynı anda değiştirildi')
  })

  it('confirms before disabling and explains that subscribers stay', async () => {
    vi.mocked(api.setPlanEnabled).mockResolvedValue({ ...five, enabled: false })
    mount()
    await screen.findByText('Legacy Unlimited')
    await userEvent.click(screen.getByRole('button', { name: '5 GB planını devre dışı bırak' }))
    const dialog = await screen.findByRole('dialog')
    expect(dialog).toHaveTextContent('Plan devre dışı bırakılsın mı?')
    expect(dialog).toHaveTextContent('taşınmaz')
    expect(api.setPlanEnabled).not.toHaveBeenCalled()
    vi.mocked(api.getPlans).mockResolvedValue(paged([{ ...five, enabled: false }, custom, legacyPlan]))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Devre dışı bırak' }))
    await waitFor(() => expect(api.setPlanEnabled).toHaveBeenCalledWith('plan-5', false))
    await waitFor(() => expect(within(rowOf('5 GB')).getByText('Devre dışı')).toBeInTheDocument())
  })

  it('enables a disabled plan', async () => {
    vi.mocked(api.setPlanEnabled).mockResolvedValue({ ...custom, enabled: true })
    mount()
    await screen.findByText('Legacy Unlimited')
    await userEvent.click(screen.getByRole('button', { name: 'Özel 2,5 planını etkinleştir' }))
    const dialog = await screen.findByRole('dialog')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Etkinleştir' }))
    await waitFor(() => expect(api.setPlanEnabled).toHaveBeenCalledWith('plan-25', true))
  })

  it('shows the mapped error when enabling fails', async () => {
    vi.mocked(api.setPlanEnabled).mockRejectedValue(new ApiError(409, { error: 'system_plan_immutable' }))
    mount()
    await screen.findByText('Legacy Unlimited')
    await userEvent.click(screen.getByRole('button', { name: '5 GB planını devre dışı bırak' }))
    const dialog = await screen.findByRole('dialog')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Devre dışı bırak' }))
    expect(await within(dialog).findByRole('alert')).toHaveTextContent('Sistem planı değiştirilemez.')
  })
})
