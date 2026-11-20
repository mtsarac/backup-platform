import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as clientModule from '@/lib/api/client'
import { apiRequest } from '@/lib/api/client'
import { renderPlatform } from '@/features/platform/test-utils'
import { NotificationAdministration } from './administration'

vi.mock('@/lib/api/client', async (importOriginal) => ({ ...(await importOriginal<typeof clientModule>()), apiRequest: vi.fn() }))

const delivery = { id: 'delivery-1', tenantId: 'tenant-1', eventType: 'backup_failed', subjectName: 'Sunucu A', channelName: 'E-posta', targetDisplay: 'a***@example.com', attempts: 5, errorCode: 'delivery_failed', createdAt: '2026-01-01T12:00:00Z' }
let items: typeof delivery[]
let total = 1

beforeEach(() => {
  items = [delivery]
  total = 1
  vi.mocked(apiRequest).mockReset().mockImplementation(async (path, options) => {
    if (path.startsWith('platform/notifications/deliveries/failed')) return { items, totalCount: items.length ? total : 0, page: 1, pageSize: 20 } as never
    if (path === 'platform/notifications/deliveries/delivery-1/retry') { items = []; return undefined as never }
    if (path === 'platform/notifications/history-retention') return (options?.method === 'PUT' ? options.json : { retentionDays: 90 }) as never
    throw new Error('Unexpected request')
  })
})

describe('notification administration', () => {
  it('lists failures and retries a delivery', async () => {
    const user = userEvent.setup()
    renderPlatform(<NotificationAdministration />)
    expect(await screen.findByText(/Sunucu A/)).toBeInTheDocument()
    expect(screen.getByText(/tenant-1/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Gönderimi yeniden dene' }))
    await waitFor(() => expect(apiRequest).toHaveBeenCalledWith('platform/notifications/deliveries/delivery-1/retry', { method: 'POST' }))
    await waitFor(() => expect(screen.queryByText(/Sunucu A/)).not.toBeInTheDocument())
  })

  it('validates and saves retention independently of audit history', async () => {
    const user = userEvent.setup()
    renderPlatform(<NotificationAdministration />)
    const input = await screen.findByLabelText('Saklama süresi (gün)')
    expect(input).toHaveValue(90)
    await user.clear(input)
    await user.type(input, '0')
    expect(screen.getByRole('button', { name: 'Saklama süresini kaydet' })).toBeDisabled()
    await user.clear(input)
    await user.type(input, '120')
    await user.click(screen.getByRole('button', { name: 'Saklama süresini kaydet' }))
    await waitFor(() => expect(apiRequest).toHaveBeenCalledWith('platform/notifications/history-retention', { method: 'PUT', json: { retentionDays: 120 } }))
  })

  it('preserves a failed delivery and reports a failed retry', async () => {
    const user = userEvent.setup()
    renderPlatform(<NotificationAdministration />)
    await screen.findByText(/Sunucu A/)
    vi.mocked(apiRequest).mockRejectedValue(new Error('Network error'))
    await user.click(screen.getByRole('button', { name: 'Gönderimi yeniden dene' }))
    expect(await screen.findByRole('alert')).toBeInTheDocument()
    expect(screen.getByText(/Sunucu A/)).toBeInTheDocument()
  })

  it('searches, sorts and pages the failed deliveries through the query', async () => {
    total = 45
    const user = userEvent.setup()
    renderPlatform(<NotificationAdministration />)
    await screen.findByText(/Sunucu A/)
    expect(apiRequest).toHaveBeenCalledWith('platform/notifications/deliveries/failed?page=1&pageSize=20', expect.anything())
    await user.type(screen.getByRole('searchbox', { name: 'Gönderim ara' }), 'smtp')
    await waitFor(() => expect(apiRequest).toHaveBeenLastCalledWith('platform/notifications/deliveries/failed?page=1&pageSize=20&search=smtp', expect.anything()))
    await user.selectOptions(screen.getByLabelText('Sırala'), 'attempts:desc')
    await waitFor(() => expect(apiRequest).toHaveBeenLastCalledWith('platform/notifications/deliveries/failed?page=1&pageSize=20&search=smtp&sort=attempts&dir=desc', expect.anything()))
    await user.click(screen.getByRole('button', { name: 'Sonraki' }))
    await waitFor(() => expect(apiRequest).toHaveBeenLastCalledWith('platform/notifications/deliveries/failed?page=2&pageSize=20&search=smtp&sort=attempts&dir=desc', expect.anything()))
  })

  it('offers clearing the search when no failure matches', async () => {
    items = []
    const user = userEvent.setup()
    renderPlatform(<NotificationAdministration />, { path: '/platform/notifications?search=zzz' })
    await screen.findByText('Aramayla eşleşen kayıt yok.')
    await user.click(screen.getByRole('button', { name: 'Filtreleri temizle' }))
    await waitFor(() => expect(apiRequest).toHaveBeenLastCalledWith('platform/notifications/deliveries/failed?page=1&pageSize=20', expect.anything()))
  })
})
