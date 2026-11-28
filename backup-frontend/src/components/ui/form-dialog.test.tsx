import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { render } from '@/test/render'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/lib/api/client'
import { FormDialog } from '@/components/ui/form-dialog'

afterEach(cleanup)
function open(props: Partial<Parameters<typeof FormDialog>[0]> = {}) {
  const onSubmit = vi.fn()
  render(<FormDialog title="Yeni plan" submitLabel="Oluştur" pendingLabel="Oluşturuluyor…" pending={false} onSubmit={onSubmit} onClose={() => {}} {...props}><input aria-label="Ad" /></FormDialog>)
  return onSubmit
}
describe('FormDialog', () => {
  it('submits the form and shows nothing extra without a failure', () => {
    const onSubmit = open()
    fireEvent.click(screen.getByRole('button', { name: 'Oluştur' }))
    expect(onSubmit).toHaveBeenCalledOnce()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
  it('does not submit while disabled or pending', () => {
    const disabled = open({ disabled: true })
    expect(screen.getByRole('button', { name: 'Oluştur' })).toBeDisabled()
    fireEvent.submit(screen.getByLabelText('Ad'))
    expect(disabled).not.toHaveBeenCalled()
    cleanup()
    const pending = open({ pending: true })
    fireEvent.submit(screen.getByLabelText('Ad'))
    expect(pending).not.toHaveBeenCalled()
  })
  it('shows a failed request as one plain sentence', () => {
    open({ error: new ApiError(500) })
    expect(screen.getByRole('alert')).toHaveTextContent('Sunucu isteği tamamlayamadı. Daha sonra tekrar deneyin.')
  })
})

describe('FormDialog keyboard containment', () => {
  it('wraps Tab from the last control to the first and Shift+Tab back, synchronously', async () => {
    open()
    const input = screen.getByLabelText('Ad'), cancel = screen.getByRole('button', { name: 'Vazgeç' }), submit = screen.getByRole('button', { name: 'Oluştur' })
    await waitFor(() => expect(input).toHaveFocus())
    await userEvent.tab(); expect(cancel).toHaveFocus()
    await userEvent.tab(); expect(submit).toHaveFocus()
    await userEvent.tab(); expect(input).toHaveFocus()
    await userEvent.tab({ shift: true }); expect(submit).toHaveFocus()
  })
  it('skips a disabled submit button when wrapping', async () => {
    open({ disabled: true })
    const input = screen.getByLabelText('Ad'), cancel = screen.getByRole('button', { name: 'Vazgeç' })
    await waitFor(() => expect(input).toHaveFocus())
    await userEvent.tab(); expect(cancel).toHaveFocus()
    await userEvent.tab(); expect(input).toHaveFocus()
    await userEvent.tab({ shift: true }); expect(cancel).toHaveFocus()
  })
  it('keeps the footer outside the scrolling body so the actions stay reachable', () => {
    open()
    const submit = screen.getByRole('button', { name: 'Oluştur' })
    expect(submit.closest('[class*="overflow-y-auto"]')).toBeNull()
  })
  it('renders a destructive submit for removals confirmed with a password', () => {
    open({ destructive: true })
    expect(screen.getByRole('button', { name: 'Oluştur' })).toHaveClass('text-error')
  })
})
