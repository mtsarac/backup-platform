import { describe, expect, it, vi } from 'vitest'
import { render, screen, within } from '@testing-library/react'
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
    render(<ErrorNotice error={new ApiError(409, { error: 'admission_denied', code: 'quota_exceeded', message: 'Storage is full.' })} />)
    expect(screen.queryByText('quota_exceeded')).toBeNull()
    expect(screen.queryByText('Storage is full.')).toBeNull()
    await userEvent.click(screen.getByRole('button', { name: 'Teknik ayrıntılar' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText('quota_exceeded')).toBeVisible()
    expect(within(dialog).getByRole('button', { name: 'Kopyala: Hata kodu' })).toBeInTheDocument()
    expect(within(dialog).getByText('Storage is full.')).toBeVisible()
    expect(within(dialog).getByRole('button', { name: 'Kopyala: Sunucu mesajı' })).toBeInTheDocument()
  })

  it('keeps a free-form server reason in copyable technical details', async () => {
    render(<ErrorNotice error={new ApiError(409, { reason: 'The remote server is offline.' })} />)
    expect(screen.queryByText('The remote server is offline.')).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: 'Teknik ayrıntılar' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText('The remote server is offline.')).toBeVisible()
    expect(within(dialog).getByRole('button', { name: 'Kopyala: Sunucu mesajı' })).toBeInTheDocument()
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
    await userEvent.click(screen.getByRole('button', { name: 'Yeniden dene' }))
    expect(retry).toHaveBeenCalledOnce()
  })

  it.each([
    ['DatabaseUnavailable', undefined, 'Bilgisayarı kontrol et'],
    ['AuthenticationFailed', undefined, 'Bilgileri düzelt'],
    ['StorageHostKeyMismatch', undefined, 'Bilgileri düzelt'],
    ['QuotaExceeded', undefined, 'Depolamayı yönet'],
    ['ScheduleGraceExpired', undefined, 'Şimdi yedekle'],
    ['SqlRestoreFailed', 'restore', 'Yeniden dene'],
  ] as const)('offers the one action of its class for %s and nothing else', async (failureCode, scope, label) => {
    const act = vi.fn()
    render(<ErrorNotice failureCode={failureCode} scope={scope} onAction={act} onRetry={act} />)
    expect(screen.getAllByRole('button').filter(button => button.textContent === label)).toHaveLength(1)
    expect(screen.queryByRole('button', { name: 'Tekrar dene' })).toBeNull()
    await userEvent.click(screen.getByRole('button', { name: label }))
    expect(act).toHaveBeenCalledOnce()
  })

  it('keeps the screen retry when the class action has no handler', async () => {
    const retry = vi.fn()
    render(<ErrorNotice failureCode="DatabaseUnavailable" onRetry={retry} />)
    await userEvent.click(screen.getByRole('button', { name: 'Yeniden dene' }))
    expect(retry).toHaveBeenCalledOnce()
  })

  it('keeps an unknown code copyable under closed technical details', async () => {
    render(<ErrorNotice failureCode="brand_new_backend_code" />)
    const alert = screen.getByRole('alert')
    expect(alert).toHaveTextContent('Beklenmeyen bir sorun oluştu. Biraz sonra yeniden deneyin; sürerse aşağıdaki kodu destek ekibine iletin.')
    expect(screen.queryByText('brand_new_backend_code')).not.toBeInTheDocument()
    await userEvent.click(within(alert).getByRole('button', { name: 'Teknik ayrıntılar' }))
    const dialog = await screen.findByRole('dialog')
    expect(within(dialog).getByText('brand_new_backend_code')).toBeVisible()
    expect(within(dialog).getByRole('button', { name: 'Kopyala: Hata kodu' })).toBeInTheDocument()
  })

  it('keeps a known code out of the text and behind the closed technical details', () => {
    render(<ErrorNotice failureCode="StorageAuthenticationFailed" />)
    expect(screen.getByRole('alert')).not.toHaveTextContent('StorageAuthenticationFailed')
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
