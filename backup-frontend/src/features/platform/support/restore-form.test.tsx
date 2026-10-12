import { fireEvent, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { renderPlatform } from '@/features/platform/test-utils'
import * as api from './api'
import { SupportRestoreForm } from './restore-form'
import { makeRestore, makeSession, restoreTargets } from './test-fixtures'
import { supportKeys } from './hooks'

vi.mock('./api')
beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getSupportRestoreTargets).mockResolvedValue(restoreTargets)
  vi.mocked(api.precheckSupportRestore).mockResolvedValue(makeRestore({ id: 'precheck-a' }))
  vi.mocked(api.getSupportRestore).mockResolvedValue(makeRestore({ id: 'precheck-a' }))
  vi.mocked(api.startSupportRestore).mockResolvedValue(makeRestore({ id: 'execution-a', purpose: 'Execution', status: 'Pending' }))
})
function mount() {
  const onStarted = vi.fn()
  const session = makeSession()
  const view = renderPlatform(<SupportRestoreForm session={session} artifactId="artifact-a" onStarted={onStarted} />)
  view.client.setQueryData(supportKeys.session(session.id), session)
  return { ...view, onStarted }
}
describe('support NewDatabase restore', () => {
  it('offers only capable eligible Agents and real configured targets; no Replace/overwrite/custom credential fields', async () => {
    mount()
    expect(await screen.findByText(/Bu hedef müşterinin canlı/)).toBeInTheDocument()
    expect(screen.getByText('Customer production SQL Server target.')).toBeInTheDocument()
    expect(screen.queryByRole('option', { name: /Offline/ })).not.toBeInTheDocument()
    expect(screen.getByRole('option', { name: /customer-sql:1433 · CustomerDB/ })).toBeInTheDocument()
    expect(screen.queryByText(/Mevcut veritabanının üzerine yaz/)).not.toBeInTheDocument()
    expect(screen.queryByLabelText(/Mod|Credential|Host/)).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Restore başlat' })).toBeDisabled()
    expect(screen.queryByText('never render backend exception text')).not.toBeInTheDocument()
  })
  it('requires successful matching Precheck and explicit acknowledgement before one Start with precheckId', async () => {
    const { onStarted } = mount()
    fireEvent.click(await screen.findByRole('button', { name: 'Precheck' }))
    await screen.findByText(/Precheck başarılı/)
    const button = screen.getByRole('button', { name: 'Restore başlat' })
    expect(button).toBeDisabled()
    expect(api.startSupportRestore).not.toHaveBeenCalled()
    fireEvent.click(screen.getByRole('checkbox', { name: /Bu işlemin müşterinin canlı/ }))
    expect(button).toBeEnabled()
    fireEvent.click(button); fireEvent.click(button)
    await waitFor(() => expect(onStarted).toHaveBeenCalledWith('execution-a'))
    expect(api.startSupportRestore).toHaveBeenCalledOnce()
    expect(api.precheckSupportRestore).toHaveBeenCalledWith('support-a', 'artifact-a', { targetJobId: 'job-a', databaseName: 'CustomerDB_support', options: { verifyChecksum: true, verifyBackup: true } }, expect.any(String))
    expect(api.startSupportRestore).toHaveBeenCalledWith('support-a', 'artifact-a', { targetJobId: 'job-a', databaseName: 'CustomerDB_support', options: { verifyChecksum: true, verifyBackup: true }, precheckId: 'precheck-a' }, expect.any(String))
    expect(screen.queryByText('never render backend exception text')).not.toBeInTheDocument()
  })
  it('input changes invalidate a successful Precheck and reset acknowledgement', async () => {
    mount()
    fireEvent.click(await screen.findByRole('button', { name: 'Precheck' }))
    await screen.findByText(/Precheck başarılı/)
    fireEvent.click(screen.getByRole('checkbox', { name: /Bu işlemin müşterinin canlı/ }))
    fireEvent.change(screen.getByLabelText('Yeni Database adı'), { target: { value: 'AnotherDatabase' } })
    expect(screen.getByRole('button', { name: 'Restore başlat' })).toBeDisabled()
    expect(screen.getByRole('checkbox', { name: /Bu işlemin müşterinin canlı/ })).not.toBeChecked()
    expect(screen.getByText(/Ayarlar değişti/)).toBeInTheDocument()
    expect(api.startSupportRestore).not.toHaveBeenCalled()
  })
  it('failed checks never enable Start even if acknowledged', async () => {
    vi.mocked(api.precheckSupportRestore).mockResolvedValue(makeRestore({ id: 'precheck-a', status: 'Failed', failureCode: 'TargetDatabaseExists' }))
    vi.mocked(api.getSupportRestore).mockResolvedValue(makeRestore({ id: 'precheck-a', status: 'Failed', failureCode: 'TargetDatabaseExists' }))
    mount()
    fireEvent.click(await screen.findByRole('button', { name: 'Precheck' }))
    await screen.findByText(/Hedef Database zaten var/)
    fireEvent.click(screen.getByRole('checkbox', { name: /Bu işlemin müşterinin canlı/ }))
    expect(screen.getByRole('button', { name: 'Restore başlat' })).toBeDisabled()
    expect(api.startSupportRestore).not.toHaveBeenCalled()
  })
  it.each(['Expired', 'Closed', 'Revoked'] as const)('%s sessions disable Precheck/Start and fetch no customer data', status => {
    renderPlatform(<SupportRestoreForm session={makeSession({ status })} artifactId="artifact-a" onStarted={vi.fn()} />)
    expect(screen.getByRole('button', { name: 'Precheck' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Restore başlat' })).toBeDisabled()
    expect(api.getSupportRestoreTargets).not.toHaveBeenCalled()
    expect(api.getSupportRestore).not.toHaveBeenCalled()
    expect(api.precheckSupportRestore).not.toHaveBeenCalled()
    expect(api.startSupportRestore).not.toHaveBeenCalled()
  })
})
