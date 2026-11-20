import { screen, waitFor } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import * as api from './api'
import { catalog, makeConnection } from './test-fixtures'
import { QuotaConnectionPicker } from './quota-connection-picker'

vi.mock('./api')

function mount() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={client}><MemoryRouter><QuotaConnectionPicker values={[]} onChange={() => {}} /></MemoryRouter></QueryClientProvider>)
}

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getPlatformProviders).mockResolvedValue({ items: catalog })
  vi.mocked(api.getPlatformConnections).mockResolvedValue({ items: [makeConnection({ id: 'c1' })], totalCount: 45, page: 1, pageSize: 20 })
})

describe('Quota connection picker', () => {
  it('searches and pages the connections on the server through the shared list state', async () => {
    mount()
    await waitFor(() => expect(api.getPlatformConnections).toHaveBeenCalledWith(expect.objectContaining({ page: 1, pageSize: 20 }), expect.anything()))
    await userEvent.type(screen.getByRole('searchbox', { name: 'Çalışma alanı veya bağlantı adı ara' }), 'arşiv')
    await waitFor(() => expect(api.getPlatformConnections).toHaveBeenLastCalledWith(expect.objectContaining({ page: 1, search: 'arşiv' }), expect.anything()))
    await userEvent.click(await screen.findByRole('button', { name: 'Sonraki' }))
    await waitFor(() => expect(api.getPlatformConnections).toHaveBeenLastCalledWith(expect.objectContaining({ page: 2, search: 'arşiv' }), expect.anything()))
  })
})
