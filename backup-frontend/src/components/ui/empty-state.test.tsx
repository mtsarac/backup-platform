import { describe, expect, it } from 'vitest'
import { render, screen } from '@testing-library/react'
import { EmptyState } from './empty-state'

describe('EmptyState', () => {
  it('says one sentence and offers the one primary action', () => {
    render(<EmptyState message="Henüz yedekleme işi oluşturulmadı." action={<button>İlk yedeğinizi oluşturun</button>} />)
    expect(screen.getByText('Henüz yedekleme işi oluşturulmadı.')).toBeInTheDocument()
    expect(screen.getAllByRole('button')).toHaveLength(1)
  })

  it('sends a user without permission to their administrator instead of showing the action', () => {
    render(<EmptyState message="Henüz depolama bağlantısı yok." action={<button>Bağlantı ekle</button>} canAct={false} />)
    expect(screen.getByText('Yöneticinize danışın')).toBeInTheDocument()
    expect(screen.queryByRole('button')).not.toBeInTheDocument()
  })

  it('shows no call to action when nothing can be done', () => {
    render(<EmptyState message="Kayıt bulunamadı." />)
    expect(screen.queryByText('Yöneticinize danışın')).not.toBeInTheDocument()
  })
})
