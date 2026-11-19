import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { EmptyState } from './empty-state'

describe('EmptyState', () => {
  it('says one sentence and offers the one primary action', () => {
    render(<EmptyState message="Henüz yedekleme işi oluşturulmadı." action={<button>İlk yedeğinizi oluşturun</button>} />)
    expect(screen.getByText('Henüz yedekleme işi oluşturulmadı.')).toBeInTheDocument()
    expect(screen.getAllByRole('button')).toHaveLength(1)
  })

  it('shows no call to action when nothing can be done', () => {
    render(<EmptyState message="Henüz paylaşım yok." />)
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })

  it('offers "Filtreleri temizle" as the one action when filters hid everything', async () => {
    const clear = vi.fn()
    render(<EmptyState message="Bu filtrelerde yedekleme denemesi yok." action={<button>Yedekle</button>} onClearFilters={clear} />)
    expect(screen.getAllByRole('button')).toHaveLength(1)
    await userEvent.click(screen.getByRole('button', { name: 'Filtreleri temizle' }))
    expect(clear).toHaveBeenCalledOnce()
  })
})
