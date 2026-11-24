import { screen } from '@testing-library/react'
import { render } from '@/test/render'
import userEvent from '@testing-library/user-event'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { setFeatureFlags } from '@/features/feature-flags/feature-flags'
import type { JobFormState } from '@/features/backup-jobs/use-job-form'
import { SourceStep } from './source-step'

afterEach(() => setFeatureFlags({}))

const form = { values: { name: '', agentId: '', selected: [], sourceMode: 'manual', enabled: true, configuration: { engine: 'file-system' } }, errors: {}, agents: { data: [] }, set: () => {}, setConfiguration: () => {}, job: undefined } as unknown as JobFormState

describe('file source hint', () => {
  it.each([['windowsVss'], ['linuxSnapshots']] as const)('mentions snapshot sources only while %s is on', (flag) => {
    const { unmount } = render(<SourceStep form={form} />)
    expect(screen.queryByText(/anlık görüntü kaynağı/)).not.toBeInTheDocument()
    unmount()
    setFeatureFlags({ [flag]: true })
    render(<SourceStep form={form} />)
    expect(screen.getByText(/anlık görüntü kaynağı/)).toBeInTheDocument()
  })
})

describe('manual database source', () => {
  const manual = (configuration: object) => {
    const setConfiguration = vi.fn()
    const state = { ...form, values: { ...form.values, configuration }, setConfiguration } as unknown as JobFormState
    render(<SourceStep form={state} />)
    return setConfiguration
  }

  it('lets SQL Server use SQL authentication', async () => {
    const setConfiguration = manual({ engine: 'mssql', integratedSecurity: true, encrypt: true })
    expect(screen.queryByLabelText('Veritabanı kullanıcı adı')).not.toBeInTheDocument()
    await userEvent.click(screen.getByRole('radio', { name: 'SQL Server oturumu (kullanıcı adı ve parola)' }))
    expect(setConfiguration).toHaveBeenCalledWith(expect.objectContaining({ integratedSecurity: false }))
  })

  it('shows the login fields once SQL authentication is chosen', () => {
    manual({ engine: 'mssql', integratedSecurity: false, encrypt: true })
    expect(screen.getByLabelText('Veritabanı kullanıcı adı')).toBeVisible()
  })

  it('lets PostgreSQL connect without TLS', async () => {
    const setConfiguration = manual({ engine: 'postgres', integratedSecurity: false, encrypt: true })
    await userEvent.click(screen.getByRole('checkbox', { name: 'Bağlantıyı şifrele' }))
    expect(setConfiguration).toHaveBeenCalledWith(expect.objectContaining({ encrypt: false }))
  })
})
