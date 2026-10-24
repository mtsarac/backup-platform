import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider, useQuery } from '@tanstack/react-query'
import { ApiError } from '@/lib/api/client'
import { QueryState } from './query-state'

function Loader({ fetcher }: { fetcher: () => Promise<string> }) {
  const query = useQuery({ queryKey: ['loader'], queryFn: fetcher, retry: false })
  return <QueryState isPending={query.isPending} error={query.error}>{query.data && <p>{query.data}</p>}</QueryState>
}

describe('QueryState', () => {
  it('offers one retry action that reloads the failed data', async () => {
    const fetcher = vi.fn().mockRejectedValueOnce(new ApiError(500)).mockResolvedValueOnce('Yedekler')
    render(<QueryClientProvider client={new QueryClient()}><Loader fetcher={fetcher} /></QueryClientProvider>)
    await userEvent.click(await screen.findByRole('button', { name: 'Tekrar dene' }))
    expect(await screen.findByText('Yedekler')).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})
