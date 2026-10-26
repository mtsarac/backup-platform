import { cleanup, fireEvent, screen } from '@testing-library/react'
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
  it('shows a failed request as what happened, effect on the data and next step', () => {
    open({ error: new ApiError(500) })
    expect(screen.getByRole('alert')).toHaveTextContent('Ne yapmalı')
  })
})
