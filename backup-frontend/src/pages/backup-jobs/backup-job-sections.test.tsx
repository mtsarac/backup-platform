import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createMemoryRouter, RouterProvider } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { BackupJobDetailPage } from './backup-job-detail-page'
import { getBackupJobHealth, getBackupJobs, getHealthTimeline, saveBackupJob } from '@/features/backup-jobs/api'
import { healthyJobHealth, savedJob } from '@/features/backup-jobs/test-fixtures'
import { getAgents } from '@/features/agents/api'
import { getBackups } from '@/features/backups/api'
import { getTenantConnections, getTenantProviders } from '@/features/storage-connections/api'
import { catalog, makeConnection } from '@/features/storage-connections/test-fixtures'
import { engineLabels } from '@/features/discovery/status'
import type { BackupJob } from '@/features/backup-jobs/types'

vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a' }) }))
vi.mock('@/features/backup-jobs/api')
vi.mock('@/features/agents/api')
vi.mock('@/features/backups/api')
vi.mock('@/features/storage-connections/api')

const job = savedJob
const other = { ...job, id: 'job-b', name: 'Satış' } as BackupJob
const withConfiguration = (patch: Partial<BackupJob['configuration']>, rest: Partial<BackupJob> = {}) => ({ ...job, ...rest, configuration: { ...job.configuration, ...patch } }) as BackupJob

function mount(jobs: BackupJob[] = [job, other]) {
  vi.mocked(getBackupJobs).mockResolvedValue(jobs)
  const router = createMemoryRouter([{ path: '/tenants/:tenantId/backup-jobs/:id', element: <BackupJobDetailPage /> }, { path: '/tenants/:tenantId/backup-jobs', element: <p>jobs list</p> }], { initialEntries: ['/tenants/tenant-a/backup-jobs/job-a'] })
  render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><RouterProvider router={router} /></QueryClientProvider>)
  return router
}
const change = async (section: string) => fireEvent.click(await screen.findByRole('button', { name: `Değiştir: ${section}` }))
const sentBody = () => vi.mocked(saveBackupJob).mock.calls.at(-1)![1]

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(getBackupJobHealth).mockResolvedValue(healthyJobHealth)
  vi.mocked(getHealthTimeline).mockResolvedValue({ jobs: [] })
  vi.mocked(getAgents).mockResolvedValue([{ agentId: 'agent-a', hostName: 'Ofis', connectivity: 'online' }] as never)
  vi.mocked(getBackups).mockResolvedValue({ items: [], totalCount: 1, page: 1, pageSize: 1 })
  vi.mocked(getTenantConnections).mockResolvedValue({ items: [makeConnection()] })
  vi.mocked(getTenantProviders).mockResolvedValue({ items: catalog })
  vi.mocked(saveBackupJob).mockResolvedValue(job)
})

describe('editing a section in place', () => {
  it('turns only that section into a form; Vazgeç drops the change', async () => {
    mount()
    await change('Zamanlama')
    expect(screen.getAllByRole('form')).toHaveLength(1)
    expect(screen.getByRole('form', { name: 'Zamanlama' })).toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Hazır zamanlamalar'), { target: { value: '0 8 * * *' } })
    fireEvent.click(screen.getByRole('button', { name: 'Vazgeç' }))
    expect(screen.queryByRole('form')).not.toBeInTheDocument()
    expect(screen.getByText('Her gün 02:00')).toBeInTheDocument()
    expect(saveBackupJob).not.toHaveBeenCalled()
  })

  it('saves with one PUT and returns to reading', async () => {
    mount()
    await change('Zamanlama')
    fireEvent.change(screen.getByLabelText('Hazır zamanlamalar'), { target: { value: '0 8 * * *' } })
    fireEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalledTimes(1))
    expect(vi.mocked(saveBackupJob).mock.calls[0][0]).toBe('tenant-a')
    expect(vi.mocked(saveBackupJob).mock.calls[0][2]).toBe('job-a')
    await waitFor(() => expect(screen.queryByRole('form')).not.toBeInTheDocument())
  })

  it('sends the job\'s own time zone, not the browser\'s, when the schedule changes', async () => {
    mount()
    await change('Zamanlama')
    fireEvent.change(screen.getByLabelText('Hazır zamanlamalar'), { target: { value: '0 8,20 * * *' } })
    fireEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalled())
    expect(sentBody().schedule).toMatchObject({ cronExpression: '0 8,20 * * *', timeZone: 'Europe/Berlin' })
  })

  it.each([
    ['retryPolicy', (body: BackupJob) => body.retryPolicy, job.retryPolicy],
    ['overlapPolicy', (body: BackupJob) => body.overlapPolicy, job.overlapPolicy],
    ['missed-run tolerance', (body: BackupJob) => body.schedule?.missedRunGraceSeconds, job.schedule!.missedRunGraceSeconds],
    ['storage prefix', (body: BackupJob) => body.configuration.storagePrefix, 'acme/erp'],
    ['staging reference', (body: BackupJob) => body.configuration.stagingReference, 'stage'],
    ['source site', (body: BackupJob) => body.configuration.sourceSite, 'ist'],
    ['copy-only flag', (body: BackupJob) => body.configuration.copyOnly, false],
    ['checksum policy', (body: BackupJob) => body.configuration.checksumPolicy, 'Preferred'],
    ['compression policy', (body: BackupJob) => body.configuration.compressionPolicy, 'Disabled'],
    ['host, port and login reference', (body: BackupJob) => [body.configuration.host, body.configuration.port, body.configuration.databaseCredentialReference, body.configuration.trustServerCertificate], ['sql01', 1433, 'cred-1', true]],
    ['retention grace period', (body: BackupJob) => body.retention?.deleteGracePeriodSeconds, 600],
  ])('keeps the hidden %s as saved', async (_name, pick, expected) => {
    mount()
    await change('Kaynak')
    fireEvent.change(screen.getByLabelText('İş adı'), { target: { value: 'Yeni ad' } })
    fireEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalled())
    expect(sentBody().name).toBe('Yeni ad')
    expect(pick(sentBody() as BackupJob)).toEqual(expected)
  })

  it('sends a PostgreSQL job\'s saved copy-only value as it is', async () => {
    const otherEngine = Object.keys(engineLabels).find(engine => engine !== 'mssql')!
    mount([withConfiguration({ engine: otherEngine as never, copyOnly: true })])
    await change('Kaynak')
    fireEvent.change(screen.getByLabelText('İş adı'), { target: { value: 'Yeni ad' } })
    fireEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalled())
    expect(sentBody().configuration.copyOnly).toBe(true)
  })

  it('shows the computer, data type and database read-only, with no control to change them', async () => {
    mount()
    await change('Kaynak')
    const form = within(screen.getByRole('form', { name: 'Kaynak' }))
    expect(form.getByText('Ofis')).toBeInTheDocument()
    expect(form.getByText('ERP')).toBeInTheDocument()
    expect(form.getByText('Veri türü')).toBeInTheDocument()
    expect(form.queryByRole('combobox')).not.toBeInTheDocument()
    expect(form.queryByRole('radio')).not.toBeInTheDocument()
    expect(form.getAllByRole('textbox')).toHaveLength(1)
  })

  it('edits the paths of a file source and rejects an empty list', async () => {
    mount([withConfiguration({ engine: 'file-system', database: 'docs', files: { paths: ['a'], includes: ['**'], excludes: ['*.tmp'], symlinkPolicy: 'Skip' } })])
    await change('Kaynak')
    const paths = screen.getByLabelText('Yedeklenecek dosya ve klasörler')
    fireEvent.change(paths, { target: { value: '' } })
    fireEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
    expect(await screen.findByText('1–256 geçerli göreli yol girin.')).toBeInTheDocument()
    expect(saveBackupJob).not.toHaveBeenCalled()
    fireEvent.change(paths, { target: { value: 'b\nc' } })
    fireEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalled())
    expect(sentBody().configuration.files).toEqual({ paths: ['b', 'c'], includes: ['**'], excludes: ['*.tmp'], symlinkPolicy: 'Skip' })
  })
})

describe('retention and encryption', () => {
  it('lets an administrator change retention and says it applies to future backups only', async () => {
    mount()
    await change('Saklama ve şifreleme')
    const note = 'Yeni süre yalnızca bundan sonraki yedeklere uygulanır; mevcut yedeklerin süresi değişmez.'
    expect(screen.queryByText(note)).not.toBeInTheDocument()
    fireEvent.change(screen.getByLabelText('Saklama süresi'), { target: { value: String(90 * 86400) } })
    expect(screen.getByText(note)).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalled())
    expect(sentBody().retention).toMatchObject({ keepForSeconds: 90 * 86400, deleteGracePeriodSeconds: 600 })
  })

  it('shows a stored "Optional" as Açık and keeps Optional on the wire until the switch is flipped', async () => {
    mount()
    expect(await screen.findByText('Şifreleme')).toBeInTheDocument()
    expect(screen.queryByText('İsteğe bağlı')).not.toBeInTheDocument()
    await change('Saklama ve şifreleme')
    expect(screen.getByRole('switch', { name: 'Şifreleme: Açık' })).toBeChecked()
    fireEvent.change(screen.getByLabelText('Saklama süresi'), { target: { value: String(90 * 86400) } })
    fireEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalledTimes(1))
    expect(sentBody().configuration.encryptionPolicy).toBe('Optional')
    await waitFor(() => expect(screen.queryByRole('form')).not.toBeInTheDocument())
    await change('Saklama ve şifreleme')
    const toggle = screen.getByRole('switch', { name: 'Şifreleme: Açık' })
    fireEvent.click(toggle)
    fireEvent.click(screen.getByRole('switch', { name: 'Şifreleme: Kapalı' }))
    fireEvent.click(screen.getByRole('button', { name: 'Kaydet' }))
    await waitFor(() => expect(saveBackupJob).toHaveBeenCalledTimes(2))
    expect(sentBody().configuration.encryptionPolicy).toBe('Required')
  })

  it('suggests a recovery key without blocking when encryption is on', async () => {
    mount([withConfiguration({ encryptionPolicy: 'Off' })])
    await change('Saklama ve şifreleme')
    expect(screen.queryByRole('link', { name: 'Kurtarma anahtarı' })).not.toBeInTheDocument()
    fireEvent.click(screen.getByRole('switch', { name: 'Şifreleme: Kapalı' }))
    expect(screen.getByRole('link', { name: 'Kurtarma anahtarı' })).toHaveAttribute('href', '/tenants/tenant-a/settings/recovery-key')
    expect(screen.getByRole('button', { name: 'Kaydet' })).toBeEnabled()
  })
})

describe('values outside the presets', () => {
  it('names a custom retention and schedule as current, without raw seconds or cron text, and lets a preset replace them', async () => {
    const custom = withConfiguration({}, { retention: { ...job.retention!, keepForSeconds: 45 * 86400 }, schedule: { ...job.schedule!, cronExpression: '15 3 * * 1' } })
    mount([custom])
    expect(screen.queryByText(/15 3 \* \* 1|3888000/)).not.toBeInTheDocument()
    await change('Zamanlama')
    const schedule = screen.getByLabelText('Hazır zamanlamalar')
    expect(within(schedule).getByRole('option', { name: 'Özel zamanlama (mevcut)' })).toBeInTheDocument()
    expect(schedule).toHaveValue('custom')
    fireEvent.change(schedule, { target: { value: '0 8 * * *' } })
    expect(schedule).toHaveValue('0 8 * * *')
    expect(screen.queryByRole('option', { name: 'Özel zamanlama (mevcut)' })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Vazgeç' }))
    await change('Saklama ve şifreleme')
    const retention = screen.getByLabelText('Saklama süresi')
    expect(within(retention).getByRole('option', { name: '45 gün (mevcut)' })).toBeInTheDocument()
    expect(retention).toHaveValue('custom')
    expect(screen.queryByText(/3888000|saniye/)).not.toBeInTheDocument()
    fireEvent.change(retention, { target: { value: String(14 * 86400) } })
    expect(retention).toHaveValue(String(14 * 86400))
  })

  it('notes that non-standard hidden settings are kept, without listing them or offering a reset', async () => {
    mount()
    await change('Zamanlama')
    const form = screen.getByRole('form', { name: 'Zamanlama' })
    expect(within(form).getByText('Bu işte standart dışı gelişmiş ayarlar var; korunuyor.')).toBeInTheDocument()
    expect(within(form).queryByRole('button', { name: /Sıfırla|varsayılan/i })).not.toBeInTheDocument()
    expect(within(form).queryByText(/Linear|acme\/erp|7200/)).not.toBeInTheDocument()
  })

  it('shows no such note on a standard job', async () => {
    const standard = withConfiguration({ stagingReference: 'default', storagePrefix: '', sourceSite: null, copyOnly: true, checksumPolicy: 'Required', compressionPolicy: 'Preferred' }, {
      retryPolicy: { maxAttempts: 3, initialDelaySeconds: 30, maxDelaySeconds: 600, backoff: 'Exponential', jitter: 0.2 }, overlapPolicy: 'Skip',
      schedule: { ...job.schedule!, missedRunGraceSeconds: 1800, overlapPolicy: 'Skip' }, retention: { ...job.retention!, deleteGracePeriodSeconds: 86400 },
    })
    mount([standard])
    await change('Zamanlama')
    expect(screen.queryByText(/standart dışı gelişmiş ayarlar/)).not.toBeInTheDocument()
  })
})

describe('leaving with unsaved changes', () => {
  it('asks before another section opens, and keeps editing when told to stay', async () => {
    mount()
    await change('Zamanlama')
    fireEvent.change(screen.getByLabelText('Hazır zamanlamalar'), { target: { value: '0 8 * * *' } })
    fireEvent.click(screen.getByRole('button', { name: 'Değiştir: Hedef' }))
    const dialog = within(await screen.findByRole('dialog', { name: 'Değişiklikler kaydedilmedi' }))
    fireEvent.click(dialog.getByRole('button', { name: 'Vazgeç' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(screen.getByRole('form', { name: 'Zamanlama' })).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Değiştir: Hedef' }))
    fireEvent.click(within(await screen.findByRole('dialog', { name: 'Değişiklikler kaydedilmedi' })).getByRole('button', { name: 'Değişiklikleri at' }))
    expect(await screen.findByRole('form', { name: 'Hedef' })).toBeInTheDocument()
    expect(screen.queryByRole('form', { name: 'Zamanlama' })).not.toBeInTheDocument()
  })

  it('opens another section at once when nothing changed', async () => {
    mount()
    await change('Zamanlama')
    fireEvent.click(screen.getByRole('button', { name: 'Değiştir: Hedef' }))
    expect(await screen.findByRole('form', { name: 'Hedef' })).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('asks before the page is left for the list', async () => {
    const router = mount()
    await change('Kaynak')
    fireEvent.change(screen.getByLabelText('İş adı'), { target: { value: 'Yeni ad' } })
    await userEvent.click(within(screen.getByRole('navigation', { name: 'Sayfa yolu' })).getByRole('link', { name: 'Yedekleme işleri' }))
    const dialog = within(await screen.findByRole('dialog', { name: 'Değişiklikler kaydedilmedi' }))
    expect(router.state.location.pathname).toBe('/tenants/tenant-a/backup-jobs/job-a')
    fireEvent.click(dialog.getByRole('button', { name: 'Değişiklikleri at' }))
    await waitFor(() => expect(router.state.location.pathname).toBe('/tenants/tenant-a/backup-jobs'))
  })
})
