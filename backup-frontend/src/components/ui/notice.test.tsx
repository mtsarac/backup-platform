import { describe, expect, it, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ApiError } from '@/lib/api/client'
import { ErrorNotice, Notice } from './notice'

describe('ErrorNotice', () => {
  it('tells what happened, what it means for the data and what to do, as an alert', () => {
    render(<ErrorNotice error={new ApiError(500)} />)
    const alert = screen.getByRole('alert')
    expect(alert).toHaveTextContent('Sunucu isteği tamamlayamadı. Daha sonra tekrar deneyin.')
    expect(alert).toHaveTextContent('Mevcut yedekleriniz etkilenmedi')
    expect(alert).toHaveTextContent('Sorun sürerse teknik ayrıntıları destek ekibine iletin.')
  })

  it('keeps the technical code collapsed and copyable', async () => {
    render(<ErrorNotice error={new ApiError(409, { error: 'admission_denied', code: 'quota_exceeded' })} />)
    expect(screen.getByText('quota_exceeded')).not.toBeVisible()
    await userEvent.click(screen.getByText('Teknik ayrıntılar'))
    expect(screen.getByText('quota_exceeded')).toBeVisible()
    expect(screen.getByRole('button', { name: 'Kopyala: Hata kodu' })).toBeInTheDocument()
  })

  it('accepts the three parts directly for failures that are not API errors', () => {
    render(<ErrorNotice what="İndirme bağlantısı güvenli değil." dataImpact="Yedeğiniz değişmedi." action="Yeni bir indirme başlatın." />)
    const alert = screen.getByRole('alert')
    expect(alert).toHaveTextContent('İndirme bağlantısı güvenli değil.')
    expect(alert).toHaveTextContent('Yedeğiniz değişmedi.')
    expect(alert).toHaveTextContent('Yeni bir indirme başlatın.')
  })

  it('offers the one action as a button when the caller can retry', async () => {
    const retry = vi.fn()
    render(<ErrorNotice error={new Error('offline')} onRetry={retry} />)
    await userEvent.click(screen.getByRole('button', { name: 'Tekrar dene' }))
    expect(retry).toHaveBeenCalledOnce()
  })

  it('shows an optional title above the message', () => {
    render(<ErrorNotice title="Veriler alınamadı" error={new ApiError(403)} />)
    expect(screen.getByText('Veriler alınamadı')).toBeInTheDocument()
    expect(screen.getByText('Bu işlem için yetkiniz yok.')).toBeInTheDocument()
  })
})

describe('Notice', () => {
  it('announces non-error messages politely', () => {
    render(<Notice tone="warning">Dikkat edin</Notice>)
    expect(screen.getByRole('status')).toHaveTextContent('Dikkat edin')
  })
})
