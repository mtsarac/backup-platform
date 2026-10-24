import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { getBandwidthProfiles, getUploadBandwidth, saveBandwidthProfile, setUploadBandwidth } from '@/features/upload-bandwidth/api'
import { ApiError } from '@/lib/api/client'
import { UploadBandwidthPage } from './upload-bandwidth-page'

const access = vi.hoisted(() => ({ tenantId: 'tenant-a', tenant: { name: 'Acme A.Ş.' }, canOperate: true, canAdmin: true }))
const jobs = vi.hoisted(() => vi.fn())
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => access }))
vi.mock('@/features/upload-bandwidth/api')
vi.mock('@/features/agents/api', () => ({ getAgents: vi.fn().mockResolvedValue([{ agentId: 'agent-a', hostName: 'SQL-01' }, { agentId: 'agent-b', hostName: 'SQL-02' }]) }))
vi.mock('@/features/backup-jobs/api', () => ({ getBackupJobs: jobs }))

const limits = {
  minimumBytesPerSecond: 16_000, maximumBytesPerSecond: 100_000_000_000,
  tenant: { bytesPerSecond: 12_500_000 as number | null }, agents: [{ agentId: 'agent-a', bytesPerSecond: 1_250_000 }], jobs: [{ jobId: 'job-a', bytesPerSecond: 16_000 }],
}
function mount() {
  render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })}><MemoryRouter><UploadBandwidthPage /></MemoryRouter></QueryClientProvider>)
}
const openTab = async (name: RegExp) => fireEvent.click(await screen.findByRole('tab', { name }))
const row = (name: string) => screen.getByRole('row', { name: new RegExp(`^${name}`) })
const technicalDetails = () => screen.getByText('Teknik ayrıntılar').closest('details')!
const rawRate = /\d B\/sn/

beforeEach(() => {
  vi.clearAllMocks()
  access.canAdmin = true
  jobs.mockResolvedValue([{ id: 'job-a', name: 'Günlük ERP', agentName: 'SQL-01' }])
  vi.mocked(getUploadBandwidth).mockResolvedValue(limits)
  vi.mocked(getBandwidthProfiles).mockResolvedValue([])
  vi.mocked(setUploadBandwidth).mockResolvedValue({ scope: 'agent', scopeId: 'agent-b', bytesPerSecond: null })
})

describe('upload bandwidth page', () => {
  it('shows tenant, agent and job limits in a friendly unit, Unlimited for missing ones, and never a raw bytes-per-second value', async () => {
    mount()
    expect(await screen.findByTestId('tenant-limit')).toHaveTextContent('100 Mbit/sn')
    expect(screen.getByTestId('tenant-limit')).not.toHaveTextContent(rawRate)
    await openTab(/^Yedekleme bilgisayarları$/)
    expect(await screen.findByRole('row', { name: /^SQL-01/ })).toBeInTheDocument()
    expect(row('SQL-01')).toHaveTextContent('10 Mbit/sn')
    expect(row('SQL-01')).not.toHaveTextContent(rawRate)
    expect(row('SQL-02')).toHaveTextContent('Sınırsız')
    await openTab(/^Yedekleme işleri$/)
    expect(await screen.findByRole('row', { name: /^Günlük ERP/ })).toHaveTextContent('16 KB/sn')
    expect(row('Günlük ERP')).not.toHaveTextContent(rawRate)
  })

  it('keeps the exact bytes per second under collapsed technical details on every tab', async () => {
    mount()
    await screen.findByTestId('tenant-limit')
    expect(technicalDetails()).not.toHaveAttribute('open')
    expect(within(technicalDetails()).getByText('12.500.000 B/sn')).toBeInTheDocument()
    await openTab(/^Yedekleme bilgisayarları$/)
    await screen.findByRole('row', { name: /^SQL-01/ })
    expect(technicalDetails()).not.toHaveAttribute('open')
    expect(within(technicalDetails()).getByText('1.250.000 B/sn')).toBeInTheDocument()
    await openTab(/^Yedekleme işleri$/)
    await screen.findByRole('row', { name: /^Günlük ERP/ })
    expect(within(technicalDetails()).getByText('16.000 B/sn')).toBeInTheDocument()
  })

  it('explains in plain words, outside the technical details, which limit wins and what is never limited', async () => {
    mount()
    const help = await screen.findByRole('region', { name: 'Hangi sınır geçerli?' })
    expect(help.closest('details')).toBeNull()
    expect(help).toHaveTextContent(/geri yükleme/i)
    expect(help).not.toHaveTextContent(rawRate)
  })

  it('shows what will be saved in the limit dialog only as an exact value under technical details', async () => {
    mount()
    await openTab(/^Yedekleme bilgisayarları$/)
    fireEvent.click(await screen.findByRole('button', { name: 'SQL-01 yükleme hız sınırını düzenle' }))
    const dialog = await screen.findByRole('dialog')
    const details = within(dialog).getByText('Teknik ayrıntılar').closest('details')!
    expect(details).not.toHaveAttribute('open')
    expect(within(details).getByText('1.250.000 B/sn')).toBeInTheDocument()
    fireEvent.change(within(dialog).getByLabelText('Hız sınırı'), { target: { value: '2,5' } })
    fireEvent.change(within(dialog).getByLabelText('Birim'), { target: { value: 'MB/s' } })
    expect(within(details).getByText('2.500.000 B/sn')).toBeInTheDocument()
    expect(within(dialog).queryByText(rawRate, { ignore: 'details *' })).not.toBeInTheDocument()
  })

  it('saves an agent limit in bytes per second after unit conversion', async () => {
    mount()
    await openTab(/^Yedekleme bilgisayarları$/)
    fireEvent.click(await screen.findByRole('button', { name: 'SQL-02 yükleme hız sınırını düzenle' }))
    const dialog = await screen.findByRole('dialog')
    fireEvent.click(within(dialog).getByLabelText("Sınırsız (sınır yok)"))
    fireEvent.change(within(dialog).getByLabelText('Hız sınırı'), { target: { value: '2,5' } })
    fireEvent.change(within(dialog).getByLabelText('Birim'), { target: { value: 'MB/s' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(setUploadBandwidth).toHaveBeenCalledWith('tenant-a', { scope: 'agent', id: 'agent-b' }, 2_500_000))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('opens an existing limit in its clean unit and removes it with Unlimited (null)', async () => {
    mount()
    await openTab(/^Yedekleme bilgisayarları$/)
    fireEvent.click(await screen.findByRole('button', { name: 'SQL-01 yükleme hız sınırını düzenle' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByLabelText('Hız sınırı')).toHaveValue('10')
    expect(within(dialog).getByLabelText('Birim')).toHaveValue('Mbit/s')
    fireEvent.click(within(dialog).getByLabelText("Sınırsız (sınır yok)"))
    expect(within(dialog).getByLabelText('Hız sınırı')).toBeDisabled()
    fireEvent.click(within(dialog).getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(setUploadBandwidth).toHaveBeenCalledWith('tenant-a', { scope: 'agent', id: 'agent-a' }, null))
  })

  it('blocks out-of-range values client-side', async () => {
    mount()
    await openTab(/^Yedekleme işleri$/)
    fireEvent.click(await screen.findByRole('button', { name: 'Günlük ERP yükleme hız sınırını düzenle' }))
    const dialog = await screen.findByRole('dialog')
    fireEvent.change(within(dialog).getByLabelText('Hız sınırı'), { target: { value: '0,1' } })
    expect(within(dialog).getByText(/arasında olmalıdır/)).toBeInTheDocument()
    expect(within(dialog).getByRole('button', { name: 'Kaydet' })).toBeDisabled()
    expect(setUploadBandwidth).not.toHaveBeenCalled()
  })

  it('keeps the dialog open and shows the server error', async () => {
    vi.mocked(setUploadBandwidth).mockRejectedValue(new ApiError(400, { error: 'invalid_request', field: 'bytesPerSecond', code: 'out_of_range' }))
    mount()
    fireEvent.click(await screen.findByRole('button', { name: 'Acme A.Ş. yükleme hız sınırını düzenle' }))
    fireEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Kaydet' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('izin verilen aralığın dışında')
    expect(screen.getByRole('dialog')).toBeInTheDocument()
  })
  it('searches Backup Jobs and pages locally in groups of 25', async () => {
    jobs.mockResolvedValue(Array.from({ length: 26 }, (_, index) => ({ id: `job-${index + 1}`, name: `Backup ${String(index + 1).padStart(2, '0')}`, agentName: 'SQL-01' })))
    mount()
    await openTab(/^Yedekleme işleri$/)
    await screen.findByRole('row', { name: /^Backup 01/ })
    expect(screen.getAllByRole('row')).toHaveLength(26)
    expect(screen.queryByRole('row', { name: /^Backup 26/ })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Sonraki' }))
    expect(screen.getByRole('row', { name: /^Backup 26/ })).toBeInTheDocument()
    expect(screen.queryByRole('row', { name: /^Backup 01/ })).not.toBeInTheDocument()
    fireEvent.change(screen.getByRole('searchbox', { name: 'Yedekleme işi ara' }), { target: { value: 'Backup 01' } })
    expect(screen.getByRole('row', { name: /^Backup 01/ })).toBeInTheDocument()
    expect(screen.queryByRole('row', { name: /^Backup 26/ })).not.toBeInTheDocument()
    expect(screen.getAllByRole('row')).toHaveLength(2)
  })

  it('creates an explicit pause window as zero and round-trips it without showing an unlimited or zero-rate label', async () => {
    const profile = { id: 'pause-profile', name: 'Mesai molası', timeZoneId: 'UTC', windows: [{ dayOfWeek: 1, startTime: '09:00:00', endTime: '18:00:00', bytesPerSecond: 0 }] }
    vi.mocked(saveBandwidthProfile).mockResolvedValue(profile)
    mount()
    await openTab(/^Profiller$/)
    fireEvent.click(await screen.findByRole('button', { name: 'Yeni Profil' }))
    let dialog = await screen.findByRole('dialog')
    fireEvent.change(within(dialog).getByLabelText('Profil Adı'), { target: { value: profile.name } })
    fireEvent.change(within(dialog).getByLabelText('Zaman Dilimi (IANA)'), { target: { value: 'UTC' } })
    fireEvent.change(within(dialog).getByLabelText('Pencere Davranışı'), { target: { value: 'pause' } })
    expect(within(dialog).getByLabelText('Hız Limiti')).toBeDisabled()
    expect(within(dialog).getByLabelText('Hız Birimi')).toBeDisabled()
    fireEvent.click(within(dialog).getByRole('button', { name: /Pencereyi Listeye Ekle/ }))
    expect(within(dialog).getByText(/09:00 - 18:00 → Aktarımı Duraklat/)).toBeInTheDocument()
    vi.mocked(getBandwidthProfiles).mockResolvedValue([profile])
    fireEvent.click(within(dialog).getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(saveBandwidthProfile).toHaveBeenCalledWith('tenant-a', { id: undefined, name: profile.name, timeZoneId: 'UTC', windows: profile.windows }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(await screen.findByText('Pazartesi 09:00–18:00: Aktarımı Duraklat')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Düzenle' }))
    dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText(/09:00 - 18:00 → Aktarımı Duraklat/)).toBeInTheDocument()
    fireEvent.click(within(dialog).getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(saveBandwidthProfile).toHaveBeenLastCalledWith('tenant-a', profile))
  })

  it('requires explicit pause selection instead of accepting a zero rate as a static-style limit', async () => {
    mount()
    await openTab(/^Profiller$/)
    fireEvent.click(await screen.findByRole('button', { name: 'Yeni Profil' }))
    const dialog = await screen.findByRole('dialog')
    fireEvent.change(within(dialog).getByLabelText('Hız Limiti'), { target: { value: '0' } })
    expect(within(dialog).getByRole('button', { name: /Pencereyi Listeye Ekle/ })).toBeDisabled()
    fireEvent.change(within(dialog).getByLabelText('Pencere Davranışı'), { target: { value: 'pause' } })
    expect(within(dialog).getByRole('button', { name: /Pencereyi Listeye Ekle/ })).toBeEnabled()
  })

  it('reads each profile as a plain schedule, with the exact rates, time zone and ID under technical details', async () => {
    const profile = {
      id: 'profile-1', name: 'Mesai', timeZoneId: 'Europe/Istanbul',
      windows: [1, 2, 3, 4, 5].map(dayOfWeek => ({ dayOfWeek, startTime: '09:00:00', endTime: '18:00:00', bytesPerSecond: 1_250_000 }))
        .concat([6, 0].map(dayOfWeek => ({ dayOfWeek, startTime: '09:00:00', endTime: '18:00:00', bytesPerSecond: 0 }))),
    }
    vi.mocked(getBandwidthProfiles).mockResolvedValue([profile])
    mount()
    await openTab(/^Profiller$/)
    expect(await screen.findByText('Hafta içi 09:00–18:00: 10 Mbit/sn')).toBeInTheDocument()
    expect(screen.getByText('Hafta sonu 09:00–18:00: Aktarımı Duraklat')).toBeInTheDocument()
    expect(screen.getByText('Diğer saatlerde sınırsız.')).toBeInTheDocument()
    expect(screen.queryByText(rawRate, { ignore: 'details *' })).not.toBeInTheDocument()
    expect(screen.queryByText('Europe/Istanbul', { ignore: 'details *' })).not.toBeInTheDocument()
    expect(technicalDetails()).not.toHaveAttribute('open')
    expect(within(technicalDetails()).getByText('profile-1')).toBeInTheDocument()
    expect(within(technicalDetails()).getByText('Europe/Istanbul')).toBeInTheDocument()
    expect(within(technicalDetails()).getAllByText('1.250.000 B/sn')).toHaveLength(5)
  })

  it('guides to the one primary action when no profile exists, or to the administrator without permission', async () => {
    mount()
    await openTab(/^Profiller$/)
    expect(await screen.findByRole('button', { name: 'Yeni Profil' })).toBeInTheDocument()
    expect(screen.queryByText('Yöneticinize danışın')).not.toBeInTheDocument()
  })

  it('is read-only without administrator rights and says whom to ask', async () => {
    access.canAdmin = false
    mount()
    expect(await screen.findByTestId('tenant-limit')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /düzenle/ })).not.toBeInTheDocument()
    expect(screen.getByText(/Yöneticinize danışın/)).toBeInTheDocument()
    await openTab(/^Profiller$/)
    expect(await screen.findByText('Yöneticinize danışın')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Yeni Profil' })).not.toBeInTheDocument()
  })
})
