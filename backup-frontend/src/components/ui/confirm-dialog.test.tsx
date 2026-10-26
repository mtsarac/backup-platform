import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react'
import { render } from '@/test/render'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '@/lib/api/client'
import { ConfirmDialog } from './confirm-dialog'

afterEach(cleanup)
const consequence = { what: 'Kanal kalıcı olarak silinir.', who: 'Bu kanala bağlı bildirimler durur.', reversible: 'Hayır, yeniden eklemeniz gerekir.' }

describe('ConfirmDialog', () => {
  it('states what happens, who is affected and whether it can be undone before the action can be taken', () => {
    render(<ConfirmDialog open onClose={() => {}} title="Silinsin mi?" consequence={consequence} pending={false} onConfirm={() => {}} confirmLabel="Sil" />)
    expect(screen.getByRole('dialog')).toHaveAccessibleName('Silinsin mi?')
    for (const text of Object.values(consequence)) expect(screen.getByText(text)).toBeInTheDocument()
    expect(screen.getByText('Ne olacak?')).toBeInTheDocument()
    expect(screen.getByText('Kimleri etkiler?')).toBeInTheDocument()
    expect(screen.getByText('Geri alınabilir mi?')).toBeInTheDocument()
  })
  it('runs the action on confirm and offers cancel with initial focus', async () => {
    const onConfirm = vi.fn(); const onClose = vi.fn()
    render(<ConfirmDialog open onClose={onClose} title="Silinsin mi?" consequence={consequence} pending={false} onConfirm={onConfirm} confirmLabel="Sil" />)
    await waitFor(() => expect(screen.getByRole('button', { name: 'Vazgeç' })).toHaveFocus())
    fireEvent.click(screen.getByRole('button', { name: 'Sil' }))
    expect(onConfirm).toHaveBeenCalledOnce()
  })
  it('blocks a second confirmation while pending and shows a failure with its next step', () => {
    const onConfirm = vi.fn()
    render(<ConfirmDialog open onClose={() => {}} title="Silinsin mi?" consequence={consequence} pending error={new ApiError(409, { code: 'connection_in_use' })} onConfirm={onConfirm} confirmLabel="Sil" />)
    expect(screen.getByRole('button', { name: 'İşleniyor…' })).toBeDisabled()
    expect(screen.getByRole('alert')).toHaveTextContent('Ne yapmalı')
  })
})

describe('ConfirmDialog with a name', () => {
  function open(onConfirm = vi.fn()) {
    render(<ConfirmDialog open onClose={() => {}} title="Silinsin mi?" consequence={consequence} name="Muhasebe" pending={false} onConfirm={onConfirm} confirmLabel="Sil" />)
    return onConfirm
  }
  it('blocks the action until the exact name is typed', () => {
    const onConfirm = open()
    const confirm = screen.getByRole('button', { name: 'Sil' })
    const input = screen.getByRole('textbox', { name: /Muhasebe/ })
    expect(confirm).toBeDisabled()
    for (const wrong of ['muhasebe', 'Muhasebe ', 'Muhaseb', '']) { fireEvent.change(input, { target: { value: wrong } }); expect(confirm).toBeDisabled() }
    fireEvent.click(confirm)
    expect(onConfirm).not.toHaveBeenCalled()
    fireEvent.change(input, { target: { value: 'Muhasebe' } })
    expect(confirm).toBeEnabled()
    fireEvent.click(confirm)
    expect(onConfirm).toHaveBeenCalledOnce()
  })
  it('does not submit a mismatching name with the Enter key', () => {
    const onConfirm = open()
    fireEvent.submit(screen.getByRole('textbox', { name: /Muhasebe/ }))
    expect(onConfirm).not.toHaveBeenCalled()
  })
  it('forgets the typed name when reopened', () => {
    const view = render(<ConfirmDialog open onClose={() => {}} title="t" consequence={consequence} name="Muhasebe" pending={false} onConfirm={() => {}} confirmLabel="Sil" />)
    fireEvent.change(screen.getByRole('textbox', { name: /Muhasebe/ }), { target: { value: 'Muhasebe' } })
    view.rerender(<ConfirmDialog open={false} onClose={() => {}} title="t" consequence={consequence} name="Muhasebe" pending={false} onConfirm={() => {}} confirmLabel="Sil" />)
    view.rerender(<ConfirmDialog open onClose={() => {}} title="t" consequence={consequence} name="Muhasebe" pending={false} onConfirm={() => {}} confirmLabel="Sil" />)
    expect(screen.getByRole('textbox', { name: /Muhasebe/ })).toHaveValue('')
  })
})
