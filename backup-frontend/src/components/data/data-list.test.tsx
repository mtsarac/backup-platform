import { describe, expect, it } from 'vitest'
import { fireEvent, render, screen, within } from '@testing-library/react'
import { setViewport } from '@/test/viewport'
import { DataList, type Column } from './data-list'

interface Job { id: string; name: string; status: string; database: string; agent: string; schedule: string; encryption: string }

const jobs: Job[] = [
  { id: '1', name: 'Muhasebe', status: 'Başarılı', database: 'accounting', agent: 'Ofis-PC', schedule: '0 2 * * *', encryption: 'Açık' },
  { id: '2', name: 'Stok', status: 'Başarısız', database: 'stock', agent: 'Depo-PC', schedule: '0 3 * * *', encryption: 'Kapalı' },
]

const columns: Column<Job>[] = [
  { key: 'name', header: 'İş adı', render: job => job.name },
  { key: 'status', header: 'Durum', card: 'status', render: job => <span>{job.status}</span> },
  { key: 'database', header: 'Veritabanı', render: job => job.database },
  { key: 'agent', header: 'Bilgisayar', render: job => job.agent },
  { key: 'schedule', header: 'Zamanlama', render: job => job.schedule },
  { key: 'encryption', header: 'Şifreleme', card: 'hidden', render: job => job.encryption },
  { key: 'actions', header: 'İşlemler', card: 'actions', render: job => <button>{`Çalıştır ${job.name}`}</button> },
]

describe('DataList', () => {
  it('is a table on a large screen', () => {
    setViewport(true)
    render(<DataList rows={jobs} columns={columns} keyExtractor={job => job.id} />)
    expect(screen.getByRole('table')).toBeInTheDocument()
    expect(screen.getAllByRole('columnheader').map(header => header.textContent)).toContain('Şifreleme')
  })

  it('is a table when the browser cannot report its width', () => {
    render(<DataList rows={jobs} columns={columns} keyExtractor={job => job.id} />)
    expect(screen.getByRole('table')).toBeInTheDocument()
  })

  it('becomes cards on a narrow screen with the title, status, key fields and actions', () => {
    setViewport(false)
    render(<DataList rows={jobs} columns={columns} keyExtractor={job => job.id} />)
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    const cards = screen.getAllByRole('listitem')
    expect(cards).toHaveLength(2)
    const first = within(cards[0])
    expect(first.getByRole('heading', { name: 'Muhasebe' })).toBeInTheDocument()
    expect(first.getByText('Başarılı')).toBeInTheDocument()
    expect(first.getByText('accounting')).toBeInTheDocument()
    expect(first.getByText('Ofis-PC')).toBeInTheDocument()
    expect(first.getByText('0 2 * * *')).toBeInTheDocument()
    expect(first.getByRole('button', { name: 'Çalıştır Muhasebe' })).toBeInTheDocument()
  })

  it('labels each card field with its column header', () => {
    setViewport(false)
    render(<DataList rows={jobs} columns={columns} keyExtractor={job => job.id} />)
    const first = within(screen.getAllByRole('listitem')[0])
    expect(first.getByText('Veritabanı').nextElementSibling).toHaveTextContent('accounting')
  })

  it('keeps cards to the title, status and at most three key fields', () => {
    setViewport(false)
    const many: Column<Job>[] = [...columns.slice(0, 5), { key: 'extra', header: 'Fazladan', render: () => 'ekstra değer' }]
    render(<DataList rows={jobs} columns={many} keyExtractor={job => job.id} />)
    const first = within(screen.getAllByRole('listitem')[0])
    expect(first.queryByText('ekstra değer')).not.toBeInTheDocument()
    expect(first.getAllByRole('term')).toHaveLength(3)
  })

  it('leaves columns marked hidden out of cards', () => {
    setViewport(false)
    render(<DataList rows={jobs} columns={columns} keyExtractor={job => job.id} />)
    expect(screen.queryByText('Açık')).not.toBeInTheDocument()
  })

  it('says why it is empty in both layouts', () => {
    for (const large of [true, false]) {
      setViewport(large)
      const { unmount } = render(<DataList rows={[]} columns={columns} keyExtractor={job => job.id} emptyMessage="Hiç iş yok." />)
      expect(screen.getByText('Hiç iş yok.')).toBeInTheDocument()
      unmount()
    }
  })

  describe('search', () => {
    const many = Array.from({ length: 30 }, (_, index): Job => ({ id: String(index), name: `İş ${String(index).padStart(2, '0')}`, status: 'Başarılı', database: `db${index}`, agent: 'Ofis-PC', schedule: '', encryption: '' }))
    const search = { label: 'İş ara', text: (job: Job) => `${job.name} ${job.agent}` }

    it('filters rows and pages them in groups of 25', () => {
      setViewport(true)
      render(<DataList rows={many} columns={columns} keyExtractor={job => job.id} search={search} />)
      expect(screen.getAllByRole('row')).toHaveLength(26)
      fireEvent.click(screen.getByRole('button', { name: 'Sonraki' }))
      expect(screen.getAllByRole('row')).toHaveLength(6)
      fireEvent.change(screen.getByRole('searchbox', { name: 'İş ara' }), { target: { value: 'iş 07' } })
      expect(screen.getAllByRole('row')).toHaveLength(2)
      expect(screen.getByText('İş 07')).toBeInTheDocument()
    })

    it('says nothing matched instead of showing an empty table', () => {
      setViewport(true)
      render(<DataList rows={many} columns={columns} keyExtractor={job => job.id} search={search} emptyMessage="Hiç iş yok." />)
      fireEvent.change(screen.getByRole('searchbox', { name: 'İş ara' }), { target: { value: 'zzz' } })
      expect(screen.queryByRole('table')).not.toBeInTheDocument()
      expect(screen.getByText('Aramayla eşleşen kayıt yok.')).toBeInTheDocument()
    })

    it('searches and shows cards on a narrow screen', () => {
      setViewport(false)
      render(<DataList rows={many} columns={columns} keyExtractor={job => job.id} search={search} />)
      fireEvent.change(screen.getByRole('searchbox', { name: 'İş ara' }), { target: { value: 'iş 07' } })
      expect(screen.getAllByRole('listitem')).toHaveLength(1)
    })
  })
})
