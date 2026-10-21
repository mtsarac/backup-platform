import { fireEvent, screen } from '@testing-library/react'
import { render } from '@/test/render'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { makeRun } from '@/features/backups/test-data'
import { dateFilterInstant } from '@/features/backups/presentation'
import type { BackupRunFilters, BackupRunPage } from '@/features/backups/types'
import { BackupsPage } from './backups-page'

const state = vi.hoisted(() => ({
  filters: [] as BackupRunFilters[],
  page: undefined as BackupRunPage | undefined,
}))
vi.mock('@/features/tenancy/use-tenant', () => ({ useTenant: () => ({ tenantId: 'tenant-a' }) }))
vi.mock('@/features/backups/hooks', () => ({ useBackups: (filters: BackupRunFilters) => { state.filters.push(filters); return { data: state.page, isPending: false, isFetching: false, error: null } } }))
vi.mock('@/features/backup-jobs/hooks', () => ({ useBackupJobs: () => ({ data: [{ id: 'job-id', name: 'Günlük SQL yedeği' }], isPending: false, error: null }) }))
vi.mock('@/features/agents/hooks', () => ({ useAgents: () => ({ data: [{ agentId: 'agent-id', hostName: 'SQL ajanı' }], isPending: false, error: null }) }))

beforeEach(() => {
  state.filters = []
  state.page = { items: [makeRun()], totalCount: 60, page: 1, pageSize: 25 }
})

describe('run history filters and pagination', () => {
  it('shows anomaly and signed comparison beside success while normal runs have no warning badge', () => {
    state.page = { items: [makeRun({ status: 'Succeeded', isSizeAnomaly: true, sizeAnomalyStatus: 'SizeAnomalyWarning', sizeAnomalyBaselineBytes: 1000, sizeAnomalyDeviationRatio: 2 }),
      makeRun({ id: 'normal-run', status: 'Succeeded', sizeAnomalyStatus: 'Normal', sizeAnomalyBaselineBytes: 1000, sizeAnomalyDeviationRatio: 0.05 })], totalCount: 2, page: 1, pageSize: 25 }
    render(<MemoryRouter><BackupsPage /></MemoryRouter>)
    expect(screen.getAllByText('Boyut Anomalisi')).toHaveLength(1)
    expect(screen.getAllByText('Başarılı')).toHaveLength(2)
    expect(screen.getAllByText(/Geçmiş ortalama: 1000 B/)[0]).toHaveTextContent('+%200')
    expect(screen.getAllByText(/Geçmiş ortalama: 1000 B/)[1]).toHaveTextContent('+%5')
  })

  it('submits all filters to query hook, resets page on change, and clears filters', () => {
    render(<MemoryRouter><BackupsPage /></MemoryRouter>)
    fireEvent.click(screen.getByRole('button', { name: 'Sonraki' }))
    expect(state.filters.at(-1)?.page).toBe(2)
    fireEvent.change(screen.getByRole('combobox', { name: 'Durum' }), { target: { value: 'Failed' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'İş' }), { target: { value: 'job-id' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Ajan' }), { target: { value: 'agent-id' } })
    fireEvent.change(screen.getByRole('combobox', { name: 'Tetikleyici' }), { target: { value: 'Retry' } })
    fireEvent.change(screen.getByLabelText('Oluşturulma başlangıcı'), { target: { value: '2026-10-01' } })
    fireEvent.change(screen.getByLabelText('Oluşturulma sonu'), { target: { value: '2026-10-06' } })
    expect(state.filters.at(-1)).toEqual({ page: 1, pageSize: 25, status: 'Failed', jobId: 'job-id', agentId: 'agent-id', trigger: 'Retry', from: dateFilterInstant('2026-10-01'), to: dateFilterInstant('2026-10-06', true) })
    fireEvent.click(screen.getByRole('button', { name: 'Filtreleri temizle' }))
    expect(state.filters.at(-1)).toEqual({ page: 1, pageSize: 25 })
    expect(screen.getByLabelText('Oluşturulma başlangıcı')).toHaveValue('')
  })

  it('shows required run columns, tenant detail links, and no raw ids', () => {
    render(<MemoryRouter><BackupsPage /></MemoryRouter>)
    for (const name of ['İş', 'Tetikleyici', 'Başlangıç', 'Süre', 'Durum']) {
      expect(screen.getByRole('columnheader', { name })).toBeInTheDocument()
    }
    expect(screen.getByText('Uygulama · SQL ajanı')).toBeInTheDocument()
    expect(screen.getByText('Planlanan: —')).toBeInTheDocument()
    expect(screen.getByText('Deneme 1 / 3')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Günlük SQL yedeği' })).toHaveAttribute('href', '/tenants/tenant-a/backup-runs/run-id')
    expect(screen.queryByText('job-id')).not.toBeInTheDocument()
    expect(screen.queryByText('agent-id')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Önceki' })).toBeDisabled()
  })
})
